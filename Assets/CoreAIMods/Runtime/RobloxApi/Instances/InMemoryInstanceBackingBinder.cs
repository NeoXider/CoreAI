using System.Collections.Generic;

namespace CoreAI.RobloxApi.Instances
{
    /// <summary>
    /// In-memory fake of the backing-object seam: tracks which instances are currently
    /// materialized and records the event stream. Serves as the solo/headless default and the
    /// test double until the Unity adapter (InstanceGameObjectBinder) lands with the
    /// world-binding task.
    /// </summary>
    public sealed class InMemoryInstanceBackingBinder : IInstanceBackingBinder
    {
        private readonly HashSet<InstanceId> _materialized = new HashSet<InstanceId>();
        private readonly List<string> _events = new List<string>();

        /// <summary>Ids with a live (materialized) fake backing object.</summary>
        public IReadOnlyCollection<InstanceId> Materialized => _materialized;

        /// <summary>Chronological event log, entries like "enter:5", "leave:5", "destroy:5",
        /// "reparent:5", "rename:5".</summary>
        public IReadOnlyList<string> Events => _events;

        public bool IsMaterialized(InstanceId id) => _materialized.Contains(id);

        public void OnEnteredWorld(InstanceRecord record)
        {
            _materialized.Add(record.Id);
            _events.Add("enter:" + record.Id.Value);
        }

        public void OnLeftWorld(InstanceRecord record)
        {
            _materialized.Remove(record.Id);
            _events.Add("leave:" + record.Id.Value);
        }

        public void OnDestroyed(InstanceRecord record)
        {
            _materialized.Remove(record.Id);
            _events.Add("destroy:" + record.Id.Value);
        }

        public void OnReparented(InstanceRecord record)
        {
            _events.Add("reparent:" + record.Id.Value);
        }

        public void OnNameChanged(InstanceRecord record)
        {
            _events.Add("rename:" + record.Id.Value);
        }
    }
}
