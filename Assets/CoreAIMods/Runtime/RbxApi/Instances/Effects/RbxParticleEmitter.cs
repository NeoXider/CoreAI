using System;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>ParticleEmitter</c>, minimal (ParticleEmitter.yaml; defaults from <c>Full-API-Dump.json</c>):
    /// <c>Enabled</c> (true), <c>Rate</c> (20 a second), <c>Lifetime</c> (NumberRange 5 to 10), <c>Speed</c> (NumberRange
    /// 5), <c>Color</c> (white), <c>Size</c> (1), <c>Transparency</c> (0), <c>Texture</c> (the sparkles default),
    /// <c>SpreadAngle</c> (0, 0), <c>Acceleration</c> (0, 0, 0) and <c>EmissionDirection</c> (Top), with
    /// <c>Emit(count)</c> (16 by default) and <c>Clear()</c>. It emits while it is the direct child of a <c>BasePart</c>
    /// in the Workspace, from the part's box, out of the chosen face; what draws it is the world's
    /// <see cref="IRbxParticleBackend"/>, through <see cref="RbxParticleReconciler"/>.
    /// </summary>
    /// <remarks>
    /// <c>Emit</c> and <c>Clear</c> are commands for the drawing: they are queued here and taken by the reconciler on
    /// its next frame, so an emitter that is not drawn (headless, outside a part) emits nothing, as an unparented
    /// Roblox emitter does. Every real property change fires <c>Changed</c> and the property signal once.
    /// </remarks>
    public sealed class RbxParticleEmitter : RbxInstance
    {
        /// <summary>The texture an emitter starts with (the dump's default); it reads back unchanged.</summary>
        public const string DefaultTexture = "rbxasset://textures/particles/sparkles_main.dds";

        /// <summary>The most particles one <c>Emit</c> call asks for; a larger count is held to it.</summary>
        public const int MaxEmitCount = 1000;

        private static readonly RbxColorSequence DefaultColor = new(new RbxColor3(1f, 1f, 1f));
        private static readonly RbxNumberSequence DefaultSize = new(1f);
        private static readonly RbxNumberSequence DefaultTransparency = new(0f);

        private bool _enabled = true;
        private float _rate = 20f;
        private RbxNumberRange _lifetime = new(5f, 10f);
        private RbxNumberRange _speed = new(5f);
        private RbxColorSequence _color = DefaultColor;
        private RbxNumberSequence _size = DefaultSize;
        private RbxNumberSequence _transparency = DefaultTransparency;
        private string _texture = DefaultTexture;
        private RbxVector2 _spreadAngle;
        private RbxVector3 _acceleration;
        private int _emissionDirection = (int)RbxParticleFace.Top;
        private int _pendingEmit;
        private bool _pendingClear;

        internal RbxParticleEmitter(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "ParticleEmitter";
        }

        public bool Enabled => _enabled;

        /// <summary>Particles a second while enabled.</summary>
        public float Rate => _rate;

        /// <summary>Each new particle's life, seconds, picked between Min and Max.</summary>
        public RbxNumberRange Lifetime => _lifetime;

        /// <summary>Each new particle's speed, studs a second, picked between Min and Max; negative flies backwards.</summary>
        public RbxNumberRange Speed => _speed;

        public RbxColorSequence Color => _color;

        /// <summary>The particle's edge in studs over its life.</summary>
        public RbxNumberSequence Size => _size;

        public RbxNumberSequence Transparency => _transparency;

        /// <summary>A key the host registered, a <c>Resources</c> path, or a Roblox id (drawn as CoreAI's soft dot).</summary>
        public string Texture => _texture;

        /// <summary>The random spread around the emission direction, degrees on the face's two axes.</summary>
        public RbxVector2 SpreadAngle => _spreadAngle;

        /// <summary>World acceleration of every live particle, studs a second squared.</summary>
        public RbxVector3 Acceleration => _acceleration;

        /// <summary>The face particles leave by, as an <c>Enum.NormalId</c> value.</summary>
        public RbxParticleFace EmissionDirection => (RbxParticleFace)_emissionDirection;

        /// <summary>Bumped on every real change; the reconciler re-applies an emitter only when it moved.</summary>
        public int StateVersion { get; private set; }

        /// <summary>Particles asked for by <see cref="Emit"/> and not yet handed to the drawing.</summary>
        public int PendingEmit => _pendingEmit;

        /// <summary>True when <see cref="Clear"/> was called since the drawing last looked.</summary>
        public bool PendingClear => _pendingClear;

        public void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            OnChanged(nameof(Enabled));
        }

        public void SetRate(double rate)
        {
            RequireFinite(rate, nameof(Rate));
            float next = (float)Math.Max(0d, rate);
            if (_rate.Equals(next))
            {
                return;
            }

            _rate = next;
            OnChanged(nameof(Rate));
        }

        public void SetLifetime(RbxNumberRange lifetime)
        {
            RbxNumberRange next = lifetime.Min < 0f ? new RbxNumberRange(0f, Math.Max(0f, lifetime.Max)) : lifetime;
            if (_lifetime == next)
            {
                return;
            }

            _lifetime = next;
            OnChanged(nameof(Lifetime));
        }

        public void SetSpeed(RbxNumberRange speed)
        {
            if (_speed == speed)
            {
                return;
            }

            _speed = speed;
            OnChanged(nameof(Speed));
        }

        public void SetColor(RbxColorSequence color)
        {
            RbxColorSequence next = color ?? throw RequireValue(nameof(Color), "a ColorSequence",
                "ColorSequence.new(Color3.new(1, 0.5, 0))");
            if (_color.Equals(next))
            {
                return;
            }

            _color = next;
            OnChanged(nameof(Color));
        }

        public void SetSize(RbxNumberSequence size)
        {
            RbxNumberSequence next = size ?? throw RequireValue(nameof(Size), "a NumberSequence",
                "NumberSequence.new(1)");
            if (_size.Equals(next))
            {
                return;
            }

            _size = next;
            OnChanged(nameof(Size));
        }

        public void SetTransparency(RbxNumberSequence transparency)
        {
            RbxNumberSequence next = transparency ?? throw RequireValue(nameof(Transparency), "a NumberSequence",
                "NumberSequence.new(0, 1)");
            if (_transparency.Equals(next))
            {
                return;
            }

            _transparency = next;
            OnChanged(nameof(Transparency));
        }

        public void SetTexture(string texture)
        {
            string next = texture ?? string.Empty;
            if (string.Equals(_texture, next, StringComparison.Ordinal))
            {
                return;
            }

            _texture = next;
            OnChanged(nameof(Texture));
        }

        public void SetSpreadAngle(RbxVector2 spreadAngle)
        {
            if (!IsFinite(spreadAngle.X) || !IsFinite(spreadAngle.Y))
            {
                throw RbxError.BadArgument(ClassName + ".SpreadAngle needs a Vector2 with finite components",
                    "check the arithmetic that produced the angles before assigning");
            }

            if (_spreadAngle == spreadAngle)
            {
                return;
            }

            _spreadAngle = spreadAngle;
            OnChanged(nameof(SpreadAngle));
        }

        public void SetAcceleration(RbxVector3 acceleration)
        {
            if (!IsFinite(acceleration.X) || !IsFinite(acceleration.Y) || !IsFinite(acceleration.Z))
            {
                throw RbxError.BadArgument(ClassName + ".Acceleration needs a Vector3 with finite components",
                    "check the arithmetic that produced the vector before assigning");
            }

            if (_acceleration == acceleration)
            {
                return;
            }

            _acceleration = acceleration;
            OnChanged(nameof(Acceleration));
        }

        public void SetEmissionDirection(RbxParticleFace face)
        {
            if (face < RbxParticleFace.Right || face > RbxParticleFace.Front)
            {
                throw RbxError.BadArgument(ClassName + ".EmissionDirection got an item outside Enum.NormalId",
                    "assign Enum.NormalId.Top, Bottom, Left, Right, Front or Back");
            }

            if (_emissionDirection == (int)face)
            {
                return;
            }

            _emissionDirection = (int)face;
            OnChanged(nameof(EmissionDirection));
        }

        /// <summary>
        /// Emits <paramref name="count"/> particles at once (ParticleEmitter.yaml, default 16), held to
        /// [0, <see cref="MaxEmitCount"/>]; they appear on the drawing's next frame.
        /// </summary>
        public void Emit(int count = 16)
        {
            ThrowIfDestroyed("Emit");
            if (count <= 0)
            {
                return;
            }

            _pendingEmit = Math.Min(MaxEmitCount, _pendingEmit + Math.Min(count, MaxEmitCount));
        }

        /// <summary>Removes every live particle on the drawing's next frame and drops any pending emit.</summary>
        public void Clear()
        {
            ThrowIfDestroyed("Clear");
            _pendingEmit = 0;
            _pendingClear = true;
        }

        /// <summary>Hands the pending commands to the drawing and forgets them.</summary>
        public void TakeCommands(out int emit, out bool clear)
        {
            emit = _pendingEmit;
            clear = _pendingClear;
            _pendingEmit = 0;
            _pendingClear = false;
        }

        /// <summary>Copies the emitter's properties; pending commands stay with the original.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxParticleEmitter emitter = (RbxParticleEmitter)copy;
            emitter._enabled = _enabled;
            emitter._rate = _rate;
            emitter._lifetime = _lifetime;
            emitter._speed = _speed;
            emitter._color = _color;
            emitter._size = _size;
            emitter._transparency = _transparency;
            emitter._texture = _texture;
            emitter._spreadAngle = _spreadAngle;
            emitter._acceleration = _acceleration;
            emitter._emissionDirection = _emissionDirection;
        }

        private void OnChanged(string propertyName)
        {
            StateVersion++;
            NotifyPropertyChanged(propertyName);
        }

        private RbxError RequireValue(string member, string expected, string example)
        {
            return RbxError.BadArgument(ClassName + "." + member + " expects " + expected,
                "assign " + expected + ", e.g. " + example);
        }

        private void RequireFinite(double value, string member)
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

    /// <summary><c>Enum.NormalId</c> values, the faces a particle leaves by.</summary>
    public enum RbxParticleFace
    {
        Right = 0,
        Top = 1,
        Back = 2,
        Left = 3,
        Bottom = 4,
        Front = 5
    }
}
