using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Logging;
using CoreAI.RuntimeUi;
using CoreAI.Tests.EditMode.ChatOverlay;
using CoreAI.UiFocus;
using CoreAI.Unity;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// M0 "ready to play", the CoreAI scope side: the UI focus gate in front of the chat overlay's open keys, the
    /// overlay reporting its typing into the focus, the scope registering runtime UI, the overlay and role
    /// definitions, the prompts manifest's roles becoming known roles, and the agent-vision blocks.
    /// </summary>
    [TestFixture]
    public sealed class ReadyToPlayCompositionEditModeTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void DestroyCreated()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        // ==================== Focus gate and overlay reporter ====================

        [Test]
        public void Gate_AllowsTheOverlayKeys_OnlyWhileNoCoreAiTextFieldHasFocus()
        {
            CoreAiUiFocus focus = new();
            UiFocusChatOverlayOpenGate gate = new(focus);

            Assert.IsTrue(gate.IsOpenAllowed(), "nothing types: T opens the chat");
            focus.Report("coreai.hub.page", true, false);
            Assert.IsFalse(gate.IsOpenAllowed(), "a Hub text field has the keyboard");
            focus.Report("coreai.hub.page", false, true);
            Assert.IsTrue(gate.IsOpenAllowed(), "hovering a panel is not typing");
        }

        [Test]
        public void Reporter_OverlayOpen_HoldsTheTextFocus_AndClosingOrDisposingReleasesIt()
        {
            CoreAiUiFocus focus = new();
            FakeOverlayInput input = new();
            ChatOverlayUiFocusReporter reporter = new(input, focus, CoreAiUiFocusInstaller.ChatOverlaySurfaceId);

            Assert.IsTrue(reporter.IsActive);
            Assert.IsFalse(focus.HasTextFocus);
            input.Set(true);
            Assert.IsTrue(focus.HasTextFocus, "typing in the overlay is a CoreAI text focus");
            Assert.IsFalse(focus.IsPointerOverUi);
            input.Set(false);
            Assert.IsFalse(focus.HasTextFocus);

            input.Set(true);
            reporter.Dispose();
            Assert.IsFalse(focus.HasTextFocus, "a disposed scope releases the overlay's claim");
            input.Set(true);
            Assert.IsFalse(focus.HasTextFocus, "and stops listening");
            Assert.AreEqual(0, input.Subscribers);
        }

        [Test]
        public void Reporter_WithoutAnOverlay_IsInert()
        {
            CoreAiUiFocus focus = new();
            ChatOverlayUiFocusReporter reporter = new(null, focus, CoreAiUiFocusInstaller.ChatOverlaySurfaceId);

            Assert.IsFalse(reporter.IsActive);
            Assert.DoesNotThrow(reporter.Dispose);
        }

        [Test]
        public void Composed_WhileTheChatPanelTypes_TheOverlayKeyDoesNothing_AndOpensOnceItStops()
        {
            using IObjectResolver container = BuildOverlayContainer(out FakeKeyboard keyboard);
            ChatOverlayController controller = container.Resolve<ChatOverlayController>();
            ICoreAiUiFocus focus = container.Resolve<ICoreAiUiFocus>();
            CoreAiChatOverlayEntryPoint entryPoint = CreateEntryPoint(container, keyboard);
            Assert.IsInstanceOf<UiFocusChatOverlayOpenGate>(container.Resolve<IChatOverlayOpenGate>());

            focus.Report("coreai.chat.panel", true, false);
            keyboard.Pressed.Add(KeyCode.T);
            entryPoint.PollKeys(1);
            Assert.IsFalse(controller.IsInputOpen, "the gate is closed: the T typed into the panel stays there");

            focus.Report("coreai.chat.panel", false, false);
            entryPoint.PollKeys(2);
            Assert.IsTrue(controller.IsInputOpen, "with the panel idle the same key opens the overlay");

            entryPoint.Dispose();
        }

        [Test]
        public void Composed_TypingInTheOverlay_HoldsTheUiFocus_UntilTheFrameAfterItCloses()
        {
            using IObjectResolver container = BuildOverlayContainer(out FakeKeyboard keyboard);
            ChatOverlayController controller = container.Resolve<ChatOverlayController>();
            ICoreAiUiFocus focus = container.Resolve<ICoreAiUiFocus>();
            CoreAiChatOverlayEntryPoint entryPoint = CreateEntryPoint(container, keyboard);

            keyboard.Pressed.Add(KeyCode.Return);
            entryPoint.PollKeys(1);
            Assert.IsTrue(focus.HasTextFocus, "the world sees the keyboard as taken while the player types");

            keyboard.Pressed.Clear();
            keyboard.Pressed.Add(KeyCode.Escape);
            entryPoint.PollKeys(2);
            Assert.IsTrue(focus.HasTextFocus, "the Escape that closed the input does not reach the game this frame");
            controller.Tick();
            Assert.IsFalse(focus.HasTextFocus, "released on the next chat tick");

            entryPoint.Dispose();
        }

        [Test]
        public void Composed_TheEnterThatClosedTheOverlay_StaysTheChatsThroughTheFramesWorldInputStep()
        {
            using IObjectResolver container = BuildOverlayContainer(out FakeKeyboard keyboard);
            ICoreAiUiFocus focus = container.Resolve<ICoreAiUiFocus>();
            FakeChatOverlayView view = (FakeChatOverlayView)container.Resolve<IChatOverlayView>();
            CoreAiChatOverlayEntryPoint entryPoint = CreateEntryPoint(container, keyboard);
            keyboard.Pressed.Add(KeyCode.Return);
            entryPoint.Tick();
            keyboard.Pressed.Clear();
            Assert.IsTrue(focus.HasTextFocus);

            view.RaiseSubmitted("");
            entryPoint.Tick();
            Assert.IsTrue(focus.HasTextFocus,
                "the overlay ticks ahead of the world's Update: the closing Enter is still the chat's this frame");

            entryPoint.Tick();
            Assert.IsFalse(focus.HasTextFocus, "released on the next frame's tick");

            keyboard.Pressed.Add(KeyCode.Return);
            entryPoint.Tick();
            keyboard.Pressed.Clear();
            keyboard.Pressed.Add(KeyCode.Escape);
            entryPoint.Tick();
            Assert.IsTrue(focus.HasTextFocus, "twin: an Escape read by the same tick is held the same way");
            keyboard.Pressed.Clear();
            entryPoint.Tick();
            Assert.IsFalse(focus.HasTextFocus);

            entryPoint.Dispose();
        }

        [Test]
        public void Installer_KeepsAFocusServiceAndAGateRegisteredBeforeIt()
        {
            CoreAiUiFocus ownFocus = new();
            OwnGate ownGate = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<ICoreAiUiFocus>(ownFocus);
            builder.RegisterInstance<IChatOverlayOpenGate>(ownGate);

            builder.RegisterCoreAiUiFocus();

            using IObjectResolver container = builder.Build();
            Assert.AreSame(ownFocus, container.Resolve<ICoreAiUiFocus>());
            Assert.AreSame(ownGate, container.Resolve<IChatOverlayOpenGate>());
        }

        [Test]
        public void ChatPanelHotkeys_AreIgnored_WhileAnotherCoreAiSurfaceTypes()
        {
            Assert.IsTrue(CoreAI.Chat.CoreAiChatPanel.IsAnotherSurfaceTyping(true, false),
                "typing C into the overlay must not open the chat panel");
            Assert.IsFalse(CoreAI.Chat.CoreAiChatPanel.IsAnotherSurfaceTyping(true, true),
                "the panel's own focused input is handled by its own field");
            Assert.IsFalse(CoreAI.Chat.CoreAiChatPanel.IsAnotherSurfaceTyping(false, false), "nobody types: keys work");
        }

        [Test]
        public void HubHotkeys_AreIgnored_WhileAnotherCoreAiSurfaceTypes()
        {
            Assert.IsTrue(CoreAI.Hub.UI.CoreAiHubWindow.IsAnotherSurfaceTyping(true, false),
                "Escape typed into the overlay must not collapse the Hub");
            Assert.IsFalse(CoreAI.Hub.UI.CoreAiHubWindow.IsAnotherSurfaceTyping(true, true));
            Assert.IsFalse(CoreAI.Hub.UI.CoreAiHubWindow.IsAnotherSurfaceTyping(false, false));
        }

        [Test]
        public void EnableLuaOnWebGl_IsObsoleteEverywhere_AndSaysHowToShipWithoutLua()
        {
            MemberInfo[] members =
            {
                typeof(ICoreAISettings).GetProperty("EnableLuaOnWebGl"),
                typeof(CoreAISettingsOptions).GetProperty("EnableLuaOnWebGl"),
                typeof(CoreAISettings).GetProperty("EnableLuaOnWebGl"),
                typeof(CoreAISettingsAsset).GetProperty("EnableLuaOnWebGl")
            };

            foreach (MemberInfo member in members)
            {
                System.ObsoleteAttribute obsolete = member.GetCustomAttribute<System.ObsoleteAttribute>();
                Assert.IsNotNull(obsolete, member.DeclaringType + "." + member.Name + " reads as a live switch");
                StringAssert.Contains("CoreAiModsLifetimeScope", obsolete.Message, "the reason names the real way off");
                Assert.IsFalse(obsolete.IsError, "existing callers keep compiling");
            }

            Assert.IsNotNull(typeof(CoreAISettingsAsset)
                .GetField("enableLuaOnWebGl", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetCustomAttribute<HideInInspector>(), "the inspector no longer shows a switch that does nothing");
        }

        // ==================== Runtime UI focus (core scope) ====================

        [Test]
        public void RuntimeUiFocus_TheCoreScopeReportsAFocusedField_WithoutTheModsPackage()
        {
            using CoreAI.Tests.EditMode.RuntimeUi.RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud",
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:TextField name=\"name\"/>" +
                "<ui:Button name=\"ok\" text=\"OK\"/></ui:UXML>", "");
            CoreAiUiFocus focus = new();
            using RuntimeUiHostFocusReporter reporter = new(panel.Host, focus, CoreAiUiFocusInstaller.RuntimeUiSurfaceId);

            Assert.IsTrue(reporter.IsActive);
            screen.GetElement("name").Focus();
            Assert.IsTrue(focus.HasTextFocus, "typing in a runtime UI field is a CoreAI text focus");
            screen.GetElement("ok").Focus();
            Assert.IsFalse(focus.HasTextFocus, "a focused button is not typing");
            screen.GetElement("name").Focus();
            reporter.Dispose();
            Assert.IsFalse(focus.HasTextFocus, "a disposed scope releases the surface");
        }

        [Test]
        public void RuntimeUiFocus_TheScopeRegistersTheReporterNextToTheHost()
        {
            ContainerBuilder builder = new();
            builder.RegisterRuntimeUi(new RuntimeUiHostOptions { AutoCreateSurface = false });
            builder.RegisterCoreAiUiFocus();

            using IObjectResolver container = builder.Build();
            Assert.IsTrue(container.Resolve<RuntimeUiHostFocusReporter>().IsActive);
        }

        [Test]
        public void RuntimeUiFocus_WithRuntimeUiSwitchedOff_TheReporterIsInert()
        {
            ContainerBuilder builder = new();
            builder.RegisterCoreAiUiFocus();

            using IObjectResolver container = builder.Build();
            Assert.IsFalse(container.Resolve<RuntimeUiHostFocusReporter>().IsActive);
        }

        // ==================== CoreAILifetimeScope ====================

        [Test]
        public void LifetimeScope_RegistersRuntimeUiTheOverlayAndItsGate_ByDefault()
        {
            ContainerBuilder builder = ConfigureScope(scope => { });

            Assert.IsTrue(builder.Exists(typeof(IRuntimeUiHost), true), "runtime UI screens work in a fresh scene");
            Assert.IsTrue(builder.Exists(typeof(CoreAiChatOverlayEntryPoint)), "the overlay is on by default");
            Assert.IsTrue(builder.Exists(typeof(IChatOverlayOpenGate), true), "behind the UI focus gate");
            Assert.IsTrue(builder.Exists(typeof(ICoreAiUiFocus), true));
            Assert.IsTrue(builder.Exists(typeof(CoreAI.Decisions.IDecisionClientProvider), true),
                "decision models are registered and, with no routes, refuse with no_route");
        }

        [Test]
        public void LifetimeScope_Decisions_DefaultToNoRoutesAndNoLocalServer()
        {
            GameObject root = Track(new GameObject("ReadyToPlay scope"));
            root.SetActive(false);
            CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();

            Assert.IsEmpty(scope.Decisions.Endpoints, "a local decision server is a scene's own choice");
            scope.Decisions = CoreAI.Decisions.CoreAiDecisionSettings.LocalLlamaServer();
            Assert.AreEqual(CoreAI.Decisions.CoreAiDecisionSettings.LocalEndpointId,
                scope.Decisions.Endpoints[0].EndpointId);
        }

        [Test]
        public void LifetimeScope_HostGameSwitchesTheOverlayAndRuntimeUiOff()
        {
            ContainerBuilder builder = ConfigureScope(scope =>
            {
                scope.ChatOverlay.Enabled = false;
                scope.RuntimeUiOptions = new RuntimeUiHostOptions { Enabled = false };
            });

            Assert.IsFalse(builder.Exists(typeof(CoreAiChatOverlayEntryPoint)));
            Assert.IsFalse(builder.Exists(typeof(ChatOverlayController)));
            Assert.IsTrue(builder.Exists(typeof(DisabledRuntimeUiHost)), "every screen call then says UI_DISABLED");
            Assert.IsFalse(builder.Exists(typeof(RuntimeUiHost)));
        }

        [Test]
        public void RoleDefinitions_AppliedAfterTheManifest_ClearItsUniversalPrefixOptOut()
        {
            AgentPromptsManifest manifest = Track(ScriptableObject.CreateInstance<AgentPromptsManifest>());
            manifest.customAgents.Add(new AgentPromptsManifest.Entry
            {
                roleId = "Blacksmith",
                overrideUniversalPrefix = true
            });
            AgentRoleDefinitionAsset asset = Track(ScriptableObject.CreateInstance<AgentRoleDefinitionAsset>());
            asset.CopyFrom(new RoleDefinition("Blacksmith") { OverrideUniversalPrefix = false });
            AgentMemoryPolicy policy = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance(policy);

            // WHY this order: it is the order CoreAILifetimeScope.Configure uses.
            builder.RegisterAgentPrompts(manifest);
            builder.RegisterAgentRoleDefinitions(new[] { asset });
            using IObjectResolver container = builder.Build();

            Assert.IsFalse(policy.IsUniversalPrefixOverridden("Blacksmith"),
                "the definition, applied second, decides the prefix");
        }

        [Test]
        public void LifetimeScope_RoleDefinitions_CannotBeReplacedAfterTheContainerBuilt_ButCanBefore()
        {
            GameObject root = Track(new GameObject("ReadyToPlay scope"));
            root.SetActive(false);
            CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
            AgentRoleDefinitionAsset asset = Track(ScriptableObject.CreateInstance<AgentRoleDefinitionAsset>());

            scope.SetRoleDefinitions(new[] { asset });

            Assert.AreEqual(1, scope.RoleDefinitions.Count);
            Assert.AreSame(asset, scope.RoleDefinitions[0]);
        }

        // ==================== NPCs hear the chat first ====================

        [Test]
        public void NpcLine_AddressedToARegisteredVillager_NeverReachesThePlayersAgent_AndOtherLinesDo()
        {
            ScriptedTurnSender sender = new();
            ContainerBuilder builder = new();
            builder.RegisterInstance<IChatOverlayView>(new FakeChatOverlayView());
            builder.RegisterInstance<IChatOverlayTurnSender>(sender);
            builder.RegisterCoreAiUiFocus();
            builder.RegisterCoreAiChatOverlayServices(new CoreAiChatOverlaySettings { RegisterBuiltInCommands = false });
            CoreAI.Ai.Npc.CoreAiNpcInstaller.RegisterCoreAiNpcs(builder, new CoreAI.Ai.Npc.CoreAiNpcSettings());

            using IObjectResolver container = builder.Build();
            foreach (VContainer.Unity.IStartable startable in container.Resolve<IReadOnlyList<VContainer.Unity.IStartable>>())
            {
                if (startable is CoreAI.Ai.Npc.CoreAiNpcEntryPoint npcs)
                {
                    npcs.Start();
                }
            }

            container.Resolve<CoreAI.Ai.Npc.INpcDirector>().Add(new CoreAI.Ai.Npc.NpcAgentDefinition
            {
                Persona = new CoreAI.Ai.Npc.NpcPersona { Id = "bob", DisplayName = "Bob", PromptFragment = "A baker." }
            });
            ChatOverlayController controller = container.Resolve<ChatOverlayController>();
            CoreAI.Ai.Npc.INpcListenerPosition listener = container.Resolve<CoreAI.Ai.Npc.INpcListenerPosition>();
            Assert.IsTrue(listener is CoreAI.Ai.Npc.NpcListenerPositionRelay,
                "the scope registers the relay the mods scope attaches the local character to");
            Assert.IsNull(listener.Current, "with no world attached the relay knows no position, so no line is near a villager");

            // WHY this order: a villager that answered keeps the player's next lines for its follow-up window
            // (NpcAddressingRules.FollowUpSeconds), so a line for nobody is checked before Bob is addressed.
            controller.Submit("build me a house");
            Assert.AreEqual(1, sender.Requests.Count, "a line for nobody in particular still goes to the agent");
            sender.Complete();

            controller.Submit("Bob, any bread?");
            Assert.AreEqual(1, sender.Requests.Count, "the villager answers; the building agent never sees the line");

            controller.Submit("@ai build a tower");
            Assert.AreEqual(2, sender.Requests.Count, "a line addressed to the AI skips Bob's follow-up window");
            Assert.AreEqual("AI", CoreAI.Ai.Npc.CoreAiNpcEntryPoint.AssistantName(controller));
        }

        // ==================== Orchestrator mode ====================

        [Test]
        public void OrchestratorModeOff_TheScopeRegistersNothingOfIt()
        {
            ContainerBuilder builder = ConfigureScope(scope => { });

            Assert.IsFalse(builder.Exists(typeof(CoreAI.Ai.SubAgents.OrchestratorChatDriver)));
            Assert.IsFalse(builder.Exists(typeof(CoreAI.Ai.SubAgents.ISubAgentRegistry), true));
            Assert.IsFalse(builder.Exists(typeof(CoreAiDefaultSubAgents)), "no default sub-agents without the mode");
        }

        [Test]
        public void OrchestratorModeOn_TheScopeRegistersTheDriverRegistryAndDefaultSubAgents()
        {
            ContainerBuilder builder = ConfigureScope(_ => { }, enableOrchestratorMode: true);

            Assert.IsTrue(builder.Exists(typeof(CoreAI.Ai.SubAgents.OrchestratorChatDriver)));
            Assert.IsTrue(builder.Exists(typeof(CoreAI.Ai.SubAgents.ISubAgentRegistry), true));
            Assert.IsTrue(builder.Exists(typeof(CoreAiDefaultSubAgents)));
        }

        [Test]
        public void OrchestratorModeOn_AHostsOwnSubAgents_ReplaceTheDefaults()
        {
            ContainerBuilder builder = ConfigureScope(
                scope => scope.SubAgents = new[]
                {
                    new CoreAI.Ai.SubAgents.SubAgentDefinition("smith", BuiltInAgentRoleIds.Merchant, "Sells swords.",
                        null)
                },
                enableOrchestratorMode: true);

            Assert.IsTrue(builder.Exists(typeof(CoreAI.Ai.SubAgents.ISubAgentRegistry), true));
            Assert.IsFalse(builder.Exists(typeof(CoreAiDefaultSubAgents)));
        }

        [Test]
        public void OrchestratorModeOn_DriverRegistryAndPresenterResolve_AndTheBuilderReadsItsToolsFromTheProfile()
        {
            ContainerBuilder builder = NewCoreContainer(out CoreAISettingsAsset settings);
            builder.RegisterWorldCommands(null);
            builder.RegisterOrchestratorMode(
                new CoreAI.Ai.SubAgents.OrchestratorModeOptions { Enabled = true },
                System.Array.Empty<CoreAI.Ai.SubAgents.SubAgentDefinition>());
            builder.RegisterCoreAiDefaultSubAgents(null);

            using IObjectResolver container = builder.Build();
            CoreAI.Ai.SubAgents.ISubAgentRegistry registry = container.Resolve<CoreAI.Ai.SubAgents.ISubAgentRegistry>();

            Assert.IsNotNull(container.Resolve<CoreAI.Ai.SubAgents.OrchestratorChatDriver>());
            Assert.IsTrue(registry.TryGet(CoreAiDefaultSubAgents.BuilderId, out CoreAI.Ai.SubAgents.SubAgentDefinition builderAgent));
            Assert.AreEqual(BuiltInAgentRoleIds.Builder, builderAgent.RoleId);
            Assert.IsTrue(builderAgent.AllowsTool("world_command"), "the world-builder profile supplied the tool");
            Assert.IsFalse(registry.TryGet(CoreAiDefaultSubAgents.ScripterId, out _),
                "no Lua tools in a scope without the mods package, so no scripter");
            bool presenterFound = false;
            foreach (VContainer.Unity.IStartable startable in container.Resolve<IReadOnlyList<VContainer.Unity.IStartable>>())
            {
                presenterFound |= startable is CoreAI.Chat.OrchestratorModeChatPresenter;
            }

            Assert.IsTrue(presenterFound, "the chat presenter is an entry point of the scope");
        }

        [Test]
        public void WorldCommands_AScopeDisposedInEditMode_DestroysItsOwnPrefabRegistry_WithoutAnErrorLog()
        {
            ContainerBuilder builder = NewCoreContainer(out CoreAISettingsAsset _);
            builder.RegisterWorldCommands(null);
            IObjectResolver container = builder.Build();
            CoreAI.Infrastructure.World.CoreAiPrefabRegistryAsset registry =
                (CoreAI.Infrastructure.World.CoreAiPrefabRegistryAsset)container
                    .Resolve<CoreAI.Infrastructure.World.ICoreAiPrefabRegistry>();
            Assert.IsTrue(registry != null, "a scope without an assigned registry creates its own");

            container.Dispose();

            Assert.IsTrue(registry == null, "the scope destroys the registry it created");
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void WorldCommands_AnAssignedRegistry_OutlivesTheScope_NegativeTwin()
        {
            CoreAI.Infrastructure.World.CoreAiPrefabRegistryAsset assigned =
                Track(ScriptableObject.CreateInstance<CoreAI.Infrastructure.World.CoreAiPrefabRegistryAsset>());
            ContainerBuilder builder = NewCoreContainer(out CoreAISettingsAsset _);
            builder.RegisterWorldCommands(assigned);
            IObjectResolver container = builder.Build();
            Assert.AreSame(assigned, container.Resolve<CoreAI.Infrastructure.World.ICoreAiPrefabRegistry>());

            container.Dispose();

            Assert.IsTrue(assigned != null, "a registry the host assigned is the host's to destroy");
        }

        [Test]
        public void DefaultSubAgents_WaitForTheirProfile_ThenRegisterOnce()
        {
            AgentMemoryPolicy policy = new();
            CoreAI.Ai.SubAgents.SubAgentRegistry registry = new();
            CoreAiDefaultSubAgents defaults = CoreAiDefaultSubAgents.CreateBuiltIn();

            Assert.IsEmpty(defaults.RegisterAvailable(registry, policy), "empty profiles register nothing");
            policy.AddToolToProfile(BuiltInToolProfileIds.LuaBuilder, new NamedTool("execute_lua"));
            policy.AddSkillToProfile(BuiltInToolProfileIds.LuaBuilder,
                SkillSet.FromTextContent("Rbx API", "Roblox API.", "Use Instance.new."));

            CollectionAssert.AreEqual(new[] { CoreAiDefaultSubAgents.ScripterId }, defaults.RegisterAvailable(registry, policy));
            Assert.IsEmpty(defaults.RegisterAvailable(registry, policy), "a registered id is never added twice");
            registry.TryGet(CoreAiDefaultSubAgents.ScripterId, out CoreAI.Ai.SubAgents.SubAgentDefinition scripter);
            Assert.IsTrue(scripter.AllowsTool("execute_lua"));
            Assert.IsTrue(scripter.AllowsTool("read_skill"), "a profile with skills lets the sub-agent open them");
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, scripter.RoleId);
        }

        // ==================== Prompts manifest roles ====================

        [Test]
        public void ManifestOnlyRole_RaisesNoUnconfiguredRoleWarning()
        {
            AgentMemoryPolicy policy = new();
            RecordingLog log = new();
            policy.DiagnosticsLog = log;
            AgentPromptsDefinition definition = new();
            definition.CustomAgents.Add(new AgentPromptEntryDefinition { RoleId = " Blacksmith ", SystemPrompt = "Forge." });

            AgentPromptsInstaller.ApplyManifestRoles(policy, definition);
            policy.GetToolsForRole("Blacksmith");

            Assert.IsTrue(policy.HasRole("Blacksmith"), "the manifest's role is a known role");
            Assert.IsEmpty(log.Warnings, string.Join("\n", log.Warnings));
        }

        [Test]
        public void MisspelledRole_StillWarns_WithTheManifestSpellingAsTheSuggestion()
        {
            AgentMemoryPolicy policy = new();
            RecordingLog log = new();
            policy.DiagnosticsLog = log;
            AgentPromptsDefinition definition = new();
            definition.CustomAgents.Add(new AgentPromptEntryDefinition { RoleId = "Blacksmith", SystemPrompt = "Forge." });
            AgentPromptsInstaller.ApplyManifestRoles(policy, definition);

            policy.GetToolsForRole("blacksmith");

            Assert.AreEqual(1, log.Warnings.Count, string.Join("\n", log.Warnings));
            StringAssert.Contains("did you mean 'Blacksmith'", log.Warnings[0]);
        }

        [Test]
        public void ManifestEntryForABuiltInRole_KeepsTheBuiltInConfiguration()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicy.RoleMemoryConfig before = policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer);
            AgentPromptsDefinition definition = new();
            definition.RoleOverrides.Add(new AgentPromptEntryDefinition
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                SystemPrompt = "Write Lua.",
                OverrideUniversalPrefix = true
            });

            AgentPromptsInstaller.ApplyManifestRoles(policy, definition);

            AgentMemoryPolicy.RoleMemoryConfig after = policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer);
            Assert.AreEqual(before.UseLlmContextCompaction, after.UseLlmContextCompaction);
            Assert.AreEqual(before.MaxChatHistoryMessages, after.MaxChatHistoryMessages);
            Assert.IsTrue(policy.IsUniversalPrefixOverridden(BuiltInAgentRoleIds.Programmer),
                "the manifest's prefix opt-out still applies");
        }

        // ==================== Agent vision ====================

        [Test]
        public void Vision_RegistersOptionsReadbackAndRig_AndTheServiceIsBuiltFromThem()
        {
            ContainerBuilder builder = new();
            WorldCommandsInstaller.RegisterAgentVision(builder);

            using IObjectResolver container = builder.Build();
            AgentCameraOptions options = container.Resolve<AgentCameraOptions>();
            IAgentCameraService service = container.Resolve<IAgentCameraService>();

            Assert.IsNotNull(container.Resolve<ICameraFrameReadback>());
            Assert.IsInstanceOf<AgentCameraRig>(container.Resolve<IAgentCameraRig>());
            Assert.IsInstanceOf<AgentCameraService>(service);
            Assert.AreSame(options, ((AgentCameraService)service).Options, "one set of rules for the service and the rig");
        }

        [Test]
        public void Vision_KeepsABlockAGameRegisteredFirst()
        {
            AgentCameraOptions own = AgentCameraOptions.CreateDefault();
            own.AllowFreeCamera = false;
            ContainerBuilder builder = new();
            builder.RegisterInstance(own);

            WorldCommandsInstaller.RegisterAgentVision(builder);

            using IObjectResolver container = builder.Build();
            Assert.AreSame(own, ((AgentCameraService)container.Resolve<IAgentCameraService>()).Options);
            Assert.IsFalse(((AgentCameraService)container.Resolve<IAgentCameraService>()).Options.AllowFreeCamera);
        }

        // ==================== Helpers ====================

        private T Track<T>(T created) where T : Object
        {
            _created.Add(created);
            return created;
        }

        private ContainerBuilder ConfigureScope(System.Action<CoreAILifetimeScope> configure,
            bool enableOrchestratorMode = false)
        {
            CoreAISettingsAsset settings = Track(ScriptableObject.CreateInstance<CoreAISettingsAsset>());
            typeof(CoreAISettingsAsset)
                .GetField("enableOrchestratorMode", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(settings, enableOrchestratorMode);
            GameObject root = Track(new GameObject("ReadyToPlay scope"));
            root.SetActive(false);
            CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(scope, settings);
            configure(scope);
            ContainerBuilder builder = new();
            typeof(CoreAILifetimeScope)
                .GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(scope, new object[] { builder });
            return builder;
        }

        /// <summary>The services a CoreAI scope registers before its optional blocks, offline and in memory.</summary>
        private ContainerBuilder NewCoreContainer(out CoreAISettingsAsset settings)
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            settings = Track(ScriptableObject.CreateInstance<CoreAISettingsAsset>());
            settings.ConfigureOffline();
            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.RegisterAgentPrompts(null);
            builder.RegisterLlmPipeline(settings, null);
            builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), AiNetworkExecutionPolicy.AllPeers),
                Lifetime.Singleton);
            CoreAILifetimeScope.RegisterConversationSummaryForCoreAiLifetimeScope(builder,
                AgentMemoryPersistenceMode.SessionOnly);
            CoreAILifetimeScope.RegisterAgentMemoryStore(builder, AgentMemoryPersistenceMode.SessionOnly);
            return builder;
        }

        private static IObjectResolver BuildOverlayContainer(out FakeKeyboard keyboard)
        {
            keyboard = new FakeKeyboard();
            ContainerBuilder builder = new();
            builder.RegisterInstance<IChatOverlayView>(new FakeChatOverlayView());
            builder.RegisterInstance<IChatOverlayTurnSender>(new ScriptedTurnSender());
            builder.RegisterInstance<IChatOverlayKeyboard>(keyboard);
            builder.RegisterCoreAiUiFocus();
            builder.RegisterCoreAiChatOverlayServices(new CoreAiChatOverlaySettings { RegisterBuiltInCommands = false });
            return builder.Build();
        }

        private static CoreAiChatOverlayEntryPoint CreateEntryPoint(IObjectResolver container, FakeKeyboard keyboard)
        {
            CoreAiChatOverlayEntryPoint entryPoint = new(
                container.Resolve<ChatOverlayController>(),
                container.Resolve<IChatOverlayView>(),
                keyboard,
                container.Resolve<CoreAiChatOverlaySettings>(),
                container.Resolve<IChatAgentSelector>(),
                null,
                container.Resolve<IChatOverlayOpenGate>());
            entryPoint.Start();
            return entryPoint;
        }

        private sealed class FakeOverlayInput : IChatOverlayInputState
        {
            private System.Action<bool> _changed;

            public bool IsInputOpen { get; private set; }

            public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

            public event System.Action<bool> InputOpenChanged
            {
                add => _changed += value;
                remove => _changed -= value;
            }

            public void Set(bool open)
            {
                IsInputOpen = open;
                _changed?.Invoke(open);
            }
        }

        private sealed class FakeKeyboard : IChatOverlayKeyboard
        {
            public readonly HashSet<KeyCode> Pressed = new();

            public bool IsModifierHeld => false;

            public bool WasPressedThisFrame(KeyCode key)
            {
                return Pressed.Contains(key);
            }
        }

        private sealed class NamedTool : ILlmTool
        {
            public NamedTool(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public string Description => "A test tool.";

            public string ParametersSchema => "{\"type\":\"object\",\"properties\":{}}";

            public bool AllowDuplicates => true;
        }

        private sealed class OwnGate : IChatOverlayOpenGate
        {
            public bool IsOpenAllowed()
            {
                return true;
            }
        }

        private sealed class RecordingLog : ILog
        {
            public readonly List<string> Warnings = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
                Warnings.Add(message);
            }

            public void Error(string message, string tag = null)
            {
            }
        }
    }
}
