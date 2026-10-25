namespace OML.Core.Bedrock;

/// <summary>
/// 使用 <c>Add-AppxPackage</c> 静默安装基岩版及其依赖。
/// <para>
/// MSIX 是每用户安装，不需要管理员权限；依赖包必须与主包在同一次调用中传入，
/// 否则会报 0x80073CF3（缺少依赖）。
/// </para>
/// </summary>
public sealed class BedrockInstaller
{
    /// <summary>
    /// 安装主包与依赖。
    /// </summary>
    /// <param name="packagePaths">主包与依赖的本地路径；主包会根据文件名自动识别。</param>
    /// <param name="status">状态回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task InstallAsync(
        IReadOnlyList<string> packagePaths,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);

        var existing = packagePaths.Where(File.Exists).ToArray();
        if (existing.Length == 0)
        {
            throw new InvalidOperationException("没有可安装的基岩版安装包。");
        }

        var main = existing.FirstOrDefault(IsMainPackage)
            ?? throw new InvalidOperationException("找不到基岩版主安装包（Minecraft*.msix / .msixbundle）。");

        var dependencies = existing
            .Where(path => !string.Equals(path, main, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var dependencyList = dependencies.Length == 0
            ? string.Empty
            : " -DependencyPath @(" + string.Join(",", dependencies.Select(BedrockPowerShell.Quote)) + ")";

        var script = $"""
            $ErrorActionPreference = 'Stop'
            Add-AppxPackage -Path {BedrockPowerShell.Quote(main)}{dependencyList} -ForceApplicationShutdown
            Write-Output 'OML_INSTALL_OK'
            """;

        status?.Invoke("正在静默安装基岩版…");

        var (exitCode, stdout, stderr) = await BedrockPowerShell.RunAsync(
            script,
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 || !stdout.Contains("OML_INSTALL_OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"基岩版安装失败：{DescribeFailure(stderr)}");
        }
    }

    private static bool IsMainPackage(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("Microsoft.MinecraftUWP", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.MinecraftWindowsBeta", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.Minecraft", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeFailure(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "Add-AppxPackage 返回失败，但没有输出错误信息。";
        }

        var text = stderr.Trim();

        // 常见错误码给出可操作的解释。
        if (text.Contains("0x80073CF3", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("0x80073D02", StringComparison.OrdinalIgnoreCase))
        {
            return $"缺少依赖或版本冲突（{text}）。请确认依赖包已全部下载，并关闭正在运行的 Minecraft。";
        }

        if (text.Contains("0x80073CFB", StringComparison.OrdinalIgnoreCase))
        {
            return $"相同版本的包已安装（{text}）。如需重装，请先在 Windows 设置中卸载基岩版。";
        }

        return text;
    }
}
