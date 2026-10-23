using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OML.Abstractions.Auth;
using OML.Abstractions.Models;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Core.Launching;

/// <summary>
/// 把版本 JSON 的模板参数、路径、用户身份拼成一条可直接交给 Java 的命令行。
/// <para>
/// 三条主线：JVM 参数（含内存与品牌标识）→ 主类 → 游戏参数。
/// 变量替换覆盖 <c>${auth_player_name}</c>、<c>${game_directory}</c>、<c>${classpath}</c> 等，
/// 条件参数（<c>{rules, value}</c>）复用 <see cref="RuleEvaluator"/> 求值。
/// </para>
/// </summary>
public sealed class LaunchArgumentBuilder
{
    private static readonly Regex VariablePattern = new(
        @"\$\{(?<name>[A-Za-z0-9_]+)\}",
        RegexOptions.Compiled);

    /// <summary>构建启动参数。</summary>
    public LaunchArguments Build(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Version);
        ArgumentNullException.ThrowIfNull(options.Libraries);
        ArgumentNullException.ThrowIfNull(options.User);

        var version = options.Version;
        var game = options.GameDirectory;
        var versionId = version.Id;

        if (string.IsNullOrWhiteSpace(version.MainClass))
        {
            throw new InvalidOperationException($"版本 {versionId} 缺少 mainClass，无法启动。");
        }

        var gameRoot = game.GetGameRoot(versionId);
        var nativesDirectory = game.GetVersionNativesDirectory(versionId);

        // 客户端 jar 必须排在 classpath 最前面：Mojang 的 mainClass 就在它里面，
        // 漏掉它 Java 会直接抛 ClassNotFoundException（而库全都在时看起来一切正常）。
        var classpathEntries = new List<string>();
        var clientJar = game.GetVersionJarPath(versionId);
        if (options.IncludeClientJarInClasspath && File.Exists(clientJar))
        {
            classpathEntries.Add(clientJar);
        }

        classpathEntries.AddRange(options.Libraries.Classpath);
        var classpath = string.Join(Path.PathSeparator, classpathEntries);

        var warnings = new List<string>();
        var variables = BuildVariables(options, gameRoot, nativesDirectory, classpath);

        var evaluator = new RuleEvaluator(PlatformInfo.Current, options.Features);

        var arguments = new List<string>();

        // ---- JVM
        if (options.MinMemoryMb > 0)
        {
            arguments.Add($"-Xms{options.MinMemoryMb}M");
        }

        arguments.Add($"-Xmx{options.MaxMemoryMb}M");
        arguments.Add($"-Dminecraft.launcher.brand={options.LauncherName}");
        arguments.Add($"-Dminecraft.launcher.version={options.LauncherVersion}");
        arguments.Add("-Dfile.encoding=UTF-8");

        // Java 18+ 的 stdout/stderr 仍按系统代码页编码（中文 Windows 是 GBK），
        // 不强制成 UTF-8 的话，重定向抓到的日志在 UTF-8 终端里就是乱码。
        arguments.Add("-Dstdout.encoding=UTF-8");
        arguments.Add("-Dstderr.encoding=UTF-8");

        arguments.AddRange(RenderEntries(version.Arguments?.Jvm, variables, evaluator, warnings));
        arguments.AddRange(options.ExtraJvmArguments);

