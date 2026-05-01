using System.Globalization;

namespace IconP1MVmixBridge;

public sealed class FileLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public string CurrentLogFile { get; }

    public FileLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        CurrentLogFile = Path.Combine(logDirectory, $"bridge-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(new FileStream(CurrentLogFile, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };

        Info("iCON P1-M vMix Bridge starting. Version {0}", typeof(FileLogger).Assembly.GetName().Version);
        Info("Log file: {0}", CurrentLogFile);
        DeleteOldLogs(logDirectory);
    }

    public void Debug(string message, params object?[] args) => Write("DEBUG", message, args);
    public void Info(string message, params object?[] args) => Write("INFO", message, args);
    public void Warn(string message, params object?[] args) => Write("WARN", message, args);
    public void Error(string message, params object?[] args) => Write("ERROR", message, args);

    public void Error(Exception exception, string message, params object?[] args)
    {
        Write("ERROR", message, args);
        Write("ERROR", exception.ToString());
    }

    private void Write(string level, string message, params object?[] args)
    {
        var formatted = args.Length == 0 ? message : string.Format(CultureInfo.InvariantCulture, message, args);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {formatted}";
        lock (_sync)
        {
            _writer.WriteLine(line);
        }
    }

    private void DeleteOldLogs(string logDirectory)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(logDirectory, "bridge-*.log"))
            {
                var info = new FileInfo(file);
                if (info.CreationTime < cutoff)
                    info.Delete();
            }
        }
        catch (Exception ex)
        {
            Warn("Could not delete old logs: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        Info("iCON P1-M vMix Bridge stopping.");
        lock (_sync)
        {
            _writer.Dispose();
        }
    }
}
