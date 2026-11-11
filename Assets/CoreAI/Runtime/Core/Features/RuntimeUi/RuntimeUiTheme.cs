using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// One theme shared by every runtime screen: design tokens (USS custom properties such as
    /// <c>--cai-color-accent</c>) that every screen inherits at its root, and a base style sheet of
    /// semantic classes that loses to any screen's own rules. Token values can change while the game
    /// runs; <see cref="Changed"/> tells the host to restyle every screen at once ("make the UI darker").
    /// A screen that declares a token itself keeps its own value, as in CSS.
    /// </summary>
    public sealed class RuntimeUiTheme
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        private readonly RuntimeUiParseLimits _limits;
        private UssCustomProperties _scope;

        /// <summary>Creates a theme.</summary>
        /// <param name="baseUss">Semantic classes; null uses <see cref="RuntimeUiDefaultTheme.Uss"/>.</param>
        /// <param name="tokens">Initial tokens; null uses <see cref="RuntimeUiDefaultTheme.Tokens"/>.</param>
        /// <param name="limits">Parse limits for the base sheet.</param>
        public RuntimeUiTheme(
            string baseUss = null,
            IEnumerable<KeyValuePair<string, string>> tokens = null,
            RuntimeUiParseLimits limits = null)
        {
            _limits = limits ?? RuntimeUiParseLimits.Default;
            BaseUss = baseUss ?? RuntimeUiDefaultTheme.Uss;
            BaseSheet = UssStyleSheet.Parse(BaseUss, _limits);
            foreach (KeyValuePair<string, string> pair in tokens ?? RuntimeUiDefaultTheme.Tokens)
            {
                if (UssPropertyCatalog.IsCustomProperty(pair.Key) && pair.Value != null)
                {
                    _tokens[pair.Key] = pair.Value.Trim();
                }
            }
        }

        /// <summary>Raised after a token or the base sheet changed.</summary>
        public event Action<RuntimeUiTheme> Changed;

        /// <summary>Base USS text.</summary>
        public string BaseUss { get; private set; }

        /// <summary>The parsed base sheet (its reports are in <see cref="UssStyleSheet.Diagnostics"/>).</summary>
        public UssStyleSheet BaseSheet { get; private set; }

        /// <summary>Increments on every change.</summary>
        public int Version { get; private set; }

        /// <summary>The current tokens.</summary>
        public IReadOnlyDictionary<string, string> Tokens => _tokens;

        /// <summary>The tokens as the custom-property scope every screen root inherits.</summary>
        public UssCustomProperties Scope => _scope ??= UssCustomProperties.From(_tokens);

        /// <summary>Reads a token.</summary>
        public bool TryGetToken(string name, out string value)
        {
            if (name == null)
            {
                value = null;
                return false;
            }

            return _tokens.TryGetValue(name, out value);
        }

        /// <summary>Sets one token; <paramref name="name"/> must start with <c>--</c>.</summary>
        /// <returns>False when the name is not a custom property name or the value is null.</returns>
        public bool SetToken(string name, string value)
        {
            if (!UssPropertyCatalog.IsCustomProperty(name) || value == null)
            {
                return false;
            }

            string trimmed = value.Trim();
            if (_tokens.TryGetValue(name, out string current) && current == trimmed)
            {
                return true;
            }

            _tokens[name] = trimmed;
            NotifyChanged();
            return true;
        }

        /// <summary>Sets several tokens with one change notification.</summary>
        /// <returns>How many names were accepted.</returns>
        public int SetTokens(IEnumerable<KeyValuePair<string, string>> tokens)
        {
            if (tokens == null)
            {
                return 0;
            }

            int accepted = 0;
            bool changed = false;
            foreach (KeyValuePair<string, string> pair in tokens)
            {
                if (!UssPropertyCatalog.IsCustomProperty(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                accepted++;
                string trimmed = pair.Value.Trim();
                if (_tokens.TryGetValue(pair.Key, out string current) && current == trimmed)
                {
                    continue;
                }

                _tokens[pair.Key] = trimmed;
                changed = true;
            }

            if (changed)
            {
                NotifyChanged();
            }

            return accepted;
        }

        /// <summary>Removes a token.</summary>
        public bool RemoveToken(string name)
        {
            if (name == null || !_tokens.Remove(name))
            {
                return false;
            }

            NotifyChanged();
            return true;
        }

        /// <summary>Replaces the base sheet; its reports are returned.</summary>
        public IReadOnlyList<RuntimeUiDiagnostic> SetBaseUss(string uss)
        {
            BaseUss = uss ?? string.Empty;
            BaseSheet = UssStyleSheet.Parse(BaseUss, _limits);
            NotifyChanged();
            return BaseSheet.Diagnostics;
        }

        private void NotifyChanged()
        {
            _scope = null;
            Version++;
            Changed?.Invoke(this);
        }
    }
}
