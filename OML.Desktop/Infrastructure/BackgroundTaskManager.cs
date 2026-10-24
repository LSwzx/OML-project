using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;

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

    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>0.0 - 1.0。</summary>
    public double Progress { get; set; }

    public string? Error { get; set; }

    /// <summary>任务结束（成功或失败）时完成。</summary>
    public Task<bool> Completed => Completion.Task;
}

/// <summary>
/// 全局后台任务队列（单例）。
/// <para>
/// 安装与下载不能绑在页面生命周期上：用户切到主页，下载必须继续。
/// 队列串行执行（避免多个安装同时写 libraries/），进度汇总到窗口底部的状态条。
/// 真正的并发度由下载引擎内部决定，队列只保证"同一时刻只有一个大任务在跑"。
/// </para>
/// </summary>
public sealed partial class BackgroundTaskManager : ObservableObject
{
    public static BackgroundTaskManager Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Queue<BackgroundTaskItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    private string currentTaskText = "空闲";

    [ObservableProperty]
    private double currentProgress;

    [ObservableProperty]
    private int pendingCount;

    [ObservableProperty]
    private bool isBusy;

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

        DispatcherHelper.Run(() => PendingCount = QueueLength());
        _signal.Release();
        return item;
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
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
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

            DispatcherHelper.Run(() =>
            {
                PendingCount = QueueLength();
                IsBusy = true;
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

                await item.Work(progress, _cts.Token).ConfigureAwait(false);
                item.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                item.Completion.TrySetResult(false);
            }
            finally
            {
                DispatcherHelper.Run(() =>
                {
                    IsBusy = false;
                    CurrentProgress = 0;
                    CurrentTaskText = "空闲";
                    PendingCount = QueueLength();
                });
            }
        }
    }
}
