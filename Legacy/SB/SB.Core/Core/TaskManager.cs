using System.Collections.Concurrent;
using System.Threading.Tasks.Schedulers;

namespace SB.Core
{
    public struct BuildTaskKey
    {
        public string TargetName { get; init; }
        public string File { get; init; }
        public string TaskName { get; init; }
    }

    public class TaskFatalError : Exception
    {
        public TaskFatalError(string tidy, string what)
            : base(what)
        {
            Tidy = tidy;
        }
        public TaskFatalError(string what)
            : base(what)
        {
            Tidy = what;
        }
        public string Tidy { get; private set; }
    }

    public static class TaskManager
    {
        public static readonly QueuedTaskScheduler BuildSchedulerPool = new(Environment.ProcessorCount, "BuildWorker", false, ThreadPriority.AboveNormal, ApartmentState.Unknown, 0);
        public static readonly QueuedTaskScheduler IoSchedulerPool = new(1, "I/O Worker", false, ThreadPriority.BelowNormal, ApartmentState.Unknown, 0);
    }
}
