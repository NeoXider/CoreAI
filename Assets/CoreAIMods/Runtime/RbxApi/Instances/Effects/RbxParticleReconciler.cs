using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>What draws a world's particle emitters: the Unity default pools <c>ParticleSystem</c>s; a headless world has none.</summary>
    public interface IRbxParticleBackend
    {
        /// <summary>Creates or updates the drawn emitter of <paramref name="emitterId"/> on <paramref name="hostPartId"/>.</summary>
        void Apply(InstanceId emitterId, InstanceId hostPartId, in RbxParticleRenderState state);

        /// <summary>Emits <paramref name="count"/> particles at once from a drawn emitter.</summary>
        void Emit(InstanceId emitterId, int count);

        /// <summary>Removes every live particle of a drawn emitter.</summary>
        void Clear(InstanceId emitterId);

        /// <summary>Removes the drawn emitter; unknown ids are ignored.</summary>
        void Release(InstanceId emitterId);

        /// <summary>True while the backend still draws <paramref name="emitterId"/>.</summary>
        bool Holds(InstanceId emitterId);
    }

    /// <summary>One emitter as its backend draws it; the part's size is the box particles start in.</summary>
    public readonly struct RbxParticleRenderState
    {
        public RbxParticleRenderState(RbxParticleEmitter emitter, RbxVector3 partSizeStuds)
        {
            Enabled = emitter.Enabled;
            Rate = emitter.Rate;
            Lifetime = emitter.Lifetime;
            Speed = emitter.Speed;
            Color = emitter.Color;
            Size = emitter.Size;
            Transparency = emitter.Transparency;
            Texture = emitter.Texture;
            SpreadAngle = emitter.SpreadAngle;
            Acceleration = emitter.Acceleration;
            EmissionDirection = emitter.EmissionDirection;
            PartSizeStuds = partSizeStuds;
        }

        public bool Enabled { get; }

        public float Rate { get; }

        public RbxNumberRange Lifetime { get; }

        public RbxNumberRange Speed { get; }

        public RbxColorSequence Color { get; }

        public RbxNumberSequence Size { get; }

        public RbxNumberSequence Transparency { get; }

        public string Texture { get; }

        public RbxVector2 SpreadAngle { get; }

        public RbxVector3 Acceleration { get; }

        public RbxParticleFace EmissionDirection { get; }

        public RbxVector3 PartSizeStuds { get; }
    }

    /// <summary>How many emitters one tier draws at once; past it the nearest to the camera are drawn.</summary>
    public sealed class RbxParticleBudget
    {
        /// <summary>Desktop: 64 emitters of at most 1,000 live particles each.</summary>
        public static readonly RbxParticleBudget Desktop = new("desktop", 64, 1000);

        /// <summary>WebGL: 16 emitters of at most 200 live particles each.</summary>
        public static readonly RbxParticleBudget WebGl = new("WebGL", 16, 200);

        public RbxParticleBudget(string tier, int maxEmitters, int maxParticlesPerEmitter)
        {
            Tier = string.IsNullOrWhiteSpace(tier) ? "custom" : tier;
            MaxEmitters = maxEmitters < 0 ? 0 : maxEmitters;
            MaxParticlesPerEmitter = maxParticlesPerEmitter < 1 ? 1 : maxParticlesPerEmitter;
        }

        public string Tier { get; }

        public int MaxEmitters { get; }

        public int MaxParticlesPerEmitter { get; }
    }

    /// <summary>
    /// Keeps a world's drawn emitters in step with its <see cref="RbxParticleEmitter"/> instances, once per frame: an
    /// emitter is drawn while it is the direct child of a <c>BasePart</c> inside the Workspace (ParticleEmitter.yaml: it
    /// emits from its parent part); over the budget only the nearest emitters to the camera are drawn. Pending
    /// <c>Emit</c> and <c>Clear</c> commands are handed over each frame; an emitter that is not drawn drops them. The
    /// backend is told about an emitter only when its state, its part or the part's size changed.
    /// </summary>
    public sealed class RbxParticleReconciler : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly IRbxParticleBackend _backend;
        private readonly Func<InstanceId, RbxVector3> _partPosition;
        private readonly Func<InstanceId, RbxVector3> _partSize;
        private readonly Action<string> _notice;
        private readonly Dictionary<InstanceId, Tracked> _emitters = new();
        private readonly List<Tracked> _candidates = new();
        private bool _overBudgetReported;
        private bool _disposed;

        private sealed class Tracked
        {
            public Tracked(RbxParticleEmitter emitter)
            {
                Emitter = emitter;
            }

            public readonly RbxParticleEmitter Emitter;
            public RbxInstance Host;
            public bool Drawn;
            public InstanceId DrawnHost;
            public int DrawnVersion = -1;
            public RbxVector3 DrawnSize;
            public float DistanceSquared;
            public bool WithinBudget;
        }

        /// <param name="partPosition">A part's position in studs; read only while over the budget.</param>
        /// <param name="partSize">A part's size in studs, the box particles start in.</param>
        /// <param name="notice">Receives the one-line budget diagnostic; null drops it.</param>
        public RbxParticleReconciler(InstanceRegistry registry, IRbxParticleBackend backend,
            Func<InstanceId, RbxVector3> partPosition, Func<InstanceId, RbxVector3> partSize, RbxParticleBudget budget,
            Action<string> notice = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _partPosition = partPosition ?? throw new ArgumentNullException(nameof(partPosition));
            _partSize = partSize ?? throw new ArgumentNullException(nameof(partSize));
            Budget = budget ?? RbxParticleBudget.Desktop;
            _notice = notice;
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                Track(live[index]);
            }
        }

        /// <summary>The budget in force; a host may replace it at any time.</summary>
        public RbxParticleBudget Budget { get; set; }

        /// <summary>Emitters in the world, drawn or not.</summary>
        public int TrackedEmitters => _emitters.Count;

        /// <summary>Emitters drawn after the last tick.</summary>
        public int DrawnEmitters { get; private set; }

        /// <summary>Emitters the budget left undrawn at the last tick.</summary>
        public int CulledByBudget { get; private set; }

        /// <summary>Brings every drawn emitter up to date; <paramref name="viewerStuds"/> decides the nearest under a budget.</summary>
        public void Tick(RbxVector3 viewerStuds)
        {
            if (_disposed)
            {
                return;
            }

            _candidates.Clear();
            foreach (KeyValuePair<InstanceId, Tracked> pair in _emitters)
            {
                Tracked tracked = pair.Value;
                tracked.Host = ResolveHost(tracked.Emitter);
                tracked.WithinBudget = false;
                if (tracked.Host != null)
                {
                    _candidates.Add(tracked);
                }
            }

            int budget = Budget.MaxEmitters;
            if (_candidates.Count > budget)
            {
                for (int index = 0; index < _candidates.Count; index++)
                {
                    Tracked candidate = _candidates[index];
                    RbxVector3 offset = _partPosition(candidate.Host.Id) - viewerStuds;
                    candidate.DistanceSquared = offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z;
                }

                _candidates.Sort(NearestFirst.Instance);
            }

            for (int index = 0; index < _candidates.Count && index < budget; index++)
            {
                _candidates[index].WithinBudget = true;
            }

            int drawn = 0;
            foreach (KeyValuePair<InstanceId, Tracked> pair in _emitters)
            {
                Tracked tracked = pair.Value;
                RbxParticleEmitter emitter = tracked.Emitter;
                emitter.TakeCommands(out int emit, out bool clear);
                if (tracked.Host == null || !tracked.WithinBudget)
                {
                    if (tracked.Drawn)
                    {
                        _backend.Release(pair.Key);
                        tracked.Drawn = false;
                    }

                    continue;
                }

                drawn++;
                RbxVector3 size = _partSize(tracked.Host.Id);
                if (!tracked.Drawn || tracked.DrawnHost != tracked.Host.Id || tracked.DrawnVersion != emitter.StateVersion
                    || tracked.DrawnSize != size || !_backend.Holds(pair.Key))
                {
                    RbxParticleRenderState state = new(emitter, size);
                    _backend.Apply(pair.Key, tracked.Host.Id, in state);
                    tracked.Drawn = true;
                    tracked.DrawnHost = tracked.Host.Id;
                    tracked.DrawnVersion = emitter.StateVersion;
                    tracked.DrawnSize = size;
                }

                if (clear)
                {
                    _backend.Clear(pair.Key);
                }

                if (emit > 0)
                {
                    _backend.Emit(pair.Key, emit);
                }
            }

            DrawnEmitters = drawn;
            CulledByBudget = _candidates.Count - drawn;
            ReportBudget();
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
            foreach (KeyValuePair<InstanceId, Tracked> pair in _emitters)
            {
                if (pair.Value.Drawn)
                {
                    _backend.Release(pair.Key);
                }
            }

            _emitters.Clear();
            _candidates.Clear();
        }

        private RbxInstance ResolveHost(RbxParticleEmitter emitter)
        {
            if (emitter.IsDestroyed)
            {
                return null;
            }

            RbxInstance parent = emitter.Parent;
            RbxInstance workspace = _registry.WorldRoot;
            if (parent == null || workspace == null || parent.IsDestroyed || !parent.IsA("BasePart")
                || !parent.IsDescendantOf(workspace))
            {
                return null;
            }

            return parent;
        }

        private void ReportBudget()
        {
            if (CulledByBudget == 0)
            {
                _overBudgetReported = false;
                return;
            }

            if (_overBudgetReported)
            {
                return;
            }

            _overBudgetReported = true;
            _notice?.Invoke("[CoreAI.RbxApi] particle budget: " + DrawnEmitters + " of " + _candidates.Count
                            + " emitters are drawn; the " + Budget.Tier + " tier draws the nearest "
                            + Budget.MaxEmitters + " to the camera (at most " + Budget.MaxParticlesPerEmitter
                            + " particles each) and skips the rest until the camera comes closer.");
        }

        private void OnRegistered(InstanceRecord record)
        {
            Track(record?.Instance);
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (record == null || !_emitters.TryGetValue(record.Id, out Tracked tracked))
            {
                return;
            }

            _emitters.Remove(record.Id);
            if (tracked.Drawn)
            {
                _backend.Release(record.Id);
            }
        }

        private void Track(RbxInstance instance)
        {
            if (instance is RbxParticleEmitter emitter && !_emitters.ContainsKey(emitter.Id))
            {
                _emitters.Add(emitter.Id, new Tracked(emitter));
            }
        }

        private sealed class NearestFirst : IComparer<Tracked>
        {
            public static readonly NearestFirst Instance = new();

            public int Compare(Tracked x, Tracked y)
            {
                int byDistance = x.DistanceSquared.CompareTo(y.DistanceSquared);
                return byDistance != 0 ? byDistance : x.Emitter.Id.Value.CompareTo(y.Emitter.Id.Value);
            }
        }
    }
}
