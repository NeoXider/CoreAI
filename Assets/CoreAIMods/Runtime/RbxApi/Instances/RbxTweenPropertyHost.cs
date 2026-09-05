namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// One property sample read through the host: whether the member exists, whether its
    /// current type is tweenable, the current value boxed for interpolation (double,
    /// <c>RbxVector3</c>, <c>RbxCFrame</c>, <c>RbxColor3</c>, <c>RbxUDim2</c>), and the
    /// Roblox type name used in error messages ("number", "Vector3", ...).
    /// </summary>
    public readonly struct TweenPropertySample
    {
        public TweenPropertySample(bool found, bool supported, object value, string typeName)
        {
            Found = found;
            Supported = supported;
            Value = value;
            TypeName = typeName;
        }

        /// <summary>False when the member does not exist on the instance.</summary>
        public bool Found { get; }

        /// <summary>False when the member exists but its type is not tweenable.</summary>
        public bool Supported { get; }

        /// <summary>Current value boxed for interpolation; null unless found and supported.</summary>
        public object Value { get; }

        /// <summary>Roblox type name of the current value ("number", "Vector3", ...).</summary>
        public string TypeName { get; }

        /// <summary>Sample for an unknown member.</summary>
        public static TweenPropertySample Unknown()
        {
            return new TweenPropertySample(false, false, null, "nil");
        }

        /// <summary>Sample for a known member whose type cannot be tweened yet.</summary>
        public static TweenPropertySample Unsupported(string typeName)
        {
            return new TweenPropertySample(true, false, null, typeName);
        }

        /// <summary>Sample for a live tweenable value.</summary>
        public static TweenPropertySample SupportedValue(object value, string typeName)
        {
            return new TweenPropertySample(true, true, value, typeName);
        }
    }

    /// <summary>
    /// Engine-free property IO behind the tween driver. The Lua bindings implement this over
    /// the part-property sink and the value objects, so the engine-free service never touches
    /// UnityEngine types (D2) while per-frame writes still flow through the same setters —
    /// including their revision behavior — that direct Lua assignments use.
    /// </summary>
    public interface ITweenPropertyHost
    {
        /// <summary>Reads the current value of a tweenable property, boxed for interpolation.</summary>
        TweenPropertySample Sample(RbxInstance target, string propertyName);

        /// <summary>
        /// Writes an interpolated box through the same setters the Lua layer uses (part sink
        /// setters advance the revision; value-object setters fire Changed on real changes).
        /// </summary>
        void Write(RbxInstance target, string propertyName, object value);
    }
}
