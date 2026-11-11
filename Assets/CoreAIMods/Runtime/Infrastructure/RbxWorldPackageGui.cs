using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The durable properties of one world-owned Roblox GUI node (the package's optional <c>gui</c> block on a
    /// <c>ScreenGui</c>, a <c>GuiObject</c> or a <c>UI*</c> layout object): every property a script can set
    /// through the class's own setters, enums as their Roblox numbers. Layout results (<c>Absolute*</c>), focus,
    /// the caret and everything the presenter keeps are not part of it.
    /// </summary>
    public sealed class RbxWorldPackageGui : IEquatable<RbxWorldPackageGui>
    {
        private readonly RbxGuiValue[] _values;

        internal RbxWorldPackageGui(string className, RbxGuiValue[] values)
        {
            ClassName = className;
            _values = values;
        }

        /// <summary>The Roblox class the block belongs to.</summary>
        public string ClassName { get; }

        /// <summary>The Roblox names of the stored properties, in the order the package writes them.</summary>
        public IReadOnlyList<string> PropertyNames => RbxWorldPackageGuiSchema.PropertyNamesOf(ClassName);

        internal int Count => _values.Length;

        /// <summary>True for a class whose nodes carry a <c>gui</c> block.</summary>
        public static bool IsGuiClass(string className)
        {
            return RbxWorldPackageGuiSchema.IsGuiClass(className);
        }

        /// <summary>The state of <paramref name="instance"/> as it is now; null when its class carries no block.</summary>
        public static RbxWorldPackageGui Of(RbxInstance instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            return RbxWorldPackageGuiSchema.Capture(instance);
        }

        /// <summary>
        /// Puts this state on <paramref name="instance"/> through its public setters; the instance must be of
        /// <see cref="ClassName"/>.
        /// </summary>
        public void ApplyTo(RbxInstance instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            RbxWorldPackageGuiSchema.Apply(this, instance);
        }

        /// <summary>
        /// The stored value of <paramref name="property"/> (a Roblox property name) as its datatype: bool, int (enums
        /// too), float, string, <see cref="RbxColor3"/>, <see cref="RbxUDim"/>, <see cref="RbxUDim2"/> or
        /// <see cref="RbxVector2"/>.
        /// </summary>
        public bool TryGetValue(string property, out object value)
        {
            int index = RbxWorldPackageGuiSchema.IndexOf(ClassName, property);
            if (index < 0)
            {
                value = null;
                return false;
            }

            value = _values[index].ToObject();
            return true;
        }

        internal RbxGuiValue ValueAt(int index)
        {
            return _values[index];
        }

        /// <summary>
        /// This block with every lone UTF-16 surrogate in its text replaced by U+FFFD; reports the Roblox name of each
        /// replaced property. The same instance when none was.
        /// </summary>
        internal RbxWorldPackageGui WithWellFormedText(Action<string> replaced)
        {
            RbxGuiValue[] values = null;
            for (int index = 0; index < _values.Length; index++)
            {
                RbxGuiValue value = _values[index];
                if (value.Kind != RbxGuiValueKind.Text)
                {
                    continue;
                }

                string wellFormed = RbxWorldPackageSerializer.ReplaceLoneSurrogates(value.Text);
                if (ReferenceEquals(wellFormed, value.Text))
                {
                    continue;
                }

                values ??= (RbxGuiValue[])_values.Clone();
                values[index] = RbxGuiValue.OfText(wellFormed);
                replaced?.Invoke(RbxWorldPackageGuiSchema.PropertyNamesOf(ClassName)[index]);
            }

            return values == null ? this : new RbxWorldPackageGui(ClassName, values);
        }

        public bool Equals(RbxWorldPackageGui other)
        {
            if (other == null || !string.Equals(ClassName, other.ClassName, StringComparison.Ordinal)
                              || _values.Length != other._values.Length)
            {
                return false;
            }

            for (int index = 0; index < _values.Length; index++)
            {
                if (!_values[index].Equals(other._values[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackageGui other && Equals(other);
        }

        public override int GetHashCode()
        {
            ulong hash = 1469598103934665603UL;
            for (int index = 0; index < _values.Length; index++)
            {
                hash = RbxGuiValue.Combine(hash, _values[index]);
            }

            return unchecked((int)(hash ^ (hash >> 32)));
        }
    }

    /// <summary>The recorded switches of the <c>StarterGui</c> service (the package's <c>starter_gui</c> block).</summary>
    public readonly struct RbxWorldPackageStarterGui : IEquatable<RbxWorldPackageStarterGui>
    {
        /// <summary>The class default of <c>ScreenOrientation</c> (<c>Enum.ScreenOrientation.Sensor</c>).</summary>
        public const int DefaultScreenOrientation = 4;

        /// <summary>One past the highest <c>Enum.CoreGuiType</c> value.</summary>
        internal const int CoreGuiTypeCount = 10;

        private readonly int[] _coreGuiDisabled;

        public RbxWorldPackageStarterGui(bool showDevelopmentGui, int screenOrientation, IReadOnlyList<int> coreGuiDisabled)
        {
            ShowDevelopmentGui = showDevelopmentGui;
            ScreenOrientation = screenOrientation;
            int[] copy = new int[coreGuiDisabled?.Count ?? 0];
            for (int index = 0; index < copy.Length; index++)
            {
                copy[index] = coreGuiDisabled[index];
            }

            _coreGuiDisabled = copy;
        }

        public bool ShowDevelopmentGui { get; }

        /// <summary><c>Enum.ScreenOrientation</c> value.</summary>
        public int ScreenOrientation { get; }

        /// <summary>
        /// The <c>Enum.CoreGuiType</c> values switched off, ascending, never <c>All</c> (4): <c>All</c> is a shorthand
        /// that <c>SetCoreGuiEnabled</c> expands into every type.
        /// </summary>
        public IReadOnlyList<int> CoreGuiDisabled => _coreGuiDisabled ?? Array.Empty<int>();

        /// <summary>True when every switch has its class default, which a package leaves out.</summary>
        public bool IsDefault => ShowDevelopmentGui && ScreenOrientation == DefaultScreenOrientation
                                 && CoreGuiDisabled.Count == 0;

        /// <summary>The switches of <paramref name="starterGui"/> as they are now.</summary>
        public static RbxWorldPackageStarterGui Of(RbxStarterGui starterGui)
        {
            if (starterGui == null)
            {
                throw new ArgumentNullException(nameof(starterGui));
            }

            List<int> disabled = null;
            for (int type = 0; type < CoreGuiTypeCount; type++)
            {
                if (type != RbxStarterGui.CoreGuiTypeAll && !starterGui.GetCoreGuiEnabled(type))
                {
                    disabled ??= new List<int>();
                    disabled.Add(type);
                }
            }

            return new RbxWorldPackageStarterGui(starterGui.ShowDevelopmentGui, starterGui.ScreenOrientation, disabled);
        }

        /// <summary>Puts these switches on <paramref name="starterGui"/> through its public setters.</summary>
        public void ApplyTo(RbxStarterGui starterGui)
        {
            if (starterGui == null)
            {
                throw new ArgumentNullException(nameof(starterGui));
            }

            starterGui.SetShowDevelopmentGui(ShowDevelopmentGui);
            starterGui.SetScreenOrientation(ScreenOrientation);
            for (int type = 0; type < CoreGuiTypeCount; type++)
            {
                if (type != RbxStarterGui.CoreGuiTypeAll)
                {
                    starterGui.SetCoreGuiEnabled(type, Array.IndexOf(_coreGuiDisabled ?? Array.Empty<int>(), type) < 0);
                }
            }
        }

        /// <summary>Null when the list is canonical (ascending, unique, each a CoreGuiType other than All), else why not.</summary>
        internal string DescribeInvalidList()
        {
            IReadOnlyList<int> list = CoreGuiDisabled;
            for (int index = 0; index < list.Count; index++)
            {
                int type = list[index];
                if (type < 0 || type >= CoreGuiTypeCount || type == RbxStarterGui.CoreGuiTypeAll)
                {
                    return "core_gui_disabled holds " + type.ToString(CultureInfo.InvariantCulture)
                           + ", which is not an Enum.CoreGuiType other than All";
                }

                if (index > 0 && list[index - 1] >= type)
                {
                    return "core_gui_disabled is not strictly ascending";
                }
            }

            return null;
        }

        public bool Equals(RbxWorldPackageStarterGui other)
        {
            if (ShowDevelopmentGui != other.ShowDevelopmentGui || ScreenOrientation != other.ScreenOrientation
                                                               || CoreGuiDisabled.Count != other.CoreGuiDisabled.Count)
            {
                return false;
            }

            for (int index = 0; index < CoreGuiDisabled.Count; index++)
            {
                if (CoreGuiDisabled[index] != other.CoreGuiDisabled[index])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackageStarterGui other && Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = (ShowDevelopmentGui ? 1 : 0) ^ (ScreenOrientation * 397);
            for (int index = 0; index < CoreGuiDisabled.Count; index++)
            {
                hash = (hash * 31) ^ CoreGuiDisabled[index];
            }

            return hash;
        }
    }

    /// <summary>The datatype of one property in a <c>gui</c> block, which fixes its JSON form.</summary>
    internal enum RbxGuiValueKind
    {
        /// <summary>A JSON boolean.</summary>
        Bool,

        /// <summary>A JSON integer (an <c>int</c> property or an enum's Roblox number).</summary>
        Int,

        /// <summary>A JSON number.</summary>
        Float,

        /// <summary>[r, g, b].</summary>
        Color3,

        /// <summary>[scale, offset], the offset an integer.</summary>
        UDim,

        /// <summary>[x scale, x offset, y scale, y offset], the offsets integers.</summary>
        UDim2,

        /// <summary>[x, y].</summary>
        Vector2,

        /// <summary>A JSON string, never null.</summary>
        Text
    }

    /// <summary>One stored GUI property value; which members are meaningful depends on <see cref="Kind"/>.</summary>
    internal readonly struct RbxGuiValue : IEquatable<RbxGuiValue>
    {
        private RbxGuiValue(RbxGuiValueKind kind, bool flag, int first, int second, float x, float y, float z, string text)
        {
            Kind = kind;
            Flag = flag;
            First = first;
            Second = second;
            X = x;
            Y = y;
            Z = z;
            Text = text;
        }

        public RbxGuiValueKind Kind { get; }

        public bool Flag { get; }

        /// <summary>The integer, a UDim's offset or a UDim2's x offset.</summary>
        public int First { get; }

        /// <summary>A UDim2's y offset.</summary>
        public int Second { get; }

        /// <summary>The float, R, a Vector2's x, a UDim's scale or a UDim2's x scale.</summary>
        public float X { get; }

        /// <summary>G, a Vector2's y or a UDim2's y scale.</summary>
        public float Y { get; }

        /// <summary>B.</summary>
        public float Z { get; }

        public string Text { get; }

        public static RbxGuiValue OfBool(bool value)
        {
            return new RbxGuiValue(RbxGuiValueKind.Bool, value, 0, 0, 0f, 0f, 0f, null);
        }

        public static RbxGuiValue OfInt(int value)
        {
            return new RbxGuiValue(RbxGuiValueKind.Int, false, value, 0, 0f, 0f, 0f, null);
        }

        public static RbxGuiValue OfFloat(float value)
        {
            return new RbxGuiValue(RbxGuiValueKind.Float, false, 0, 0, Positive(value), 0f, 0f, null);
        }

        public static RbxGuiValue OfColor(RbxColor3 value)
        {
            return new RbxGuiValue(RbxGuiValueKind.Color3, false, 0, 0, Positive(value.R), Positive(value.G),
                Positive(value.B), null);
        }

        public static RbxGuiValue OfUDim(RbxUDim value)
        {
            return new RbxGuiValue(RbxGuiValueKind.UDim, false, value.Offset, 0, Positive(value.Scale), 0f, 0f, null);
        }

        public static RbxGuiValue OfUDim2(RbxUDim2 value)
        {
            return new RbxGuiValue(RbxGuiValueKind.UDim2, false, value.X.Offset, value.Y.Offset,
                Positive(value.X.Scale), Positive(value.Y.Scale), 0f, null);
        }

        public static RbxGuiValue OfVector2(RbxVector2 value)
        {
            return new RbxGuiValue(RbxGuiValueKind.Vector2, false, 0, 0, Positive(value.X), Positive(value.Y), 0f, null);
        }

        public static RbxGuiValue OfText(string value)
        {
            return new RbxGuiValue(RbxGuiValueKind.Text, false, 0, 0, 0f, 0f, 0f, value ?? string.Empty);
        }

        public RbxColor3 AsColor => new(X, Y, Z);

        public RbxUDim AsUDim => new(X, First);

        public RbxUDim2 AsUDim2 => new(X, First, Y, Second);

        public RbxVector2 AsVector2 => new(X, Y);

        public object ToObject()
        {
            switch (Kind)
            {
                case RbxGuiValueKind.Bool:
                    return Flag;
                case RbxGuiValueKind.Int:
                    return First;
                case RbxGuiValueKind.Float:
                    return X;
                case RbxGuiValueKind.Color3:
                    return AsColor;
                case RbxGuiValueKind.UDim:
                    return AsUDim;
                case RbxGuiValueKind.UDim2:
                    return AsUDim2;
                case RbxGuiValueKind.Vector2:
                    return AsVector2;
                default:
                    return Text;
            }
        }

        public bool Equals(RbxGuiValue other)
        {
            return Kind == other.Kind && Flag == other.Flag && First == other.First && Second == other.Second
                   && X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z)
                   && string.Equals(Text, other.Text, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxGuiValue other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)Combine(0UL, this));
        }

        /// <summary>Folds <paramref name="value"/> into an FNV-style running hash, without allocating.</summary>
        public static ulong Combine(ulong hash, in RbxGuiValue value)
        {
            hash = Mix(hash, (ulong)value.Kind);
            hash = Mix(hash, value.Flag ? 1UL : 0UL);
            hash = Mix(hash, unchecked((uint)value.First));
            hash = Mix(hash, unchecked((uint)value.Second));
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value.X)));
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value.Y)));
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value.Z)));
            if (value.Text != null)
            {
                for (int index = 0; index < value.Text.Length; index++)
                {
                    hash = Mix(hash, value.Text[index]);
                }

                hash = Mix(hash, (ulong)value.Text.Length);
            }

            return hash;
        }

        private static ulong Mix(ulong hash, ulong data)
        {
            return unchecked((hash ^ data) * 1099511628211UL);
        }

        /// <summary>
        /// WHY negative zero becomes zero: every GUI setter compares with <c>==</c>, so restoring -0 over a default of
        /// 0 is skipped as no change, and a world saved with -0 would re-save with 0.
        /// </summary>
        private static float Positive(float value)
        {
            return value == 0f ? 0f : value;
        }
    }

    /// <summary>
    /// The property table of the <c>gui</c> block: for every GUI class the package stores, which properties it
    /// carries, in what order, their JSON key and kind, the range the class's setter accepts, and how to read and
    /// set each through the class's public API. One table drives capture, restore, validation and both JSON writers,
    /// so they cannot disagree.
    /// </summary>
    internal static class RbxWorldPackageGuiSchema
    {
        [Flags]
        private enum Layer
        {
            Base2d = 1 << 0,
            LayerCollector = 1 << 1,
            ScreenGui = 1 << 2,
            GuiObject = 1 << 3,
            Frame = 1 << 4,
            GuiButton = 1 << 5,
            Text = 1 << 6,
            TextBox = 1 << 7,
            Image = 1 << 8,
            ImageButton = 1 << 9,
            ScrollingFrame = 1 << 10,
            Corner = 1 << 11,
            Padding = 1 << 12,
            Stroke = 1 << 13,
            Scale = 1 << 14,
            GridStyle = 1 << 15,
            ListLayout = 1 << 16,
            GridLayout = 1 << 17,
            AspectRatio = 1 << 18,
            SizeConstraint = 1 << 19
        }

        /// <summary>Which values a property accepts beyond its kind.</summary>
        private enum Range
        {
            /// <summary>Any value of the kind; floats and vectors finite.</summary>
            Any,

            /// <summary>An enum number between <see cref="Property.Minimum"/> and <see cref="Property.Maximum"/>.</summary>
            Enum,

            /// <summary><c>Enum.ScrollingDirection</c>: 1, 2 or 4.</summary>
            ScrollingDirection,

            /// <summary>A finite float above zero.</summary>
            Positive,

            /// <summary>A vector whose components are never NaN but may be infinite (UISizeConstraint).</summary>
            Unbounded
        }

        private sealed class Property
        {
            public Property(Layer layer, string key, string name, RbxGuiValueKind kind, Range range, int minimum, int maximum,
                Func<RbxInstance, RbxGuiValue> get, Action<RbxInstance, RbxGuiValue> set)
            {
                Layer = layer;
                Key = key;
                Name = name;
                Kind = kind;
                Range = range;
                Minimum = minimum;
                Maximum = maximum;
                Get = get;
                Set = set;
            }

            public Layer Layer { get; }

            public string Key { get; }

            public string Name { get; }

            public RbxGuiValueKind Kind { get; }

            public Range Range { get; }

            public int Minimum { get; }

            public int Maximum { get; }

            public Func<RbxInstance, RbxGuiValue> Get { get; }

            public Action<RbxInstance, RbxGuiValue> Set { get; }
        }

        private sealed class ClassSchema
        {
            public ClassSchema(string className, Type type, Property[] properties)
            {
                ClassName = className;
                Type = type;
                Properties = properties;
                Names = Array.ConvertAll(properties, property => property.Name);
                ByKey = new Dictionary<string, int>(StringComparer.Ordinal);
                ByName = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int index = 0; index < properties.Length; index++)
                {
                    ByKey.Add(properties[index].Key, index);
                    ByName.Add(properties[index].Name, index);
                }
            }

            public string ClassName { get; }

            public Type Type { get; }

            public Property[] Properties { get; }

            public string[] Names { get; }

            public Dictionary<string, int> ByKey { get; }

            public Dictionary<string, int> ByName { get; }
        }

        private static readonly Property[] Table = BuildTable();

        private static readonly Dictionary<string, ClassSchema> Classes = BuildClasses();

        private static readonly Dictionary<string, RbxGuiValueKind> KindsByKey = BuildKinds();

        /// <summary>The Roblox properties a script can set that the block leaves out on purpose, with the reason.</summary>
        internal static readonly IReadOnlyDictionary<string, string> Omitted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CursorPosition"] = "the caret of a TextBox, which the field on screen moves while the player types",
            ["CornerRadius"] = "an alias that writes all four corner radii, which the block stores one by one"
        };

        /// <summary>Every class whose nodes carry a block.</summary>
        internal static IEnumerable<string> ClassNames => Classes.Keys;

        public static bool IsGuiClass(string className)
        {
            return className != null && Classes.ContainsKey(className);
        }

        public static IReadOnlyList<string> PropertyNamesOf(string className)
        {
            return className != null && Classes.TryGetValue(className, out ClassSchema schema)
                ? schema.Names
                : Array.Empty<string>();
        }

        /// <summary>Every Roblox property name the table stores for any class (the drift guard reads it).</summary>
        internal static IEnumerable<string> AllPropertyNames()
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (Property property in Table)
            {
                names.Add(property.Name);
            }

            return names;
        }

        public static int IndexOf(string className, string property)
        {
            return property != null && className != null && Classes.TryGetValue(className, out ClassSchema schema)
                                    && schema.ByName.TryGetValue(property, out int index)
                ? index
                : -1;
        }

        public static string KeyAt(string className, int index)
        {
            return Classes[className].Properties[index].Key;
        }

        /// <summary>The kind every class gives <paramref name="key"/>; false for a key no GUI class has.</summary>
        public static bool TryGetKind(string key, out RbxGuiValueKind kind)
        {
            return KindsByKey.TryGetValue(key, out kind);
        }

        /// <summary>True when <paramref name="key"/> may hold an infinite component (written as a quoted Infinity).</summary>
        public static bool AllowsInfinity(string key)
        {
            return string.Equals(key, "min_size", StringComparison.Ordinal)
                   || string.Equals(key, "max_size", StringComparison.Ordinal);
        }

        public static RbxWorldPackageGui Capture(RbxInstance instance)
        {
            if (!Classes.TryGetValue(instance.ClassName, out ClassSchema schema))
            {
                return null;
            }

            if (!schema.Type.IsInstanceOfType(instance))
            {
                throw new RbxWorldPackageException(
                    "GUI instance " + instance.Id.Value.ToString(CultureInfo.InvariantCulture) + " of class '"
                    + instance.ClassName + "' is not the engine's " + schema.Type.Name
                    + "; its properties cannot be read for the package.");
            }

            RbxGuiValue[] values = new RbxGuiValue[schema.Properties.Length];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = schema.Properties[index].Get(instance);
            }

            return new RbxWorldPackageGui(schema.ClassName, values);
        }

        /// <summary>Folds the durable GUI state of <paramref name="instance"/> into <paramref name="hash"/> without allocating.</summary>
        public static ulong Fold(ulong hash, RbxInstance instance)
        {
            if (!Classes.TryGetValue(instance.ClassName, out ClassSchema schema) || !schema.Type.IsInstanceOfType(instance))
            {
                return hash;
            }

            for (int index = 0; index < schema.Properties.Length; index++)
            {
                RbxGuiValue value = schema.Properties[index].Get(instance);
                hash = RbxGuiValue.Combine(hash, in value);
            }

            return hash;
        }

        public static void Apply(RbxWorldPackageGui gui, RbxInstance instance)
        {
            if (!Classes.TryGetValue(gui.ClassName, out ClassSchema schema)
                || !string.Equals(instance.ClassName, gui.ClassName, StringComparison.Ordinal)
                || !schema.Type.IsInstanceOfType(instance))
            {
                throw new RbxWorldPackageException(
                    "A gui block of class '" + gui.ClassName + "' cannot be put on instance "
                    + instance.Id.Value.ToString(CultureInfo.InvariantCulture) + " of class '" + instance.ClassName + "'.");
            }

            for (int index = 0; index < schema.Properties.Length; index++)
            {
                schema.Properties[index].Set(instance, gui.ValueAt(index));
            }
        }

        /// <summary>
        /// Builds a block from the key/value pairs the reader parsed; refuses a class without a block, a key the class
        /// does not carry, a repeated key and a missing one, naming it.
        /// </summary>
        public static RbxWorldPackageGui FromEntries(string className, ulong instanceId, IReadOnlyList<KeyValuePair<string, RbxGuiValue>> entries)
        {
            string node = "Instance " + instanceId.ToString(CultureInfo.InvariantCulture);
            if (className == null || !Classes.TryGetValue(className, out ClassSchema schema))
            {
                throw new RbxWorldPackageException(
                    node + " carries a gui block, but its class '" + className + "' is not a GUI class the package stores.");
            }

            RbxGuiValue[] values = new RbxGuiValue[schema.Properties.Length];
            bool[] seen = new bool[values.Length];
            foreach (KeyValuePair<string, RbxGuiValue> entry in entries)
            {
                if (!schema.ByKey.TryGetValue(entry.Key, out int index))
                {
                    throw new RbxWorldPackageException(
                        node + " (" + className + ") carries gui key '" + entry.Key + "', which a " + className + " does not have.");
                }

                if (seen[index])
                {
                    throw new RbxWorldPackageException(node + " repeats gui key '" + entry.Key + "'.");
                }

                seen[index] = true;
                values[index] = entry.Value;
            }

            for (int index = 0; index < seen.Length; index++)
            {
                if (!seen[index])
                {
                    throw new RbxWorldPackageException(
                        node + " (" + className + ") has a gui block without '" + schema.Properties[index].Key
                        + "'; a writer stores every property of the class.");
                }
            }

            return new RbxWorldPackageGui(className, values);
        }

        /// <summary>Null when <paramref name="gui"/> is a block its class's setters accept as it is, else why not.</summary>
        public static string DescribeInvalid(RbxWorldPackageGui gui)
        {
            if (gui == null || gui.ClassName == null || !Classes.TryGetValue(gui.ClassName, out ClassSchema schema))
            {
                return "it is not a block of a GUI class the package stores";
            }

            if (gui.Count != schema.Properties.Length)
            {
                return "it does not hold every property of " + gui.ClassName;
            }

            for (int index = 0; index < schema.Properties.Length; index++)
            {
                Property property = schema.Properties[index];
                RbxGuiValue value = gui.ValueAt(index);
                if (value.Kind != property.Kind)
                {
                    return property.Key + " is not a " + property.Kind;
                }

                string problem = DescribeOutOfRange(property, value);
                if (problem != null)
                {
                    return property.Key + " " + problem;
                }
            }

            return null;
        }

        private static string DescribeOutOfRange(Property property, RbxGuiValue value)
        {
            switch (property.Kind)
            {
                case RbxGuiValueKind.Int:
                    if (property.Range == Range.Enum && (value.First < property.Minimum || value.First > property.Maximum))
                    {
                        return "is " + value.First.ToString(CultureInfo.InvariantCulture) + ", outside the enum's items "
                               + property.Minimum.ToString(CultureInfo.InvariantCulture) + " to "
                               + property.Maximum.ToString(CultureInfo.InvariantCulture);
                    }

                    if (property.Range == Range.ScrollingDirection && value.First != 1 && value.First != 2 && value.First != 4)
                    {
                        return "is " + value.First.ToString(CultureInfo.InvariantCulture) + "; Enum.ScrollingDirection is 1, 2 or 4";
                    }

                    return null;
                case RbxGuiValueKind.Float:
                    if (!IsFinite(value.X))
                    {
                        return "is not finite";
                    }

                    return property.Range == Range.Positive && value.X <= 0f ? "must be above 0" : null;
                case RbxGuiValueKind.Color3:
                    return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z) ? null : "is not finite";
                case RbxGuiValueKind.UDim:
                case RbxGuiValueKind.UDim2:
                    return IsFinite(value.X) && IsFinite(value.Y) ? null : "has a scale that is not finite";
                case RbxGuiValueKind.Vector2:
                    if (property.Range == Range.Unbounded)
                    {
                        return float.IsNaN(value.X) || float.IsNaN(value.Y) ? "has a NaN component" : null;
                    }

                    return IsFinite(value.X) && IsFinite(value.Y) ? null : "is not finite";
                case RbxGuiValueKind.Text:
                    return value.Text == null ? "is null" : null;
                default:
                    return null;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Dictionary<string, ClassSchema> BuildClasses()
        {
            Dictionary<string, ClassSchema> classes = new(StringComparer.Ordinal);
            Add(classes, "ScreenGui", typeof(RbxScreenGui), Layer.Base2d | Layer.LayerCollector | Layer.ScreenGui);
            Add(classes, "Frame", typeof(RbxFrame), Layer.Base2d | Layer.GuiObject | Layer.Frame);
            Add(classes, "TextLabel", typeof(RbxTextLabel), Layer.Base2d | Layer.GuiObject | Layer.Text);
            Add(classes, "TextButton", typeof(RbxTextButton), Layer.Base2d | Layer.GuiObject | Layer.GuiButton | Layer.Text);
            Add(classes, "TextBox", typeof(RbxTextBox), Layer.Base2d | Layer.GuiObject | Layer.Text | Layer.TextBox);
            Add(classes, "ImageLabel", typeof(RbxImageLabel), Layer.Base2d | Layer.GuiObject | Layer.Image);
            Add(classes, "ImageButton", typeof(RbxImageButton),
                Layer.Base2d | Layer.GuiObject | Layer.GuiButton | Layer.Image | Layer.ImageButton);
            Add(classes, "ScrollingFrame", typeof(RbxScrollingFrame), Layer.Base2d | Layer.GuiObject | Layer.ScrollingFrame);
            Add(classes, "UICorner", typeof(RbxUiCorner), Layer.Corner);
            Add(classes, "UIPadding", typeof(RbxUiPadding), Layer.Padding);
            Add(classes, "UIStroke", typeof(RbxUiStroke), Layer.Stroke);
            Add(classes, "UIScale", typeof(RbxUiScale), Layer.Scale);
            Add(classes, "UIListLayout", typeof(RbxUiListLayout), Layer.GridStyle | Layer.ListLayout);
            Add(classes, "UIGridLayout", typeof(RbxUiGridLayout), Layer.GridStyle | Layer.GridLayout);
            Add(classes, "UIAspectRatioConstraint", typeof(RbxUiAspectRatioConstraint), Layer.AspectRatio);
            Add(classes, "UISizeConstraint", typeof(RbxUiSizeConstraint), Layer.SizeConstraint);
            return classes;
        }

        private static void Add(Dictionary<string, ClassSchema> classes, string className, Type type, Layer layers)
        {
            List<Property> properties = new();
            foreach (Property property in Table)
            {
                if ((layers & property.Layer) != 0)
                {
                    properties.Add(property);
                }
            }

            classes.Add(className, new ClassSchema(className, type, properties.ToArray()));
        }

        private static Dictionary<string, RbxGuiValueKind> BuildKinds()
        {
            Dictionary<string, RbxGuiValueKind> kinds = new(StringComparer.Ordinal);
            foreach (Property property in Table)
            {
                if (kinds.TryGetValue(property.Key, out RbxGuiValueKind existing) && existing != property.Kind)
                {
                    throw new InvalidOperationException("gui key '" + property.Key + "' has two kinds.");
                }

                kinds[property.Key] = property.Kind;
            }

            return kinds;
        }

        private static Property Bool(Layer layer, string key, string name, Func<RbxInstance, bool> get,
            Action<RbxInstance, bool> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Bool, Range.Any, 0, 0,
                instance => RbxGuiValue.OfBool(get(instance)), (instance, value) => set(instance, value.Flag));
        }

        private static Property Int(Layer layer, string key, string name, Func<RbxInstance, int> get,
            Action<RbxInstance, int> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Int, Range.Any, 0, 0,
                instance => RbxGuiValue.OfInt(get(instance)), (instance, value) => set(instance, value.First));
        }

        private static Property Enum(Layer layer, string key, string name, int maximum, Func<RbxInstance, int> get,
            Action<RbxInstance, int> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Int, Range.Enum, 0, maximum,
                instance => RbxGuiValue.OfInt(get(instance)), (instance, value) => set(instance, value.First));
        }

        private static Property Float(Layer layer, string key, string name, Func<RbxInstance, float> get,
            Action<RbxInstance, double> set, Range range = Range.Any)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Float, range, 0, 0,
                instance => RbxGuiValue.OfFloat(get(instance)), (instance, value) => set(instance, value.X));
        }

        private static Property Color(Layer layer, string key, string name, Func<RbxInstance, RbxColor3> get,
            Action<RbxInstance, RbxColor3> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Color3, Range.Any, 0, 0,
                instance => RbxGuiValue.OfColor(get(instance)), (instance, value) => set(instance, value.AsColor));
        }

        private static Property UDim(Layer layer, string key, string name, Func<RbxInstance, RbxUDim> get,
            Action<RbxInstance, RbxUDim> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.UDim, Range.Any, 0, 0,
                instance => RbxGuiValue.OfUDim(get(instance)), (instance, value) => set(instance, value.AsUDim));
        }

        private static Property UDim2(Layer layer, string key, string name, Func<RbxInstance, RbxUDim2> get,
            Action<RbxInstance, RbxUDim2> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.UDim2, Range.Any, 0, 0,
                instance => RbxGuiValue.OfUDim2(get(instance)), (instance, value) => set(instance, value.AsUDim2));
        }

        private static Property Vector(Layer layer, string key, string name, Func<RbxInstance, RbxVector2> get,
            Action<RbxInstance, RbxVector2> set, Range range = Range.Any)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Vector2, range, 0, 0,
                instance => RbxGuiValue.OfVector2(get(instance)), (instance, value) => set(instance, value.AsVector2));
        }

        private static Property Text(Layer layer, string key, string name, Func<RbxInstance, string> get,
            Action<RbxInstance, string> set)
        {
            return new Property(layer, key, name, RbxGuiValueKind.Text, Range.Any, 0, 0,
                instance => RbxGuiValue.OfText(get(instance)), (instance, value) => set(instance, value.Text));
        }

        private static RbxGuiObject G(RbxInstance instance) => (RbxGuiObject)instance;

        private static RbxGuiText T(RbxInstance instance) => ((IRbxGuiTextObject)instance).TextState;

        private static RbxGuiImage I(RbxInstance instance) => ((IRbxGuiImageObject)instance).ImageState;

        private static Property[] BuildTable()
        {
            return new[]
            {
                Bool(Layer.Base2d, "auto_localize", "AutoLocalize",
                    i => ((RbxGuiBase2d)i).AutoLocalize, (i, v) => ((RbxGuiBase2d)i).SetAutoLocalize(v)),

                Bool(Layer.LayerCollector, "enabled", "Enabled",
                    i => ((RbxLayerCollector)i).Enabled, (i, v) => ((RbxLayerCollector)i).SetEnabled(v)),
                Bool(Layer.LayerCollector, "reset_on_spawn", "ResetOnSpawn",
                    i => ((RbxLayerCollector)i).ResetOnSpawn, (i, v) => ((RbxLayerCollector)i).SetResetOnSpawn(v)),
                Enum(Layer.LayerCollector, "z_index_behavior", "ZIndexBehavior", 1,
                    i => (int)((RbxLayerCollector)i).ZIndexBehavior,
                    (i, v) => ((RbxLayerCollector)i).SetZIndexBehavior((RbxZIndexBehavior)v)),

                Int(Layer.ScreenGui, "display_order", "DisplayOrder",
                    i => ((RbxScreenGui)i).DisplayOrder, (i, v) => ((RbxScreenGui)i).SetDisplayOrder(v)),
                Bool(Layer.ScreenGui, "ignore_gui_inset", "IgnoreGuiInset",
                    i => ((RbxScreenGui)i).IgnoreGuiInset, (i, v) => ((RbxScreenGui)i).SetIgnoreGuiInset(v)),
                Bool(Layer.ScreenGui, "clip_to_device_safe_area", "ClipToDeviceSafeArea",
                    i => ((RbxScreenGui)i).ClipToDeviceSafeArea, (i, v) => ((RbxScreenGui)i).SetClipToDeviceSafeArea(v)),
                Int(Layer.ScreenGui, "safe_area_compatibility", "SafeAreaCompatibility",
                    i => ((RbxScreenGui)i).SafeAreaCompatibility, (i, v) => ((RbxScreenGui)i).SetSafeAreaCompatibility(v)),
                Int(Layer.ScreenGui, "screen_insets", "ScreenInsets",
                    i => ((RbxScreenGui)i).ScreenInsets, (i, v) => ((RbxScreenGui)i).SetScreenInsets(v)),

                Bool(Layer.GuiObject, "active", "Active", i => G(i).Active, (i, v) => G(i).SetActive(v)),
                Vector(Layer.GuiObject, "anchor_point", "AnchorPoint", i => G(i).AnchorPoint, (i, v) => G(i).SetAnchorPoint(v)),
                Enum(Layer.GuiObject, "automatic_size", "AutomaticSize", 3,
                    i => (int)G(i).AutomaticSize, (i, v) => G(i).SetAutomaticSize((RbxAutomaticSize)v)),
                Color(Layer.GuiObject, "background_color3", "BackgroundColor3",
                    i => G(i).BackgroundColor3, (i, v) => G(i).SetBackgroundColor3(v)),
                Float(Layer.GuiObject, "background_transparency", "BackgroundTransparency",
                    i => G(i).BackgroundTransparency, (i, v) => G(i).SetBackgroundTransparency(v)),
                Color(Layer.GuiObject, "border_color3", "BorderColor3", i => G(i).BorderColor3, (i, v) => G(i).SetBorderColor3(v)),
                Enum(Layer.GuiObject, "border_mode", "BorderMode", 2,
                    i => (int)G(i).BorderMode, (i, v) => G(i).SetBorderMode((RbxBorderMode)v)),
                Int(Layer.GuiObject, "border_size_pixel", "BorderSizePixel",
                    i => G(i).BorderSizePixel, (i, v) => G(i).SetBorderSizePixel(v)),
                Bool(Layer.GuiObject, "clips_descendants", "ClipsDescendants",
                    i => G(i).ClipsDescendants, (i, v) => G(i).SetClipsDescendants(v)),
                Bool(Layer.GuiObject, "interactable", "Interactable", i => G(i).Interactable, (i, v) => G(i).SetInteractable(v)),
                Int(Layer.GuiObject, "layout_order", "LayoutOrder", i => G(i).LayoutOrder, (i, v) => G(i).SetLayoutOrder(v)),
                UDim2(Layer.GuiObject, "position", "Position", i => G(i).Position, (i, v) => G(i).SetPosition(v)),
                Float(Layer.GuiObject, "rotation", "Rotation", i => G(i).Rotation, (i, v) => G(i).SetRotation(v)),
                Bool(Layer.GuiObject, "selectable", "Selectable", i => G(i).Selectable, (i, v) => G(i).SetSelectable(v)),
                UDim2(Layer.GuiObject, "size", "Size", i => G(i).Size, (i, v) => G(i).SetSize(v)),
                Enum(Layer.GuiObject, "size_constraint", "SizeConstraint", 2,
                    i => (int)G(i).SizeConstraint, (i, v) => G(i).SetSizeConstraint((RbxSizeConstraint)v)),
                Bool(Layer.GuiObject, "visible", "Visible", i => G(i).Visible, (i, v) => G(i).SetVisible(v)),
                Int(Layer.GuiObject, "z_index", "ZIndex", i => G(i).ZIndex, (i, v) => G(i).SetZIndex(v)),

                Int(Layer.Frame, "style", "Style", i => ((RbxFrame)i).Style, (i, v) => ((RbxFrame)i).SetStyle(v)),

                Bool(Layer.GuiButton, "auto_button_color", "AutoButtonColor",
                    i => ((RbxGuiButton)i).AutoButtonColor, (i, v) => ((RbxGuiButton)i).SetAutoButtonColor(v)),
                Bool(Layer.GuiButton, "modal", "Modal", i => ((RbxGuiButton)i).Modal, (i, v) => ((RbxGuiButton)i).SetModal(v)),
                Bool(Layer.GuiButton, "selected", "Selected",
                    i => ((RbxGuiButton)i).Selected, (i, v) => ((RbxGuiButton)i).SetSelected(v)),
                Int(Layer.GuiButton, "style", "Style", i => ((RbxGuiButton)i).Style, (i, v) => ((RbxGuiButton)i).SetStyle(v)),

                Text(Layer.Text, "text", "Text", i => T(i).Text, (i, v) => T(i).SetText(v)),
                Color(Layer.Text, "text_color3", "TextColor3", i => T(i).TextColor3, (i, v) => T(i).SetTextColor3(v)),
                Float(Layer.Text, "text_size", "TextSize", i => T(i).TextSize, (i, v) => T(i).SetTextSize(v)),
                Float(Layer.Text, "text_transparency", "TextTransparency",
                    i => T(i).TextTransparency, (i, v) => T(i).SetTextTransparency(v)),
                Bool(Layer.Text, "text_scaled", "TextScaled", i => T(i).TextScaled, (i, v) => T(i).SetTextScaled(v)),
                Bool(Layer.Text, "text_wrapped", "TextWrapped", i => T(i).TextWrapped, (i, v) => T(i).SetTextWrapped(v)),
                Enum(Layer.Text, "text_x_alignment", "TextXAlignment", 2,
                    i => (int)T(i).TextXAlignment, (i, v) => T(i).SetTextXAlignment((RbxTextXAlignment)v)),
                Enum(Layer.Text, "text_y_alignment", "TextYAlignment", 2,
                    i => (int)T(i).TextYAlignment, (i, v) => T(i).SetTextYAlignment((RbxTextYAlignment)v)),
                Enum(Layer.Text, "text_truncate", "TextTruncate", 2,
                    i => (int)T(i).TextTruncate, (i, v) => T(i).SetTextTruncate((RbxTextTruncate)v)),
                Int(Layer.Text, "font", "Font", i => T(i).Font, (i, v) => T(i).SetFont(v)),
                Bool(Layer.Text, "rich_text", "RichText", i => T(i).RichText, (i, v) => T(i).SetRichText(v)),
                Color(Layer.Text, "text_stroke_color3", "TextStrokeColor3",
                    i => T(i).TextStrokeColor3, (i, v) => T(i).SetTextStrokeColor3(v)),
                Float(Layer.Text, "text_stroke_transparency", "TextStrokeTransparency",
                    i => T(i).TextStrokeTransparency, (i, v) => T(i).SetTextStrokeTransparency(v)),
                Float(Layer.Text, "line_height", "LineHeight", i => T(i).LineHeight, (i, v) => T(i).SetLineHeight(v)),
                Int(Layer.Text, "max_visible_graphemes", "MaxVisibleGraphemes",
                    i => T(i).MaxVisibleGraphemes, (i, v) => T(i).SetMaxVisibleGraphemes(v)),
                Int(Layer.Text, "text_direction", "TextDirection", i => T(i).TextDirection, (i, v) => T(i).SetTextDirection(v)),

                Bool(Layer.TextBox, "clear_text_on_focus", "ClearTextOnFocus",
                    i => ((RbxTextBox)i).ClearTextOnFocus, (i, v) => ((RbxTextBox)i).SetClearTextOnFocus(v)),
                Bool(Layer.TextBox, "multi_line", "MultiLine",
                    i => ((RbxTextBox)i).MultiLine, (i, v) => ((RbxTextBox)i).SetMultiLine(v)),
                Text(Layer.TextBox, "placeholder_text", "PlaceholderText",
                    i => ((RbxTextBox)i).PlaceholderText, (i, v) => ((RbxTextBox)i).SetPlaceholderText(v)),
                Color(Layer.TextBox, "placeholder_color3", "PlaceholderColor3",
                    i => ((RbxTextBox)i).PlaceholderColor3, (i, v) => ((RbxTextBox)i).SetPlaceholderColor3(v)),
                Bool(Layer.TextBox, "text_editable", "TextEditable",
                    i => ((RbxTextBox)i).TextEditable, (i, v) => ((RbxTextBox)i).SetTextEditable(v)),
                Bool(Layer.TextBox, "show_native_input", "ShowNativeInput",
                    i => ((RbxTextBox)i).ShowNativeInput, (i, v) => ((RbxTextBox)i).SetShowNativeInput(v)),

                Text(Layer.Image, "image", "Image", i => I(i).Image, (i, v) => I(i).SetImage(v)),
                Color(Layer.Image, "image_color3", "ImageColor3", i => I(i).ImageColor3, (i, v) => I(i).SetImageColor3(v)),
                Float(Layer.Image, "image_transparency", "ImageTransparency",
                    i => I(i).ImageTransparency, (i, v) => I(i).SetImageTransparency(v)),
                Enum(Layer.Image, "scale_type", "ScaleType", 4,
                    i => (int)I(i).ScaleType, (i, v) => I(i).SetScaleType((RbxScaleType)v)),
                Vector(Layer.Image, "image_rect_offset", "ImageRectOffset",
                    i => I(i).ImageRectOffset, (i, v) => I(i).SetImageRectOffset(v)),
                Vector(Layer.Image, "image_rect_size", "ImageRectSize",
                    i => I(i).ImageRectSize, (i, v) => I(i).SetImageRectSize(v)),
                Float(Layer.Image, "slice_scale", "SliceScale", i => I(i).SliceScale, (i, v) => I(i).SetSliceScale(v)),
                UDim2(Layer.Image, "tile_size", "TileSize", i => I(i).TileSize, (i, v) => I(i).SetTileSize(v)),
                Int(Layer.Image, "resample_mode", "ResampleMode", i => I(i).ResampleMode, (i, v) => I(i).SetResampleMode(v)),

                Text(Layer.ImageButton, "hover_image", "HoverImage",
                    i => ((RbxImageButton)i).HoverImage, (i, v) => ((RbxImageButton)i).SetHoverImage(v)),
                Text(Layer.ImageButton, "pressed_image", "PressedImage",
                    i => ((RbxImageButton)i).PressedImage, (i, v) => ((RbxImageButton)i).SetPressedImage(v)),

                UDim2(Layer.ScrollingFrame, "canvas_size", "CanvasSize",
                    i => ((RbxScrollingFrame)i).CanvasSize, (i, v) => ((RbxScrollingFrame)i).SetCanvasSize(v)),
                Vector(Layer.ScrollingFrame, "canvas_position", "CanvasPosition",
                    i => ((RbxScrollingFrame)i).CanvasPosition, (i, v) => ((RbxScrollingFrame)i).SetCanvasPosition(v)),
                Int(Layer.ScrollingFrame, "scroll_bar_thickness", "ScrollBarThickness",
                    i => ((RbxScrollingFrame)i).ScrollBarThickness, (i, v) => ((RbxScrollingFrame)i).SetScrollBarThickness(v)),
                new Property(Layer.ScrollingFrame, "scrolling_direction", "ScrollingDirection", RbxGuiValueKind.Int,
                    Range.ScrollingDirection, 0, 0,
                    i => RbxGuiValue.OfInt((int)((RbxScrollingFrame)i).ScrollingDirection),
                    (i, v) => ((RbxScrollingFrame)i).SetScrollingDirection((RbxScrollingDirection)v.First)),
                Bool(Layer.ScrollingFrame, "scrolling_enabled", "ScrollingEnabled",
                    i => ((RbxScrollingFrame)i).ScrollingEnabled, (i, v) => ((RbxScrollingFrame)i).SetScrollingEnabled(v)),
                Color(Layer.ScrollingFrame, "scroll_bar_image_color3", "ScrollBarImageColor3",
                    i => ((RbxScrollingFrame)i).ScrollBarImageColor3,
                    (i, v) => ((RbxScrollingFrame)i).SetScrollBarImageColor3(v)),
                Float(Layer.ScrollingFrame, "scroll_bar_image_transparency", "ScrollBarImageTransparency",
                    i => ((RbxScrollingFrame)i).ScrollBarImageTransparency,
                    (i, v) => ((RbxScrollingFrame)i).SetScrollBarImageTransparency(v)),
                Enum(Layer.ScrollingFrame, "automatic_canvas_size", "AutomaticCanvasSize", 3,
                    i => (int)((RbxScrollingFrame)i).AutomaticCanvasSize,
                    (i, v) => ((RbxScrollingFrame)i).SetAutomaticCanvasSize((RbxAutomaticSize)v)),
                Int(Layer.ScrollingFrame, "elastic_behavior", "ElasticBehavior",
                    i => ((RbxScrollingFrame)i).ElasticBehavior, (i, v) => ((RbxScrollingFrame)i).SetElasticBehavior(v)),
                Int(Layer.ScrollingFrame, "vertical_scroll_bar_position", "VerticalScrollBarPosition",
                    i => ((RbxScrollingFrame)i).VerticalScrollBarPosition,
                    (i, v) => ((RbxScrollingFrame)i).SetVerticalScrollBarPosition(v)),
                Int(Layer.ScrollingFrame, "horizontal_scroll_bar_inset", "HorizontalScrollBarInset",
                    i => ((RbxScrollingFrame)i).HorizontalScrollBarInset,
                    (i, v) => ((RbxScrollingFrame)i).SetHorizontalScrollBarInset(v)),
                Int(Layer.ScrollingFrame, "vertical_scroll_bar_inset", "VerticalScrollBarInset",
                    i => ((RbxScrollingFrame)i).VerticalScrollBarInset,
                    (i, v) => ((RbxScrollingFrame)i).SetVerticalScrollBarInset(v)),

                UDim(Layer.Corner, "top_left_radius", "TopLeftRadius",
                    i => ((RbxUiCorner)i).TopLeftRadius, (i, v) => ((RbxUiCorner)i).SetTopLeftRadius(v)),
                UDim(Layer.Corner, "top_right_radius", "TopRightRadius",
                    i => ((RbxUiCorner)i).TopRightRadius, (i, v) => ((RbxUiCorner)i).SetTopRightRadius(v)),
                UDim(Layer.Corner, "bottom_right_radius", "BottomRightRadius",
                    i => ((RbxUiCorner)i).BottomRightRadius, (i, v) => ((RbxUiCorner)i).SetBottomRightRadius(v)),
                UDim(Layer.Corner, "bottom_left_radius", "BottomLeftRadius",
                    i => ((RbxUiCorner)i).BottomLeftRadius, (i, v) => ((RbxUiCorner)i).SetBottomLeftRadius(v)),

                UDim(Layer.Padding, "padding_left", "PaddingLeft",
                    i => ((RbxUiPadding)i).PaddingLeft, (i, v) => ((RbxUiPadding)i).SetPaddingLeft(v)),
                UDim(Layer.Padding, "padding_top", "PaddingTop",
                    i => ((RbxUiPadding)i).PaddingTop, (i, v) => ((RbxUiPadding)i).SetPaddingTop(v)),
                UDim(Layer.Padding, "padding_right", "PaddingRight",
                    i => ((RbxUiPadding)i).PaddingRight, (i, v) => ((RbxUiPadding)i).SetPaddingRight(v)),
                UDim(Layer.Padding, "padding_bottom", "PaddingBottom",
                    i => ((RbxUiPadding)i).PaddingBottom, (i, v) => ((RbxUiPadding)i).SetPaddingBottom(v)),

                Enum(Layer.Stroke, "apply_stroke_mode", "ApplyStrokeMode", 1,
                    i => (int)((RbxUiStroke)i).ApplyStrokeMode,
                    (i, v) => ((RbxUiStroke)i).SetApplyStrokeMode((RbxApplyStrokeMode)v)),
                Color(Layer.Stroke, "color", "Color", i => ((RbxUiStroke)i).Color, (i, v) => ((RbxUiStroke)i).SetColor(v)),
                Bool(Layer.Stroke, "enabled", "Enabled", i => ((RbxUiStroke)i).Enabled, (i, v) => ((RbxUiStroke)i).SetEnabled(v)),
                Int(Layer.Stroke, "line_join_mode", "LineJoinMode",
                    i => ((RbxUiStroke)i).LineJoinMode, (i, v) => ((RbxUiStroke)i).SetLineJoinMode(v)),
                Float(Layer.Stroke, "thickness", "Thickness",
                    i => ((RbxUiStroke)i).Thickness, (i, v) => ((RbxUiStroke)i).SetThickness(v)),
                Float(Layer.Stroke, "transparency", "Transparency",
                    i => ((RbxUiStroke)i).Transparency, (i, v) => ((RbxUiStroke)i).SetTransparency(v)),
                Int(Layer.Stroke, "z_index", "ZIndex", i => ((RbxUiStroke)i).ZIndex, (i, v) => ((RbxUiStroke)i).SetZIndex(v)),
                Enum(Layer.Stroke, "border_stroke_position", "BorderStrokePosition", 2,
                    i => (int)((RbxUiStroke)i).BorderStrokePosition,
                    (i, v) => ((RbxUiStroke)i).SetBorderStrokePosition((RbxBorderStrokePosition)v)),
                UDim(Layer.Stroke, "border_offset", "BorderOffset",
                    i => ((RbxUiStroke)i).BorderOffset, (i, v) => ((RbxUiStroke)i).SetBorderOffset(v)),
                Int(Layer.Stroke, "stroke_sizing_mode", "StrokeSizingMode",
                    i => ((RbxUiStroke)i).StrokeSizingMode, (i, v) => ((RbxUiStroke)i).SetStrokeSizingMode(v)),

                Float(Layer.Scale, "scale", "Scale", i => ((RbxUiScale)i).Scale, (i, v) => ((RbxUiScale)i).SetScale(v)),

                Enum(Layer.GridStyle, "fill_direction", "FillDirection", 1,
                    i => (int)((RbxUiGridStyleLayout)i).FillDirection,
                    (i, v) => ((RbxUiGridStyleLayout)i).SetFillDirection((RbxFillDirection)v)),
                Enum(Layer.GridStyle, "horizontal_alignment", "HorizontalAlignment", 2,
                    i => (int)((RbxUiGridStyleLayout)i).HorizontalAlignment,
                    (i, v) => ((RbxUiGridStyleLayout)i).SetHorizontalAlignment((RbxHorizontalAlignment)v)),
                Enum(Layer.GridStyle, "vertical_alignment", "VerticalAlignment", 2,
                    i => (int)((RbxUiGridStyleLayout)i).VerticalAlignment,
                    (i, v) => ((RbxUiGridStyleLayout)i).SetVerticalAlignment((RbxVerticalAlignment)v)),
                Enum(Layer.GridStyle, "sort_order", "SortOrder", 2,
                    i => (int)((RbxUiGridStyleLayout)i).SortOrder,
                    (i, v) => ((RbxUiGridStyleLayout)i).SetSortOrder((RbxGuiSortOrder)v)),

                UDim(Layer.ListLayout, "padding", "Padding",
                    i => ((RbxUiListLayout)i).Padding, (i, v) => ((RbxUiListLayout)i).SetPadding(v)),
                Bool(Layer.ListLayout, "wraps", "Wraps", i => ((RbxUiListLayout)i).Wraps, (i, v) => ((RbxUiListLayout)i).SetWraps(v)),
                Int(Layer.ListLayout, "horizontal_flex", "HorizontalFlex",
                    i => ((RbxUiListLayout)i).HorizontalFlex, (i, v) => ((RbxUiListLayout)i).SetHorizontalFlex(v)),
                Int(Layer.ListLayout, "vertical_flex", "VerticalFlex",
                    i => ((RbxUiListLayout)i).VerticalFlex, (i, v) => ((RbxUiListLayout)i).SetVerticalFlex(v)),
                Int(Layer.ListLayout, "item_line_alignment", "ItemLineAlignment",
                    i => ((RbxUiListLayout)i).ItemLineAlignment, (i, v) => ((RbxUiListLayout)i).SetItemLineAlignment(v)),

                UDim2(Layer.GridLayout, "cell_size", "CellSize",
                    i => ((RbxUiGridLayout)i).CellSize, (i, v) => ((RbxUiGridLayout)i).SetCellSize(v)),
                UDim2(Layer.GridLayout, "cell_padding", "CellPadding",
                    i => ((RbxUiGridLayout)i).CellPadding, (i, v) => ((RbxUiGridLayout)i).SetCellPadding(v)),
                Int(Layer.GridLayout, "fill_direction_max_cells", "FillDirectionMaxCells",
                    i => ((RbxUiGridLayout)i).FillDirectionMaxCells, (i, v) => ((RbxUiGridLayout)i).SetFillDirectionMaxCells(v)),
                Enum(Layer.GridLayout, "start_corner", "StartCorner", 3,
                    i => (int)((RbxUiGridLayout)i).StartCorner,
                    (i, v) => ((RbxUiGridLayout)i).SetStartCorner((RbxStartCorner)v)),

                Float(Layer.AspectRatio, "aspect_ratio", "AspectRatio",
                    i => ((RbxUiAspectRatioConstraint)i).AspectRatio,
                    (i, v) => ((RbxUiAspectRatioConstraint)i).SetAspectRatio(v), Range.Positive),
                Enum(Layer.AspectRatio, "aspect_type", "AspectType", 1,
                    i => (int)((RbxUiAspectRatioConstraint)i).AspectType,
                    (i, v) => ((RbxUiAspectRatioConstraint)i).SetAspectType((RbxAspectType)v)),
                Enum(Layer.AspectRatio, "dominant_axis", "DominantAxis", 1,
                    i => (int)((RbxUiAspectRatioConstraint)i).DominantAxis,
                    (i, v) => ((RbxUiAspectRatioConstraint)i).SetDominantAxis((RbxDominantAxis)v)),

                Vector(Layer.SizeConstraint, "min_size", "MinSize",
                    i => ((RbxUiSizeConstraint)i).MinSize, (i, v) => ((RbxUiSizeConstraint)i).SetMinSize(v), Range.Unbounded),
                Vector(Layer.SizeConstraint, "max_size", "MaxSize",
                    i => ((RbxUiSizeConstraint)i).MaxSize, (i, v) => ((RbxUiSizeConstraint)i).SetMaxSize(v), Range.Unbounded)
            };
        }
    }
}
