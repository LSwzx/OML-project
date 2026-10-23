namespace OML.Abstractions.Launching;

/// <summary>
/// 启动前预检失败。携带完整的缺失项清单，
/// 让上层能一次性列出"缺哪些文件"，而不是让 Java 在几秒后抛一个
/// <c>ClassNotFoundException</c> 让用户去猜。
/// </summary>
public sealed class LaunchPrecheckException : Exception
{
    public LaunchPrecheckException(IReadOnlyList<string> problems)
        : base(BuildMessage(problems))
    {
        Problems = problems ?? Array.Empty<string>();
    }

    /// <summary>全部问题描述。</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string BuildMessage(IReadOnlyList<string> problems)
    {
        if (problems is null || problems.Count == 0)
        {
            return "启动预检失败。";
        }

        return $"启动预检失败（{problems.Count} 项）：{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", problems)}";
    }
}
