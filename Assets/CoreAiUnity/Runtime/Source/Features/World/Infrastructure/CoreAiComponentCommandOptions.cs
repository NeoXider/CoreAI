using System;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Host-selected native allocation limits for curated component commands.</summary>
    public sealed class CoreAiComponentCommandOptions
    {
        public const int DefaultMaxLineRendererPositions = 4096;

        public int MaxLineRendererPositions { get; }

        public CoreAiComponentCommandOptions(int maxLineRendererPositions = DefaultMaxLineRendererPositions)
        {
            if (maxLineRendererPositions < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLineRendererPositions));
            }

            MaxLineRendererPositions = maxLineRendererPositions;
        }
    }
}
