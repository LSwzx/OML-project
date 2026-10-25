using OML.Abstractions.Launching;

namespace OML.Core.Launching;

/// <summary>
/// 把 <c>IProgress&lt;LaunchProgress&gt;</c> 的上报收口成一个入口：
/// 安装（<see cref="Versioning.VersionInstaller"/>）与补全启动（<see cref="LaunchSession"/>）共用。
/// </summary>
public static class LaunchProgressReporter
{
    public static void Report(IProgress<LaunchProgress>? progress, LaunchStage stage, double value, string message) =>
        progress?.Report(new LaunchProgress(stage, Math.Clamp(value, 0d, 1d), message));

    /// <summary>把 <c>IProgress&lt;double&gt;</c> 直接转成阶段回调（不回线程池，避免顺序错乱）。</summary>
    public sealed class PhaseProgress : IProgress<double>
    {
        private readonly Action<double> _onReport;

        public PhaseProgress(Action<double> onReport) => _onReport = onReport;

        public void Report(double value) => _onReport(Math.Clamp(value, 0d, 1d));
    }
}
