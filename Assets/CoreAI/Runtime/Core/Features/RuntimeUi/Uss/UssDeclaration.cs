using System;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// One <c>property: value</c> declaration of the supported subset. Declarations without <c>var()</c>
    /// are parsed once when created; declarations with <c>var()</c> are parsed per custom-property scope
    /// and cached against the last scope instance, so a subtree sharing one scope parses once.
    /// </summary>
    public sealed class UssDeclaration
    {
        private UssCustomProperties _cachedScope;
        private bool _cachedOk;
        private UssValue _cachedValue;
        private UssValue[] _cachedShorthand;
        private UssVarFailure _cachedFailure;
        private string _cachedError;

        private UssDeclaration(
            string property,
            string rawValue,
            int line,
            int column,
            RuntimeUiSource source,
            UssPropertySpec longhand,
            UssShorthandSpec shorthand)
        {
            Property = property;
            RawValue = rawValue;
            Line = line;
            Column = column;
            Source = source;
            Longhand = longhand;
            Shorthand = shorthand;
            IsCustom = longhand == null && shorthand == null;
            HasVar = UssVarSubstitution.ContainsVar(rawValue);
        }

        /// <summary>Property name (lower case; custom property names keep their case).</summary>
        public string Property { get; }

        /// <summary>Value text as written (without <c>!important</c>).</summary>
        public string RawValue { get; }

        /// <summary>1-based line in its source.</summary>
        public int Line { get; }

        /// <summary>1-based column in its source.</summary>
        public int Column { get; }

        /// <summary>Which text the declaration came from.</summary>
        public RuntimeUiSource Source { get; }

        /// <summary>The longhand, or null for a shorthand or custom property.</summary>
        public UssPropertySpec Longhand { get; }

        /// <summary>The shorthand, or null.</summary>
        public UssShorthandSpec Shorthand { get; }

        /// <summary>True for <c>--token</c> declarations.</summary>
        public bool IsCustom { get; }

        /// <summary>True when the value contains <c>var()</c>.</summary>
        public bool HasVar { get; }

        /// <summary>Why the last resolution failed on a <c>var()</c>, or <see cref="UssVarFailure.None"/>.</summary>
        public UssVarFailure LastVarFailure => _cachedFailure;

        /// <summary>
        /// Validates and creates a declaration, reporting into <paramref name="diagnostics"/> and returning
        /// null when it is dropped (unsupported property, invalid value).
        /// </summary>
        public static UssDeclaration Create(
            string property,
            string value,
            int line,
            int column,
            RuntimeUiSource source,
            RuntimeUiDiagnosticList diagnostics)
        {
            string name = (property ?? string.Empty).Trim();
            string raw = (value ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                diagnostics?.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error, source, line,
                    column, "a declaration has no property name");
                return null;
            }

            if (UssPropertyCatalog.IsCustomProperty(name))
            {
                return new UssDeclaration(name, raw, line, column, source, null, null);
            }

            string lower = name.ToLowerInvariant();
            if (raw.Length == 0)
            {
                diagnostics?.Add(RuntimeUiDiagnosticCodes.UssInvalidValue, RuntimeUiDiagnosticSeverity.Warning, source,
                    line, column, "'" + lower + "' has no value");
                return null;
            }

            if (UssPropertyCatalog.TryGetLonghand(lower, out UssPropertySpec longhand))
            {
                UssDeclaration declaration = new(lower, raw, line, column, source, longhand, null);
                if (!declaration.HasVar)
                {
                    if (!UssValueParser.TryParseLonghand(longhand, raw, out UssValue parsed, out string error))
                    {
                        diagnostics?.Add(RuntimeUiDiagnosticCodes.UssInvalidValue, RuntimeUiDiagnosticSeverity.Warning,
                            source, line, column, "'" + lower + ": " + raw + "' was dropped: " + error);
                        return null;
                    }

                    declaration._cachedOk = true;
                    declaration._cachedValue = parsed;
                }

                return declaration;
            }

            if (UssPropertyCatalog.TryGetShorthand(lower, out UssShorthandSpec shorthand))
            {
                UssDeclaration declaration = new(lower, raw, line, column, source, null, shorthand);
                if (!declaration.HasVar)
                {
                    UssValue[] expanded = new UssValue[shorthand.Longhands.Length];
                    if (!UssValueParser.TryExpandShorthand(shorthand, raw, expanded, out string error))
                    {
                        diagnostics?.Add(RuntimeUiDiagnosticCodes.UssInvalidValue, RuntimeUiDiagnosticSeverity.Warning,
                            source, line, column, "'" + lower + ": " + raw + "' was dropped: " + error);
                        return null;
                    }

                    declaration._cachedOk = true;
                    declaration._cachedShorthand = expanded;
                }

                return declaration;
            }

            string reason = UssPropertyCatalog.IsKnownUnsupported(lower)
                ? "'" + lower + "' is not in the supported USS subset"
                : "'" + lower + "' is not a USS property";
            diagnostics?.Add(RuntimeUiDiagnosticCodes.UssUnsupportedProperty, RuntimeUiDiagnosticSeverity.Warning,
                source, line, column, reason + "; the declaration was dropped");
            return null;
        }

        /// <summary>A declaration of a typed value, for C# callers that set one property without text.</summary>
        public static UssDeclaration FromValue(UssPropertyId id, UssValue value)
        {
            UssPropertySpec spec = UssPropertyCatalog.Get(id);
            if (spec == null)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "Not a USS longhand.");
            }

            UssDeclaration declaration = new(spec.Name, value.ToUssText(), 0, 0, RuntimeUiSource.Api, spec, null);
            declaration._cachedOk = true;
            declaration._cachedValue = value;
            return declaration;
        }

        /// <summary>
        /// Resolves a longhand declaration against a custom-property scope.
        /// </summary>
        /// <returns>False when a <c>var()</c> is unresolved or the substituted value is invalid.</returns>
        public bool TryResolveLonghand(UssCustomProperties scope, out UssValue value, out string error)
        {
            error = null;
            if (!HasVar || ReferenceEquals(scope, _cachedScope))
            {
                value = _cachedValue;
                if (!_cachedOk)
                {
                    error = _cachedError ?? "the value could not be resolved";
                }

                return _cachedOk;
            }

            _cachedScope = scope;
            _cachedShorthand = null;
            if (!UssVarSubstitution.TrySubstitute(RawValue, scope, out string substituted, out UssVarFailure failure,
                    out string failedName))
            {
                _cachedOk = false;
                _cachedFailure = failure;
                _cachedValue = default;
                value = default;
                error = DescribeFailure(failure, failedName);
                _cachedError = error;
                return false;
            }

            _cachedFailure = UssVarFailure.None;
            _cachedOk = UssValueParser.TryParseLonghand(Longhand, substituted, out _cachedValue, out error);
            _cachedError = error;
            value = _cachedValue;
            return _cachedOk;
        }

        /// <summary>Resolves a shorthand declaration; the returned array is cached and must not be modified.</summary>
        public bool TryResolveShorthand(UssCustomProperties scope, out UssValue[] values, out string error)
        {
            error = null;
            if (!HasVar || ReferenceEquals(scope, _cachedScope))
            {
                values = _cachedShorthand;
                if (!_cachedOk)
                {
                    error = _cachedError ?? "the value could not be resolved";
                }

                return _cachedOk && values != null;
            }

            _cachedScope = scope;
            if (!UssVarSubstitution.TrySubstitute(RawValue, scope, out string substituted, out UssVarFailure failure,
                    out string failedName))
            {
                _cachedOk = false;
                _cachedFailure = failure;
                _cachedShorthand = null;
                values = null;
                error = DescribeFailure(failure, failedName);
                _cachedError = error;
                return false;
            }

            _cachedFailure = UssVarFailure.None;
            UssValue[] expanded = new UssValue[Shorthand.Longhands.Length];
            _cachedOk = UssValueParser.TryExpandShorthand(Shorthand, substituted, expanded, out error);
            _cachedError = error;
            _cachedShorthand = _cachedOk ? expanded : null;
            values = _cachedShorthand;
            return _cachedOk;
        }

        internal static string DescribeFailure(UssVarFailure failure, string name)
        {
            switch (failure)
            {
                case UssVarFailure.Unresolved:
                    return "var(" + name + ") is not defined by any ancestor and has no fallback";
                case UssVarFailure.TooDeep:
                    return "var() substitution nests too deeply (a custom property cycle?)";
                default:
                    return "var() is not well formed" + (string.IsNullOrEmpty(name) ? string.Empty : ": " + name);
            }
        }
    }
}
