using Dream.Multiplayer.Diagnostics;

namespace Dream.Rendering.Core;

public interface IRenderFaultSink
{
    void Report(string source, Exception exception);
}

// Logs the first fault per source in full, then only a one-line count at 2, 4, 8, ... repeats.
public sealed class RenderFaultLog : IRenderFaultSink
{
    public const string DiagnosticsCategory = "render-fault";

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _faultCountsBySource = new(StringComparer.Ordinal);
    private readonly Action<string> _writeLine;
    private int _faultCount;
    private Exception? _lastFault;

    public RenderFaultLog()
        : this(WriteToConsoleAndDiagnostics)
    {
    }

    public RenderFaultLog(Action<string> writeLine)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        _writeLine = writeLine;
    }

    public static RenderFaultLog Shared { get; } = new();

    public int FaultCount
    {
        get
        {
            lock (_sync)
            {
                return _faultCount;
            }
        }
    }

    public Exception? LastFault
    {
        get
        {
            lock (_sync)
            {
                return _lastFault;
            }
        }
    }

    public int GetFaultCount(string source)
    {
        lock (_sync)
        {
            return _faultCountsBySource.TryGetValue(source, out int count) ? count : 0;
        }
    }

    public void Report(string source, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(exception);

        string? line;
        lock (_sync)
        {
            _faultCount++;
            _lastFault = exception;
            int sourceCount = _faultCountsBySource.TryGetValue(source, out int previous) ? previous + 1 : 1;
            _faultCountsBySource[source] = sourceCount;
            line = FormatLine(source, exception, sourceCount);
        }

        if (line == null)
        {
            return;
        }

        try
        {
            _writeLine(line);
        }
        catch (Exception)
        {
            // Fault reporting must never become a second fault.
        }
    }

    private static string? FormatLine(string source, Exception exception, int sourceCount)
    {
        if (sourceCount == 1)
        {
            return $"[render-fault] [{source}] {exception}";
        }

        if ((sourceCount & (sourceCount - 1)) != 0)
        {
            return null;
        }

        return $"[render-fault] [{source}] {sourceCount} faults so far; latest {exception.GetType().Name}: {exception.Message}";
    }

    private static void WriteToConsoleAndDiagnostics(string line)
    {
        if (NetworkDiagnosticsLog.IsEnabled)
        {
            // The diagnostics log also echoes to the console.
            NetworkDiagnosticsLog.Write(DiagnosticsCategory, line);
            return;
        }

        Console.WriteLine(line);
    }
}
