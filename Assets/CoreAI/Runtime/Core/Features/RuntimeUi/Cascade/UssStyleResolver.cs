using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The engine-free USS cascade. For one element it collects the matching rules of an ordered list of
    /// style sheets, orders them the way UI Toolkit does (theme sheets first, then specificity, then
    /// sheet order, then rule order), lays the element's <c>style</c> attribute and API overrides on top,
    /// resolves custom properties and <c>var()</c>, and writes the winning longhand values into a
    /// <see cref="UssResolvedStyle"/>. All working storage is reused between calls.
    /// </summary>
    public sealed class UssStyleResolver
    {
        private static readonly MatchComparer MatchOrder = new();

        private readonly RuntimeUiDiagnosticList _diagnostics;
        private readonly List<UssSelectorEntry> _candidates = new();
        private readonly List<Match> _matches = new();
        private readonly UssDeclaration[] _winners = new UssDeclaration[(int)UssPropertyId.Count];
        private readonly int[] _winnerComponent = new int[(int)UssPropertyId.Count];
        private readonly List<UssPropertyId> _winnerIds = new();
        private readonly Dictionary<string, UssDeclaration> _customWinners = new(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, string>> _customRaw = new();
        private readonly List<KeyValuePair<string, string>> _customResolved = new();

        /// <summary>Creates a resolver that reports cascade-time problems into <paramref name="diagnostics"/>.</summary>
        public UssStyleResolver(RuntimeUiDiagnosticList diagnostics)
        {
            _diagnostics = diagnostics ?? new RuntimeUiDiagnosticList();
        }

        /// <summary>The report sink.</summary>
        public RuntimeUiDiagnosticList Diagnostics => _diagnostics;

        /// <summary>Resolves the style of one element.</summary>
        /// <param name="sheets">Style sheets in order; the first <paramref name="defaultSheetCount"/> are theme sheets that lose to every later sheet.</param>
        /// <param name="defaultSheetCount">How many leading sheets are theme sheets.</param>
        /// <param name="element">The element.</param>
        /// <param name="inherited">Custom properties inherited from the parent (or the theme tokens at a root).</param>
        /// <param name="inline">The element's <c>style</c> attribute declarations; may be null.</param>
        /// <param name="overrides">Declarations set through the API, applied last; may be null.</param>
        /// <param name="output">Receives the result; cleared first.</param>
        public void Resolve(
            IReadOnlyList<UssStyleSheet> sheets,
            int defaultSheetCount,
            IUssElement element,
            UssCustomProperties inherited,
            IReadOnlyList<UssDeclaration> inline,
            IReadOnlyList<UssDeclaration> overrides,
            UssResolvedStyle output)
        {
            output.Clear();
            ClearWinners();
            UssCustomProperties scope = inherited ?? UssCustomProperties.Empty;
            if (element == null)
            {
                output.CustomProperties = scope;
                return;
            }

            CollectMatches(sheets, defaultSheetCount, element);
            for (int m = 0; m < _matches.Count; m++)
            {
                IReadOnlyList<UssDeclaration> declarations = _matches[m].Entry.Rule.Declarations;
                for (int d = 0; d < declarations.Count; d++)
                {
                    Take(declarations[d]);
                }
            }

            if (inline != null)
            {
                for (int d = 0; d < inline.Count; d++)
                {
                    Take(inline[d]);
                }
            }

            if (overrides != null)
            {
                for (int d = 0; d < overrides.Count; d++)
                {
                    Take(overrides[d]);
                }
            }

            scope = ResolveCustomProperties(scope);
            output.CustomProperties = scope;
            for (int i = 0; i < _winnerIds.Count; i++)
            {
                UssPropertyId id = _winnerIds[i];
                UssDeclaration declaration = _winners[(int)id];
                int component = _winnerComponent[(int)id];
                if (component < 0)
                {
                    if (declaration.TryResolveLonghand(scope, out UssValue value, out string error))
                    {
                        output.Set(id, value);
                    }
                    else
                    {
                        ReportResolveFailure(declaration, error);
                    }
                }
                else if (declaration.TryResolveShorthand(scope, out UssValue[] values, out string error))
                {
                    output.Set(id, values[component]);
                }
                else
                {
                    ReportResolveFailure(declaration, error);
                }
            }
        }

        /// <summary>True when <paramref name="selector"/> matches <paramref name="element"/>; convenience for queries.</summary>
        public static bool Matches(UssComplexSelector selector, IUssElement element)
        {
            return UssSelectorMatcher.Matches(selector, element);
        }

        private void CollectMatches(IReadOnlyList<UssStyleSheet> sheets, int defaultSheetCount, IUssElement element)
        {
            _matches.Clear();
            if (sheets == null)
            {
                return;
            }

            for (int s = 0; s < sheets.Count; s++)
            {
                UssStyleSheet sheet = sheets[s];
                if (sheet == null || sheet.Rules.Count == 0)
                {
                    continue;
                }

                _candidates.Clear();
                sheet.CollectCandidates(element, _candidates);
                for (int c = 0; c < _candidates.Count; c++)
                {
                    UssSelectorEntry entry = _candidates[c];
                    if (UssSelectorMatcher.Matches(entry.Selector, element))
                    {
                        _matches.Add(new Match(entry, s, s < defaultSheetCount ? 0 : 1));
                    }
                }
            }

            _candidates.Clear();
            if (_matches.Count > 1)
            {
                _matches.Sort(MatchOrder);
            }
        }

        private void Take(UssDeclaration declaration)
        {
            if (declaration == null)
            {
                return;
            }

            if (declaration.IsCustom)
            {
                _customWinners[declaration.Property] = declaration;
                return;
            }

            if (declaration.Longhand != null)
            {
                SetWinner(declaration.Longhand.Id, declaration, -1);
                return;
            }

            UssPropertyId[] longhands = declaration.Shorthand.Longhands;
            for (int i = 0; i < longhands.Length; i++)
            {
                SetWinner(longhands[i], declaration, i);
            }
        }

        private void SetWinner(UssPropertyId id, UssDeclaration declaration, int component)
        {
            int index = (int)id;
            if (_winners[index] == null)
            {
                _winnerIds.Add(id);
            }

            _winners[index] = declaration;
            _winnerComponent[index] = component;
        }

        private void ClearWinners()
        {
            for (int i = 0; i < _winnerIds.Count; i++)
            {
                _winners[(int)_winnerIds[i]] = null;
            }

            _winnerIds.Clear();
            _customWinners.Clear();
        }

        private UssCustomProperties ResolveCustomProperties(UssCustomProperties inherited)
        {
            if (_customWinners.Count == 0)
            {
                return inherited;
            }

            _customRaw.Clear();
            bool anyVar = false;
            foreach (KeyValuePair<string, UssDeclaration> pair in _customWinners)
            {
                _customRaw.Add(new KeyValuePair<string, string>(pair.Key, pair.Value.RawValue));
                anyVar |= pair.Value.HasVar;
            }

            if (!anyVar)
            {
                return inherited.With(_customRaw);
            }

            UssCustomProperties unresolved = inherited.With(_customRaw);
            _customResolved.Clear();
            for (int i = 0; i < _customRaw.Count; i++)
            {
                string name = _customRaw[i].Key;
                string raw = _customRaw[i].Value;
                if (!UssVarSubstitution.ContainsVar(raw))
                {
                    _customResolved.Add(_customRaw[i]);
                    continue;
                }

                if (UssVarSubstitution.TrySubstitute(raw, unresolved, out string value, out UssVarFailure failure,
                        out string failedName))
                {
                    _customResolved.Add(new KeyValuePair<string, string>(name, value));
                    continue;
                }

                UssDeclaration declaration = _customWinners[name];
                string code = failure == UssVarFailure.TooDeep
                    ? RuntimeUiDiagnosticCodes.UssVarCycle
                    : RuntimeUiDiagnosticCodes.UssUnresolvedVar;
                _diagnostics.Add(code, RuntimeUiDiagnosticSeverity.Warning, declaration.Source, declaration.Line,
                    declaration.Column,
                    "the custom property '" + name + "' is invalid: " +
                    UssDeclaration.DescribeFailure(failure, failedName));
            }

            return inherited.With(_customResolved);
        }

        private void ReportResolveFailure(UssDeclaration declaration, string error)
        {
            string code;
            switch (declaration.LastVarFailure)
            {
                case UssVarFailure.None:
                    code = RuntimeUiDiagnosticCodes.UssInvalidValue;
                    break;
                case UssVarFailure.TooDeep:
                    code = RuntimeUiDiagnosticCodes.UssVarCycle;
                    break;
                default:
                    code = RuntimeUiDiagnosticCodes.UssUnresolvedVar;
                    break;
            }

            _diagnostics.Add(code, RuntimeUiDiagnosticSeverity.Warning, declaration.Source, declaration.Line,
                declaration.Column,
                "'" + declaration.Property + ": " + declaration.RawValue + "' was not applied: " + error);
        }

        private sealed class MatchComparer : IComparer<Match>
        {
            public int Compare(Match a, Match b)
            {
                return CompareMatches(a, b);
            }
        }

        private static int CompareMatches(Match a, Match b)
        {
            int result = a.Tier.CompareTo(b.Tier);
            if (result != 0)
            {
                return result;
            }

            result = a.Entry.Selector.Specificity.CompareTo(b.Entry.Selector.Specificity);
            if (result != 0)
            {
                return result;
            }

            result = a.SheetIndex.CompareTo(b.SheetIndex);
            if (result != 0)
            {
                return result;
            }

            return a.Entry.Rule.Order.CompareTo(b.Entry.Rule.Order);
        }

        private readonly struct Match
        {
            public Match(UssSelectorEntry entry, int sheetIndex, int tier)
            {
                Entry = entry;
                SheetIndex = sheetIndex;
                Tier = tier;
            }

            public UssSelectorEntry Entry { get; }

            public int SheetIndex { get; }

            public int Tier { get; }
        }
    }
}
