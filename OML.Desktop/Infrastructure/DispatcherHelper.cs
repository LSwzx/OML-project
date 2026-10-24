using System.Windows;
using System.Windows.Threading;

namespace OML.Desktop.Infrastructure;

/// <summary>
/// UI 线程工具。
/// <para>
/// <c>LaunchSession.LogReceived</c> 与下载进度都来自后台线程，
/// 直接改 <c>ObservableCollection</c> 或绑定属性会抛
/// <c>The calling thread cannot access this object</c>。统一从这里回到 UI 线程，
/// 避免每个 ViewModel 各写一遍 <c>Dispatcher.Invoke</c>。
/// </para>
/// </summary>
public static class DispatcherHelper
{
    /// <summary>当前应用的 UI Dispatcher；应用未启动时为 <c>null</c>。</summary>
    public static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

    /// <summary>当前线程是否就是 UI 线程。</summary>
    public static bool IsOnUiThread => UiDispatcher?.CheckAccess() ?? true;

    /// <summary>在 UI 线程同步执行（已在 UI 线程则直接执行）。</summary>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = UiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>在 UI 线程异步执行。</summary>
    public static Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = UiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
