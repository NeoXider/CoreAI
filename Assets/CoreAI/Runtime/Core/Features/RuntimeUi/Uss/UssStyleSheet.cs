using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>A selector of a rule, as stored in the lookup index.</summary>
    public readonly struct UssSelectorEntry
    {
        internal UssSelectorEntry(UssRule rule, UssComplexSelector selector)
        {
            Rule = rule;
            Selector = selector;
        }

        /// <summary>The rule the selector belongs to.</summary>
        public UssRule Rule { get; }

        /// <summary>The selector.</summary>
        public UssComplexSelector Selector { get; }
    }

    /// <summary>
    /// A parsed USS style sheet of the supported subset, with every dropped rule, selector and
    /// declaration reported in <see cref="Diagnostics"/>. Rules are indexed by their subject's name,
    /// first class or type so matching an element only tests plausible rules.
    /// </summary>
    public sealed class UssStyleSheet
    {
        /// <summary>A sheet with no rules.</summary>
        public static readonly UssStyleSheet Empty = new(Array.Empty<UssRule>(), Array.Empty<RuntimeUiDiagnostic>());

        private readonly Dictionary<string, List<UssSelectorEntry>> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<UssSelectorEntry>> _byType = new(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, List<UssSelectorEntry>>> _byClass = new();
        private readonly List<UssSelectorEntry> _universal = new();
        private readonly List<UssCompoundSelector> _stateCompounds = new();
        private readonly List<bool> _stateCompoundIsSubject = new();

        internal UssStyleSheet(UssRule[] rules, RuntimeUiDiagnostic[] diagnostics)
        {
            Rules = rules;
            Diagnostics = diagnostics;
            Dictionary<string, List<UssSelectorEntry>> classBuckets = new(StringComparer.Ordinal);
            for (int r = 0; r < rules.Length; r++)
            {
                UssRule rule = rules[r];
                for (int s = 0; s < rule.Selectors.Count; s++)
                {
                    UssComplexSelector selector = rule.Selectors[s];
                    UssSelectorEntry entry = new(rule, selector);
                    UssCompoundSelector subject = selector.Subject;
                    if (subject.Name != null)
                    {
                        Bucket(_byName, subject.Name).Add(entry);
                    }
                    else if (subject.Classes.Length > 0)
                    {
                        Bucket(classBuckets, subject.Classes[0]).Add(entry);
                    }
                    else if (subject.TypeName != null)
                    {
                        Bucket(_byType, subject.TypeName).Add(entry);
                    }
                    else
                    {
                        _universal.Add(entry);
                    }

                    for (int c = 0; c < selector.Compounds.Length; c++)
                    {
                        if (selector.Compounds[c].HasDynamicState)
                        {
                            _stateCompounds.Add(selector.Compounds[c]);
                            _stateCompoundIsSubject.Add(c == selector.Compounds.Length - 1);
                        }
                    }
                }
            }

            foreach (KeyValuePair<string, List<UssSelectorEntry>> pair in classBuckets)
            {
                _byClass.Add(pair);
            }
        }

        /// <summary>The rules that survived parsing, in source order.</summary>
        public IReadOnlyList<UssRule> Rules { get; }

        /// <summary>Reports produced while parsing.</summary>
        public IReadOnlyList<RuntimeUiDiagnostic> Diagnostics { get; }

        /// <summary>Compounds that test an interaction state; elements matching one need state tracking.</summary>
        public IReadOnlyList<UssCompoundSelector> StateCompounds => _stateCompounds;

        /// <summary>Parses USS text. Never throws; problems are in <see cref="Diagnostics"/>.</summary>
        public static UssStyleSheet Parse(string text, RuntimeUiParseLimits limits = null)
        {
            return UssParser.Parse(text, limits);
        }

        /// <summary>Appends the selectors that could match <paramref name="element"/> (subject bucket hits).</summary>
        public void CollectCandidates(IUssElement element, List<UssSelectorEntry> output)
        {
            string name = element.Name;
            if (!string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out List<UssSelectorEntry> named))
            {
                output.AddRange(named);
            }

            for (int i = 0; i < _byClass.Count; i++)
            {
                if (element.HasClass(_byClass[i].Key))
                {
                    output.AddRange(_byClass[i].Value);
                }
            }

            string typeName = element.TypeName;
            if (typeName != null && _byType.TryGetValue(typeName, out List<UssSelectorEntry> typed))
            {
                output.AddRange(typed);
            }

            output.AddRange(_universal);
        }

        /// <summary>The interaction states <paramref name="element"/> can be tested for by this sheet.</summary>
        public UssPseudoState TrackedStatesFor(IUssElement element)
        {
            return TrackedStatesFor(element, out _);
        }

        /// <summary>
        /// The interaction states <paramref name="element"/> can be tested for by this sheet, and whether a
        /// state change can restyle its descendants (the state is tested on an ancestor compound, as in
        /// <c>.slot:hover &gt; .icon</c>) rather than only the element itself.
        /// </summary>
        public UssPseudoState TrackedStatesFor(IUssElement element, out bool affectsDescendants)
        {
            UssPseudoState states = UssPseudoState.None;
            affectsDescendants = false;
            for (int i = 0; i < _stateCompounds.Count; i++)
            {
                UssCompoundSelector compound = _stateCompounds[i];
                if (compound.MatchesIgnoringState(element))
                {
                    states |= compound.Required | compound.Forbidden;
                    affectsDescendants |= !_stateCompoundIsSubject[i];
                }
            }

            return states & ~UssPseudoState.Root;
        }

        private static List<UssSelectorEntry> Bucket(Dictionary<string, List<UssSelectorEntry>> buckets, string key)
        {
            if (!buckets.TryGetValue(key, out List<UssSelectorEntry> list))
            {
                list = new List<UssSelectorEntry>();
                buckets[key] = list;
            }

            return list;
        }
    }
}
