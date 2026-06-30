using System.IO;

namespace Poe2PriceGui.Services;

/// <summary>
/// 应用文件日志服务：每次启动创建新文件，每日最多保留 3 个，线程安全写入 logs/ 目录。
/// 文件命名：app_yyyyMMdd_N.log（N 为当日序号，从 1 开始）。
/// </summary>
public class AppLogger
{
    private static readonly Lazy<AppLogger> _instance = new(() => new AppLogger());
    public static AppLogger Instance => _instance.Value;

    private readonly string _logDirectory;
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// 每日最多保留的日志文件数量。
    /// </summary>
    private const int MaxDailyLogFiles = 3;

    private AppLogger()
    {
        _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        _logFilePath = InitLogFile();
    }

    /// <summary>
    /// 初始化本次启动的日志文件：
    /// 1. 扫描当天已有的日志文件；
    /// 2. 若数量已达上限，删除最早的（序号最小的）；
    /// 3. 创建新文件，序号 = 当日最大序号 + 1。
    /// </summary>
    private string InitLogFile()
    {
        Directory.CreateDirectory(_logDirectory);
        var today = DateTime.Now.ToString("yyyyMMdd");
        var pattern = $"app_{today}_*.log";

        // 按文件名排序（序号升序），便于删除最早的。
        var existingFiles = Directory.GetFiles(_logDirectory, pattern)
            .OrderBy(f => f)
            .ToList();

        // 每日最多保留 MaxDailyLogFiles 个，本次启动还要新增 1 个，
        // 所以先删除到 MaxDailyLogFiles - 1 个。
        var keepCount = MaxDailyLogFiles - 1;
        while (existingFiles.Count > keepCount)
        {
            try
            {
                File.Delete(existingFiles[0]);
            }
            catch
            {
                // 删除失败不阻塞启动。
            }
            existingFiles.RemoveAt(0);
        }

        // 计算新序号：取剩余文件中最大序号 + 1。
        var nextSeq = 1;
        if (existingFiles.Count > 0)
        {
            var lastFile = Path.GetFileNameWithoutExtension(existingFiles[^1]);
            var parts = lastFile.Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[^1], out var lastSeq))
            {
                nextSeq = lastSeq + 1;
            }
        }

        return Path.Combine(_logDirectory, $"app_{today}_{nextSeq}.log");
    }

    /// <summary>
    /// 当前日志文件完整路径。
    /// </summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// 日志目录。
    /// </summary>
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// 清理所有日志文件。
    /// </summary>
    public int CleanLogs()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return 0;
        }

        var files = Directory.GetFiles(_logDirectory, "*.log");
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Error(ex, $"删除日志文件失败：{file}");
            }
        }

        return files.Length;
    }

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Error(Exception exception, string message) => Write("ERROR", $"{message}\n{exception}");

    private async void Write(string level, string message)
    {
        await _semaphore.WaitAsync();
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine);
        }
        catch
        {
            // 日志写入失败时不抛异常，避免影响主流程。
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
