using System.Diagnostics;
using System.Text;

namespace OML.Core.Bedrock;

/// <summary>
/// 调用 Windows PowerShell 执行 Appx 相关操作。
/// <para>
/// 基岩版是 UWP/MSIX 应用，Core 目标框架是 <c>net8.0</c>（无 WinRT 投影），
/// 因此统一通过 <c>Get-AppxPackage</c> / <c>Add-AppxPackage</c> 完成探测与安装，
/// 避免为几个 API 引入整套 Windows SDK。
/// </para>
/// </summary>
internal static class BedrockPowerShell
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 powershell.exe。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出。
            }

            throw new TimeoutException($"PowerShell 操作超时（{timeout.TotalSeconds:0} 秒）。");
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    /// <summary>把路径包装成 PowerShell 单引号字符串。</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
