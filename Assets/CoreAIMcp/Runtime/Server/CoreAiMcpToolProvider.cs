using System;
using System.Collections.Generic;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using CoreAI.Mcp.Tools;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Builds the <see cref="McpToolRegistry"/> from whatever CoreAI services are present in the current
    /// composition. This is the single "register only what's available" seam: each tool is added only
    /// when its backing service was resolved, so a Lua-only game exposes execute_lua/manage_mods while a
    /// full game also exposes world_command + screenshot. Pure (no Unity, no container) so the presence
    /// logic is unit-tested with fakes.
    /// </summary>
    public static class CoreAiMcpToolProvider
    {
        /// <param name="luaExecutor">One-off Lua executor (execute_lua). Required - execute_lua is absent without it.</param>
        /// <param name="modRuntime">Persistent mod runtime (manage_mods). Optional.</param>
        /// <param name="settings">Settings for the manage_mods logging path. Required for manage_mods.</param>
        /// <param name="logger">Logger for the manage_mods path. Required for manage_mods.</param>
        /// <param name="modCapabilities">Capability tier applied to mods loaded via manage_mods.</param>
        /// <param name="modActorIdentityProvider">Explicit host identity for manage_mods. Null omits the tool.</param>
        /// <param name="logService">Lua log service (get_mod_logs). Optional.</param>
        /// <param name="worldTool">Constructed world tool (world_command). Optional - absent when no executor resolved.</param>
        /// <param name="skills">Programmer-role skills (read_skill). Optional - absent when empty/null.</param>
        /// <param name="screenshotSource">Screenshot source (screenshot). Optional - absent when null.</param>
        /// <param name="residencyPolicy">
        /// Host residency override (delegate channel). Null means all Native except
        /// <paramref name="brokeredTools"/>, which default to Dynamic.
        /// </param>
        /// <param name="readEnvironmentVariable">Variable reader (variable channel). Null reads the process environment.</param>
        /// <param name="warningSink">Unknown variable names warn here. Null sinks to the CoreAI log.</param>
        /// <param name="brokeredTools">
        /// Additional tools reached through <c>coreai_tools</c> by default, so they cost no <c>tools/list</c>
        /// context until used. <c>COREAI_MCP_NATIVE</c> or an explicit <paramref name="residencyPolicy"/>
        /// can still list them. Null adds none.
        /// </param>
        /// <param name="modRateLimiter">
        /// The mods scope's shared limiter of AI world changes, which MCP <c>manage_mods</c> then spends like the
        /// in-game tools. Null leaves it unlimited.
        /// </param>
        public static McpToolRegistry Build(
            LuaTool.ILuaExecutor luaExecutor,
            ILuaModRuntime modRuntime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities modCapabilities,
            IActorIdentityProvider modActorIdentityProvider,
            ILuaLogService logService,
            WorldLlmTool worldTool,
            IReadOnlyList<SkillSet> skills,
            IScreenshotSource screenshotSource,
            IMcpToolResidencyPolicy residencyPolicy = null,
            Func<string, string> readEnvironmentVariable = null,
            Action<string> warningSink = null,
            IReadOnlyList<IMcpTool> brokeredTools = null,
            LuaGenerationRateLimiter modRateLimiter = null)
        {
            return BuildWithModFolders(luaExecutor, modRuntime, settings, logger, modCapabilities,
                modActorIdentityProvider, logService, worldTool, skills, screenshotSource, residencyPolicy,
                readEnvironmentVariable, warningSink, brokeredTools, modRateLimiter, null);
        }

        /// <summary>
        /// <see cref="Build"/> whose <c>manage_mods</c> also shares the mod store's folder list (the one the in-game
        /// tool, the Hub Mods page and the world session use); null keeps one in-memory list.
        /// </summary>
        /// <remarks>
        /// WHY a separate name and not another optional parameter of Build: a parameter of the folder type would
        /// make every caller of Build reference the mod-folders assembly.
        /// </remarks>
        internal static McpToolRegistry BuildWithModFolders(
            LuaTool.ILuaExecutor luaExecutor,
            ILuaModRuntime modRuntime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities modCapabilities,
            IActorIdentityProvider modActorIdentityProvider,
            ILuaLogService logService,
            WorldLlmTool worldTool,
            IReadOnlyList<SkillSet> skills,
            IScreenshotSource screenshotSource,
            IMcpToolResidencyPolicy residencyPolicy,
            Func<string, string> readEnvironmentVariable,
            Action<string> warningSink,
            IReadOnlyList<IMcpTool> brokeredTools,
            LuaGenerationRateLimiter modRateLimiter,
            CoreAI.Mods.Folders.IModFolderListStore modFolderList,
            IReadOnlyList<IMcpTool> nativeTools = null,
            ILlmAsyncMarshaler logWaitMarshaler = null,
            CoreAI.Mods.WorldPackages.IConfirmedWorldMutationGate modMutationGate = null)
        {
            List<IMcpTool> tools = new();

            if (luaExecutor != null)
            {
                tools.Add(modActorIdentityProvider == null
                    ? new ExecuteLuaMcpTool(luaExecutor)
                    : new ExecuteLuaMcpTool(
                        luaExecutor,
                        modActorIdentityProvider,
                        BuiltInAgentRoleIds.Programmer));
            }

            if (modRuntime != null && settings != null && logger != null && modActorIdentityProvider != null)
            {
                tools.Add(new ManageModsMcpTool(
                    modRuntime,
                    settings,
                    logger,
                    modCapabilities,
                    modActorIdentityProvider,
                    modRateLimiter,
                    modFolderList,
                    modMutationGate));
            }

            if (logService != null)
            {
                tools.Add(new GetModLogsMcpTool(logService, logWaitMarshaler));
            }

            if (worldTool != null)
            {
                tools.Add(new WorldCommandMcpTool(worldTool));
            }

            if (skills != null && skills.Count > 0)
            {
                tools.Add(new ReadSkillMcpTool(skills));
            }

            if (screenshotSource != null)
            {
                tools.Add(new ScreenshotMcpTool(screenshotSource));
            }

            if (nativeTools != null)
            {
                foreach (IMcpTool tool in nativeTools)
                {
                    if (tool != null) tools.Add(tool);
                }
            }

            HashSet<string> brokeredByDefault = new(StringComparer.Ordinal);
            if (brokeredTools != null)
            {
                foreach (IMcpTool tool in brokeredTools)
                {
                    if (tool == null) continue;
                    tools.Add(tool);
                    brokeredByDefault.Add(tool.Name);
                }
            }

            List<string> knownNames = new(tools.Count);
            foreach (IMcpTool tool in tools)
            {
                knownNames.Add(tool.Name);
            }

            IMcpToolResidencyPolicy hostPolicy = residencyPolicy ?? (brokeredByDefault.Count == 0
                ? null
                : new FuncMcpToolResidencyPolicy(tool => brokeredByDefault.Contains(tool.Name)
                    ? McpToolResidency.Dynamic
                    : McpToolResidency.Native));
            IMcpToolResidencyPolicy effectivePolicy = McpToolResidencyPolicies.FromEnvironment(
                knownNames,
                hostPolicy,
                readEnvironmentVariable,
                warningSink ?? (static message => Log.Instance.Warn(message)));
            return new McpToolRegistry(tools, effectivePolicy, true);
        }
    }
}
