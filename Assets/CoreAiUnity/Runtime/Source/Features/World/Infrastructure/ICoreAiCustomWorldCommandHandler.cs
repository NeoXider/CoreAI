namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Host-provided handler for world-command actions not built into
    /// <see cref="CoreAiWorldCommandExecutor"/>. Register on the executor from game bootstrap code
    /// to extend the AI/Lua world pipeline without modifying CoreAI packages.
    /// </summary>
    public interface ICoreAiCustomWorldCommandHandler
    {
        /// <summary>True when this handler owns <paramref name="action"/> (case-insensitive).</summary>
        bool CanHandle(string action);

        /// <summary>Applies the envelope; return true when handled (success or handled failure).</summary>
        bool TryExecute(CoreAiWorldCommandEnvelope envelope);
    }
}
