using System.Diagnostics;

namespace OML.Abstractions.Launching;

/// <summary>进程退出后的结果。</summary>
/// <param name="ExitCode">退出码；0 表示正常退出。</param>
/// <param name="CrashReportPath">崩溃报告绝对路径；非崩溃为 <c>null</c>。</param>
public sealed record GameExitInfo(int ExitCode, string? CrashReportPath)
{
    /// <summary>是否疑似崩溃（存在新生成的 crash report）。</summary>
    public bool Crashed => CrashReportPath is not null;
}

/// <summary>
/// 已启动的游戏进程。
/// <para>
/// 启动是异步的：<see cref="LaunchGameAsync"/> 在进程起来后立刻返回，
/// 退出码要等到进程结束才可知，所以这里用任务承载"退出后才知道的东西"，
/// 同时提供同步可读的 <see cref="ExitCode"/>（未退出时为 <c>null</c>）。
/// </para>
/// </summary>
public sealed class GameProcessInfo
{
    private readonly Task<GameExitInfo> _completion;

    public GameProcessInfo(Process process, Task<GameExitInfo> completion)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    /// <summary>底层进程对象，便于 UI 提供"结束游戏"按钮。</summary>
    public Process Process { get; }

    /// <summary>进程是否已退出。</summary>
    public bool HasExited => Process.HasExited;

    /// <summary>退出码；进程仍在运行时为 <c>null</c>。</summary>
    public int? ExitCode =>
        _completion.Status == TaskStatus.RanToCompletion ? _completion.Result.ExitCode : null;

    /// <summary>崩溃报告路径；未崩溃或尚未退出时为 <c>null</c>。</summary>
    public string? CrashReportPath =>
        _completion.Status == TaskStatus.RanToCompletion ? _completion.Result.CrashReportPath : null;

    /// <summary>等待进程退出，并返回退出码与崩溃报告。</summary>
    public Task<GameExitInfo> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _completion.WaitAsync(cancellationToken);
}
