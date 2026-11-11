using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.RuntimeUi
{
    /// <summary>Interaction states a selector can test with a pseudo-class.</summary>
    [Flags]
    public enum UssPseudoState : byte
    {
        /// <summary>No state.</summary>
        None = 0,

        /// <summary><c>:hover</c> - the pointer is over the element or a descendant.</summary>
        Hover = 1,

        /// <summary><c>:active</c> - the element is being pressed.</summary>
        Active = 2,

        /// <summary><c>:focus</c> - the element (or the field it belongs to) has keyboard focus.</summary>
        Focus = 4,

        /// <summary><c>:disabled</c> - the element or an ancestor is disabled.</summary>
        Disabled = 8,

        /// <summary><c>:checked</c> - a toggle is on.</summary>
        Checked = 16,

        /// <summary><c>:root</c> - the element the style sheet is attached to (a screen root).</summary>
        Root = 32
    }

    /// <summary>How a compound selector relates to the one on its left.</summary>
    public enum UssCombinator : byte
    {
        /// <summary>The leftmost compound.</summary>
        None = 0,

        /// <summary>Whitespace: any ancestor.</summary>
        Descendant = 1,

        /// <summary><c>&gt;</c>: the parent.</summary>
        Child = 2
    }

    /// <summary>One compound selector: an optional type, an optional <c>#name</c>, classes and pseudo-classes.</summary>
    public sealed class UssCompoundSelector
    {
        internal UssCompoundSelector(string typeName, string name, string[] classes, UssPseudoState required,
            UssPseudoState forbidden)
        {
            TypeName = typeName;
            Name = name;
            Classes = classes ?? Array.Empty<string>();
            Required = required;
            Forbidden = forbidden;
        }

        /// <summary>Element type name, or null for any type (<c>*</c> or no type).</summary>
        public string TypeName { get; }

        /// <summary>Element name (<c>#name</c>), or null.</summary>
        public string Name { get; }

        /// <summary>Required classes.</summary>
        public string[] Classes { get; }

        /// <summary>States that must be present.</summary>
        public UssPseudoState Required { get; }

        /// <summary>States that must be absent (<c>:enabled</c>, <c>:inactive</c>).</summary>
        public UssPseudoState Forbidden { get; }

        /// <summary>True when the compound tests an interaction state (anything but <c>:root</c>).</summary>
        public bool HasDynamicState => ((Required | Forbidden) & ~UssPseudoState.Root) != UssPseudoState.None;

        /// <summary>Tests type, name and classes, ignoring pseudo-classes.</summary>
        public bool MatchesIgnoringState(IUssElement element)
        {
            if (TypeName != null && !string.Equals(element.TypeName, TypeName, StringComparison.Ordinal))
            {
                return false;
            }

            if (Name != null && !string.Equals(element.Name, Name, StringComparison.Ordinal))
            {
                return false;
            }

            for (int i = 0; i < Classes.Length; i++)
            {
                if (!element.HasClass(Classes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Tests the full compound, pseudo-classes included.</summary>
        public bool Matches(IUssElement element)
        {
            if (!MatchesIgnoringState(element))
            {
                return false;
            }

            if (Required == UssPseudoState.None && Forbidden == UssPseudoState.None)
            {
                return true;
            }

            UssPseudoState states = element.PseudoStates;
            return (states & Required) == Required && (states & Forbidden) == UssPseudoState.None;
        }
    }

    /// <summary>
    /// A complex selector: compounds joined by descendant (whitespace) or child (<c>&gt;</c>) combinators.
    /// The supported grammar is type, <c>*</c>, <c>.class</c>, <c>#name</c>, and the pseudo-classes
    /// <c>:hover :active :inactive :focus :disabled :enabled :checked :root</c>.
    /// </summary>
    public sealed class UssComplexSelector
    {
        private UssComplexSelector(UssCompoundSelector[] compounds, UssCombinator[] combinators, string text,
            int line, int column)
        {
            Compounds = compounds;
            Combinators = combinators;
            Text = text;
            Line = line;
            Column = column;
            int specificity = 0;
            bool dynamic = false;
            for (int i = 0; i < compounds.Length; i++)
            {
                UssCompoundSelector compound = compounds[i];
                if (compound.Name != null)
                {
                    specificity += 100;
                }

                specificity += compound.Classes.Length * 10;
                specificity += CountBits((byte)(compound.Required | compound.Forbidden)) * 10;
                if (compound.TypeName != null)
                {
                    specificity += 1;
                }

                dynamic |= compound.HasDynamicState;
            }

            Specificity = specificity;
            HasDynamicState = dynamic;
        }

        /// <summary>Compounds from left to right.</summary>
        public UssCompoundSelector[] Compounds { get; }

        /// <summary><c>Combinators[i]</c> joins compound i-1 and compound i; index 0 is <see cref="UssCombinator.None"/>.</summary>
        public UssCombinator[] Combinators { get; }

        /// <summary>Specificity with UI Toolkit's weights: 100 per name, 10 per class or pseudo-class, 1 per type.</summary>
        public int Specificity { get; }

        /// <summary>True when any compound tests an interaction state.</summary>
        public bool HasDynamicState { get; }

        /// <summary>Normalized selector text.</summary>
        public string Text { get; }

        /// <summary>1-based line in the source.</summary>
        public int Line { get; }

        /// <summary>1-based column in the source.</summary>
        public int Column { get; }

        /// <summary>The rightmost compound (the subject).</summary>
        public UssCompoundSelector Subject => Compounds[Compounds.Length - 1];

        /// <summary>Parses one complex selector.</summary>
        /// <param name="text">Selector text, for example <c>#hotbar &gt; .slot:hover</c>.</param>
        /// <param name="line">Line used for reports.</param>
        /// <param name="column">Column used for reports.</param>
        /// <param name="selector">The parsed selector.</param>
        /// <param name="error">Why the selector was refused.</param>
        public static bool TryParse(string text, int line, int column, out UssComplexSelector selector,
            out string error)
        {
            selector = null;
            error = null;
            string source = (text ?? string.Empty).Trim();
            if (source.Length == 0)
            {
                error = "the selector is empty";
                return false;
            }

            List<UssCompoundSelector> compounds = new();
            List<UssCombinator> combinators = new();
            UssCombinator pending = UssCombinator.None;
            int i = 0;
            while (i < source.Length)
            {
                char c = source[i];
                if (char.IsWhiteSpace(c))
                {
                    if (compounds.Count > 0 && pending == UssCombinator.None)
                    {
                        pending = UssCombinator.Descendant;
                    }

                    i++;
                    continue;
                }

                if (c == '>')
                {
                    if (compounds.Count == 0 || pending == UssCombinator.Child)
                    {
                        error = "'>' needs a selector on both sides";
                        return false;
                    }

                    pending = UssCombinator.Child;
                    i++;
                    continue;
                }

                if (c == '+' || c == '~')
                {
                    error = "the sibling combinator '" + c + "' is not supported (use descendant or '>')";
                    return false;
                }

                if (c == '[')
                {
                    error = "attribute selectors are not supported";
                    return false;
                }

                if (c == ',')
                {
                    error = "unexpected ','";
                    return false;
                }

                if (compounds.Count > 0 && pending == UssCombinator.None)
                {
                    error = "unexpected '" + c + "'";
                    return false;
                }

                if (!TryParseCompound(source, ref i, out UssCompoundSelector compound, out error))
                {
                    return false;
                }

                combinators.Add(compounds.Count == 0 ? UssCombinator.None : pending);
                compounds.Add(compound);
                pending = UssCombinator.None;
            }

            if (compounds.Count == 0)
            {
                error = "the selector is empty";
                return false;
            }

            if (pending == UssCombinator.Child)
            {
                error = "'>' needs a selector on its right";
                return false;
            }

            selector = new UssComplexSelector(compounds.ToArray(), combinators.ToArray(), Normalize(compounds, combinators),
                line, column);
            return true;
        }

        private static bool TryParseCompound(string source, ref int i, out UssCompoundSelector compound,
            out string error)
        {
            compound = null;
            error = null;
            string typeName = null;
            string name = null;
            List<string> classes = null;
            UssPseudoState required = UssPseudoState.None;
            UssPseudoState forbidden = UssPseudoState.None;
            bool any = false;

            if (source[i] == '*')
            {
                i++;
                any = true;
            }
            else if (IsIdentStart(source[i]))
            {
                typeName = ReadIdent(source, ref i);
                any = true;
            }

            while (i < source.Length)
            {
                char c = source[i];
                if (c == '.')
                {
                    i++;
                    if (i >= source.Length || !IsIdentStart(source[i]))
                    {
                        error = "'.' must be followed by a class name";
                        return false;
                    }

                    classes ??= new List<string>();
                    classes.Add(ReadIdent(source, ref i));
                    any = true;
                }
                else if (c == '#')
                {
                    i++;
                    if (i >= source.Length || !IsIdentStart(source[i]))
                    {
                        error = "'#' must be followed by an element name";
                        return false;
                    }

                    string parsedName = ReadIdent(source, ref i);
                    if (name != null && name != parsedName)
                    {
                        error = "a compound selector cannot have two names";
                        return false;
                    }

                    name = parsedName;
                    any = true;
                }
                else if (c == ':')
                {
                    i++;
                    if (i < source.Length && source[i] == ':')
                    {
                        error = "pseudo-elements ('::') are not supported";
                        return false;
                    }

                    if (i >= source.Length || !IsIdentStart(source[i]))
                    {
                        error = "':' must be followed by a pseudo-class";
                        return false;
                    }

                    string pseudo = ReadIdent(source, ref i).ToLowerInvariant();
                    if (i < source.Length && source[i] == '(')
                    {
                        error = "functional pseudo-classes (':" + pseudo + "()') are not supported";
                        return false;
                    }

                    switch (pseudo)
                    {
                        case "hover":
                            required |= UssPseudoState.Hover;
                            break;
                        case "active":
                            required |= UssPseudoState.Active;
                            break;
                        case "inactive":
                            forbidden |= UssPseudoState.Active;
                            break;
                        case "focus":
                            required |= UssPseudoState.Focus;
                            break;
                        case "disabled":
                            required |= UssPseudoState.Disabled;
                            break;
                        case "enabled":
                            forbidden |= UssPseudoState.Disabled;
                            break;
                        case "checked":
                            required |= UssPseudoState.Checked;
                            break;
                        case "root":
                            required |= UssPseudoState.Root;
                            break;
                        default:
                            error = "the pseudo-class ':" + pseudo + "' is not supported";
                            return false;
                    }

                    any = true;
                }
                else
                {
                    break;
                }
            }

            if (!any)
            {
                error = "unexpected '" + source[i] + "'";
                return false;
            }

            if (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] != '>' && source[i] != '+'
                && source[i] != '~' && source[i] != '[' && source[i] != ',')
            {
                error = "unexpected '" + source[i] + "' in a selector";
                return false;
            }

            compound = new UssCompoundSelector(typeName, name, classes?.ToArray(), required, forbidden);
            return true;
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_' || c == '-';
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-';
        }

        private static string ReadIdent(string source, ref int i)
        {
            int start = i;
            while (i < source.Length && IsIdentChar(source[i]))
            {
                i++;
            }

            return source.Substring(start, i - start);
        }

        private static int CountBits(byte value)
        {
            int count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }

            return count;
        }

        private static string Normalize(List<UssCompoundSelector> compounds, List<UssCombinator> combinators)
        {
            StringBuilder builder = new();
            for (int i = 0; i < compounds.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(combinators[i] == UssCombinator.Child ? " > " : " ");
                }

                UssCompoundSelector compound = compounds[i];
                bool wroteAny = false;
                if (compound.TypeName != null)
                {
                    builder.Append(compound.TypeName);
                    wroteAny = true;
                }

                if (compound.Name != null)
                {
                    builder.Append('#').Append(compound.Name);
                    wroteAny = true;
                }

                for (int c = 0; c < compound.Classes.Length; c++)
                {
                    builder.Append('.').Append(compound.Classes[c]);
                    wroteAny = true;
                }

                AppendPseudo(builder, compound.Required, compound.Forbidden, ref wroteAny);
                if (!wroteAny)
                {
                    builder.Append('*');
                }
            }

            return builder.ToString();
        }

        private static void AppendPseudo(StringBuilder builder, UssPseudoState required, UssPseudoState forbidden,
            ref bool wroteAny)
        {
            if ((required & UssPseudoState.Root) != 0)
            {
                builder.Append(":root");
                wroteAny = true;
            }

            if ((required & UssPseudoState.Hover) != 0)
            {
                builder.Append(":hover");
                wroteAny = true;
            }

            if ((required & UssPseudoState.Active) != 0)
            {
                builder.Append(":active");
                wroteAny = true;
            }

            if ((forbidden & UssPseudoState.Active) != 0)
            {
                builder.Append(":inactive");
                wroteAny = true;
            }

            if ((required & UssPseudoState.Focus) != 0)
            {
                builder.Append(":focus");
                wroteAny = true;
            }

            if ((required & UssPseudoState.Disabled) != 0)
            {
                builder.Append(":disabled");
                wroteAny = true;
            }

            if ((forbidden & UssPseudoState.Disabled) != 0)
            {
                builder.Append(":enabled");
                wroteAny = true;
            }

            if ((required & UssPseudoState.Checked) != 0)
            {
                builder.Append(":checked");
                wroteAny = true;
            }
        }
    }
}
