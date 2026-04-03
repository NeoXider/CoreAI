using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace CoreAI.ExampleGame.ArenaSurvival.Infrastructure
{
    /// <summary>
    /// Временно отключает агентов/контроллеры перед запеканием NavMesh, чтобы избежать артефактов и зависаний.
    /// </summary>
    internal static class ArenaNavMeshRuntimeBake
    {
        public static IDisposable SuspendAgentsForNavMeshBake(bool disableCharacterControllers)
        {
            var agents = UnityEngine.Object.FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include);
            var agentStates = new List<(NavMeshAgent agent, bool enabled)>();
            foreach (var a in agents)
            {
                if (a == null)
                    continue;
                agentStates.Add((a, a.enabled));
                a.enabled = false;
            }

            var ccStates = new List<(CharacterController cc, bool enabled)>();
            if (disableCharacterControllers)
            {
                var ccs = UnityEngine.Object.FindObjectsByType<CharacterController>(FindObjectsInactive.Include);
                foreach (var cc in ccs)
                {
                    if (cc == null)
                        continue;
                    ccStates.Add((cc, cc.enabled));
                    cc.enabled = false;
                }
            }

            return new Restore(agentStates, ccStates);
        }

        /// <param name="forceFullRebuild">Если true — всегда <see cref="NavMeshSurface.BuildNavMesh"/> (дороже).</param>
        public static void EnsureNavMeshBuilt(NavMeshSurface surface, bool forceFullRebuild)
        {
            if (surface == null)
                return;
            if (forceFullRebuild || surface.navMeshData == null)
                surface.BuildNavMesh();
        }

        private sealed class Restore : IDisposable
        {
            private readonly List<(NavMeshAgent agent, bool enabled)> _agents;
            private readonly List<(CharacterController cc, bool enabled)> _cc;

            public Restore(List<(NavMeshAgent agent, bool enabled)> agents, List<(CharacterController cc, bool enabled)> cc)
            {
                _agents = agents;
                _cc = cc;
            }

            public void Dispose()
            {
                foreach (var (a, e) in _agents)
                {
                    if (a != null)
                        a.enabled = e;
                }

                foreach (var (c, e) in _cc)
                {
                    if (c != null)
                        c.enabled = e;
                }
            }
        }
    }
}
