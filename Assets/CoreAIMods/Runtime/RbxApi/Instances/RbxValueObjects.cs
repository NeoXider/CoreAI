using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Mirror <c>ValueBase</c> (abstract, NotCreatable): the common ancestor of all value
    /// instances. Every concrete value stores one payload in <c>Value</c> and fires
    /// <see cref="Changed"/> with the NEW value — never a property-name string — plus the
    /// standard <c>GetPropertyChangedSignal("Value")</c>.
    /// </summary>
    public abstract class RbxValueBase : RbxInstance
    {
        protected RbxValueBase(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>
        /// Mirror <c>Changed</c>: "Fires whenever the Value is changed". The mirror is silent
        /// on assigning the SAME value again, so this slice pins OURS: an equal assignment is
        /// not a change — nothing fires, no revision advances (same guard as
        /// <c>Name</c>/<c>Archivable</c>).
        /// </summary>
        public RbxScriptSignal Changed => GetOrCreateSignal("Changed");

        /// <summary>Fires <see cref="Changed"/> and the property signal after a real change.</summary>
        protected void FireValueChanged(object newValue)
        {
            Registry?.AdvanceRevision(Id);
            FireSignal("Changed", newValue);
            FireSignal("GetPropertyChangedSignal(Value)");
        }

        /// <summary>Copies the payload into a <see cref="RbxInstance.Clone"/> copy.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            if (copy is RbxValueBase valueCopy)
            {
                CopyValueTo(valueCopy);
            }
        }

        /// <summary>Per-type payload copy used by <see cref="CopyCustomStateTo"/>.</summary>
        protected abstract void CopyValueTo(RbxValueBase copy);
    }

    /// <summary>
    /// Mirror <c>IntValue</c>: a signed 64-bit integer. Assignments from Lua arrive as doubles
    /// and are rounded to the nearest integer with halfway cases away from zero (mirror);
    /// non-finite assignments are refused (OURS — the mirror does not specify them).
    /// Default 0 (OURS — the mirror does not specify defaults).
    /// </summary>
    public sealed class RbxIntValue : RbxValueBase
    {
        private long _value;

        internal RbxIntValue(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "IntValue";
        }

        /// <summary>Mirror <c>IntValue.Value</c> (int64, serializable).</summary>
        public long Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (_value == value)
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        /// <summary>Rounds a Lua number to the mirror's integer rule; refuses non-finite input.</summary>
        public void SetFromDouble(double number)
        {
            ThrowIfDestroyed("Value");
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw RbxError.BadArgument(
                    "IntValue.Value expects a finite number",
                    "pass an integer, e.g. intValue.Value = 3");
            }

            Value = (long)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxIntValue)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(long value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// Mirror <c>NumberValue</c>: a double-precision float (serializable). Non-finite values are
    /// held in memory and rejected at save time by the world package ("non-finite values are
    /// rejected"). Default 0 (OURS — the mirror does not specify defaults).
    /// </summary>
    public sealed class RbxNumberValue : RbxValueBase
    {
        private double _value;

        internal RbxNumberValue(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "NumberValue";
        }

        /// <summary>Mirror <c>NumberValue.Value</c> (double, serializable).</summary>
        public double Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (_value == value)
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxNumberValue)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(double value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// Mirror <c>StringValue</c>: a string of at most 200,000 characters — anything longer
    /// raises a <c>String too long</c> error (mirror). Nil is refused. Default "" (OURS).
    /// </summary>
    public sealed class RbxStringValue : RbxValueBase
    {
        /// <summary>Mirror cap: longer assignments raise <c>String too long</c>.</summary>
        public const int MaxLength = 200000;

        private string _value = string.Empty;

        internal RbxStringValue(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "StringValue";
        }

        /// <summary>Mirror <c>StringValue.Value</c> (string, serializable).</summary>
        public string Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (value == null)
                {
                    throw RbxError.BadArgument(
                        "StringValue.Value expects a string, got nil",
                        "pass a string, e.g. stringValue.Value = \"Coins\"");
                }

                if (value.Length > MaxLength)
                {
                    throw RbxError.BadArgument(
                        "String too long: StringValue.Value holds at most "
                        + MaxLength + " characters",
                        "store shorter text or split it across several values");
                }

                if (string.Equals(_value, value, StringComparison.Ordinal))
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxStringValue)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(string value)
        {
            _value = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Mirror <c>BoolValue</c>: a single boolean (serializable). Default false (OURS).
    /// </summary>
    public sealed class RbxBoolValue : RbxValueBase
    {
        private bool _value;

        internal RbxBoolValue(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "BoolValue";
        }

        /// <summary>Mirror <c>BoolValue.Value</c> (boolean, serializable).</summary>
        public bool Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (_value == value)
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxBoolValue)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(bool value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// Mirror <c>ObjectValue</c>: a reference to another instance, or nil (serializable as the
    /// target id; 0 means nil). A package whose target id is outside the package is rejected.
    /// Default nil. Clone copies the reference as-is (OURS — Studio remaps references covered
    /// by the duplicate; the engine-free Clone has no duplicate set to remap against).
    /// </summary>
    public sealed class RbxObjectValue : RbxValueBase
    {
        private RbxInstance _value;

        internal RbxObjectValue(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "ObjectValue";
        }

        /// <summary>Mirror <c>ObjectValue.Value</c> (Instance or nil, serializable).</summary>
        public RbxInstance Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (ReferenceEquals(_value, value))
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxObjectValue)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(RbxInstance value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// Mirror <c>Vector3Value</c>: a single Vector3 (serializable). Default (0, 0, 0) (OURS).
    /// </summary>
    public sealed class RbxVector3Value : RbxValueBase
    {
        private RbxVector3 _value = RbxVector3.Zero;

        internal RbxVector3Value(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Vector3Value";
        }

        /// <summary>Mirror <c>Vector3Value.Value</c> (Vector3, serializable).</summary>
        public RbxVector3 Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (_value == value)
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxVector3Value)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(RbxVector3 value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// Mirror <c>CFrameValue</c>: a single CFrame (serializable). Default identity (OURS).
    /// </summary>
    public sealed class RbxCFrameValue : RbxValueBase
    {
        private RbxCFrame _value = RbxCFrame.Identity;

        internal RbxCFrameValue(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "CFrameValue";
        }

        /// <summary>Mirror <c>CFrameValue.Value</c> (CFrame, serializable).</summary>
        public RbxCFrame Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (_value == value)
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxCFrameValue)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(RbxCFrame value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// Mirror <c>Color3Value</c>: a single Color3 (serializable). Default black (0, 0, 0)
    /// (OURS — the mirror does not specify defaults).
    /// </summary>
    public sealed class RbxColor3Value : RbxValueBase
    {
        private RbxColor3 _value;

        internal RbxColor3Value(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Color3Value";
            _value = new RbxColor3(0f, 0f, 0f);
        }

        /// <summary>Mirror <c>Color3Value.Value</c> (Color3, serializable).</summary>
        public RbxColor3 Value
        {
            get
            {
                ThrowIfDestroyed("Value");
                return _value;
            }

            set
            {
                ThrowIfDestroyed("Value");
                if (_value == value)
                {
                    return;
                }

                _value = value;
                FireValueChanged(_value);
            }
        }

        protected override void CopyValueTo(RbxValueBase copy)
        {
            ((RbxColor3Value)copy).SetValueSilent(_value);
        }

        internal void SetValueSilent(RbxColor3 value)
        {
            _value = value;
        }
    }

    /// <summary>
    /// <c>leaderstats</c> is CONVENTION, not API: the local docs mirror documents no
    /// <c>leaderstats</c> class, property, or event — the leaderboard convention is a plain
    /// <c>Folder</c> named exactly <c>leaderstats</c> parented under a <c>Player</c>, whose
    /// <c>ValueBase</c> children (typically <c>IntValue</c>) the Roblox client renders as
    /// leaderboard columns. CoreAI therefore ships no <c>RbxLeaderstats</c> class: any
    /// <c>Folder</c> named <c>leaderstats</c> round-trips through the world package like any
    /// other folder, and its value children persist through the Value durable surface below.
    /// Do not present <c>leaderstats</c> as engine API — nothing constructs, validates, or
    /// special-cases it.
    /// </summary>
    public static class RbxLeaderstatsConvention
    {
        /// <summary>The exact folder name the leaderboard convention keys on.</summary>
        public const string FolderName = "leaderstats";
    }
}
