namespace CoreAI.Scripting
{
    /// <summary>
    /// Single authority for CLR &lt;-&gt; script value conversion. Script values travel through the seam as
    /// opaque <see cref="object"/> handles (the engine adapter's boxed VM value); host values are plain
    /// CLR nil/bool/double/string plus dictionaries and lists. Consolidates the conversions that were
    /// previously scattered across the registry, the mod runtime, and the logic slots, so a second engine
    /// reimplements exactly one class.
    /// </summary>
    public interface IValueMarshaller
    {
        /// <summary>
        /// Rich host-to-script conversion: scalars, <c>IDictionary</c> (to table), <c>IEnumerable</c>
        /// (to 1-based array table), delegates/functions and already-script values (pass-through).
        /// </summary>
        object ToScriptValue(object hostValue);

        /// <summary>
        /// Scalar host-to-script conversion used for call arguments (nil/bool/number/string fast path;
        /// script values pass through; everything else is handed to the engine's default object wrap).
        /// </summary>
        object ToScriptArgument(object hostValue);

        /// <summary>
        /// Script-to-host conversion: nil to null, boolean/number/string to bool/double/string; other
        /// kinds surface as the engine's underlying object (opaque to the host).
        /// </summary>
        object ToHostValue(object scriptValue);

        /// <summary>
        /// Deep-copies a script value into a state-independent representation
        /// (nil/boolean/number/string plus tables up to <paramref name="maxTableDepth"/> levels).
        /// Functions and live references are rejected, so no state can leak across the boundary.
        /// </summary>
        object ToPortable(object scriptValue, int maxTableDepth);

        /// <summary>Rebuilds a <see cref="ToPortable"/> value as a fresh script value (new tables).</summary>
        object FromPortable(object portable);

        /// <summary>Classifies a raw script value (also tolerates plain host scalars).</summary>
        ScriptValueKind GetKind(object scriptValue);

        /// <summary>Human-readable rendering matching the engine's <c>tostring</c> semantics.</summary>
        string Describe(object scriptValue);
    }
}
