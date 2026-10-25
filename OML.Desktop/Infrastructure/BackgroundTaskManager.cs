using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Core.Downloads;

namespace OML.Desktop.Infrastructure;

/// <summary>队列中的一项后台任务。</summary>
public sealed class BackgroundTaskItem
{
    public BackgroundTaskItem(string title, Func<IProgress<double>, CancellationToken, Task> work, TimeSpan? stallTimeout = null)
    {
        Title = title;
        Work = work;
        StallTimeout = stallTimeout;
    }

    public string Title { get; }

    public Func<IProgress<double>, CancellationToken, Task> Work { get; }

    /// <summary>进度长时间不变时的看门狗超时；null 表示不启用（例如运行外部安装器）。</summary>
    public TimeSpan? StallTimeout { get; set; }

    /// <summary>每个任务独立取消，互不影响。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>0.0 - 1.0。</summary>
    public double Progress { get; set; }

    public string? Error { get; set; }

    public bool WasCanceled { get; set; }

    /// <summary>任务结束（成功/失败/取消）时完成。</summary>
    public Task<bool> Completed => Completion.Task;
}

/// <summary>
/// 全局后台任务队列（单例）。
/// <para>
/// 串行执行：避免两个安装同时写 libraries/。
/// 支持取消当前任务与重试最近一次失败的任务。
/// </para>
/// <para>
/// 看门狗：下载类任务如果超过 <see cref="BackgroundTaskItem.StallTimeout"/> 没有任何
/// 进度/状态/日志更新，会被判定为卡死并强制取消，避免用户无限期等待。
/// </para>
/// </summary>
public sealed partial class BackgroundTaskManager : ObservableObject
{
    public static BackgroundTaskManager Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Queue<BackgroundTaskItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<string> _failureDetails = new();

    private BackgroundTaskItem? _current;
    private BackgroundTaskItem? _lastFailed;

    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _watchdogTripped;

    [ObservableProperty]
    private string currentTaskText = "空闲";

    [ObservableProperty]
    private double currentProgress;

    [ObservableProperty]
    private int pendingCount;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool canCancel;

    [ObservableProperty]
    private bool hasFailed;

    /// <summary>当前任务是否有可展示的失败明细。</summary>
    [ObservableProperty]
    private bool hasFailureDetails;

    /// <summary>供 UI 弹出的失败明细（每项一段）。</summary>
    public IReadOnlyList<string> FailureDetails => _failureDetails;

    /// <summary>下载/安装诊断日志，UI 订阅后写入日志面板。</summary>
    public event Action<string>? LogReceived;

    private BackgroundTaskManager()
    {
        _ = Task.Run(RunAsync);
        _ = Task.Run(WatchdogAsync);
    }

    /// <summary>把任务加入队列，返回可等待的句柄。</summary>
    public BackgroundTaskItem Enqueue(
        string title,
        Func<IProgress<double>, CancellationToken, Task> work,
        TimeSpan? stallTimeout = null)
    {
        var item = new BackgroundTaskItem(title, work, stallTimeout);

        lock (_gate)
        {
            _queue.Enqueue(item);
        }

        DispatcherHelper.Run(() =>
        {
            PendingCount = QueueLength();
            HasFailed = false;
        });

        _signal.Release();
        return item;
    }

    /// <summary>为当前任务启用看门狗（例如下载任务），默认不启用。</summary>
    public void ArmWatchdog(TimeSpan timeout)
    {
        if (_current is not null)
        {
            _current.StallTimeout = timeout;
            MarkActivity();
        }
    }

    /// <summary>更新全局状态栏（也会重置看门狗计时）。</summary>
    public void ReportStatus(string message)
    {
        MarkActivity();
        DispatcherHelper.Run(() => CurrentTaskText = message);
    }

    /// <summary>输出一条诊断日志到日志面板（也会重置看门狗计时）。</summary>
    public void ReportLog(string message)
    {
        MarkActivity();
        LogReceived?.Invoke(message);
    }

    /// <summary>取消当前正在执行的任务（已排队的其余任务不受影响）。</summary>
    [RelayCommand]
    private void Cancel() => _current?.Cancellation.Cancel();

