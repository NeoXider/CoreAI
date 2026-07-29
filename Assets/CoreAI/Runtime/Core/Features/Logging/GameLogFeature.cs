using System;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Log categories CoreAI subsystems write under; used as a filter mask.
    /// </summary>
    [Flags]
    public enum GameLogFeature
    {
        None = 0,
        Core = 1 << 0,
        Composition = 1 << 1,
        MessagePipe = 1 << 2,
        ExampleRoguelite = 1 << 3,
        Llm = 1 << 4,
        Metrics = 1 << 5,

        /// <summary>Every category shipped with CoreAI (bits 0-5 = 63).</summary>
        AllBuiltIn = Core | Composition | MessagePipe | ExampleRoguelite | Llm | Metrics,

        CustomA = 1 << 8,
        CustomB = 1 << 9,

        /// <summary>Every category, built-in and project-defined (63 | 256 | 512 = 831).</summary>
        All = AllBuiltIn | CustomA | CustomB
    }
}
