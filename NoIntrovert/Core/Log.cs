namespace NoIntrovert.Core;

public enum LogLevel { Debug, Info, Success, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Success => "OK ",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {Message}";
}

public interface ILog
{
    void Write(LogLevel level, string message);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string m) => log.Write(LogLevel.Debug, m);
    public static void Info(this ILog log, string m) => log.Write(LogLevel.Info, m);
    public static void Success(this ILog log, string m) => log.Write(LogLevel.Success, m);
    public static void Warn(this ILog log, string m) => log.Write(LogLevel.Warning, m);
    public static void Error(this ILog log, string m) => log.Write(LogLevel.Error, m);
}

/// <summary>Prefixes every message, so lines from parallel jobs can be told apart.</summary>
public sealed class PrefixedLog(ILog inner, string prefix) : ILog
{
    public void Write(LogLevel level, string message) => inner.Write(level, $"{prefix}{message}");
}
