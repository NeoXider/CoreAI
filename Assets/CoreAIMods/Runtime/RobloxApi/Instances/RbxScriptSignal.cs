namespace CoreAI.Mods.Roblox.Instances
{
    /// <summary>
    /// Inert MVP1 hook point for Roblox signals (ChildAdded, Destroying, ...). The signal
    /// PROPERTIES exist so the surface shape is final, but dispatch, connections, and yielding
    /// belong to the MVP2 scheduler/signal system — every entry point is a loud stub until then.
    /// WHY: exposing the shape now lets the registry/tree code compile against the final surface
    /// without building the deferred-dispatch machinery out of order (roadmap §5.1.6).
    /// </summary>
    public sealed class RbxScriptSignal
    {
        private readonly string _signalName;

        public RbxScriptSignal(string signalName)
        {
            _signalName = signalName;
        }

        /// <summary>Signal name used in stub errors, e.g. "Instance.ChildAdded".</summary>
        public string SignalName => _signalName;

        // TODO: MVP2 — RbxScriptSignal dispatch
        public object Connect(object handler)
        {
            throw Stub("Connect");
        }

        // TODO: MVP2 — RbxScriptSignal dispatch
        public object Once(object handler)
        {
            throw Stub("Once");
        }

        // TODO: MVP2 — RbxScriptSignal dispatch
        public object Wait()
        {
            throw Stub("Wait");
        }

        private RbxError Stub(string member)
        {
            return RbxError.NotImplemented(_signalName + ":" + member, "MVP2",
                "signals land in MVP2 (scheduler); poll with FindFirstChild/GetAttribute until then");
        }
    }
}
