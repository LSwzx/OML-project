using System.Text.RegularExpressions;
using OML.Abstractions.Models;

namespace OML.Core.Versioning;

/// <summary>
/// Mojang 的 <c>rules</c> 求值器。
/// <para>
/// 语义（必须严格遵守，否则会加载到别的平台的本地库，表现为 <c>UnsatisfiedLinkError</c>）：
/// <list type="number">
/// <item>没有 <c>rules</c> 或为空 → <b>允许</b>。</item>
/// <item>有 <c>rules</c> → 初始为<b>拒绝</b>，然后按数组顺序逐条评估。</item>
/// <item>只有"条件匹配"的规则才生效，生效规则把结果改为 <c>action != disallow</c>。</item>
/// <item><c>features</c> 中的键若未在环境里声明，按 <c>false</c> 处理（Mojang 官方语义）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyDictionary<string, bool> _features;

    /// <param name="platform">目标平台。</param>
    /// <param name="features">
    /// 启动特性开关（如 <c>has_custom_resolution</c>）。默认全部为 <c>false</c>，
    /// 留给后续 UI 勾选项接线。
    /// </param>
    public RuleEvaluator(PlatformInfo platform, IReadOnlyDictionary<string, bool>? features = null)
    {
        Platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _features = features ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>目标平台。</summary>
    public PlatformInfo Platform { get; }

    /// <summary>按当前机器构造。</summary>
    public static RuleEvaluator ForCurrentPlatform(IReadOnlyDictionary<string, bool>? features = null) =>
        new(PlatformInfo.Current, features);

    /// <summary>求值一组规则，返回该条目是否适用于当前平台。</summary>
    public bool Evaluate(IReadOnlyList<Rule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule))
            {
                continue;
            }

            allowed = !IsDisallow(rule.Action);
        }

        return allowed;
    }

    /// <summary>单条规则的条件是否成立（不涉及 action 语义）。</summary>
    public bool Matches(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Os is { } os)
        {
            if (!string.IsNullOrEmpty(os.Name) &&
                !string.Equals(os.Name, Platform.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(os.Arch) &&
                !string.Equals(PlatformInfo.NormalizeArch(os.Arch), PlatformInfo.NormalizeArch(Platform.Arch), StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(os.Version) && !VersionMatches(os.Version))
            {
                return false;
            }
        }

        if (rule.Features is { Count: > 0 } features)
        {
            foreach (var (name, expected) in features)
            {
                if (!_features.TryGetValue(name, out var actual) || actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool VersionMatches(string pattern)
    {
        try
        {
            return Regex.IsMatch(Platform.OsVersion, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException)
        {
            // 版本 JSON 里的正则可能是坏的；坏的规则视为不匹配，而不是让启动器崩溃。
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsDisallow(string? action) =>
        string.Equals(action, "disallow", StringComparison.OrdinalIgnoreCase);
}
