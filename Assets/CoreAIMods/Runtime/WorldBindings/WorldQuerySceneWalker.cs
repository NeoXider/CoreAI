using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Shared iterative scene-hierarchy walker used by both the MoonSharp
    /// (<see cref="CoreAiWorldQueryLuaBindings"/>) and Lua-CSharp (<c>LuaCsWorldQueryBindings</c>)
    /// world query bindings. Bounds traversal with a visited-node budget so a no-match query over a
    /// deep or wide scene cannot walk the entire hierarchy on the main thread, and uses an explicit
    /// stack instead of recursion to avoid stack-overflow risk on deep hierarchies.
    /// </summary>
    public static class WorldQuerySceneWalker
    {
        /// <summary>
        /// Maximum number of GameObjects visited by <see cref="CollectByName"/> before the walk is
        /// truncated, regardless of how many matches (if any) have been found so far.
        /// </summary>
        public const int MaxVisitedNodes = 10_000;

        /// <summary>Matches a candidate object's name against a search pattern.</summary>
        public delegate bool NameMatch(string objectName, string searchPattern);

        /// <summary>
        /// Walks <paramref name="rootObjects"/> and their descendants depth-first using an explicit
        /// stack, appending the name of every object matched by <paramref name="match"/> to
        /// <paramref name="results"/> until either <paramref name="maxResults"/> matches are collected
        /// or <see cref="MaxVisitedNodes"/> objects have been visited.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the walk stopped early because the visited-node budget was exhausted
        /// (results may be incomplete); <c>false</c> if it finished the reachable hierarchy or
        /// stopped only because enough matches were already found.
        /// </returns>
        public static bool CollectByName(
            IReadOnlyList<GameObject> rootObjects,
            string searchPattern,
            int maxResults,
            NameMatch match,
            List<object> results)
        {
            Stack<GameObject> stack = new();
            for (int i = rootObjects.Count - 1; i >= 0; i--)
            {
                if (rootObjects[i] != null)
                {
                    stack.Push(rootObjects[i]);
                }
            }

            int visited = 0;
            while (stack.Count > 0)
            {
                if (results.Count >= maxResults)
                {
                    return false;
                }

                if (visited >= MaxVisitedNodes)
                {
                    return true;
                }

                GameObject current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                visited++;

                if (string.IsNullOrEmpty(searchPattern) || match(current.name, searchPattern))
                {
                    results.Add(current.name);
                }

                Transform t = current.transform;
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    stack.Push(t.GetChild(i).gameObject);
                }
            }

            return false;
        }
    }
}
