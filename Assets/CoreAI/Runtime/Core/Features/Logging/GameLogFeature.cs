using System;

namespace CoreAI.Infrastructure.Logging
{
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
        AllBuiltIn = Core | Composition | MessagePipe | ExampleRoguelite | Llm,
        CustomA = 1 << 8,
        CustomB = 1 << 9,
        All = AllBuiltIn | CustomA | CustomB
    }
}
