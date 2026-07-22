namespace CoreAI.Scripting
{
    /// <summary>
    /// Engine-neutral view of one var-args script call into a host callback. Replaces direct exposure of
    /// the VM's execution context: arguments surface as opaque raw script values plus typed accessors
    /// whose coercions match the typed-delegate argument coercion (nil maps to null/0/false).
    /// </summary>
    public abstract class ScriptCallContext
    {
        /// <summary>The state the call originates from (usable with <see cref="IScriptExecutionGuard"/>).</summary>
        public abstract IScriptState State { get; }

        /// <summary>The engine's marshaller, for kind checks and conversions on raw arguments.</summary>
        public abstract IValueMarshaller Marshaller { get; }

        /// <summary>Number of arguments the script passed.</summary>
        public abstract int ArgumentCount { get; }

        /// <summary>True when the script passed an argument at this position.</summary>
        public abstract bool HasArgument(int index);

        /// <summary>Raw script value at this position (nil when absent).</summary>
        public abstract object GetArgument(int index);

        /// <summary>String argument; null for nil/absent, engine cast error for other kinds.</summary>
        public abstract string GetString(int index);

        /// <summary>Number argument; 0 for nil/absent, engine cast error for other kinds.</summary>
        public abstract double GetNumber(int index);

        /// <summary>Boolean argument; false for nil/absent, engine cast error for other kinds.</summary>
        public abstract bool GetBoolean(int index);

        /// <summary>Table argument as a neutral view; null for nil/absent, engine cast error otherwise.</summary>
        public abstract IScriptTable GetTable(int index);

        /// <summary>Kind of the raw argument at this position (<see cref="ScriptValueKind.Nil"/> when absent).</summary>
        public abstract ScriptValueKind GetKind(int index);

        /// <summary>Argument rendered with the engine's <c>tostring</c> semantics.</summary>
        public abstract string DescribeArgument(int index);
    }
}
