using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// One parsed slash command: the name the player typed (without the prefix), the argument text after it,
    /// and that text split into arguments on whitespace, where a double-quoted run is one argument.
    /// </summary>
    public readonly struct ChatCommandInvocation
    {
        private readonly string _name;
        private readonly string _argumentText;
        private readonly IReadOnlyList<string> _arguments;
        private readonly string _rawText;

        /// <summary>Creates an invocation; null strings become empty and a null list becomes empty.</summary>
        public ChatCommandInvocation(string name, string argumentText, IReadOnlyList<string> arguments,
            string rawText)
        {
            _name = name ?? "";
            _argumentText = argumentText ?? "";
            _arguments = arguments ?? Array.Empty<string>();
            _rawText = rawText ?? "";
        }

        /// <summary>The command name as typed, without the prefix; empty for a bare prefix.</summary>
        public string Name => _name ?? "";

        /// <summary>Everything after the name, trimmed.</summary>
        public string ArgumentText => _argumentText ?? "";

        /// <summary>The argument text split on whitespace; a double-quoted run is one argument.</summary>
        public IReadOnlyList<string> Arguments => _arguments ?? Array.Empty<string>();

        /// <summary>The whole submitted line, trimmed.</summary>
        public string RawText => _rawText ?? "";

        /// <summary>True when at least one argument follows the name.</summary>
        public bool HasArguments => Arguments.Count > 0;

        /// <summary>The argument at <paramref name="index"/>, or an empty string when there is none.</summary>
        public string ArgumentAt(int index)
        {
            IReadOnlyList<string> arguments = Arguments;
            return index >= 0 && index < arguments.Count ? arguments[index] : "";
        }
    }
}
