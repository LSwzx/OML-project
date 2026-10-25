namespace OML.Abstractions.Configuration;

/// <summary>更新源类型。</summary>
public enum UpdateSourceType
{
    /// <summary>GitHub Releases（默认，直连）。</summary>
    Github,

    /// <summary>自定义地址（HTTP(S) 目录或本地目录）。</summary>
    Custom,

    /// <summary>镜像（国内加速地址，例如 ghproxy 前缀）。</summary>
    Mirror,
}
