namespace OML.Abstractions.Launching;

/// <summary>启动流程的阶段。</summary>
public enum LaunchStage
{
    /// <summary>准备目录、加载配置。</summary>
    Preparing,

    /// <summary>读取版本清单/详情、解析依赖。</summary>
    Checking,

    /// <summary>下载缺失的库、客户端 jar、资源。</summary>
    Downloading,

    /// <summary>解压 natives。</summary>
    Extracting,

    /// <summary>构建参数并拉起进程。</summary>
    Launching,

    /// <summary>流程完成（进程已在运行）。</summary>
    Completed,
}

/// <summary>
/// 启动进度。
/// <para>
/// <see cref="Progress"/> 是<b>当前阶段内部</b>的百分比，不是全局百分比：
/// 下载 54 个库和下载 3900 个资源耗时差几个数量级，硬凑全局百分比只会误导用户。
/// UI 应根据 <see cref="Stage"/> 显示不同的文案与进度条。
/// </para>
/// </summary>
/// <param name="Stage">当前阶段。</param>
/// <param name="Progress">阶段内进度，0.0 - 1.0。</param>
/// <param name="Message">面向用户的状态文本。</param>
public sealed record LaunchProgress(LaunchStage Stage, double Progress, string Message)
{
    /// <summary>阶段内百分比（0-100）。</summary>
    public int Percent => (int)Math.Round(Math.Clamp(Progress, 0d, 1d) * 100);
}
