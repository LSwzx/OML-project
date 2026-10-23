using System.Diagnostics;
using System.Text;

namespace OML.Core.Launching;

/// <summary>
/// 真正拉起 Java 进程并接管日志。
/// <para>
/// 两个关键点：
/// 1. <c>UseShellExecute = false</c> + <c>ArgumentList</c>：路径里有空格（<c>C:\Program Files\...</c>）
///    也不会被拆坏，且无需自己处理引号转义；
/// 2. 同时重定向 stdout 与 stderr：Minecraft 的崩溃栈经常只出现在 stderr，
///    漏掉它就只能看到"进程莫名退出"。
/// </para>
/// </summary>
public sealed class JvmLauncher : IDisposable
{
    private readonly string _logDirectory;
    private readonly string _logFileName;
    private readonly object _logGate = new();

    private StreamWriter? _logWriter;
    private bool _disposed;

    /// <param name="logDirectory">日志目录，通常是 <c>&lt;game_directory&gt;/logs</c>。</param>
    /// <param name="logFileName">
    /// 捕获日志的文件名，默认 <c>oml-launch.log</c>。
    /// <para>
    /// <b>为什么不是 latest.log：</b>Minecraft 自己的 Log4j 配置（版本 JSON 的 logging 字段）
    /// 就指向 <c>logs/latest.log</c>，启动器再占住同名文件会让游戏侧报
    /// <c>Unable to delete file ...: 另一个程序正在使用此文件</c>，日志轮转直接失败。
    /// 实测确认过这条错误，所以捕获输出单独落一个文件；游戏自己的 latest.log 仍然由游戏写入，
    /// 两者内容互补：一个是进程原始输出，一个是游戏日志框架格式化后的结果。
    /// </para>
    /// <para>确实需要写 latest.log 时显式传参即可，但请自行承担与游戏互抢的风险。</para>
    /// </param>
    public JvmLauncher(string logDirectory, string logFileName = "oml-launch.log")
    {
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _logFileName = string.IsNullOrWhiteSpace(logFileName) ? "oml-launch.log" : logFileName;
    }

    /// <summary>每收到一行日志（stdout 与 stderr 都算）触发一次，供 UI 渲染控制台。</summary>
    public Action<string>? OnLogReceived { get; set; }

    /// <summary>本次启动写入的日志文件路径。</summary>
    public string LogFilePath => Path.Combine(_logDirectory, _logFileName);

    /// <summary>启动 Java 进程并立即返回（日志在后台持续写入）。</summary>
    public Task<Process> LaunchAsync(LaunchArguments arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(arguments.JavaPath))
        {
            throw new ArgumentException("JavaPath 为空。", nameof(arguments));
        }

        if (string.IsNullOrWhiteSpace(arguments.WorkingDirectory) || !Directory.Exists(arguments.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{arguments.WorkingDirectory}");
        }

        Directory.CreateDirectory(_logDirectory);

        lock (_logGate)
        {
            _logWriter?.Dispose();

            // FileShare.ReadWrite：Minecraft 自己也会打开 logs/latest.log，
            // 独占打开会让游戏侧的 log4j 直接抛异常。
            var stream = new FileStream(
                LogFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite);

            _logWriter = new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _logWriter.WriteLine($"# {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} OML launch");
            _logWriter.WriteLine($"# {arguments.ToCommandLine()}");
        }

        var startInfo = new ProcessStartInfo(arguments.JavaPath)
        {
            WorkingDirectory = arguments.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) => WriteLine(e.Data);
        process.ErrorDataReceived += (_, e) => WriteLine(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 Java 进程：{arguments.JavaPath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return Task.FromResult(process);
    }

    private void WriteLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logGate)
        {
            try
            {
                _logWriter?.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
                // 进程退出后仍可能有尾部日志到达，忽略即可。
            }
        }

        try
        {
            OnLogReceived?.Invoke(line);
        }
        catch
        {
            // 订阅方的异常不应拖垮日志泵。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_logGate)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }
}
