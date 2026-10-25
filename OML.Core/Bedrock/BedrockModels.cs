namespace OML.Core.Bedrock;

/// <summary>基岩版发布通道。</summary>
public enum BedrockChannel
{
    /// <summary>正式版（Microsoft Store: 9NBLGGH2JHXJ）。</summary>
    Release,

    /// <summary>预览版（Microsoft Store: 9P5X4QVLC2XR）。</summary>
    Preview,
}

/// <summary>本地已安装的基岩版。</summary>
/// <param name="Name">包名，例如 <c>Microsoft.MinecraftUWP</c>。</param>
/// <param name="Version">包版本，例如 <c>1.21.90.3</c>。</param>
/// <param name="PackageFamilyName">包族名，用于构造 AUMID。</param>
/// <param name="InstallLocation">安装目录。</param>
public sealed record BedrockInstallation(
    string Name,
    string Version,
    string PackageFamilyName,
    string InstallLocation)
{
    /// <summary>是否为 Preview 版。</summary>
    public bool IsPreview => Name.Contains("Beta", StringComparison.OrdinalIgnoreCase);

    /// <summary>激活用的 AUMID：<c>PackageFamilyName!App</c>。</summary>
    public string AppUserModelId => $"{PackageFamilyName}!App";

    public BedrockChannel Channel => IsPreview ? BedrockChannel.Preview : BedrockChannel.Release;
}

/// <summary>Store 解析出的一个安装包文件。</summary>
/// <param name="Name">文件名，例如 <c>Microsoft.MinecraftUWP_..._x64__8wekyb3d8bbwe.msix</c>。</param>
/// <param name="Url">下载直链。</param>
public sealed record BedrockDownloadFile(string Name, string Url)
{
    private static readonly string[] MainPrefixes =
    {
        "Microsoft.MinecraftUWP",
        "Microsoft.MinecraftWindowsBeta",
        "Microsoft.Minecraft",
    };

    private static readonly string[] DependencyPrefixes =
    {
        "Microsoft.VCLibs",
        "Microsoft.UI.Xaml",
        "Microsoft.NET.Native",
        "Microsoft.Services.Store",
    };

    /// <summary>主包（Minecraft 本体）。</summary>
    public bool IsMain => MainPrefixes.Any(prefix =>
        Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>运行依赖（VCLibs / UI.Xaml / .NET Native 等）。</summary>
    public bool IsDependency => !IsMain && DependencyPrefixes.Any(prefix =>
        Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>基岩版安装结果。</summary>
public sealed record BedrockInstallResult(BedrockInstallation Installation, int PackageFiles);

/// <summary>授权检查状态。</summary>
public enum BedrockEntitlementStatus
{
    Owned,
    NotOwned,
    NotSignedIn,
    Error,
}

/// <summary>授权检查结果。</summary>
/// <param name="Status">状态。</param>
/// <param name="Message">给用户看的说明。</param>
public sealed record BedrockEntitlementResult(BedrockEntitlementStatus Status, string Message)
{
    public bool IsOwned => Status == BedrockEntitlementStatus.Owned;
}
