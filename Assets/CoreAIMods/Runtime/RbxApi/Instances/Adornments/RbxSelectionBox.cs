using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Adornments
{
    /// <summary>
    /// Mirror <c>GuiBase3d</c> (NotCreatable): an object drawn in the 3D world, with <see cref="Color3"/>,
    /// <see cref="Transparency"/> and <see cref="Visible"/>. Every real change advances the revision and fires
    /// <c>Changed</c> and the property signal.
    /// </summary>
    public abstract class RbxGuiBase3d : RbxInstance
    {
        /// <summary>The default colour of Roblox's adornments, RGB (13, 105, 172).</summary>
        public static readonly RbxColor3 DefaultColor = new(13f / 255f, 105f / 255f, 172f / 255f);

        private RbxColor3 _color3 = DefaultColor;
        private float _transparency;
        private bool _visible = true;

        private protected RbxGuiBase3d(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>GuiBase3d.Color3</c>.</summary>
        public RbxColor3 Color3
        {
            get => _color3;
            set => Set(ref _color3, value, nameof(Color3));
        }

        /// <summary>Mirror <c>GuiBase3d.Transparency</c>.</summary>
        public float Transparency
        {
            get => _transparency;
            set => Set(ref _transparency, RequireFinite(value, nameof(Transparency)), nameof(Transparency));
        }

        /// <summary>Mirror <c>GuiBase3d.Visible</c>.</summary>
        public bool Visible
        {
            get => _visible;
            set => Set(ref _visible, value, nameof(Visible));
        }

        private protected void Set<T>(ref T field, T value, string member)
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

        private protected float RequireFinite(float value, string member)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw RbxError.BadArgument(ClassName + "." + member + " must be a finite number",
                    "assign a number such as 0.5");
            }

            return value;
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxGuiBase3d target = (RbxGuiBase3d)copy;
            target._color3 = _color3;
            target._transparency = _transparency;
            target._visible = _visible;
        }
    }

    /// <summary>Mirror <c>InstanceAdornment</c> (NotCreatable): a 3D adornment drawn around its <see cref="Adornee"/>.</summary>
    public abstract class RbxInstanceAdornment : RbxGuiBase3d
    {
        private RbxInstance _adornee;

        private protected RbxInstanceAdornment(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>InstanceAdornment.Adornee</c>: the part or model it is drawn around; nil draws nothing.</summary>
        public RbxInstance Adornee
        {
            get => _adornee != null && !_adornee.IsDestroyed ? _adornee : null;
            set => Set(ref _adornee, value, nameof(Adornee));
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            ((RbxInstanceAdornment)copy)._adornee = _adornee;
        }
    }

    /// <summary>
    /// Mirror <c>SelectionBox</c>: the edges of its adornee's box (a part's own box, or the box around every part of
    /// a model) drawn <see cref="LineThickness"/> studs thick, and its faces in <see cref="SurfaceColor3"/> when
    /// <see cref="SurfaceTransparency"/> is below 1. It is drawn while it sits in the Workspace or a PlayerGui and
    /// its adornee is in the Workspace; it is never hit by a ray and never collides.
    /// </summary>
    public sealed class RbxSelectionBox : RbxInstanceAdornment
    {
        private float _lineThickness = 0.15f;
        private RbxColor3 _surfaceColor3 = DefaultColor;
        private float _surfaceTransparency = 1f;

        internal RbxSelectionBox(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>SelectionBox.LineThickness</c>, studs (default 0.15).</summary>
        public float LineThickness
        {
            get => _lineThickness;
            set => Set(ref _lineThickness, RequireFinite(value, nameof(LineThickness)), nameof(LineThickness));
        }

        /// <summary>Mirror <c>SelectionBox.SurfaceColor3</c>.</summary>
        public RbxColor3 SurfaceColor3
        {
            get => _surfaceColor3;
            set => Set(ref _surfaceColor3, value, nameof(SurfaceColor3));
        }

        /// <summary>Mirror <c>SelectionBox.SurfaceTransparency</c> (default 1: no faces).</summary>
        public float SurfaceTransparency
        {
            get => _surfaceTransparency;
            set => Set(ref _surfaceTransparency, RequireFinite(value, nameof(SurfaceTransparency)),
                nameof(SurfaceTransparency));
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxSelectionBox target = (RbxSelectionBox)copy;
            target._lineThickness = _lineThickness;
            target._surfaceColor3 = _surfaceColor3;
            target._surfaceTransparency = _surfaceTransparency;
        }
    }

    /// <summary>One selection box as its backend draws it, in Roblox space.</summary>
    public readonly struct RbxSelectionBoxRenderState : IEquatable<RbxSelectionBoxRenderState>
    {
        public RbxSelectionBoxRenderState(RbxCFrame cframe, RbxVector3 size, RbxColor3 color, float transparency,
            float lineThickness, RbxColor3 surfaceColor, float surfaceTransparency)
        {
            CFrame = cframe;
            Size = size;
            Color = color;
            Transparency = transparency;
            LineThickness = lineThickness;
            SurfaceColor = surfaceColor;
            SurfaceTransparency = surfaceTransparency;
        }

        /// <summary>The centre and orientation of the box.</summary>
        public RbxCFrame CFrame { get; }

        /// <summary>The box size, studs.</summary>
        public RbxVector3 Size { get; }

        public RbxColor3 Color { get; }

        public float Transparency { get; }

        public float LineThickness { get; }

        public RbxColor3 SurfaceColor { get; }

        public float SurfaceTransparency { get; }

        public bool Equals(RbxSelectionBoxRenderState other)
        {
            return CFrame == other.CFrame && Size == other.Size && Color == other.Color
                   && Transparency.Equals(other.Transparency) && LineThickness.Equals(other.LineThickness)
                   && SurfaceColor == other.SurfaceColor && SurfaceTransparency.Equals(other.SurfaceTransparency);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxSelectionBoxRenderState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Size.GetHashCode() ^ (Transparency.GetHashCode() * 31);
        }
    }

    /// <summary>What draws a world's selection boxes; the Unity default draws twelve thin edges without colliders.</summary>
    public interface IRbxSelectionBoxBackend
    {
        /// <summary>Creates or updates the drawn box of <paramref name="boxId"/>.</summary>
        void Apply(InstanceId boxId, in RbxSelectionBoxRenderState state);

        /// <summary>Removes the drawn box of <paramref name="boxId"/>; unknown ids are ignored.</summary>
        void Release(InstanceId boxId);
    }

    /// <summary>Reads a part's box as the world draws it: its live CFrame and its Size; false when it has none.</summary>
    public delegate bool RbxPartBoxReader(InstanceId partId, out RbxCFrame cframe, out RbxVector3 size);

    /// <summary>
    /// Keeps a world's drawn selection boxes in step with its <see cref="RbxSelectionBox"/> instances, once per frame:
    /// a box is drawn while it is visible, sits in the Workspace or a PlayerGui, and has an adornee in the Workspace
    /// that is a part, or a model with parts. The backend is called only for a box whose drawn state changed.
    /// </summary>
    public sealed class RbxSelectionBoxReconciler : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly IRbxSelectionBoxBackend _backend;
        private readonly RbxPartBoxReader _partBox;
        private readonly Dictionary<InstanceId, Tracked> _boxes = new();
        private bool _disposed;

        public RbxSelectionBoxReconciler(InstanceRegistry registry, IRbxSelectionBoxBackend backend,
            RbxPartBoxReader partBox)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _partBox = partBox ?? throw new ArgumentNullException(nameof(partBox));
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxSelectionBox box)
                {
                    _boxes[box.Id] = new Tracked(box);
                }
            }
        }

        /// <summary>Selection boxes drawn after the last <see cref="Tick"/>.</summary>
        public int DrawnCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<InstanceId, Tracked> pair in _boxes)
                {
                    if (pair.Value.Drawn)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Brings every drawn box in step with its instance and adornee.</summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            foreach (KeyValuePair<InstanceId, Tracked> pair in _boxes)
            {
                Tracked tracked = pair.Value;
                if (TryResolve(tracked.Box, out RbxSelectionBoxRenderState state))
                {
                    if (!tracked.Drawn || !tracked.State.Equals(state))
                    {
                        _backend.Apply(pair.Key, in state);
                        tracked.State = state;
                        tracked.Drawn = true;
                    }
                }
                else if (tracked.Drawn)
                {
                    _backend.Release(pair.Key);
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
            foreach (KeyValuePair<InstanceId, Tracked> pair in _boxes)
            {
                if (pair.Value.Drawn)
                {
                    _backend.Release(pair.Key);
                }
            }

            _boxes.Clear();
        }

        private bool TryResolve(RbxSelectionBox box, out RbxSelectionBoxRenderState state)
        {
            state = default;
            RbxInstance adornee = box.IsDestroyed ? null : box.Adornee;
            RbxInstance workspace = _registry.WorldRoot;
            if (adornee == null || !box.Visible || workspace == null || !adornee.IsDescendantOf(workspace)
                || !IsShownWhereItSits(box, workspace))
            {
                return false;
            }

            if (!TryMeasure(adornee, out RbxCFrame cframe, out RbxVector3 size))
            {
                return false;
            }

            state = new RbxSelectionBoxRenderState(cframe, size, box.Color3, box.Transparency, box.LineThickness,
                box.SurfaceColor3, box.SurfaceTransparency);
            return true;
        }

        private static bool IsShownWhereItSits(RbxInstance box, RbxInstance workspace)
        {
            if (box.IsDescendantOf(workspace))
            {
                return true;
            }

            for (RbxInstance node = box.Parent; node != null; node = node.Parent)
            {
                if (string.Equals(node.ClassName, "PlayerGui", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryMeasure(RbxInstance adornee, out RbxCFrame cframe, out RbxVector3 size)
        {
            if (adornee is RbxBasePart)
            {
                return _partBox(adornee.Id, out cframe, out size);
            }

            cframe = default;
            size = default;
            if (!(adornee is RbxModel))
            {
                return false;
            }

            bool any = false;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            IReadOnlyList<RbxInstance> descendants = adornee.GetDescendants();
            for (int index = 0; index < descendants.Count; index++)
            {
                if (!(descendants[index] is RbxBasePart part) || !_partBox(part.Id, out RbxCFrame partFrame,
                        out RbxVector3 partSize))
                {
                    continue;
                }

                for (int corner = 0; corner < 8; corner++)
                {
                    RbxVector3 local = new(
                        (corner & 1) == 0 ? -partSize.X * 0.5f : partSize.X * 0.5f,
                        (corner & 2) == 0 ? -partSize.Y * 0.5f : partSize.Y * 0.5f,
                        (corner & 4) == 0 ? -partSize.Z * 0.5f : partSize.Z * 0.5f);
                    RbxVector3 world = partFrame.PointToWorldSpace(local);
                    minX = Math.Min(minX, world.X);
                    minY = Math.Min(minY, world.Y);
                    minZ = Math.Min(minZ, world.Z);
                    maxX = Math.Max(maxX, world.X);
                    maxY = Math.Max(maxY, world.Y);
                    maxZ = Math.Max(maxZ, world.Z);
                }

                any = true;
            }

            if (!any)
            {
                return false;
            }

            cframe = RbxCFrame.FromPosition((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            size = new RbxVector3(maxX - minX, maxY - minY, maxZ - minZ);
            return true;
        }

        private void OnRegistered(InstanceRecord record)
        {
            if (record?.Instance is RbxSelectionBox box)
            {
                _boxes[box.Id] = new Tracked(box);
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (record == null || !_boxes.TryGetValue(record.Id, out Tracked tracked))
            {
                return;
            }

            _boxes.Remove(record.Id);
            if (tracked.Drawn)
            {
                _backend.Release(record.Id);
            }
        }

        private sealed class Tracked
        {
            public Tracked(RbxSelectionBox box)
            {
                Box = box;
            }

            public RbxSelectionBox Box { get; }

            public bool Drawn;

            public RbxSelectionBoxRenderState State;
        }
    }
}
