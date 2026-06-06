namespace CoreAI.Authority
{
    /// <summary>
    /// Evaluates whether AI work may run on the current host or peer.
    /// </summary>
    public interface IAuthorityHost
    {
        /// <summary>Returns whether AI tasks may run for the requested network execution policy.</summary>
        bool CanRunAiTasks { get; }
    }

    /// <summary>Authority host implementation for local solo-player execution.</summary>
    public sealed class SoloAuthorityHost : IAuthorityHost
    {
        /// <inheritdoc />
        public bool CanRunAiTasks => true;
    }
}