    /// <summary>重新排队最近一次失败的任务。</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_lastFailed is null)
        {
            return;
        }

        var failed = _lastFailed;
        _lastFailed = null;
        HasFailed = false;
        Enqueue(failed.Title, failed.Work, failed.StallTimeout);
    }

    /// <summary>展示当前任务的失败明细。</summary>
    [RelayCommand]
    private async Task ShowFailureDetailsAsync()
    {
        if (_failureDetails.Count == 0)
        {
            return;
        }

        await OML.Desktop.Infrastructure.DialogService.Instance.ShowErrorAsync("下载失败详情", string.Join("\n\n", _failureDetails));
    }

    private void MarkActivity() => _lastActivityUtc = DateTime.UtcNow;

    private int QueueLength()
    {
        lock (_gate)
        {
            return _queue.Count;
        }
    }

    private async Task WatchdogAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var item = _current;
            if (item?.StallTimeout is not { } timeout || item.Cancellation.IsCancellationRequested)
            {
                continue;
            }

            if (DateTime.UtcNow - _lastActivityUtc < timeout)
            {
                continue;
            }

            _watchdogTripped = true;
            item.Cancellation.Cancel();

            DispatcherHelper.Run(() =>
            {
                CurrentTaskText = "下载疑似卡死，已自动中断，请尝试更换下载源或网络";
                HasFailed = true;
            });
        }
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            BackgroundTaskItem item;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    continue;
                }

                item = _queue.Dequeue();
            }

            _current = item;
            _watchdogTripped = false;
            MarkActivity();

            DispatcherHelper.Run(() =>
            {
                PendingCount = QueueLength();
                IsBusy = true;
                CanCancel = true;
                HasFailed = false;
                HasFailureDetails = false;
                _failureDetails.Clear();
                CurrentTaskText = item.Title;
                CurrentProgress = 0;
            });

            try
            {
                var progress = new Progress<double>(value =>
                {
                    item.Progress = value;
                    MarkActivity();
                    DispatcherHelper.Run(() => CurrentProgress = value * 100);
                });

                await item.Work(progress, item.Cancellation.Token).ConfigureAwait(false);
                item.Completion.TrySetResult(true);
                DispatcherHelper.Run(() => CurrentTaskText = "空闲");
            }
            catch (OperationCanceledException)
            {
                item.WasCanceled = true;
                item.Completion.TrySetResult(false);

                if (_watchdogTripped)
                {
                    item.Error = "下载疑似卡死，已自动中断，请尝试更换下载源或网络";
                    _lastFailed = item;
                    DispatcherHelper.Run(() =>
                    {
                        CurrentTaskText = item.Error;
                        HasFailed = true;
                    });
                }
                else
                {
                    DispatcherHelper.Run(() => CurrentTaskText = $"已取消：{item.Title}");
                }
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                _lastFailed = item;

                if (ex is DownloadBatchException batch)
                {
                    var details = new StringBuilder();
                    foreach (var failure in batch.Failures)
                    {
                        details.AppendLine($"{failure.Task.Url}");
                        details.AppendLine($"  原因：{failure.Error.Message}");
                        details.AppendLine($"  目标路径：{failure.Task.SavePath}");
                        details.AppendLine();
                    }

                    DispatcherHelper.Run(() =>
                    {
                        _failureDetails.Clear();
                        _failureDetails.Add(details.ToString().TrimEnd());
                        HasFailureDetails = true;
                        CurrentTaskText = $"{batch.Failures.Count} 个文件下载失败，点击查看详情";
                        HasFailed = true;
                    });
                }
                else
                {
                    DispatcherHelper.Run(() =>
                    {
                        CurrentTaskText = $"失败：{item.Title}";
                        HasFailed = true;
                    });
                }

                item.Completion.TrySetResult(false);
            }
            finally
            {
                _current = null;
                _watchdogTripped = false;
                DispatcherHelper.Run(() =>
                {
                    IsBusy = false;
                    CanCancel = false;
                    CurrentProgress = 0;
                    PendingCount = QueueLength();
                });
            }
        }
    }
}
