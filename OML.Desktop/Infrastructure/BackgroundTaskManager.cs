using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OML.Desktop.Infrastructure;

/// <summary>队列中的一项后台任务。</summary>
public sealed class BackgroundTaskItem
{
    public BackgroundTaskItem(string title, Func<IProgress<double>, CancellationToken, Task> work)
    {
        Title = title;
        Work = work;
    }

    public string Title { get; }

    public Func<IProgress<double>, CancellationToken, Task> Work { get; }

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
/// </summary>
public sealed partial class BackgroundTaskManager : ObservableObject
{
    public static BackgroundTaskManager Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Queue<BackgroundTaskItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    private BackgroundTaskItem? _current;
    private BackgroundTaskItem? _lastFailed;

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

    private BackgroundTaskManager()
    {
        _ = Task.Run(RunAsync);
    }

    /// <summary>把任务加入队列，返回可等待的句柄。</summary>
    public BackgroundTaskItem Enqueue(string title, Func<IProgress<double>, CancellationToken, Task> work)
    {
        var item = new BackgroundTaskItem(title, work);

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
        Enqueue(failed.Title, failed.Work);
    }

    private int QueueLength()
    {
        lock (_gate)
        {
            return _queue.Count;
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

            DispatcherHelper.Run(() =>
            {
                PendingCount = QueueLength();
                IsBusy = true;
                CanCancel = true;
                HasFailed = false;
                CurrentTaskText = item.Title;
                CurrentProgress = 0;
            });

            try
            {
                var progress = new Progress<double>(value =>
                {
                    item.Progress = value;
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
                DispatcherHelper.Run(() => CurrentTaskText = $"已取消：{item.Title}");
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                _lastFailed = item;
                item.Completion.TrySetResult(false);
                DispatcherHelper.Run(() =>
                {
                    CurrentTaskText = $"失败：{item.Title}";
                    HasFailed = true;
                });
            }
            finally
            {
                _current = null;
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
