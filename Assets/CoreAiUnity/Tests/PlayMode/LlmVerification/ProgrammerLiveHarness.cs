using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Production-like Programmer pipeline for live scenario tests: the real Lua-CSharp mod stack with
    /// the Rbx API bound to the active scene, the production <c>execute_lua</c> / <c>manage_mods</c>
    /// tools, both built-in skills, and the orchestrator over the default agent-memory policy.
    /// </summary>
    internal static class ProgrammerLiveHarness
    {
        /// <summary>Everything one live Programmer scenario needs; dispose in <c>finally</c>.</summary>
        internal sealed class Setup
        {
            public LuaCsModStack Stack;
            public AiOrchestrator Orchestrator;
            public CapturingLlmClient Capturing;
            public CoreAISettingsAsset Settings;
            public ActorContext ActorContext;

            /// <summary>The live Rbx world the Lua bindings write into.</summary>
            public RbxWorldHost WorldHost;

            /// <summary>Scene object owning <see cref="WorldHost"/>; everything built hangs under it.</summary>
            public GameObject WorldHostObject;

            public void Dispose()
            {
                if (WorldHostObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(WorldHostObject);
                    WorldHostObject = null;
                    WorldHost = null;
                }

                if (Settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(Settings);
                    Settings = null;
                }
            }
        }

        internal sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionResult LastResult;

            private readonly ILlmClient _inner;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastResult = await _inner.CompleteAsync(request, cancellationToken);
                return LastResult;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }

        private static readonly Lazy<IObjectResolver> ProductionCoreContainer = new(() =>
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            return builder.Build();
        });

        internal static Setup Build(PlayModeProductionLikeLlmHandle handle, int orchestratorTimeoutSeconds = 600)
        {
            Setup setup = new()
            {
                Settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>()
            };
            setup.Settings.SetOrchestratorTimeoutSeconds(orchestratorTimeoutSeconds);
            IActorIdentityProvider actorIdentityProvider =
                ProductionCoreContainer.Value.Resolve<IActorIdentityProvider>();
            setup.ActorContext = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);

            // WHY: mirrors CoreAiModsInstaller with enableFullLuaAccess=false — mods get All, one-off
            // execute_lua gets All minus Full, the exact production capability split.
            LuaCapabilities scriptCapabilities = LuaCapabilities.All;
            LuaCapabilities oneOffCapabilities = scriptCapabilities & ~LuaCapabilities.Full;

            // WHY: LuaCsRbxApiBindings with no registry runs the Rbx API headless - workspace fills up
            // with Instances that never become GameObjects, so a live run measured 86 parts while the
            // screenshot showed an empty scene. Production always has a world host; so does this harness.
            setup.WorldHostObject = new GameObject("ProgrammerLiveRbxWorld");
            setup.WorldHost = setup.WorldHostObject.AddComponent<RbxWorldHost>();
            setup.WorldHost.Initialize();

            setup.Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = GameLoggerUnscopedFallback.Instance,
                CommandSink = new NullSink(),
                ModStore = new InMemoryModStore(),
                Log = Log.Instance,
                Capabilities = scriptCapabilities,
                OneOffCapabilities = oneOffCapabilities,
                RbxApi = new LuaCsRbxApiBindings(
                    registry: setup.WorldHost.Registry,
                    game: setup.WorldHost.Game,
                    partSink: setup.WorldHost.Binder,
                    cameraRig: setup.WorldHost.CameraRig,
                    pickSource: setup.WorldHost.PickSource)
            });

            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                new LuaLlmTool(setup.Stack.ToolExecutor, setup.Settings, Log.Instance,
                    new LuaGenerationRateLimiter()));
            policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                new LuaModsLlmTool(
                    setup.Stack.Runtime,
                    setup.Settings,
                    Log.Instance,
                    scriptCapabilities,
                    true,
                    actorIdentityProvider,
                    BuiltInAgentRoleIds.Programmer));
            policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                BuiltInLuaModdingSkillText.SkillName,
                BuiltInLuaModdingSkillText.SkillDescription,
                BuiltInLuaModdingSkillText.Instructions));
            policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                BuiltInRbxApiSkillText.SkillName,
                BuiltInRbxApiSkillText.SkillDescription,
                BuiltInRbxApiSkillText.Instructions));
            // WHY: the Programmer prompt and the execute_lua description both tell the model to call
            // read_skill('Full Lua') before reaching for reflection, and CoreAiModsInstaller registers it
            // in production. Leaving it out here made that first call fail in every live run and count
            // toward the consecutive-error budget.
            policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                BuiltInFullLuaSkillText.SkillName,
                BuiltInFullLuaSkillText.SkillDescription,
                BuiltInFullLuaSkillText.Instructions));

            InMemoryStore memoryStore = new();
            setup.Capturing = new CapturingLlmClient(handle.WrapWithMemoryStore(memoryStore));

            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            setup.Orchestrator = new AiOrchestrator(
                new SoloAuthorityHost(),
                setup.Capturing,
                new NullSink(),
                new SessionTelemetryCollector(),
                composer,
                memoryStore,
                policy,
                new CompositeRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                setup.Settings,
                actorIdentityProvider);

            return setup;
        }

        internal static void LogToolCallTranscript(string label)
        {
            IReadOnlyList<LlmToolCallRecord> history = CoreAi.GetToolCallHistorySnapshot();
            TestContext.WriteLine($"[{label}] Tool calls recorded: {history?.Count ?? 0}");
            if (history == null)
            {
                return;
            }

            foreach (LlmToolCallRecord record in history)
            {
                if (record == null)
                {
                    continue;
                }

                string args = record.Info.ArgumentsJson ?? "";
                TestContext.WriteLine(
                    $"[{label}]   {record.Info.ToolName} [{record.Status}] " +
                    $"args={args.Substring(0, Math.Min(240, args.Length))}");
            }
        }

        /// <summary>Thread-safe in-memory <see cref="ILuaModStore"/>: store_set/store_get exist without FileLuaModStore.</summary>
        private sealed class InMemoryModStore : ILuaModStore
        {
            private readonly object _lock = new();
            private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.Ordinal);

            public string Get(string modId, string key)
            {
                lock (_lock)
                {
                    return _data.TryGetValue(modId ?? "", out Dictionary<string, string> mod) &&
                           mod.TryGetValue(key ?? "", out string value)
                        ? value
                        : "";
                }
            }

            public void Set(string modId, string key, string value)
            {
                lock (_lock)
                {
                    if (!_data.TryGetValue(modId ?? "", out Dictionary<string, string> mod))
                    {
                        mod = new Dictionary<string, string>(StringComparer.Ordinal);
                        _data[modId ?? ""] = mod;
                    }

                    if (value == null)
                    {
                        mod.Remove(key ?? "");
                    }
                    else
                    {
                        mod[key ?? ""] = value;
                    }
                }
            }

            public void Clear(string modId)
            {
                lock (_lock)
                {
                    _data.Remove(modId ?? "");
                }
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
