using System;
using System.Collections.Generic;

namespace SB
{
    public static class TargetTags
    {
        public const string Core = nameof(Core);
        public const string Runtime = nameof(Runtime);
        public const string DevTime = nameof(DevTime);
        public const string Tool = nameof(Tool);
        public const string Test = nameof(Test);
        public const string Bench = nameof(Bench);
        public const string Package = nameof(Package);
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class TargetScript : Attribute
    {
        public TargetScript()
            : this(TargetTags.Runtime)
        {
        }

        public TargetScript(params string[] tags)
        {
            Tags = tags.Length == 0 ? [TargetTags.Runtime] : tags;
        }

        public IReadOnlyList<string> Tags { get; }
    }
}
