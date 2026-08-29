using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Incant.Base.Log;

/// <summary>Provides the process-wide asynchronous logging runtime.</summary>
public static class Log
{
    private static readonly Lock s_lifecycleLock = new();
    private static readonly List<ILogSink> s_sinks = [];
    private static IReadOnlyList<ILogSink> s_sinkSnapshot = Array.AsReadOnly(Array.Empty<ILogSink>());
    private static int s_minimumLevel = (int)LogLevel.Info;
    private static LogRuntime? s_runtime;
    private static long s_nextRuntimeId;

    [ThreadStatic]
    private static LogProducer? s_threadProducer;

    /// <summary>Gets whether a logging runtime currently accepts events.</summary>
    public static bool IsRunning => Volatile.Read(ref s_runtime)?.IsAccepting == true;

    /// <summary>Gets a stable read-only snapshot of the currently registered sinks.</summary>
    /// <remarks>Mutations made after this property is read do not alter the returned snapshot.</remarks>
    public static IReadOnlyList<ILogSink> Sinks => Volatile.Read(ref s_sinkSnapshot);

    /// <summary>Gets or sets the process-wide minimum accepted level.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned level is invalid.</exception>
    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref s_minimumLevel);
        set
        {
            LogRuntime.ValidateMinimumLevel(value);
            lock (s_lifecycleLock)
            {
                LogRuntime? runtime = Volatile.Read(ref s_runtime);
                runtime?.SetMinimumLevel(value);
                Volatile.Write(ref s_minimumLevel, (int)value);
            }
        }
    }

    /// <summary>Gets whether an event at the specified level would be accepted.</summary>
    /// <param name="level">The event level.</param>
    /// <returns><see langword="true" /> when at least one sink can receive the event.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level)
    {
        LogRuntime? runtime = Volatile.Read(ref s_runtime);
        return runtime is not null && runtime.IsEnabled(level);
    }

    /// <summary>Registers a sink and transfers its ownership to the logger.</summary>
    /// <param name="sink">The sink to register.</param>
    /// <remarks>
    /// A sink registered while logging is running is initialized on the worker before this method returns. A sink
    /// registered while logging is stopped is initialized by the next successful <see cref="Start(LogOptions)"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sink minimum level is invalid.</exception>
    /// <exception cref="InvalidOperationException">The same sink instance is already registered.</exception>
    public static void AddSink(ILogSink sink)
    {
        LogLevel sinkMinimumLevel = LogRuntime.ValidateSink(sink);
        lock (s_lifecycleLock)
        {
            if (s_sinks.Any(candidate => ReferenceEquals(candidate, sink)))
            {
                throw new InvalidOperationException("The log sink is already registered.");
            }

            LogRuntime? runtime = Volatile.Read(ref s_runtime);
            runtime?.AddSink(sink, sinkMinimumLevel);
            s_sinks.Add(sink);
            PublishSinkSnapshot();
        }
    }

    /// <summary>Removes and disposes a registered sink.</summary>
    /// <param name="sink">The sink instance to remove.</param>
    /// <returns><see langword="true"/> when the sink was registered; otherwise, <see langword="false"/>.</returns>
    /// <remarks>When logging is running, events published before this call are flushed before removal.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    public static bool RemoveSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (s_lifecycleLock)
        {
            int index = s_sinks.FindIndex(candidate => ReferenceEquals(candidate, sink));
            if (index < 0)
            {
                return false;
            }

            LogRuntime? runtime = Volatile.Read(ref s_runtime);
            if (runtime is not null)
            {
                runtime.RemoveSink(sink);
            }
            else
            {
                DisposeDetachedSink(sink);
            }

            s_sinks.RemoveAt(index);
            PublishSinkSnapshot();
            return true;
        }
    }

    /// <summary>Removes and disposes all registered sinks.</summary>
    /// <remarks>When logging is running, events published before this call are flushed before removal.</remarks>
    public static void ClearSinks()
    {
        lock (s_lifecycleLock)
        {
            if (s_sinks.Count == 0)
            {
                return;
            }

            LogRuntime? runtime = Volatile.Read(ref s_runtime);
            if (runtime is not null)
            {
                runtime.ClearSinks();
            }
            else
            {
                foreach (ILogSink sink in s_sinks)
                {
                    DisposeDetachedSink(sink);
                }
            }

            s_sinks.Clear();
            PublishSinkSnapshot();
        }
    }

    /// <summary>Starts a new process-wide runtime using the currently registered sinks and minimum level.</summary>
    /// <param name="options">The runtime options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capacity or flush interval is invalid.</exception>
    /// <exception cref="InvalidOperationException">Logging is already running.</exception>
    public static void Start(LogOptions options)
    {
        lock (s_lifecycleLock)
        {
            if (Volatile.Read(ref s_runtime) is not null)
            {
                throw new InvalidOperationException("Logging is already running.");
            }

            LogRuntime.ValidateOptions(options);
            foreach (ILogSink sink in s_sinks)
            {
                LogRuntime.ValidateSink(sink);
            }

            long runtimeId = Interlocked.Increment(ref s_nextRuntimeId);
            try
            {
                var runtime = new LogRuntime(runtimeId, options, s_sinks, MinimumLevel);
                Volatile.Write(ref s_runtime, runtime);
            }
            catch
            {
                s_sinks.Clear();
                PublishSinkSnapshot();
                throw;
            }
        }
    }

    /// <summary>Waits until all events published before this call are flushed by every active sink.</summary>
    /// <exception cref="InvalidOperationException">Logging is not running.</exception>
    public static void Flush()
    {
        lock (s_lifecycleLock)
        {
            LogRuntime runtime = Volatile.Read(ref s_runtime)
                ?? throw new InvalidOperationException("Logging is not running.");
            runtime.Flush();
        }
    }

    /// <summary>Stops the runtime, drains all accepted events, flushes, and disposes owned sinks.</summary>
    /// <exception cref="InvalidOperationException">Logging is not running.</exception>
    public static void Stop()
    {
        lock (s_lifecycleLock)
        {
            LogRuntime runtime = Volatile.Read(ref s_runtime)
                ?? throw new InvalidOperationException("Logging is not running.");
            Volatile.Write(ref s_runtime, null);
            try
            {
                runtime.Stop();
            }
            finally
            {
                s_sinks.Clear();
                PublishSinkSnapshot();
            }
        }
    }

    private static void PublishSinkSnapshot()
    {
        IReadOnlyList<ILogSink> snapshot = Array.AsReadOnly(s_sinks.ToArray());
        Volatile.Write(ref s_sinkSnapshot, snapshot);
    }

    private static void DisposeDetachedSink(ILogSink sink)
    {
        try
        {
            sink.Dispose();
        }
        catch (Exception exception)
        {
            EmergencyLog.Write($"Log sink '{sink.GetType().FullName}' failed while disposing.", exception);
        }
    }

    /// <summary>Writes a trace event in the general category.</summary>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Trace(string messageTemplate) =>
        Write0(LogLevel.Trace, LogCategory.General, null, null, messageTemplate);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0>(string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Trace, LogCategory.General, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1>(string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Trace, LogCategory.General, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Trace, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2, T3>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Trace, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Trace, LogCategory.General, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(LogCategory category, string messageTemplate) =>
        Write0(LogLevel.Trace, category, null, null, messageTemplate);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0>(LogCategory category, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Trace, category, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Trace, category, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Trace, category, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2, T3>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Trace, category, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(LogCategory category, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Trace, category, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Trace, LogCategory.General, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0>(TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Trace, LogCategory.General, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Trace, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Trace, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2, T3>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Trace, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Trace, LogCategory.General, rootDecorator, null, messageTemplate, arguments);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(LogCategory category, TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Trace, category, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Trace, category, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Trace, category, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Trace, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Trace, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Trace(string)" />
    public static void Trace(LogCategory category, TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Trace, category, rootDecorator, null, messageTemplate, arguments);

    /// <summary>Writes a debug event in the general category.</summary>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Debug(string messageTemplate) =>
        Write0(LogLevel.Debug, LogCategory.General, null, null, messageTemplate);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0>(string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Debug, LogCategory.General, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1>(string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Debug, LogCategory.General, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Debug, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2, T3>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Debug, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Debug, LogCategory.General, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(LogCategory category, string messageTemplate) =>
        Write0(LogLevel.Debug, category, null, null, messageTemplate);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0>(LogCategory category, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Debug, category, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Debug, category, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Debug, category, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2, T3>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Debug, category, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(LogCategory category, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Debug, category, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Debug, LogCategory.General, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0>(TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Debug, LogCategory.General, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Debug, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Debug, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2, T3>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Debug, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Debug, LogCategory.General, rootDecorator, null, messageTemplate, arguments);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(LogCategory category, TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Debug, category, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Debug, category, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Debug, category, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Debug, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Debug, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Debug(string)" />
    public static void Debug(LogCategory category, TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Debug, category, rootDecorator, null, messageTemplate, arguments);

    /// <summary>Writes an informational event in the general category.</summary>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Info(string messageTemplate) =>
        Write0(LogLevel.Info, LogCategory.General, null, null, messageTemplate);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0>(string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Info, LogCategory.General, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1>(string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Info, LogCategory.General, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Info, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2, T3>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Info, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Info, LogCategory.General, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(LogCategory category, string messageTemplate) =>
        Write0(LogLevel.Info, category, null, null, messageTemplate);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0>(LogCategory category, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Info, category, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Info, category, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Info, category, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2, T3>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Info, category, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(LogCategory category, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Info, category, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Info, LogCategory.General, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0>(TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Info, LogCategory.General, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Info, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Info, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2, T3>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Info, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Info, LogCategory.General, rootDecorator, null, messageTemplate, arguments);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(LogCategory category, TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Info, category, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Info, category, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Info, category, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Info, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Info(string)" />
    public static void Info<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Info, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Info(string)" />
    public static void Info(LogCategory category, TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Info, category, rootDecorator, null, messageTemplate, arguments);

    /// <summary>Writes a warning event in the general category.</summary>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Warning(string messageTemplate) =>
        Write0(LogLevel.Warning, LogCategory.General, null, null, messageTemplate);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0>(string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Warning, LogCategory.General, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1>(string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Warning, LogCategory.General, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Warning, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2, T3>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Warning, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Warning, LogCategory.General, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(LogCategory category, string messageTemplate) =>
        Write0(LogLevel.Warning, category, null, null, messageTemplate);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0>(LogCategory category, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Warning, category, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Warning, category, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Warning, category, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2, T3>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Warning, category, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(LogCategory category, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Warning, category, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Warning, LogCategory.General, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0>(TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Warning, LogCategory.General, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Warning, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Warning, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2, T3>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Warning, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Warning, LogCategory.General, rootDecorator, null, messageTemplate, arguments);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(LogCategory category, TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Warning, category, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Warning, category, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Warning, category, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Warning, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Warning, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Warning(string)" />
    public static void Warning(LogCategory category, TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Warning, category, rootDecorator, null, messageTemplate, arguments);

    /// <summary>Writes an error event in the general category.</summary>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Error(string messageTemplate) =>
        Write0(LogLevel.Error, LogCategory.General, null, null, messageTemplate);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0>(string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, LogCategory.General, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1>(string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, LogCategory.General, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2, T3>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, LogCategory.General, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(LogCategory category, string messageTemplate) =>
        Write0(LogLevel.Error, category, null, null, messageTemplate);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0>(LogCategory category, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, category, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, category, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, category, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2, T3>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, category, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(LogCategory category, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, category, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Error, LogCategory.General, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0>(TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, LogCategory.General, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2, T3>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, LogCategory.General, rootDecorator, null, messageTemplate, arguments);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(LogCategory category, TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Error, category, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, category, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, category, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(string)" />
    public static void Error<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(string)" />
    public static void Error(LogCategory category, TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, category, rootDecorator, null, messageTemplate, arguments);

    /// <summary>Writes an error event with an exception snapshot.</summary>
    /// <param name="exception">The exception captured on the calling thread.</param>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Error(Exception exception, string messageTemplate) =>
        Write0(LogLevel.Error, LogCategory.General, null, exception, messageTemplate);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0>(Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, LogCategory.General, null, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1>(Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, LogCategory.General, null, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2>(Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, LogCategory.General, null, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2, T3>(Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, LogCategory.General, null, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, LogCategory.General, null, exception, messageTemplate, arguments);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(LogCategory category, Exception exception, string messageTemplate) =>
        Write0(LogLevel.Error, category, null, exception, messageTemplate);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0>(LogCategory category, Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, category, null, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1>(LogCategory category, Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, category, null, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2>(LogCategory category, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, category, null, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2, T3>(LogCategory category, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, category, null, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(LogCategory category, Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, category, null, exception, messageTemplate, arguments);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(TextDecorator rootDecorator, Exception exception, string messageTemplate) =>
        Write0(LogLevel.Error, LogCategory.General, rootDecorator, exception, messageTemplate);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, LogCategory.General, rootDecorator, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, LogCategory.General, rootDecorator, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, LogCategory.General, rootDecorator, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2, T3>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, LogCategory.General, rootDecorator, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(TextDecorator rootDecorator, Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, LogCategory.General, rootDecorator, exception, messageTemplate, arguments);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate) =>
        Write0(LogLevel.Error, category, rootDecorator, exception, messageTemplate);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Error, category, rootDecorator, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Error, category, rootDecorator, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Error, category, rootDecorator, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Error, category, rootDecorator, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Error(Exception, string)" />
    public static void Error(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Error, category, rootDecorator, exception, messageTemplate, arguments);

    /// <summary>Writes a fatal event in the general category.</summary>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Fatal(string messageTemplate) =>
        Write0(LogLevel.Fatal, LogCategory.General, null, null, messageTemplate);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0>(string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, LogCategory.General, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1>(string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, LogCategory.General, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2, T3>(string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, LogCategory.General, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, LogCategory.General, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(LogCategory category, string messageTemplate) =>
        Write0(LogLevel.Fatal, category, null, null, messageTemplate);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0>(LogCategory category, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, category, null, null, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, category, null, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, category, null, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2, T3>(LogCategory category, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, category, null, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(LogCategory category, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, category, null, null, messageTemplate, arguments);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Fatal, LogCategory.General, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0>(TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, LogCategory.General, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2, T3>(TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, LogCategory.General, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, LogCategory.General, rootDecorator, null, messageTemplate, arguments);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(LogCategory category, TextDecorator rootDecorator, string messageTemplate) =>
        Write0(LogLevel.Fatal, category, rootDecorator, null, messageTemplate);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, category, rootDecorator, null, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, category, rootDecorator, null, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, category, rootDecorator, null, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(string)" />
    public static void Fatal(LogCategory category, TextDecorator rootDecorator, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, category, rootDecorator, null, messageTemplate, arguments);

    /// <summary>Writes a fatal event with an exception snapshot.</summary>
    /// <param name="exception">The exception captured on the calling thread.</param>
    /// <param name="messageTemplate">The message template parsed by the worker.</param>
    public static void Fatal(Exception exception, string messageTemplate) =>
        Write0(LogLevel.Fatal, LogCategory.General, null, exception, messageTemplate);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0>(Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, LogCategory.General, null, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1>(Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, LogCategory.General, null, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2>(Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, LogCategory.General, null, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2, T3>(Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, LogCategory.General, null, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, LogCategory.General, null, exception, messageTemplate, arguments);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(LogCategory category, Exception exception, string messageTemplate) =>
        Write0(LogLevel.Fatal, category, null, exception, messageTemplate);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0>(LogCategory category, Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, category, null, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1>(LogCategory category, Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, category, null, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2>(LogCategory category, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, category, null, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2, T3>(LogCategory category, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, category, null, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(LogCategory category, Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, category, null, exception, messageTemplate, arguments);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(TextDecorator rootDecorator, Exception exception, string messageTemplate) =>
        Write0(LogLevel.Fatal, LogCategory.General, rootDecorator, exception, messageTemplate);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, LogCategory.General, rootDecorator, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, LogCategory.General, rootDecorator, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, LogCategory.General, rootDecorator, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2, T3>(TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, LogCategory.General, rootDecorator, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(TextDecorator rootDecorator, Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, LogCategory.General, rootDecorator, exception, messageTemplate, arguments);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate) =>
        Write0(LogLevel.Fatal, category, rootDecorator, exception, messageTemplate);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0) =>
        Write1(LogLevel.Fatal, category, rootDecorator, exception, messageTemplate, argument0);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1) =>
        Write2(LogLevel.Fatal, category, rootDecorator, exception, messageTemplate, argument0, argument1);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2) =>
        Write3(LogLevel.Fatal, category, rootDecorator, exception, messageTemplate, argument0, argument1, argument2);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal<T0, T1, T2, T3>(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3) =>
        Write4(LogLevel.Fatal, category, rootDecorator, exception, messageTemplate, argument0, argument1, argument2, argument3);

    /// <inheritdoc cref="Fatal(Exception, string)" />
    public static void Fatal(LogCategory category, TextDecorator rootDecorator, Exception exception, string messageTemplate, params object?[] arguments) =>
        WriteMany(LogLevel.Fatal, category, rootDecorator, exception, messageTemplate, arguments);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write0(LogLevel level, LogCategory category, TextDecorator? rootDecorator, Exception? exception, string messageTemplate)
    {
        if (!TryAcquire(level, out LogRuntime? runtime, out LogProducer? producer, out int index, out long localSequence, out long timestamp))
        {
            return;
        }

        ref LogRecord record = ref producer.GetRecord(index);
        try
        {
            record.Initialize(level, category, messageTemplate, CaptureException(exception), timestamp, localSequence, 0, rootDecorator);
            producer.Publish(runtime);
        }
        catch (Exception captureException)
        {
            record.Release();
            EmergencyLog.Write("A log event could not be captured.", captureException);
        }
        finally
        {
            producer.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write1<T0>(LogLevel level, LogCategory category, TextDecorator? rootDecorator, Exception? exception, string messageTemplate, T0 argument0)
    {
        if (!TryAcquire(level, out LogRuntime? runtime, out LogProducer? producer, out int index, out long localSequence, out long timestamp))
        {
            return;
        }

        ref LogRecord record = ref producer.GetRecord(index);
        try
        {
            record.Initialize(level, category, messageTemplate, CaptureException(exception), timestamp, localSequence, 1, rootDecorator);
            record.SetArgument(0, LogArgument.Capture(argument0));
            producer.Publish(runtime);
        }
        catch (Exception captureException)
        {
            record.Release();
            EmergencyLog.Write("A log event could not be captured.", captureException);
        }
        finally
        {
            producer.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write2<T0, T1>(LogLevel level, LogCategory category, TextDecorator? rootDecorator, Exception? exception, string messageTemplate, T0 argument0, T1 argument1)
    {
        if (!TryAcquire(level, out LogRuntime? runtime, out LogProducer? producer, out int index, out long localSequence, out long timestamp))
        {
            return;
        }

        ref LogRecord record = ref producer.GetRecord(index);
        try
        {
            record.Initialize(level, category, messageTemplate, CaptureException(exception), timestamp, localSequence, 2, rootDecorator);
            record.SetArgument(0, LogArgument.Capture(argument0));
            record.SetArgument(1, LogArgument.Capture(argument1));
            producer.Publish(runtime);
        }
        catch (Exception captureException)
        {
            record.Release();
            EmergencyLog.Write("A log event could not be captured.", captureException);
        }
        finally
        {
            producer.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write3<T0, T1, T2>(LogLevel level, LogCategory category, TextDecorator? rootDecorator, Exception? exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2)
    {
        if (!TryAcquire(level, out LogRuntime? runtime, out LogProducer? producer, out int index, out long localSequence, out long timestamp))
        {
            return;
        }

        ref LogRecord record = ref producer.GetRecord(index);
        try
        {
            record.Initialize(level, category, messageTemplate, CaptureException(exception), timestamp, localSequence, 3, rootDecorator);
            record.SetArgument(0, LogArgument.Capture(argument0));
            record.SetArgument(1, LogArgument.Capture(argument1));
            record.SetArgument(2, LogArgument.Capture(argument2));
            producer.Publish(runtime);
        }
        catch (Exception captureException)
        {
            record.Release();
            EmergencyLog.Write("A log event could not be captured.", captureException);
        }
        finally
        {
            producer.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write4<T0, T1, T2, T3>(LogLevel level, LogCategory category, TextDecorator? rootDecorator, Exception? exception, string messageTemplate, T0 argument0, T1 argument1, T2 argument2, T3 argument3)
    {
        if (!TryAcquire(level, out LogRuntime? runtime, out LogProducer? producer, out int index, out long localSequence, out long timestamp))
        {
            return;
        }

        ref LogRecord record = ref producer.GetRecord(index);
        try
        {
            record.Initialize(level, category, messageTemplate, CaptureException(exception), timestamp, localSequence, 4, rootDecorator);
            record.SetArgument(0, LogArgument.Capture(argument0));
            record.SetArgument(1, LogArgument.Capture(argument1));
            record.SetArgument(2, LogArgument.Capture(argument2));
            record.SetArgument(3, LogArgument.Capture(argument3));
            producer.Publish(runtime);
        }
        catch (Exception captureException)
        {
            record.Release();
            EmergencyLog.Write("A log event could not be captured.", captureException);
        }
        finally
        {
            producer.EndWrite();
        }
    }

    private static void WriteMany(
        LogLevel level,
        LogCategory category,
        TextDecorator? rootDecorator,
        Exception? exception,
        string messageTemplate,
        object?[]? arguments)
    {
        int argumentCount = arguments?.Length ?? 0;
        if (!TryAcquire(
                level,
                out LogRuntime? runtime,
                out LogProducer? producer,
                out int index,
                out long localSequence,
                out long timestamp))
        {
            return;
        }

        ref LogRecord record = ref producer.GetRecord(index);
        try
        {
            record.Initialize(
                level,
                category,
                messageTemplate,
                CaptureException(exception),
                timestamp,
                localSequence,
                argumentCount,
                rootDecorator);
            for (int argumentIndex = 0; argumentIndex < argumentCount; ++argumentIndex)
            {
                record.SetArgument(argumentIndex, LogArgument.CaptureObjectValue(arguments![argumentIndex]));
            }

            producer.Publish(runtime);
        }
        catch (Exception captureException)
        {
            record.Release();
            EmergencyLog.Write("A log event could not be captured.", captureException);
        }
        finally
        {
            producer.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryAcquire(
        LogLevel level,
        out LogRuntime runtime,
        out LogProducer producer,
        out int index,
        out long localSequence,
        out long timestamp)
    {
        LogRuntime? currentRuntime = Volatile.Read(ref s_runtime);
        if (currentRuntime is null || !currentRuntime.IsEnabled(level))
        {
            runtime = null!;
            producer = null!;
            index = 0;
            localSequence = 0;
            timestamp = 0;
            return false;
        }

        timestamp = Stopwatch.GetTimestamp();
        LogProducer? currentProducer = s_threadProducer;
        if (currentProducer is null || currentProducer.RuntimeId != currentRuntime.Id)
        {
            currentProducer = currentRuntime.RegisterProducer();
            if (currentProducer is null)
            {
                runtime = null!;
                producer = null!;
                index = 0;
                localSequence = 0;
                return false;
            }

            s_threadProducer = currentProducer;
        }

        if (!currentProducer.TryBeginWrite(currentRuntime))
        {
            runtime = null!;
            producer = null!;
            index = 0;
            localSequence = 0;
            return false;
        }

        if (!currentProducer.TryReserve(currentRuntime, level, out index, out localSequence))
        {
            currentProducer.EndWrite();
            currentRuntime.EmitEmergency(level, "A high-priority log event could not be queued.");
            runtime = null!;
            producer = null!;
            return false;
        }

        runtime = currentRuntime;
        producer = currentProducer;
        return true;
    }

    private static string? CaptureException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            return exception.ToString();
        }
        catch (Exception captureException)
        {
            return $"Exception snapshot failed ({captureException.GetType().FullName}).";
        }
    }
}