        // 模板里没有的情况（旧版本 JSON 没有 jvm 参数数组）由这里补齐。
        if (!arguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
        {
            arguments.Add($"-Djava.library.path={nativesDirectory}");
        }

        if (!ContainsClasspathFlag(arguments))
        {
            arguments.Add("-cp");
            arguments.Add(classpath);
        }

        // ---- 主类
        arguments.Add(version.MainClass);

        // ---- 游戏参数
        if (version.Arguments is { } template)
        {
            arguments.AddRange(RenderEntries(template.Game, variables, evaluator, warnings));
        }
        else if (!string.IsNullOrWhiteSpace(version.MinecraftArguments))
        {
            // 1.12- 的扁平字符串格式。
            foreach (var token in SplitLegacyArguments(version.MinecraftArguments))
            {
                arguments.Add(Substitute(token, variables, warnings));
            }
        }

        arguments.AddRange(options.ExtraGameArguments);

        return new LaunchArguments
        {
            JavaPath = options.JavaPath,
            Arguments = arguments,
            MainClass = version.MainClass,
            WorkingDirectory = gameRoot,
            Classpath = classpath,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private static Dictionary<string, string> BuildVariables(
        LaunchOptions options,
        string gameRoot,
        string nativesDirectory,
        string classpath)
    {
        var version = options.Version;
        var user = options.User;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = user.Username,
            ["auth_uuid"] = user.Uuid,
            ["auth_access_token"] = string.IsNullOrWhiteSpace(user.AccessToken) ? "0" : user.AccessToken!,
            ["auth_session"] = string.IsNullOrWhiteSpace(user.AccessToken) ? "0" : user.AccessToken!,
            ["user_type"] = string.Equals(user.Type, AuthTypes.Microsoft, StringComparison.OrdinalIgnoreCase) ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_name"] = version.Id,
            ["version_type"] = version.Type,
            ["game_directory"] = gameRoot,
            ["game_assets"] = options.GameDirectory.AssetsDirectory,
            ["assets_root"] = options.GameDirectory.AssetsDirectory,
            ["assets_index_name"] = version.Assets ?? version.AssetIndex?.Id ?? "legacy",
            ["natives_directory"] = nativesDirectory,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = options.GameDirectory.LibrariesDirectory,
            ["launcher_name"] = options.LauncherName,
            ["launcher_version"] = options.LauncherVersion,
            ["resolution_width"] = options.ResolutionWidth.ToString(),
            ["resolution_height"] = options.ResolutionHeight.ToString(),
            ["game_assets_root"] = options.GameDirectory.AssetsDirectory,
        };
    }

    /// <summary>
    /// 渲染一组参数：字符串直接替换变量；对象则先按 rules 求值，再把 value（字符串或字符串数组）展开。
    /// </summary>
    private static IEnumerable<string> RenderEntries(
        IEnumerable<JsonElement>? entries,
        IReadOnlyDictionary<string, string> variables,
        RuleEvaluator evaluator,
        List<string> warnings)
    {
        if (entries is null)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            switch (entry.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var rendered = Substitute(entry.GetString() ?? string.Empty, variables, warnings);
                    if (!string.IsNullOrEmpty(rendered))
                    {
                        yield return rendered;
                    }

                    break;
                }

                case JsonValueKind.Object:
                {
                    ConditionalEntry? conditional;
                    try
                    {
                        conditional = JsonSerializer.Deserialize<ConditionalEntry>(entry.GetRawText(), OmlJsonOptions.Default);
                    }
                    catch (JsonException)
                    {
                        warnings.Add($"无法解析条件参数：{entry.GetRawText()}");
                        break;
                    }

                    if (conditional is null || (conditional.Rules is { Count: > 0 } && !evaluator.Evaluate(conditional.Rules)))
                    {
                        break;
                    }

                    foreach (var value in ExpandValue(conditional.Value, variables, warnings))
                    {
                        yield return value;
                    }

                    break;
                }
            }
        }
    }

    private static IEnumerable<string> ExpandValue(
        JsonElement value,
        IReadOnlyDictionary<string, string> variables,
        List<string> warnings)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var rendered = Substitute(value.GetString() ?? string.Empty, variables, warnings);
            if (!string.IsNullOrEmpty(rendered))
            {
                yield return rendered;
            }

            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var rendered = Substitute(item.GetString() ?? string.Empty, variables, warnings);
            if (!string.IsNullOrEmpty(rendered))
            {
                yield return rendered;
            }
        }
    }

    private static string Substitute(
        string input,
        IReadOnlyDictionary<string, string> variables,
        List<string> warnings)
    {
        if (!input.Contains("${", StringComparison.Ordinal))
        {
            return input;
        }

        return VariablePattern.Replace(input, match =>
        {
            var name = match.Groups["name"].Value;
            if (variables.TryGetValue(name, out var value))
            {
                return value;
            }

            warnings.Add($"未定义的启动变量：${{{name}}}");
            return match.Value;
        });
    }

    private static bool ContainsClasspathFlag(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "-cp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "-classpath", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-cp=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>旧格式的 <c>minecraftArguments</c> 以空格分隔，引号内的空格需要保留。</summary>
    private static IEnumerable<string> SplitLegacyArguments(string arguments)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var character in arguments)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (character == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary><c>arguments</c> 中条件对象的形状。</summary>
    private sealed class ConditionalEntry
    {
        [JsonPropertyName("rules")]
        public List<Rule>? Rules { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }
    }
}
