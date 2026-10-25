using System.Diagnostics;

namespace OML.Core.Bedrock;

/// <summary>通过 AUMID 激活已安装的基岩版。</summary>
public sealed class BedrockLauncher
{
    /// <summary>
    /// 用 <c>explorer.exe shell:appsFolder\&lt;PackageFamilyName&gt;!App</c> 拉起游戏。
    /// <para>
    /// 这是最稳妥的免 COM 方式；explorer 会转交给 Application Activation Manager。
    /// </para>
    /// </summary>
    public Task LaunchAsync(BedrockInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(installation.PackageFamilyName))
        {
            throw new InvalidOperationException("基岩版包族名为空，无法构造启动 URI。");
        }

        var target = $"shell:appsFolder\\{installation.AppUserModelId}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", target)
            {
                UseShellExecute = true,
            });

            if (process is null)
            {
                throw new InvalidOperationException("explorer.exe 未能启动。");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"启动基岩版失败：{ex.Message}", ex);
        }

        return Task.CompletedTask;
    }
}
