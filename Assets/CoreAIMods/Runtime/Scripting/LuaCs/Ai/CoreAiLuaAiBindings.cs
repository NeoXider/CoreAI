using System;
using CoreAI.Ai.Npc;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The one-line hook of the Lua AI surface (<c>TextGenerator</c> and <c>AIService</c>) into a mod stack: it builds
    /// the surface over the world's <see cref="LuaCsRbxApiBindings"/> and returns the
    /// <c>LuaCsModStackOptions.AdditionalGameplayBindings</c> callback that installs it into every mod and one-off
    /// chunk, chained after the host's own additions. The surface lives as long as the Rbx world.
    /// </summary>
    public static class CoreAiLuaAiBindings
    {
        /// <summary>
        /// Builds the surface and returns the chained callback; returns <paramref name="existing"/> unchanged when there
        /// is no Rbx world. <paramref name="options"/> null keeps the framework setting off.
        /// </summary>
        /// <param name="rbxApi">The world's Rbx bindings.</param>
        /// <param name="options">The host game's framework settings for AI from Lua.</param>
        /// <param name="orchestrator">The model pipeline behind <c>TextGenerator</c>; null leaves generation unavailable.</param>
        /// <param name="director">The NPC director agents join; null leaves <c>CreateAgent</c> unavailable.</param>
        /// <param name="existing">The host's own gameplay additions, kept and run first.</param>
        /// <param name="bodyAnchor">Bubble anchor of an agent's body part in renderer space; null converts its position through RbxSpace.</param>
        /// <param name="anchors">The bubble anchor registry agent bodies are added to; null shows no bubbles for them.</param>
        /// <param name="moderation">The moderation filter of prompts and answers; null passes everything.</param>
        /// <param name="textBackend">Replaces the orchestrator-based text backend.</param>
        /// <param name="log">Diagnostics.</param>
        public static Action<LuaCsApiRegistry, LuaCapabilities> Attach(
            LuaCsRbxApiBindings rbxApi,
            CoreAiLuaAiOptions options,
            IAiOrchestrationService orchestrator,
            INpcDirector director,
            Action<LuaCsApiRegistry, LuaCapabilities> existing = null,
            Func<RbxInstance, NpcPosition?> bodyAnchor = null,
            NpcSpeechAnchorRegistry anchors = null,
            ITextModerationFilter moderation = null,
            IRbxTextGenerationBackend textBackend = null,
            ILog log = null)
        {
            return Attach(rbxApi, options, orchestrator, director, existing, bodyAnchor, anchors, moderation, textBackend,
                log, out _);
        }

        internal static Action<LuaCsApiRegistry, LuaCapabilities> Attach(
            LuaCsRbxApiBindings rbxApi,
            CoreAiLuaAiOptions options,
            IAiOrchestrationService orchestrator,
            INpcDirector director,
            Action<LuaCsApiRegistry, LuaCapabilities> existing,
            Func<RbxInstance, NpcPosition?> bodyAnchor,
            NpcSpeechAnchorRegistry anchors,
            ITextModerationFilter moderation,
            IRbxTextGenerationBackend textBackend,
            ILog log,
            out LuaCsRbxAiServiceAdapter adapter)
        {
            adapter = null;
            if (rbxApi == null)
            {
                return existing;
            }

            CoreAiLuaAiOptions resolved = options ?? new CoreAiLuaAiOptions();
            IRbxTextGenerationBackend text = textBackend ??
                                             (orchestrator != null
                                                 ? new OrchestratorTextGenerationBackend(orchestrator, resolved)
                                                 : null);
            LuaCsRbxAiServiceAdapter created = new(rbxApi, resolved, text, director, bodyAnchor, anchors, moderation,
                null, log);
            adapter = created;
            return (registry, capabilities) =>
            {
                existing?.Invoke(registry, capabilities);
                created.Register(registry, capabilities);
            };
        }
    }
}
