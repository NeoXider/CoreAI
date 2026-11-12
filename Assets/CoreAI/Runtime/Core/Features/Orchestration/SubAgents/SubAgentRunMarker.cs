using System.Threading;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// Marks the execution flow of a sub-agent run so every sub-agent tool of the session (the Plan-style
    /// <c>delegate_task</c> and the Team-style tools) refuses a call that comes from inside one, whatever route the
    /// call took (a skill proxy, a host that registered a tool on the sub-agent's role).
    /// <para>
    /// WHY an instance <see cref="AsyncLocal{T}"/>: it flows from the run into the sub-agent turn it starts and into
    /// any tool that turn invokes, while the lead's own flow never sees it (a change made inside an async method
    /// does not flow back to its caller). Siblings run in their own flows and do not see each other's mark.
    /// </para>
    /// </summary>
    internal sealed class SubAgentRunMarker
    {
        private readonly AsyncLocal<int> _depth = new();

        /// <summary>True while the current execution flow is inside a sub-agent run.</summary>
        internal bool IsInside => _depth.Value > 0;

        /// <summary>Marks the rest of the calling async method's flow as a sub-agent run.</summary>
        internal void Enter()
        {
            _depth.Value = _depth.Value + 1;
        }
    }
}
