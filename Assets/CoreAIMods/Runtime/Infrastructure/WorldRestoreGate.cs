using System;
using System.Collections;
using CoreAI.Infrastructure.World;
using UnityEngine;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Delays a startup action (mod rehydrate) until <see cref="IWorldStateManager.WorldRestoreCompleted"/>
    /// is true, or a timeout elapses. Closes the audit finding W4 ordering gap: without this, Lua mod
    /// rehydrate (which runs from a VContainer <c>RegisterBuildCallback</c> at child-scope <c>Awake</c>
    /// time) could run before <c>WorldStateEntryPoint.Start()</c> restores the saved world snapshot — a
    /// mod that re-spawns its own objects could double-spawn, or the snapshot's clean-slate destroy
    /// could remove what the mod just made. See <c>WORLD_COMMANDS.md</c> §7.
    /// </summary>
    internal sealed class WorldRestoreGate : MonoBehaviour
    {
        private const float TimeoutSeconds = 5f;

        public void Begin(IWorldStateManager worldState, Action onReady)
        {
            StartCoroutine(WaitThenRun(worldState, onReady));
        }

        private static IEnumerator WaitThenRun(IWorldStateManager worldState, Action onReady)
        {
            float elapsed = 0f;
            while (worldState != null && !worldState.WorldRestoreCompleted && elapsed < TimeoutSeconds)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            onReady?.Invoke();
        }
    }
}
