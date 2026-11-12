using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The clock of every <see cref="RbxSound"/> in one world, run at each Heartbeat of the world's scheduler: a
    /// sound inside the DataModel loads from the <see cref="RbxSoundService.Library"/> (one frame after its id is
    /// set, as Roblox loads asynchronously), and a playing loaded sound advances by the frame's scaled time, wraps
    /// or ends. The local copies of <c>PlayLocalSound</c> and <c>PlayOnRemove</c> advance here too. Nothing here
    /// makes a sound audible; that is <see cref="RbxSoundReconciler"/>'s job, so a world without audio output keeps
    /// the same timeline.
    /// </summary>
    /// <remarks>
    /// A sound outside the DataModel (parent nil) neither loads nor advances: <c>Playing</c> may be true, yet it is
    /// silent and never ends, as an unparented Roblox sound is not heard. A destroyed sound is dropped and fires
    /// nothing (Sound.yaml: destroying a playing sound does not fire <c>Stopped</c>); with <c>PlayOnRemove</c> a
    /// loaded sound that is destroyed or leaves the DataModel starts a local copy at its last part. A still world
    /// allocates nothing per frame.
    /// </remarks>
    public sealed class RbxSoundPlayback : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly RbxDataModel _game;
        private readonly ModScheduler _scheduler;
        private readonly Action<string> _log;
        private readonly Dictionary<InstanceId, Tracked> _byId = new();
        private readonly List<Tracked> _sounds = new();
        private RbxSoundService _service;
        private bool _disposed;

        private sealed class Tracked
        {
            public Tracked(RbxSound sound)
            {
                Sound = sound;
            }

            public readonly RbxSound Sound;
            public string AttemptedId;
            public int AttemptedVersion = -1;
            public RbxSoundLibrary AttemptedLibrary;
            public bool WasInGame;
            public InstanceId LastHostPart;
        }

        /// <param name="scheduler">Advances the sounds at each Heartbeat; null leaves <see cref="Step"/> to the host.</param>
        /// <param name="log">Receives the one line per id that loads nothing; null drops it.</param>
        public RbxSoundPlayback(InstanceRegistry registry, RbxDataModel game, ModScheduler scheduler,
            Action<string> log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _scheduler = scheduler;
            _log = log;
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                Track(live[index]);
            }

            if (_scheduler != null)
            {
                _scheduler.PhaseReached += OnPhaseReached;
            }
        }

        /// <summary>Sounds in the world, playing or not.</summary>
        public int TrackedSounds => _sounds.Count;

        /// <summary>The world's SoundService, or null when the tree has none.</summary>
        public RbxSoundService Service => ResolveService();

        /// <summary>Loads, advances, wraps and ends every sound by <paramref name="deltaSeconds"/> scaled seconds.</summary>
        public void Step(double deltaSeconds)
        {
            if (_disposed)
            {
                return;
            }

            double dt = double.IsNaN(deltaSeconds) || deltaSeconds < 0d || double.IsInfinity(deltaSeconds)
                ? 0d
                : deltaSeconds;
            RbxSoundService service = ResolveService();
            RbxSoundLibrary library = service?.Library;
            for (int index = 0; index < _sounds.Count; index++)
            {
                Tracked tracked = _sounds[index];
                RbxSound sound = tracked.Sound;
                if (sound.IsDestroyed)
                {
                    continue;
                }

                bool inGame = sound.IsDescendantOf(_game);
                if (!inGame)
                {
                    if (tracked.WasInGame)
                    {
                        tracked.WasInGame = false;
                        StartRemovalCopy(service, tracked);
                    }

                    continue;
                }

                tracked.WasInGame = true;
                tracked.LastHostPart = RbxSoundService.ResolveHostPart(sound);
                if (!sound.IsLoaded)
                {
                    TryLoad(service, library, tracked);
                }

                sound.Advance(dt);
            }

            service?.AdvanceLocalVoices(dt);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Registered -= OnRegistered;
            _registry.Unregistered -= OnUnregistered;
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnPhaseReached;
            }

            _sounds.Clear();
            _byId.Clear();
        }

        private void TryLoad(RbxSoundService service, RbxSoundLibrary library, Tracked tracked)
        {
            RbxSound sound = tracked.Sound;
            string id = sound.SoundId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            int version = library?.Version ?? -1;
            if (string.Equals(tracked.AttemptedId, id, StringComparison.Ordinal)
                && tracked.AttemptedVersion == version && ReferenceEquals(tracked.AttemptedLibrary, library))
            {
                return;
            }

            tracked.AttemptedId = id;
            tracked.AttemptedVersion = version;
            tracked.AttemptedLibrary = library;
            if (library != null && library.TryResolve(id, out RbxSoundAsset asset))
            {
                sound.ApplyLoaded(in asset);
                return;
            }

            if (service != null && service.TryNoteUnknownId(id))
            {
                _log?.Invoke(RbxSoundService.DescribeUnknownId(id, library));
            }
        }

        private void StartRemovalCopy(RbxSoundService service, Tracked tracked)
        {
            RbxSound sound = tracked.Sound;
            if (service == null || service.IsDestroyed || !sound.PlayOnRemove || !sound.IsLoaded)
            {
                return;
            }

            service.StartCopy(sound, tracked.LastHostPart, sound.Playing ? sound.TimePosition : 0d);
        }

        private RbxSoundService ResolveService()
        {
            if (_service != null && !_service.IsDestroyed && ReferenceEquals(_service.Parent, _game))
            {
                return _service;
            }

            _service = _game.IsDestroyed ? null : _game.FindFirstChildOfClass("SoundService") as RbxSoundService;
            return _service;
        }

        private void OnPhaseReached(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase == SchedulerPhase.Heartbeat)
            {
                Step(deltaSeconds);
            }
        }

        private void OnRegistered(InstanceRecord record)
        {
            Track(record?.Instance);
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (record == null || !_byId.TryGetValue(record.Id, out Tracked tracked))
            {
                return;
            }

            _byId.Remove(record.Id);
            _sounds.Remove(tracked);
            if (tracked.WasInGame)
            {
                StartRemovalCopy(ResolveService(), tracked);
            }
        }

        private void Track(RbxInstance instance)
        {
            if (instance is RbxSound sound && !_byId.ContainsKey(sound.Id))
            {
                Tracked tracked = new(sound);
                _byId.Add(sound.Id, tracked);
                _sounds.Add(tracked);
            }
        }
    }
}
