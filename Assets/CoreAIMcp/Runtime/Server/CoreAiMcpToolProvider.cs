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
        /// <param name="residencyPolicy">Host residency override (delegate channel). Null means all Native.</param>
        /// <param name="readEnvironmentVariable">Variable reader (variable channel). Null reads the process environment.</param>
        /// <param name="warningSink">Unknown variable names warn here. Null sinks to the CoreAI log.</param>
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
            Action<string> warningSink = null)
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
                    modActorIdentityProvider));
            }

            if (logService != null)
            {
                tools.Add(new GetModLogsMcpTool(logService));
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

            List<string> knownNames = new(tools.Count);
            foreach (IMcpTool tool in tools)
            {
                knownNames.Add(tool.Name);
            }

            IMcpToolResidencyPolicy effectivePolicy = McpToolResidencyPolicies.FromEnvironment(
                knownNames,
                residencyPolicy,
                readEnvironmentVariable,
                warningSink ?? (static message => Log.Instance.Warn(message)));
            return new McpToolRegistry(tools, effectivePolicy, true);
        }
    }
}
