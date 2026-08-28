namespace Incant.Base.Trace;

/// <summary>Identifies groups of trace events that can be enabled for a capture.</summary>
[Flags]
public enum TraceCategory : ulong
{
    /// <summary>No trace category.</summary>
    None = 0,

    /// <summary>General-purpose trace events.</summary>
    General = 1UL << 0,

    /// <summary>Build execution trace events.</summary>
    Build = 1UL << 1,

    /// <summary>Dependency discovery and resolution trace events.</summary>
    Dependency = 1UL << 2,

    /// <summary>Scheduling trace events.</summary>
    Scheduler = 1UL << 3,

    /// <summary>External process trace events.</summary>
    Process = 1UL << 4,

    /// <summary>Input and output trace events.</summary>
    IO = 1UL << 5,

    /// <summary>Cache trace events.</summary>
    Cache = 1UL << 6,

    /// <summary>All defined trace categories.</summary>
    All = General | Build | Dependency | Scheduler | Process | IO | Cache,
}
