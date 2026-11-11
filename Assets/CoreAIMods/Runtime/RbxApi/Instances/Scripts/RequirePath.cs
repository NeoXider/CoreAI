using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>Where a require-by-string path starts resolving.</summary>
    public enum RequirePathAnchor
    {
        /// <summary>The calling chunk's <c>script</c>: <c>./</c>, <c>../</c> and <c>@self/</c>.</summary>
        Script,

        /// <summary>The DataModel: <c>@game/</c>.</summary>
        Game
    }

    /// <summary>One step of a parsed path: to the parent, or to the child with a name.</summary>
    public readonly struct RequirePathStep
    {
        /// <summary>True for a step to the parent (the <c>..</c> component, and the parents the prefixes imply).</summary>
        public readonly bool IsParent;

        /// <summary>The child name for a child step; null for a parent step.</summary>
        public readonly string ChildName;

        private RequirePathStep(bool isParent, string childName)
        {
            IsParent = isParent;
            ChildName = childName;
        }

        public static RequirePathStep Parent => new RequirePathStep(true, null);

        public static RequirePathStep Child(string name)
        {
            return new RequirePathStep(false, name);
        }
    }

    /// <summary>The outcome of <see cref="RequirePath.Resolve{TNode}"/>: the instance, or why the path does not reach one.</summary>
    public readonly struct RequirePathResolution<TNode> where TNode : class
    {
        public readonly TNode Node;
        public readonly string Error;

        public bool Found => Node != null;

        public RequirePathResolution(TNode node, string error)
        {
            Node = node;
            Error = error;
        }
    }

    /// <summary>
    /// A parsed require-by-string path (mirror <c>reference/engine/globals/LuaGlobals.yaml</c>, <c>require</c>; rulebook
    /// R3.5): <c>./</c> starts at <c>script.Parent</c>, <c>../</c> at <c>script.Parent.Parent</c>, <c>@self/</c> at
    /// <c>script</c>, <c>@game/</c> at <c>game</c>; each later component is a child, except <c>..</c>, which is the parent.
    /// Resolution never waits (R3.6): a missing instance is an error at once.
    /// </summary>
    public sealed class RequirePath
    {
        private const string PrefixHint = "start it with ./, ../, @self/ or @game/";

        /// <summary>The path as written.</summary>
        public string Text { get; }

        public RequirePathAnchor Anchor { get; }

        /// <summary>The steps from the anchor, the parents implied by <c>./</c> and <c>../</c> included.</summary>
        public IReadOnlyList<RequirePathStep> Steps { get; }

        private RequirePath(string text, RequirePathAnchor anchor, IReadOnlyList<RequirePathStep> steps)
        {
            Text = text;
            Anchor = anchor;
            Steps = steps;
        }

        /// <summary>
        /// Parses a path. Fails, with an error the caller raises as BAD_ARGUMENT, on an empty path, a missing or unknown
        /// prefix (Luau aliases other than <c>@self</c> and <c>@game</c> do not exist on Roblox instances), an empty or
        /// <c>.</c> component, or a path that names nothing after its prefix.
        /// </summary>
        public static bool TryParse(string text, out RequirePath path, out string error)
        {
            path = null;
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                error = "require path is empty; " + PrefixHint;
                return false;
            }

            List<RequirePathStep> steps = new List<RequirePathStep>();
            RequirePathAnchor anchor = RequirePathAnchor.Script;
            string rest;
            if (text.StartsWith("./", StringComparison.Ordinal))
            {
                steps.Add(RequirePathStep.Parent);
                rest = text.Substring(2);
            }
            else if (text.StartsWith("../", StringComparison.Ordinal))
            {
                steps.Add(RequirePathStep.Parent);
                steps.Add(RequirePathStep.Parent);
                rest = text.Substring(3);
            }
            else if (text.StartsWith("@self/", StringComparison.Ordinal))
            {
                rest = text.Substring(6);
            }
            else if (text.StartsWith("@game/", StringComparison.Ordinal))
            {
                anchor = RequirePathAnchor.Game;
                rest = text.Substring(6);
            }
            else if (text.StartsWith("@", StringComparison.Ordinal))
            {
                int slash = text.IndexOf('/');
                string alias = slash > 0 ? text.Substring(0, slash) : text;
                error = "require path alias '" + alias + "' does not exist on Roblox instances; " + PrefixHint;
                return false;
            }
            else
            {
                error = "require path '" + text + "' has no prefix; " + PrefixHint;
                return false;
            }

            if (rest.Length == 0)
            {
                error = "require path '" + text + "' names no module after its prefix";
                return false;
            }

            string[] components = rest.Split('/');
            for (int index = 0; index < components.Length; index++)
            {
                string component = components[index];
                if (component.Length == 0)
                {
                    error = "require path '" + text + "' has an empty component";
                    return false;
                }

                if (component == ".")
                {
                    error = "require path '" + text + "' has a '.' component; '.' is only the ./ prefix";
                    return false;
                }

                steps.Add(component == ".." ? RequirePathStep.Parent : RequirePathStep.Child(component));
            }

            path = new RequirePath(text, anchor, steps.AsReadOnly());
            return true;
        }

        /// <summary>
        /// Walks the steps from <paramref name="script"/> or <paramref name="game"/> through the two lookups the caller
        /// supplies (a parent, or null; the first child with a name, or null), without waiting.
        /// </summary>
        public RequirePathResolution<TNode> Resolve<TNode>(TNode script, TNode game, Func<TNode, TNode> getParent,
            Func<TNode, string, TNode> findFirstChild) where TNode : class
        {
            if (getParent == null)
            {
                throw new ArgumentNullException(nameof(getParent));
            }

            if (findFirstChild == null)
            {
                throw new ArgumentNullException(nameof(findFirstChild));
            }

            TNode node = Anchor == RequirePathAnchor.Game ? game : script;
            if (node == null)
            {
                return new RequirePathResolution<TNode>(null,
                    "require path '" + Text + "' starts at " + (Anchor == RequirePathAnchor.Game ? "game" : "script")
                    + ", which this chunk does not have");
            }

            for (int index = 0; index < Steps.Count; index++)
            {
                RequirePathStep step = Steps[index];
                TNode next = step.IsParent ? getParent(node) : findFirstChild(node, step.ChildName);
                if (next == null)
                {
                    string detail = step.IsParent
                        ? "goes above the top of the tree"
                        : "finds no child named '" + step.ChildName + "'";
                    return new RequirePathResolution<TNode>(null,
                        "require path '" + Text + "' " + detail + " (require does not wait for a module to appear)");
                }

                node = next;
            }

            return new RequirePathResolution<TNode>(node, null);
        }
    }
}
