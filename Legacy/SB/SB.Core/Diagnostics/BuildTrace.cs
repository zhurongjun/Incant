using System.Diagnostics;
using System.Globalization;

namespace SB;

public static class BuildTrace
{
    private static readonly bool _enabled = IsEnabled();
    private static readonly DateTimeOffset _processStartTime = GetProcessStartTime();
    private static readonly Stopwatch _monotonicClock = Stopwatch.StartNew();
    private static readonly object _writeLock = new();

    public static bool Enabled => _enabled;

    public static void Mark(string name)
    {
        if (!_enabled)
            return;

        Write(name);
    }

    public static void Mark(string name, string detail)
    {
        if (!_enabled)
            return;

        Write($"{name}: {detail}");
    }

    public static IDisposable Scope(string name)
    {
        if (!_enabled)
            return NullScope.Instance;

        return new TraceScope(name);
    }

    public static T Measure<T>(string name, Func<T> action)
    {
        if (!_enabled)
            return action();

        using (Scope(name))
        {
            return action();
        }
    }

    public static void Measure(string name, Action action)
    {
        if (!_enabled)
        {
            action();
            return;
        }

        using (Scope(name))
        {
            action();
        }
    }

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("SB_BUILD_TRACE");
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset GetProcessStartTime()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime;
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }

    private static void Write(string message)
    {
        var sinceProcessStart = DateTimeOffset.Now - _processStartTime;
        var elapsed = _monotonicClock.Elapsed;
        lock (_writeLock)
        {
            Console.Error.WriteLine(
                "[SB_TRACE +{0}ms clock={1}ms tid={2}] {3}",
                sinceProcessStart.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                Environment.CurrentManagedThreadId,
                message);
        }
    }

    private sealed class TraceScope : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TraceScope(string name)
        {
            _name = name;
            Write($"{_name}.begin");
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            Write($"{_name}.end elapsed={_stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}ms");
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
