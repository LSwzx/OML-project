using System.Text.Json;

namespace OML.Core.Bedrock;

/// <summary>读取本机已安装的基岩版（正式版 / Preview）。</summary>
public sealed class BedrockPackageLocator
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $pkgs = Get-AppxPackage | Where-Object {
            $_.Name -eq 'Microsoft.MinecraftUWP' -or $_.Name -eq 'Microsoft.MinecraftWindowsBeta'
        }
        if ($pkgs) {
            $pkgs | Select-Object Name, Version, PackageFamilyName, InstallLocation |
                ConvertTo-Json -Compress
        }
        """;

    /// <summary>返回所有已安装的基岩版；未安装返回空数组。</summary>
    public async Task<IReadOnlyList<BedrockInstallation>> GetInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var (exitCode, stdout, stderr) = await BedrockPowerShell.RunAsync(
            Script,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                throw new InvalidOperationException($"读取已安装基岩版失败：{stderr.Trim()}");
            }

            return Array.Empty<BedrockInstallation>();
        }

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };

        var results = new List<BedrockInstallation>();

        foreach (var element in elements)
        {
            var name = element.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;
            var version = element.TryGetProperty("Version", out var versionElement) ? versionElement.GetString() : null;
            var family = element.TryGetProperty("PackageFamilyName", out var familyElement) ? familyElement.GetString() : null;
            var location = element.TryGetProperty("InstallLocation", out var locationElement) ? locationElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(family))
            {
                continue;
            }

            results.Add(new BedrockInstallation(
                name!,
                version ?? "未知",
                family!,
                location ?? string.Empty));
        }

        return results;
    }

    /// <summary>按通道读取；未安装返回 <c>null</c>。</summary>
    public async Task<BedrockInstallation?> GetInstalledAsync(
        BedrockChannel channel,
        CancellationToken cancellationToken = default)
    {
        var all = await GetInstalledAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(installation =>
            channel == BedrockChannel.Preview ? installation.IsPreview : !installation.IsPreview);
    }
}
