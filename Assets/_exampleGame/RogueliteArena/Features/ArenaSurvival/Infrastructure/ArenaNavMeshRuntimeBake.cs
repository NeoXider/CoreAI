using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace CoreAI.ExampleGame.ArenaSurvival.Infrastructure
{
    /// <summary>
    /// Temporarily disables agents and controllers before NavMesh baking to avoid artifacts and stalls.
    /// </summary>
    internal static class ArenaNavMeshRuntimeBake
    {
        public static IDisposable SuspendAgentsForNavMeshBake(bool disableCharacterControllers)
        {
            NavMeshAgent[] agents = UnityEngine.Object.FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            List<(NavMeshAgent agent, bool enabled)> agentStates = new();
            foreach (NavMeshAgent a in agents)
            {
                if (a == null)
                {
                    continue;
                }

                agentStates.Add((a, a.enabled));
                a.enabled = false;
            }

            List<(CharacterController cc, bool enabled)> ccStates = new();
            if (disableCharacterControllers)
            {
                CharacterController[] ccs = UnityEngine.Object.FindObjectsByType<CharacterController>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                foreach (CharacterController cc in ccs)
                {
                    if (cc == null)
                    {
                        continue;
                    }

                    ccStates.Add((cc, cc.enabled));
                    cc.enabled = false;
                }
            }

            return new Restore(agentStates, ccStates);
        }

        /// <param name="forceFullRebuild">When true, always runs the more expensive full <see cref="NavMeshSurface.BuildNavMesh"/> path.</param>
        public static void EnsureNavMeshBuilt(NavMeshSurface surface, bool forceFullRebuild)
        {
            if (surface == null)
            {
                return;
            }

            if (forceFullRebuild || surface.navMeshData == null)
            {
                surface.BuildNavMesh();
            }
        }

        private sealed class Restore : IDisposable
        {
            private readonly List<(NavMeshAgent agent, bool enabled)> _agents;
            private readonly List<(CharacterController cc, bool enabled)> _cc;

            public Restore(List<(NavMeshAgent agent, bool enabled)> agents,
                List<(CharacterController cc, bool enabled)> cc)
            {
                _agents = agents;
                _cc = cc;
            }

            public void Dispose()
            {
                foreach ((NavMeshAgent a, bool e) in _agents)
                {
                    if (a != null)
                    {
                        a.enabled = e;
                    }
                }

                foreach ((CharacterController c, bool e) in _cc)
                {
                    if (c != null)
                    {
                        c.enabled = e;
                    }
                }
            }
        }
    }
}