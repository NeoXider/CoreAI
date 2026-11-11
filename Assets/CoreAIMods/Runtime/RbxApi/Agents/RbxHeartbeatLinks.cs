using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>
    /// Runs one callback on every Heartbeat of the world schedulers it is attached to, the world's own frame, so a
    /// paused world pauses it too. Attach each world as it is built: only the published world's scheduler advances,
    /// and a scheduler is held weakly, so a replaced world is not kept alive by the attachment.
    /// </summary>
    public sealed class RbxHeartbeatLinks : IDisposable
    {
        private readonly Action _onHeartbeat;
        private readonly List<WeakReference<Link>> _links = new();
        private bool _disposed;

        /// <summary>Creates the links; nothing runs until a scheduler is attached.</summary>
        public RbxHeartbeatLinks(Action onHeartbeat)
        {
            _onHeartbeat = onHeartbeat ?? throw new ArgumentNullException(nameof(onHeartbeat));
        }

        /// <summary>Runs the callback on every Heartbeat of <paramref name="scheduler"/>; attaching it twice runs it once.</summary>
        public IDisposable Attach(ModScheduler scheduler)
        {
            if (scheduler == null)
            {
                throw new ArgumentNullException(nameof(scheduler));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RbxHeartbeatLinks));
            }

            _links.RemoveAll(reference => !reference.TryGetTarget(out Link alive) || !alive.IsAttached);
            foreach (WeakReference<Link> reference in _links)
            {
                if (reference.TryGetTarget(out Link existing) && existing.Is(scheduler))
                {
                    return existing;
                }
            }

            Link link = new(this, scheduler);
            _links.Add(new WeakReference<Link>(link));
            return link;
        }

        /// <summary>Leaves every scheduler.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (WeakReference<Link> reference in _links.ToArray())
            {
                if (reference.TryGetTarget(out Link link))
                {
                    link.Dispose();
                }
            }

            _links.Clear();
        }

        private sealed class Link : IDisposable
        {
            private readonly WeakReference<ModScheduler> _scheduler;
            private RbxHeartbeatLinks _owner;

            public Link(RbxHeartbeatLinks owner, ModScheduler scheduler)
            {
                _owner = owner;
                _scheduler = new WeakReference<ModScheduler>(scheduler);
                scheduler.PhaseReached += OnPhaseReached;
            }

            public bool IsAttached => _owner != null && _scheduler.TryGetTarget(out ModScheduler _);

            public bool Is(ModScheduler scheduler)
            {
                return _scheduler.TryGetTarget(out ModScheduler target) && ReferenceEquals(target, scheduler);
            }

            public void Dispose()
            {
                _owner = null;
                if (_scheduler.TryGetTarget(out ModScheduler scheduler))
                {
                    scheduler.PhaseReached -= OnPhaseReached;
                }
            }

            private void OnPhaseReached(SchedulerPhase phase, double deltaSeconds)
            {
                RbxHeartbeatLinks owner = _owner;
                if (phase == SchedulerPhase.Heartbeat && owner != null && !owner._disposed)
                {
                    owner._onHeartbeat();
                }
            }
        }
    }
}
