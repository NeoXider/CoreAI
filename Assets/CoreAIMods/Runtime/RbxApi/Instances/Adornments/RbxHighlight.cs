using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Adornments
{
    /// <summary>Roblox <c>Enum.HighlightDepthMode</c> (HighlightDepthMode.yaml).</summary>
    public enum RbxHighlightDepthMode
    {
        /// <summary>Drawn over everything between the camera and the object (the default).</summary>
        AlwaysOnTop = 0,

        /// <summary>Hidden where something stands between the camera and the object.</summary>
        Occluded = 1
    }

    /// <summary>
    /// Mirror <c>Highlight</c>: an outline around the silhouette of its object and a tinted fill over it. The object is
    /// <see cref="Adornee"/>, or the Highlight's parent while <see cref="Adornee"/> is nil, as in Roblox; for a model
    /// (or any instance) every part below it is drawn as one shape. Every real change advances the revision and fires
    /// <c>Changed</c> and the property signal.
    /// </summary>
    public sealed class RbxHighlight : RbxInstance
    {
        /// <summary>Highlights drawn at once; the mirror's limit, the extra ones are silently ignored.</summary>
        public const int MaxDrawn = 255;

        /// <summary>The mirror's default fill colour, RGB (255, 0, 0).</summary>
        public static readonly RbxColor3 DefaultFillColor = new(1f, 0f, 0f);

        /// <summary>The mirror's default outline colour, RGB (255, 255, 255).</summary>
        public static readonly RbxColor3 DefaultOutlineColor = new(1f, 1f, 1f);

        private RbxInstance _adornee;
        private RbxHighlightDepthMode _depthMode = RbxHighlightDepthMode.AlwaysOnTop;
        private bool _enabled = true;
        private RbxColor3 _fillColor = DefaultFillColor;
        private float _fillTransparency = 0.5f;
        private RbxColor3 _outlineColor = DefaultOutlineColor;
        private float _outlineTransparency;

        internal RbxHighlight(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>Highlight.Adornee</c>: the object to highlight; nil highlights the parent.</summary>
        public RbxInstance Adornee
        {
            get => _adornee != null && !_adornee.IsDestroyed ? _adornee : null;
            set => Set(ref _adornee, value, nameof(Adornee));
        }

        /// <summary>The object drawn now: <see cref="Adornee"/>, else the parent.</summary>
        public RbxInstance EffectiveAdornee => Adornee ?? Parent;

        /// <summary>Mirror <c>Highlight.DepthMode</c> (default AlwaysOnTop).</summary>
        public RbxHighlightDepthMode DepthMode
        {
            get => _depthMode;
            set => Set(ref _depthMode, value, nameof(DepthMode));
        }

        /// <summary>Mirror <c>Highlight.Enabled</c>.</summary>
        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value, nameof(Enabled));
        }

        /// <summary>Mirror <c>Highlight.FillColor</c>.</summary>
        public RbxColor3 FillColor
        {
            get => _fillColor;
            set => Set(ref _fillColor, value, nameof(FillColor));
        }

        /// <summary>Mirror <c>Highlight.FillTransparency</c> (default 0.5).</summary>
        public float FillTransparency
        {
            get => _fillTransparency;
            set => Set(ref _fillTransparency, RequireFinite(value, nameof(FillTransparency)), nameof(FillTransparency));
        }

        /// <summary>Mirror <c>Highlight.OutlineColor</c>.</summary>
        public RbxColor3 OutlineColor
        {
            get => _outlineColor;
            set => Set(ref _outlineColor, value, nameof(OutlineColor));
        }

        /// <summary>Mirror <c>Highlight.OutlineTransparency</c> (default 0).</summary>
        public float OutlineTransparency
        {
            get => _outlineTransparency;
            set => Set(ref _outlineTransparency, RequireFinite(value, nameof(OutlineTransparency)),
                nameof(OutlineTransparency));
        }

        /// <summary>
        /// Puts a saved <c>highlight</c> block back: the values the setters take, the property signals of what changed,
        /// and no revision advance (a restore reproduces saved state, as for <see cref="RbxSelectionBox"/>).
        /// </summary>
        public void RestoreDurableState(RbxInstance adornee, RbxHighlightDepthMode depthMode, bool enabled,
            RbxColor3 fillColor, float fillTransparency, RbxColor3 outlineColor, float outlineTransparency)
        {
            Restore(ref _adornee, adornee, nameof(Adornee));
            Restore(ref _depthMode, depthMode, nameof(DepthMode));
            Restore(ref _enabled, enabled, nameof(Enabled));
            Restore(ref _fillColor, fillColor, nameof(FillColor));
            Restore(ref _fillTransparency, RequireFinite(fillTransparency, nameof(FillTransparency)),
                nameof(FillTransparency));
            Restore(ref _outlineColor, outlineColor, nameof(OutlineColor));
            Restore(ref _outlineTransparency, RequireFinite(outlineTransparency, nameof(OutlineTransparency)),
                nameof(OutlineTransparency));
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxHighlight target = (RbxHighlight)copy;
            target._adornee = _adornee;
            target._depthMode = _depthMode;
            target._enabled = _enabled;
            target._fillColor = _fillColor;
            target._fillTransparency = _fillTransparency;
            target._outlineColor = _outlineColor;
            target._outlineTransparency = _outlineTransparency;
        }

        private void Set<T>(ref T field, T value, string member)
        {
            ThrowIfDestroyed(member);
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            Registry?.AdvanceRevision(Id, member);
            NotifyPropertyChanged(member);
        }

        private void Restore<T>(ref T field, T value, string member)
        {
            ThrowIfDestroyed(member);
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            NotifyPropertyChanged(member);
        }

        private float RequireFinite(float value, string member)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw RbxError.BadArgument(ClassName + "." + member + " must be a finite number",
                    "assign a number such as 0.5");
            }

            return value;
        }
    }

    /// <summary>One highlight as its backend draws it: the parts it covers and how.</summary>
    public readonly struct RbxHighlightRenderState : IEquatable<RbxHighlightRenderState>
    {
        private readonly InstanceId[] _parts;

        public RbxHighlightRenderState(InstanceId[] parts, RbxHighlightDepthMode depthMode, RbxColor3 fillColor,
            float fillTransparency, RbxColor3 outlineColor, float outlineTransparency)
        {
            _parts = parts ?? Array.Empty<InstanceId>();
            DepthMode = depthMode;
            FillColor = fillColor;
            FillTransparency = fillTransparency;
            OutlineColor = outlineColor;
            OutlineTransparency = outlineTransparency;
        }

        /// <summary>The parts drawn as one silhouette, in tree order.</summary>
        public IReadOnlyList<InstanceId> Parts => _parts ?? Array.Empty<InstanceId>();

        public RbxHighlightDepthMode DepthMode { get; }

        public RbxColor3 FillColor { get; }

        public float FillTransparency { get; }

        public RbxColor3 OutlineColor { get; }

        public float OutlineTransparency { get; }

        public bool Equals(RbxHighlightRenderState other)
        {
            if (DepthMode != other.DepthMode || FillColor != other.FillColor
                                             || !FillTransparency.Equals(other.FillTransparency)
                                             || OutlineColor != other.OutlineColor
                                             || !OutlineTransparency.Equals(other.OutlineTransparency))
            {
                return false;
            }

            IReadOnlyList<InstanceId> mine = Parts;
            IReadOnlyList<InstanceId> theirs = other.Parts;
            if (mine.Count != theirs.Count)
            {
                return false;
            }

            for (int index = 0; index < mine.Count; index++)
            {
                if (mine[index].Value != theirs[index].Value)
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxHighlightRenderState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Parts.Count ^ ((int)DepthMode * 397) ^ FillTransparency.GetHashCode();
        }
    }

    /// <summary>
    /// What draws a world's highlights: the Unity default draws each part's own mesh with a fill and a silhouette
    /// outline, or a wireframe box around each part where the outline shader is not supported.
    /// </summary>
    public interface IRbxHighlightBackend
    {
        /// <summary>Creates or updates the drawn highlight <paramref name="highlightId"/>.</summary>
        void Apply(InstanceId highlightId, in RbxHighlightRenderState state);

        /// <summary>Stops drawing <paramref name="highlightId"/>; unknown ids are ignored.</summary>
        void Release(InstanceId highlightId);
    }

    /// <summary>
    /// Keeps a world's drawn highlights in step with its <see cref="RbxHighlight"/> instances, once per frame: a
    /// highlight is drawn while it is enabled, sits in the Workspace or a PlayerGui, holds one of the first
    /// <see cref="RbxHighlight.MaxDrawn"/> slots (every live Highlight takes one, in creation order, as in Roblox) and its
    /// object is in the Workspace with at least one part. The backend is called only for a highlight whose drawn state
    /// changed, and the part lists are gathered again only after the tree changed.
    /// </summary>
    public sealed class RbxHighlightReconciler : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly IRbxHighlightBackend _backend;
        private readonly List<Tracked> _highlights = new();
        private readonly List<InstanceId> _scratch = new();
        private bool _treeChanged = true;
        private bool _disposed;

        public RbxHighlightReconciler(InstanceRegistry registry, IRbxHighlightBackend backend)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
            _registry.RevisionAdvanced += OnRevisionAdvanced;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxHighlight highlight)
                {
                    _highlights.Add(new Tracked(highlight));
                }
            }
        }

        /// <summary>Highlights drawn after the last <see cref="Tick"/>.</summary>
        public int DrawnCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < _highlights.Count; index++)
                {
                    if (_highlights[index].Drawn)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Brings every drawn highlight in step with its instance and object.</summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            bool regather = _treeChanged;
            _treeChanged = false;
            for (int index = 0; index < _highlights.Count; index++)
            {
                Tracked tracked = _highlights[index];
                if (regather || tracked.Parts == null)
                {
                    tracked.Parts = GatherParts(tracked.Highlight);
                }

                bool shown = index < RbxHighlight.MaxDrawn && IsShown(tracked.Highlight) && tracked.Parts.Length > 0;
                if (shown)
                {
                    RbxHighlight highlight = tracked.Highlight;
                    RbxHighlightRenderState state = new(tracked.Parts, highlight.DepthMode, highlight.FillColor,
                        highlight.FillTransparency, highlight.OutlineColor, highlight.OutlineTransparency);
                    if (!tracked.Drawn || !tracked.State.Equals(state))
                    {
                        _backend.Apply(highlight.Id, in state);
                        tracked.State = state;
                        tracked.Drawn = true;
                    }
                }
                else if (tracked.Drawn)
                {
                    _backend.Release(tracked.Highlight.Id);
                    tracked.Drawn = false;
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Registered -= OnRegistered;
            _registry.Unregistered -= OnUnregistered;
            _registry.RevisionAdvanced -= OnRevisionAdvanced;
            for (int index = 0; index < _highlights.Count; index++)
            {
                if (_highlights[index].Drawn)
                {
                    _backend.Release(_highlights[index].Highlight.Id);
                }
            }

            _highlights.Clear();
        }

        private bool IsShown(RbxHighlight highlight)
        {
            if (highlight.IsDestroyed || !highlight.Enabled)
            {
                return false;
            }

            RbxInstance workspace = _registry.WorldRoot;
            RbxInstance target = highlight.EffectiveAdornee;
            if (workspace == null || target == null || !(ReferenceEquals(target, workspace) || target.IsDescendantOf(workspace)))
            {
                return false;
            }

            if (highlight.IsDescendantOf(workspace))
            {
                return true;
            }

            for (RbxInstance node = highlight.Parent; node != null; node = node.Parent)
            {
                if (string.Equals(node.ClassName, "PlayerGui", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private InstanceId[] GatherParts(RbxHighlight highlight)
        {
            RbxInstance target = highlight.IsDestroyed ? null : highlight.EffectiveAdornee;
            if (target == null || target.IsDestroyed)
            {
                return Array.Empty<InstanceId>();
            }

            _scratch.Clear();
            if (IsDrawnPart(target))
            {
                _scratch.Add(target.Id);
            }

            IReadOnlyList<RbxInstance> descendants = target.GetDescendants();
            for (int index = 0; index < descendants.Count; index++)
            {
                if (IsDrawnPart(descendants[index]))
                {
                    _scratch.Add(descendants[index].Id);
                }
            }

            return _scratch.Count == 0 ? Array.Empty<InstanceId>() : _scratch.ToArray();
        }

        private static bool IsDrawnPart(RbxInstance instance)
        {
            // WHY not Terrain: its cells are no part mesh, and a highlight of the Workspace would otherwise try to
            // outline the whole voxel world.
            return instance is RbxBasePart && !instance.IsA("Terrain");
        }

        private void OnRegistered(InstanceRecord record)
        {
            _treeChanged = true;
            if (record?.Instance is RbxHighlight highlight)
            {
                _highlights.Add(new Tracked(highlight));
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            _treeChanged = true;
            if (record == null)
            {
                return;
            }

            for (int index = 0; index < _highlights.Count; index++)
            {
                if (_highlights[index].Highlight.Id.Value != record.Id.Value)
                {
                    continue;
                }

                if (_highlights[index].Drawn)
                {
                    _backend.Release(record.Id);
                }

                _highlights.RemoveAt(index);
                return;
            }
        }

        private void OnRevisionAdvanced(InstanceId id, long revision, string member)
        {
            // WHY only these members: a part a script moves every frame advances its revision every frame, and the part
            // lists depend only on where instances sit and on what each highlight adorns.
            if (string.Equals(member, "Parent", StringComparison.Ordinal)
                || string.Equals(member, nameof(RbxHighlight.Adornee), StringComparison.Ordinal))
            {
                _treeChanged = true;
            }
        }

        private sealed class Tracked
        {
            public Tracked(RbxHighlight highlight)
            {
                Highlight = highlight;
            }

            public RbxHighlight Highlight { get; }

            public InstanceId[] Parts;

            public bool Drawn;

            public RbxHighlightRenderState State;
        }
    }
}
