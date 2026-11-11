using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Npc;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.UiFocus;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// M0 "ready to play", the mods scope side: manage_mods and execute_lua spend one rate limit, the player camera
    /// preset reaches the world, the default scripter sub-agent reads the Lua tools from its profile, and typing in
    /// the in-world chat overlay never reaches the bundled controls (overlay -> UI focus -> world input focus).
    /// </summary>
    [TestFixture]
    public sealed class ReadyToPlayModsCompositionEditModeTests
    {
        private const int KeyW = 119;
        private const double FrameSeconds = 1d / 60d;

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void ManageMods_SpendsTheLimiterExecuteLuaSpends()
        {
            AgentMemoryPolicy policy = new();
            ContainerBuilder builder = NewModsContainer(policy);

            using IObjectResolver container = builder.Build();
            LuaGenerationRateLimiter limiter = container.Resolve<LuaGenerationRateLimiter>();
            LuaModsLlmTool manageMods = FindTool<LuaModsLlmTool>(policy, "manage_mods");

            Assert.IsNotNull(manageMods, "the Programmer gets manage_mods through the lua-builder profile");
            Assert.AreSame(limiter, manageMods.RateLimiter, "one budget of AI world changes for both tools");
        }

        [Test]
        public void PlayerCamera_DefaultIsTheThirdPersonModule_AndThePresetCanSwitchItOff()
        {
            AgentMemoryPolicy policy = new();
            using (IObjectResolver container = NewModsContainer(policy).Build())
            {
                Assert.IsInstanceOf<RbxDefaultCameraModule>(WorldApi(container).CameraModule);
            }

            ContainerBuilder builder = NewModsContainer(new AgentMemoryPolicy());
            builder.RegisterInstance(new CoreAiPlayerCameraSettings { useDefaultCamera = false });
            using (IObjectResolver container = builder.Build())
            {
                Assert.IsNull(WorldApi(container).CameraModule, "the game owns the camera");
            }
        }

        [Test]
        public void PlayerCamera_AFactoryReplacesTheDefaultModule()
        {
            RbxDefaultCameraModule own = new(new RbxCameraModuleOptions { DefaultZoomDistance = 30d });
            ContainerBuilder builder = NewModsContainer(new AgentMemoryPolicy());
            builder.RegisterInstance(new CoreAiPlayerCameraSettings { ModuleFactory = () => own });

            using IObjectResolver container = builder.Build();
            Assert.AreSame(own, WorldApi(container).CameraModule);
        }

        [Test]
        public void DefaultScripter_JoinsOnceTheModsScopeFilledTheLuaBuilderProfile()
        {
            AgentMemoryPolicy policy = new();
            SubAgentRegistry registry = new();
            ContainerBuilder builder = NewModsContainer(policy);
            builder.RegisterInstance(CoreAiDefaultSubAgents.CreateBuiltIn());
            builder.RegisterInstance<ISubAgentRegistry>(registry);

            using IObjectResolver container = builder.Build();

            Assert.IsTrue(registry.TryGet(CoreAiDefaultSubAgents.ScripterId, out SubAgentDefinition scripter));
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, scripter.RoleId);
            Assert.IsTrue(scripter.AllowsTool(LuaTool.ExecuteLuaToolName));
            Assert.IsTrue(scripter.AllowsTool("manage_mods"));
            Assert.IsFalse(scripter.AllowsTool(DelegateTaskTool.DefaultName));
        }

        [Test]
        public void WithoutOrchestratorMode_TheModsScopeRegistersNoSubAgent()
        {
            AgentMemoryPolicy policy = new();
            SubAgentRegistry registry = new();
            ContainerBuilder builder = NewModsContainer(policy);
            builder.RegisterInstance<ISubAgentRegistry>(registry);

            using IObjectResolver container = builder.Build();

            Assert.AreEqual(0, registry.Definitions.Count, "no default set registered, nothing is added");
        }

        // ==================== AI from Lua (framework setting) ====================

        private const string CreateAgentProbe = @"
            local ok, err = pcall(function()
                return game:GetService('AIService'):CreateAgent({ Name = 'Bob' })
            end)
            workspace:SetAttribute('agentOk', ok)
            workspace:SetAttribute('agentError', tostring(err))";

        [Test]
        public async Task LuaAi_SettingOff_CreateAgentIsRefusedNamingTheSetting_InTheInitialAndAStagedWorld()
        {
            ContainerBuilder builder = NewModsContainer(new AgentMemoryPolicy());
            builder.RegisterInstance(new CoreAiLuaAiOptions { AllowAgentsFromLua = false });
            using IObjectResolver container = builder.Build();

            (bool ok, string error) initial = RunCreateAgentProbe(container);
            await ReplaceWorldAsync(container);
            (bool ok, string error) staged = RunCreateAgentProbe(container);

            Assert.IsFalse(initial.ok);
            StringAssert.Contains(CoreAiLuaAiOptions.SettingName, initial.error);
            Assert.IsFalse(staged.ok, "a world a load staged gets the same framework setting");
            StringAssert.Contains(CoreAiLuaAiOptions.SettingName, staged.error);
        }

        [Test]
        public async Task LuaAi_SettingOn_CreateAgentWorks_InTheInitialAndAStagedWorld()
        {
            ContainerBuilder builder = NewModsContainer(new AgentMemoryPolicy());
            builder.RegisterInstance(new CoreAiLuaAiOptions { AllowAgentsFromLua = true });
            builder.RegisterInstance<INpcDirector>(new NpcDirector(UnavailableAiOrchestrationService.Instance));
            using IObjectResolver container = builder.Build();

            (bool ok, string error) initial = RunCreateAgentProbe(container);
            await ReplaceWorldAsync(container);
            (bool ok, string error) staged = RunCreateAgentProbe(container);

            Assert.IsTrue(initial.ok, initial.error);
            Assert.IsTrue(staged.ok, staged.error);
        }

        [Test]
        public void LuaAi_TheModsScopeRegistersTheSettingOffByDefault_AndKeepsAHostsOwnOptions()
        {
            UnityEngine.GameObject scopeObject = new("ReadyToPlay mods scope");
            try
            {
                scopeObject.SetActive(false);
                CoreAiModsLifetimeScope scope = scopeObject.AddComponent<CoreAiModsLifetimeScope>();
                Assert.IsFalse(scope.AllowAgentsFromLua, "off unless the game's framework composition says so");
                scope.SetWorldPackageStore(new P3MemoryPackageStore());

                ContainerBuilder defaults = new();
                Configure(scope, defaults);
                Assert.IsTrue(defaults.Exists(typeof(CoreAiLuaAiOptions)));

                CoreAiLuaAiOptions own = new() { AllowAgentsFromLua = true };
                ContainerBuilder hosted = new();
                hosted.RegisterInstance(own);
                Configure(scope, hosted);
                using IObjectResolver container = hosted.Build();
                Assert.AreSame(own, container.Resolve<CoreAiLuaAiOptions>(), "a host registration wins");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scopeObject);
            }
        }

        // ==================== Runtime UI focus: one reporter per surface ====================

        [Test]
        public void RuntimeUiFocus_WithTheCoreReporter_TheLuaSurfaceStandsDown_AndTheSurfaceIsReportedOnce()
        {
            CoreAI.RuntimeUi.RuntimeUiHost host = new(new CoreAI.RuntimeUi.RuntimeUiHostOptions { AutoCreateSurface = false });
            CountingFocus focus = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<CoreAI.RuntimeUi.IRuntimeUiHost>(host);
            builder.RegisterInstance<ICoreAiUiFocus>(focus);
            builder.RegisterCoreAiUiFocus();
            builder.RegisterRuntimeUiLua();

            using (IObjectResolver container = builder.Build())
            {
                Assert.AreEqual(1, focus.ReportsFor(CoreAiUiFocusInstaller.RuntimeUiSurfaceId),
                    "the CoreAI scope's reporter, not a second one from the Lua surface");
            }

            host.Dispose();
        }

        [Test]
        public void RuntimeUiFocus_WithoutTheCoreReporter_TheLuaSurfaceReportsTheHost()
        {
            CoreAI.RuntimeUi.RuntimeUiHost host = new(new CoreAI.RuntimeUi.RuntimeUiHostOptions { AutoCreateSurface = false });
            CountingFocus focus = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<CoreAI.RuntimeUi.IRuntimeUiHost>(host);
            builder.RegisterInstance<ICoreAiUiFocus>(focus);
            builder.RegisterRuntimeUiLua();

            using (IObjectResolver container = builder.Build())
            {
                Assert.AreEqual(1, focus.ReportsFor(CoreAiUiFocusInstaller.RuntimeUiSurfaceId),
                    "a hand-built CoreAI scope without the reporter still gets the surface reported");
            }

            host.Dispose();
        }

        // ==================== Typing in the overlay ====================

        [Test]
        public void TypingInTheChatOverlay_DoesNotMoveTheCharacter()
        {
            ControlledWorld world = LoadControls();
            FakeOverlayInput overlay = new();
            CoreAiUiFocus focus = new();
            using ChatOverlayUiFocusReporter reporter = new(overlay, focus, CoreAiUiFocusInstaller.ChatOverlaySurfaceId);
            using CoreAiUiFocusBridge bridge = new(focus, world.Focus);

            overlay.Set(true);
            world.Source.PressKey(KeyW);
            world.Frames(3);

            Assert.IsTrue(world.Focus.IsGameInputCaptured, "the open overlay holds the world's input");
            Assert.AreEqual(0, world.Motor.NonZeroMoves, "the W typed into the overlay never reaches Humanoid:Move");
        }

        [Test]
        public void WithTheOverlayClosed_TheSameKeyWalks()
        {
            ControlledWorld world = LoadControls();
            FakeOverlayInput overlay = new();
            CoreAiUiFocus focus = new();
            using ChatOverlayUiFocusReporter reporter = new(overlay, focus, CoreAiUiFocusInstaller.ChatOverlaySurfaceId);
            using CoreAiUiFocusBridge bridge = new(focus, world.Focus);

            overlay.Set(true);
            overlay.Set(false);
            world.Source.PressKey(KeyW);
            world.Frames(3);

            Assert.IsFalse(world.Focus.IsGameInputCaptured);
            Assert.Greater(world.Motor.NonZeroMoves, 0, "the closed overlay leaves the keys to the game");
        }

        // ==================== Helpers ====================

        private static ContainerBuilder NewModsContainer(AgentMemoryPolicy policy)
        {
            ContainerBuilder builder = new();
            builder.RegisterCoreAiInstantEditLoop(new CoreAiInstantEditSettings(), () => false);
            builder.RegisterCoreAiMods(
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null,
                worldSessionSourceStore: new P3MemorySourceStore(),
                worldPackageStore: new P3MemoryPackageStore());
            builder.RegisterInstance(policy);
            builder.RegisterInstance<ICoreAISettings>(new CoreAISettingsOptions());
            builder.RegisterInstance<IGameLogger>(new SilentLogger());
            builder.RegisterInstance<Logging.ILog>(Logging.NullLog.Instance);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterInstance<ILuaModStore>(new RbxApi.Acceptance.Mvp1AcceptanceMemoryStore());
            return builder;
        }

        private static (bool ok, string error) RunCreateAgentProbe(IObjectResolver container)
        {
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            session.CurrentConcreteRuntime.LoadMod(host, "ai-probe", CreateAgentProbe, LuaCapabilities.All, false);
            RbxInstance workspace = session.CurrentRbxApi.Game.FindFirstChildOfClass("Workspace");
            return (workspace.GetAttribute("agentOk") is bool ok && ok, workspace.GetAttribute("agentError") as string ?? "");
        }

        private static async Task ReplaceWorldAsync(IObjectResolver container)
        {
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            LuaCsRbxApiBindings before = session.CurrentRbxApi;
            RbxWorldLoadResult loaded = await session.LoadConfirmedAsync(session.CaptureCurrent());
            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreNotSame(before, session.CurrentRbxApi, "the load staged a new world");
        }

        private static void Configure(CoreAiModsLifetimeScope scope, IContainerBuilder builder)
        {
            typeof(CoreAiModsLifetimeScope)
                .GetMethod("Configure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(IContainerBuilder) }, null)
                .Invoke(scope, new object[] { builder });
            builder.RegisterInstance<IGameLogger>(new SilentLogger());
            builder.RegisterInstance<Logging.ILog>(Logging.NullLog.Instance);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterInstance<ILuaModStore>(new RbxApi.Acceptance.Mvp1AcceptanceMemoryStore());
        }

        private static LuaCsRbxApiBindings WorldApi(IObjectResolver container)
        {
            return container.Resolve<LuaCsModStack>().GameplayBindings.RbxApi;
        }

        private static T FindTool<T>(AgentMemoryPolicy policy, string name) where T : class, ILlmTool
        {
            foreach (ILlmTool tool in policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer))
            {
                if (tool != null && tool.Name == name)
                {
                    return tool as T;
                }
            }

            return null;
        }

        private static string ControlScriptSource()
        {
            string path = System.IO.Path.Combine(UnityEngine.Application.dataPath, "CoreAIMods", "Runtime",
                "Resources", "CoreAIMods", "coreai_player_controls.lua");
            return System.IO.File.ReadAllText(path);
        }

        private static ControlledWorld LoadControls()
        {
            ControlledWorld world = new()
            {
                Focus = new InputFocusState(),
                Motor = new RecordingMotor()
            };
            world.Source = new InMemoryInputSource { FocusState = world.Focus };
            world.Roblox = new LuaCsRbxApiBindings(inputSource: world.Source);
            world.Roblox.AttachCharacterMotorFactory(_ => world.Motor);
            world.Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = new RbxApi.Acceptance.Mvp1AcceptanceMemoryStore(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = world.Roblox
            });
            world.Roblox.Players.EnsureActor(world.Roblox.Registry, "player-1");
            world.Stack.Runtime.LoadMod("coreai_player_controls", ControlScriptSource());
            world.Frames(3);
            return world;
        }

        private sealed class ControlledWorld
        {
            public InMemoryInputSource Source;
            public InputFocusState Focus;
            public RecordingMotor Motor;
            public LuaCsRbxApiBindings Roblox;
            public LuaCsModStack Stack;

            public void Frames(int count)
            {
                for (int index = 0; index < count; index++)
                {
                    Roblox.Scheduler.Advance(FrameSeconds);
                }
            }
        }

        private sealed class RecordingMotor : IRbxCharacterMotor
        {
            public int NonZeroMoves;

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => true;

            public bool IsFlying => false;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
            }

            public void Move(RbxVector3 directionStuds)
            {
                if (directionStuds.Magnitude > 1e-4f)
                {
                    NonZeroMoves++;
                }
            }

            public bool SetFlying(bool flying)
            {
                return false;
            }
        }

        private sealed class CountingFocus : ICoreAiUiFocus
        {
            private readonly System.Collections.Generic.Dictionary<string, int> _reports = new();

            public bool HasTextFocus => false;

            public bool IsPointerOverUi => false;

            public event System.Action Changed
            {
                add { }
                remove { }
            }

            public int ReportsFor(string surfaceId)
            {
                return _reports.TryGetValue(surfaceId, out int count) ? count : 0;
            }

            public void Report(string surfaceId, bool textFocused, bool pointerOver)
            {
                _reports[surfaceId] = ReportsFor(surfaceId) + 1;
            }

            public void Clear(string surfaceId)
            {
            }
        }

        private sealed class FakeOverlayInput : IChatOverlayInputState
        {
            public bool IsInputOpen { get; private set; }

            public event System.Action<bool> InputOpenChanged;

            public void Set(bool open)
            {
                IsInputOpen = open;
                InputOpenChanged?.Invoke(open);
            }
        }

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
