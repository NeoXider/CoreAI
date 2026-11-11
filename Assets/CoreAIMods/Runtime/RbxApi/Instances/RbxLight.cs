using System;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>Light</c>, the abstract base of the dynamic lights: <c>Brightness</c> (default 1, as
    /// Light.yaml documents), <c>Color</c> (white), <c>Enabled</c> (true) and <c>Shadows</c> (false). A
    /// light shines only while it is the direct child of a <c>BasePart</c> inside the Workspace
    /// (PointLight.yaml); what draws it is the world's <see cref="IRbxLightBackend"/>, through
    /// <see cref="RbxLightReconciler"/>. Every real change fires <c>Changed</c> and the property signal;
    /// an equal write fires nothing.
    /// </summary>
    public abstract class RbxLight : RbxInstance
    {
        private float _brightness = 1f;
        private RbxColor3 _color = new(1f, 1f, 1f);
        private bool _enabled = true;
        private bool _shadows;

        private protected RbxLight(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>How bright the emitted light is.</summary>
        public float Brightness => _brightness;

        /// <summary>The colour of the emitted light.</summary>
        public RbxColor3 Color => _color;

        /// <summary>Whether the light is emitted at all.</summary>
        public bool Enabled => _enabled;

        /// <summary>Whether the light casts shadows where it is blocked.</summary>
        public bool Shadows => _shadows;

        /// <summary>Bumped on every real change; the reconciler re-applies a light only when it moved.</summary>
        public int StateVersion { get; private set; }

        public void SetBrightness(double brightness)
        {
            RequireFinite(brightness, "Brightness");
            float next = (float)brightness;
            if (_brightness.Equals(next))
            {
                return;
            }

            _brightness = next;
            OnChanged(nameof(Brightness));
        }

        public void SetColor(RbxColor3 color)
        {
            if (!IsFinite(color.R) || !IsFinite(color.G) || !IsFinite(color.B))
            {
                throw RbxError.BadArgument(
                    ClassName + ".Color needs a Color3 with finite components",
                    "check the arithmetic that produced the color before assigning");
            }

            if (_color == color)
            {
                return;
            }

            _color = color;
            OnChanged(nameof(Color));
        }

        public void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            OnChanged(nameof(Enabled));
        }

        public void SetShadows(bool shadows)
        {
            if (_shadows == shadows)
            {
                return;
            }

            _shadows = shadows;
            OnChanged(nameof(Shadows));
        }

        /// <summary>The light's reach in studs; the base light has none of its own.</summary>
        public virtual float RangeStuds => 0f;

        /// <summary>Copies light state without signals or revision changes.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxLight light = (RbxLight)copy;
            light._brightness = _brightness;
            light._color = _color;
            light._enabled = _enabled;
            light._shadows = _shadows;
        }

        private protected void OnChanged(string propertyName)
        {
            StateVersion++;
            NotifyPropertyChanged(propertyName);
        }

        private protected void RequireFinite(double value, string member)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw RbxError.BadArgument(
                    ClassName + "." + member + " needs a finite number, got "
                    + value.ToString(CultureInfo.InvariantCulture),
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// Roblox <c>PointLight</c>: light from the parent part's position in every direction, out to
    /// <see cref="Range"/> studs (default 8, held to [0, <see cref="MaxRange"/>]).
    /// </summary>
    public sealed class RbxPointLight : RbxLight
    {
        /// <summary>The default reach, studs.</summary>
        public const float DefaultRange = 8f;

        /// <summary>
        /// The longest reach, studs. WHY 60: the dump's <c>Lighting.ExtendLightRangeTo120</c> rollout flag
        /// names the cap a place has until it opts in, and CoreAI has no such opt-in.
        /// </summary>
        public const float MaxRange = 60f;

        private float _range = DefaultRange;

        internal RbxPointLight(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "PointLight";
        }

        /// <summary>How far the light reaches, studs.</summary>
        public float Range => _range;

        /// <inheritdoc />
        public override float RangeStuds => _range;

        public void SetRange(double range)
        {
            RequireFinite(range, "Range");
            float next = (float)Math.Max(0d, Math.Min(MaxRange, range));
            if (_range.Equals(next))
            {
                return;
            }

            _range = next;
            OnChanged(nameof(Range));
        }

        /// <summary>Copies point-light state without signals or revision changes.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            ((RbxPointLight)copy)._range = _range;
        }
    }
}
