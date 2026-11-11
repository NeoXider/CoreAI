using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// What draws a world's lights: one light per lit <c>Light</c> instance, attached to the host part's
    /// backing object. The Unity default creates a Unity <c>Light</c> under the part's GameObject; a
    /// headless world has none; a host can supply its own.
    /// </summary>
    public interface IRbxLightBackend
    {
        /// <summary>Creates or updates the drawn light of <paramref name="lightId"/> on <paramref name="hostPartId"/>.</summary>
        void Apply(InstanceId lightId, InstanceId hostPartId, in RbxLightRenderState state);

        /// <summary>Removes the drawn light of <paramref name="lightId"/>; unknown ids are ignored.</summary>
        void Release(InstanceId lightId);

        /// <summary>True while the backend still draws <paramref name="lightId"/> (its object was not destroyed under it).</summary>
        bool Holds(InstanceId lightId);
    }

    /// <summary>One light as its backend draws it.</summary>
    public readonly struct RbxLightRenderState : IEquatable<RbxLightRenderState>
    {
        public RbxLightRenderState(float brightness, RbxColor3 color, float rangeStuds, bool shadows,
            bool active)
        {
            Brightness = brightness;
            Color = color;
            RangeStuds = rangeStuds;
            Shadows = shadows;
            Active = active;
        }

        public float Brightness { get; }

        public RbxColor3 Color { get; }

        /// <summary>The reach in studs; the backend converts it to its own units.</summary>
        public float RangeStuds { get; }

        public bool Shadows { get; }

        /// <summary>False while the light is disabled by its script or left out by the light budget.</summary>
        public bool Active { get; }

        public bool Equals(RbxLightRenderState other)
        {
            return Brightness.Equals(other.Brightness) && Color == other.Color
                                                       && RangeStuds.Equals(other.RangeStuds)
                                                       && Shadows == other.Shadows
                                                       && Active == other.Active;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxLightRenderState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Brightness.GetHashCode() ^ (RangeStuds.GetHashCode() * 31) ^ (Active ? 1 : 0);
        }
    }

    /// <summary>
    /// How many lights one tier keeps on at once. Over the budget the nearest lights to the viewer stay
    /// on and the rest are switched off until the viewer comes closer, and the reconciler says so once.
    /// </summary>
    public sealed class RbxLightBudget
    {
        /// <summary>
        /// Desktop, Forward+ (the PC renderer): lights are clustered per tile, so 64 active lights stay far
        /// under URP's 256 visible additional lights and the per-object limit does not apply.
        /// </summary>
        public static readonly RbxLightBudget Desktop = new("desktop (Forward+)", 64);

        /// <summary>
        /// WebGL (the Mobile renderer, plain Forward): at most 4 additional lights shade one object and
        /// 32 are visible at once, so the 8 nearest stay on and every other light is off.
        /// </summary>
        public static readonly RbxLightBudget WebGl = new("WebGL (Forward)", 8);

        public RbxLightBudget(string tier, int maxActiveLights)
        {
            Tier = string.IsNullOrWhiteSpace(tier) ? "custom" : tier;
            MaxActiveLights = maxActiveLights < 0 ? 0 : maxActiveLights;
        }

        /// <summary>The tier name the diagnostic quotes.</summary>
        public string Tier { get; }

        /// <summary>The most lights on at once.</summary>
        public int MaxActiveLights { get; }
    }

    /// <summary>
    /// Keeps a world's drawn lights in step with its <see cref="RbxLight"/> instances, once per frame:
    /// a light shines while it is enabled, has brightness and range, and is the direct child of a
    /// <c>BasePart</c> inside the Workspace; over the budget only the nearest lights to the viewer shine.
    /// The backend is called only for a light whose drawn state changed, so a still world costs one walk
    /// over its lights and no allocation.
    /// </summary>
    public sealed class RbxLightReconciler : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly IRbxLightBackend _backend;
        private readonly Func<InstanceId, RbxVector3> _partPosition;
        private readonly Action<string> _notice;
        private readonly Dictionary<InstanceId, Tracked> _lights = new();
        private readonly List<Tracked> _candidates = new();
        private readonly List<InstanceId> _released = new();
        private bool _overBudgetReported;
        private bool _disposed;

        /// <param name="partPosition">The host part's position in studs; read only while over the budget.</param>
        /// <param name="notice">Receives the one-line budget diagnostic; null drops it.</param>
        public RbxLightReconciler(InstanceRegistry registry, IRbxLightBackend backend,
            Func<InstanceId, RbxVector3> partPosition, RbxLightBudget budget, Action<string> notice = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _partPosition = partPosition ?? throw new ArgumentNullException(nameof(partPosition));
            Budget = budget ?? RbxLightBudget.Desktop;
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
        public RbxLightBudget Budget { get; set; }

        /// <summary>Lights in the world, lit or not, after the last tick.</summary>
        public int TrackedLights => _lights.Count;

        /// <summary>Lights that met every condition to shine at the last tick.</summary>
        public int LitCandidates { get; private set; }

        /// <summary>Lights shining after the last tick.</summary>
        public int ActiveLights { get; private set; }

        /// <summary>Lights the budget switched off at the last tick.</summary>
        public int CulledByBudget { get; private set; }

        /// <summary>Brings every drawn light up to date; <paramref name="viewerStuds"/> decides the nearest under a budget.</summary>
        public void Tick(RbxVector3 viewerStuds)
        {
            if (_disposed)
            {
                return;
            }

            _candidates.Clear();
            foreach (KeyValuePair<InstanceId, Tracked> pair in _lights)
            {
                Tracked tracked = pair.Value;
                tracked.Host = ResolveHost(tracked.Light);
                tracked.WantsLight = tracked.Host != null && tracked.Light.Enabled
                                                          && tracked.Light.Brightness > 0f
                                                          && tracked.Light.RangeStuds > 0f;
                if (tracked.WantsLight)
                {
                    _candidates.Add(tracked);
                }
            }

            int budget = Budget.MaxActiveLights;
            LitCandidates = _candidates.Count;
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

            for (int index = 0; index < _candidates.Count; index++)
            {
                _candidates[index].WithinBudget = index < budget;
            }

            int active = 0;
            foreach (KeyValuePair<InstanceId, Tracked> pair in _lights)
            {
                Tracked tracked = pair.Value;
                if (tracked.Host == null)
                {
                    if (tracked.Applied)
                    {
                        _backend.Release(pair.Key);
                        tracked.Applied = false;
                    }

                    continue;
                }

                bool shines = tracked.WantsLight && tracked.WithinBudget;
                tracked.WithinBudget = false;
                if (shines)
                {
                    active++;
                }

                RbxLight light = tracked.Light;
                RbxLightRenderState state = new(light.Brightness, light.Color, light.RangeStuds,
                    light.Shadows, shines);
                if (tracked.Applied && tracked.AppliedHost == tracked.Host.Id
                                    && tracked.AppliedState.Equals(state) && _backend.Holds(pair.Key))
                {
                    continue;
                }

                _backend.Apply(pair.Key, tracked.Host.Id, in state);
                tracked.Applied = true;
                tracked.AppliedHost = tracked.Host.Id;
                tracked.AppliedState = state;
            }

            ActiveLights = active;
            CulledByBudget = LitCandidates - active;
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
            foreach (KeyValuePair<InstanceId, Tracked> pair in _lights)
            {
                if (pair.Value.Applied)
                {
                    _released.Add(pair.Key);
                }
            }

            for (int index = 0; index < _released.Count; index++)
            {
                _backend.Release(_released[index]);
            }

            _released.Clear();
            _lights.Clear();
        }

        private RbxInstance ResolveHost(RbxLight light)
        {
            if (light.IsDestroyed)
            {
                return null;
            }

            RbxInstance parent = light.Parent;
            RbxInstance workspace = _registry.WorldRoot;
            if (parent == null || workspace == null || !parent.IsA("BasePart") || parent.IsDestroyed
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
            _notice?.Invoke("[CoreAI.RbxApi] light budget: " + ActiveLights + " of " + LitCandidates
                            + " lights are on; the " + Budget.Tier + " tier keeps the nearest "
                            + Budget.MaxActiveLights + " lights enabled and switches the rest off until "
                            + "the camera comes closer.");
        }

        private void OnRegistered(InstanceRecord record)
        {
            Track(record?.Instance);
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (record == null || !_lights.TryGetValue(record.Id, out Tracked tracked))
            {
                return;
            }

            _lights.Remove(record.Id);
            if (tracked.Applied)
            {
                _backend.Release(record.Id);
            }
        }

        private void Track(RbxInstance instance)
        {
            if (instance is RbxLight light && !_lights.ContainsKey(light.Id))
            {
                _lights.Add(light.Id, new Tracked(light));
            }
        }

        private sealed class Tracked
        {
            public Tracked(RbxLight light)
            {
                Light = light;
            }

            public RbxLight Light { get; }

            public RbxInstance Host;
            public bool WantsLight;
            public bool WithinBudget;
            public float DistanceSquared;
            public bool Applied;
            public InstanceId AppliedHost;
            public RbxLightRenderState AppliedState;
        }

        private sealed class NearestFirst : IComparer<Tracked>
        {
            public static readonly NearestFirst Instance = new();

            public int Compare(Tracked x, Tracked y)
            {
                int byDistance = x.DistanceSquared.CompareTo(y.DistanceSquared);
                return byDistance != 0 ? byDistance : x.Light.Id.Value.CompareTo(y.Light.Id.Value);
            }
        }
    }
}
