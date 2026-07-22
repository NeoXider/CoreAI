using System;
using System.Collections.Generic;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Values returned to the script from a var-args host callback. Each value may be a plain host value
    /// (converted via <see cref="IValueMarshaller.ToScriptValue"/>) or a raw script value obtained from
    /// <see cref="ScriptCallContext.GetArgument"/> (passed through unchanged).
    /// </summary>
    public readonly struct ScriptCallResult
    {
        private static readonly object[] None = Array.Empty<object>();

        private readonly object[] _values;

        private ScriptCallResult(object[] values)
        {
            _values = values;
        }

        /// <summary>A result with no return values.</summary>
        public static ScriptCallResult Empty => new(None);

        /// <summary>Builds a result returning the given values in order.</summary>
        public static ScriptCallResult Return(params object[] values)
        {
            return new ScriptCallResult(values ?? None);
        }

        /// <summary>The values to hand back to the script, in order.</summary>
        public IReadOnlyList<object> Values => _values ?? None;
    }
}
