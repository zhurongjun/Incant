using System.Text.Json;

namespace Incant.Base.Trace;

/// <summary>Writes completed captures in the Google Trace Event JSON format.</summary>
public static class GoogleTraceJsonWriter
{
    /// <summary>Writes a complete Google Trace JSON document.</summary>
    /// <param name="writer">The caller-owned JSON writer.</param>
    /// <param name="capture">The capture to write.</param>
    /// <remarks>This method does not flush or dispose <paramref name="writer"/> or its underlying stream.</remarks>
    public static void Write(Utf8JsonWriter writer, TraceCapture capture)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(capture);

        writer.WriteStartObject();
        writer.WriteStartArray("traceEvents");

        WriteProcessMetadata(writer, capture);
        foreach (TraceThreadInfo thread in capture.Threads.Span)
        {
            WriteThreadMetadata(writer, capture.ProcessId, thread);
        }

        foreach (TraceEvent traceEvent in capture.Events.Span)
        {
            WriteEvent(writer, capture, traceEvent);
        }

        writer.WriteEndArray();
        writer.WriteString("displayTimeUnit", "ms");
        writer.WriteEndObject();
    }

    private static void WriteProcessMetadata(Utf8JsonWriter writer, TraceCapture capture)
    {
        writer.WriteStartObject();
        writer.WriteString("name", "process_name");
        writer.WriteString("ph", "M");
        writer.WriteNumber("pid", capture.ProcessId);
        writer.WriteNumber("tid", 0);
        writer.WriteStartObject("args");
        writer.WriteString("name", capture.ProcessName);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteThreadMetadata(Utf8JsonWriter writer, int processId, TraceThreadInfo thread)
    {
        writer.WriteStartObject();
        writer.WriteString("name", "thread_name");
        writer.WriteString("ph", "M");
        writer.WriteNumber("pid", processId);
        writer.WriteNumber("tid", thread.Id);
        writer.WriteStartObject("args");
        writer.WriteString("name", thread.Name);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteEvent(Utf8JsonWriter writer, TraceCapture capture, TraceEvent traceEvent)
    {
        writer.WriteStartObject();
        writer.WriteString("name", traceEvent.Name);
        writer.WriteString("cat", FormatCategory(traceEvent.Category));
        writer.WriteString("ph", GetPhase(traceEvent.Kind));
        writer.WriteNumber("ts", ToMicroseconds(traceEvent.TimestampTicks, capture.TimestampFrequency));
        writer.WriteNumber("pid", capture.ProcessId);
        writer.WriteNumber("tid", traceEvent.ThreadId);

        switch (traceEvent.Kind)
        {
            case TraceEventKind.Complete:
                writer.WriteNumber("dur", ToMicroseconds(traceEvent.DurationTicks, capture.TimestampFrequency));
                break;
            case TraceEventKind.Instant:
                writer.WriteString("s", GetInstantScope(traceEvent.InstantScope));
                break;
            case TraceEventKind.AsyncBegin:
            case TraceEventKind.AsyncEvent:
            case TraceEventKind.AsyncEnd:
            case TraceEventKind.FlowStart:
            case TraceEventKind.FlowStep:
            case TraceEventKind.FlowEnd:
                writer.WriteString("id", $"0x{traceEvent.Id:x}");
                break;
        }

        if (traceEvent.Arguments is JsonElement arguments)
        {
            writer.WritePropertyName("args");
            arguments.WriteTo(writer);
        }
        else if (traceEvent.Kind == TraceEventKind.Counter)
        {
            WriteCounterArguments(writer, traceEvent.CounterValue);
        }

        writer.WriteEndObject();
    }

    private static void WriteCounterArguments(Utf8JsonWriter writer, TraceCounterValue value)
    {
        writer.WriteStartObject("args");
        switch (value.Kind)
        {
            case TraceCounterValueKind.Signed:
                writer.WriteNumber("value", value.SignedValue);
                break;
            case TraceCounterValueKind.Unsigned:
                writer.WriteNumber("value", value.UnsignedValue);
                break;
            case TraceCounterValueKind.FloatingPoint:
                writer.WriteNumber("value", value.FloatingPointValue);
                break;
        }

        writer.WriteEndObject();
    }

    private static string GetPhase(TraceEventKind kind)
    {
        return kind switch
        {
            TraceEventKind.Complete => "X",
            TraceEventKind.Instant => "i",
            TraceEventKind.Counter => "C",
            TraceEventKind.AsyncBegin => "b",
            TraceEventKind.AsyncEvent => "n",
            TraceEventKind.AsyncEnd => "e",
            TraceEventKind.FlowStart => "s",
            TraceEventKind.FlowStep => "t",
            TraceEventKind.FlowEnd => "f",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string GetInstantScope(TraceInstantScope scope)
    {
        return scope switch
        {
            TraceInstantScope.Thread => "t",
            TraceInstantScope.Process => "p",
            TraceInstantScope.Global => "g",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
    }

    private static string FormatCategory(TraceCategory category)
    {
        return category.ToString().Replace(", ", ",", StringComparison.Ordinal);
    }

    private static double ToMicroseconds(long ticks, long frequency)
    {
        return (double)ticks * 1_000_000d / frequency;
    }
}
