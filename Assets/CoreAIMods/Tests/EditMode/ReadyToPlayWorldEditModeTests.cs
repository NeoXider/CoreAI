using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// M0 "ready to play", the world side: <c>/undo</c>, <c>/redo</c> and <c>/fly</c> in the in-world chat, the local
    /// player joining every published world, and the default content (baseplate and spawn pad) that is rebuilt in
    /// every world and never reaches the undo history.
    /// </summary>
    [TestFixture]
    public sealed class ReadyToPlayWorldEditModeTests
    {
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

        // ==================== /undo /redo /fly ====================

        [Test]
        public void Undo_WithNothingToUndo_AnswersWithAReason_AndRunsNothing()
        {
            FakeUndo undo = new();
            ChatCommandRegistry registry = new();
            using CoreAiWorldChatCommands commands = new(registry, undo, Host, () => null);

            ChatCommandResult result = Run(registry, "undo", out RecordingMessages _);

            Assert.AreEqual(ChatCommandOutcome.Failed, result.Outcome);
            StringAssert.Contains("Nothing to undo", result.Reply);
            Assert.AreEqual(0, undo.UndoCalls);
        }

        [Test]
        public void Undo_WhenTheWorldChangedSince_AnswersWithTheHistorysReason()
        {
            FakeUndo undo = new() { Status = new RbxWorldUndoStatus("local", 1, 0, false, false,
                "The world changed after that AI change; open the World Loads page.") };
            ChatCommandRegistry registry = new();
            using CoreAiWorldChatCommands commands = new(registry, undo, Host, () => null);

            ChatCommandResult result = Run(registry, "undo", out RecordingMessages _);

            Assert.AreEqual(ChatCommandOutcome.Failed, result.Outcome);
            StringAssert.Contains("World Loads", result.Reply);
            Assert.AreEqual(0, undo.UndoCalls);
        }

        [Test]
        public void Undo_WhenPossible_StartsTheRestore_AndPostsItsOutcome()
        {
            FakeUndo undo = new()
            {
                Status = new RbxWorldUndoStatus("local", 1, 0, true, false, ""),
                Result = new RbxWorldUndoResult(true, RbxWorldUndoResult.UndoneStatus, "Undid the tower.", "local", 0, 1)
            };
            ChatCommandRegistry registry = new();
            using CoreAiWorldChatCommands commands = new(registry, undo, Host, () => null);

            ChatCommandResult result = Run(registry, "undo", out RecordingMessages messages);

            Assert.AreEqual(ChatCommandOutcome.Handled, result.Outcome);
            Assert.AreEqual(1, undo.UndoCalls);
            Assert.AreEqual("local", undo.LastCaller.ActorId, "the history of the chat's own actor");
            CollectionAssert.AreEqual(new[] { "Undid the tower." }, messages.Lines);
        }

        [Test]
        public void Redo_WithNothingToRedo_AnswersWithAReason()
        {
            FakeUndo undo = new() { Status = new RbxWorldUndoStatus("local", 3, 0, true, false, "") };
            ChatCommandRegistry registry = new();
            using CoreAiWorldChatCommands commands = new(registry, undo, Host, () => null);

            ChatCommandResult result = Run(registry, "redo", out RecordingMessages _);

            Assert.AreEqual(ChatCommandOutcome.Failed, result.Outcome);
            StringAssert.Contains("Nothing to redo", result.Reply);
            Assert.AreEqual(0, undo.RedoCalls);
        }

        [Test]
        public void Commands_KeepAGamesOwnCommand_AndDisposingRemovesOnlyTheirs()
        {
            ChatCommandRegistry registry = new();
            IDisposable own = registry.Register(new ChatCommand("undo", "The game's undo.",
                _ => ChatCommandResult.Handled("game undo")));

            CoreAiWorldChatCommands commands = new(registry, new FakeUndo(), Host, () => null);

            CollectionAssert.AreEquivalent(new[] { "redo", "fly" }, commands.RegisteredNames);
            Assert.AreEqual("game undo", Run(registry, "undo", out RecordingMessages _).Reply);
            commands.Dispose();
            Assert.IsTrue(registry.TryResolve("undo", out _), "the game's command stays");
            Assert.IsFalse(registry.TryResolve("redo", out _));
            Assert.IsFalse(registry.TryResolve("fly", out _));
            own.Dispose();
        }

        [Test]
        public void Fly_WithoutACharacter_AnswersWithAReason()
        {
            ChatCommandRegistry registry = new();
            using CoreAiWorldChatCommands commands = new(registry, null, Host, () => null);

            ChatCommandResult result = Run(registry, "fly", out RecordingMessages _);

            Assert.AreEqual(ChatCommandOutcome.Failed, result.Outcome);
            StringAssert.Contains("No character", result.Reply);
            Assert.IsFalse(registry.TryResolve("undo", out _), "no undo history, no /undo");
        }

        [Test]
        public void Fly_TogglesFlightOfTheLocalCharacter()
        {
            FlyingMotor motor = new();
            LuaCsRbxApiBindings world = new();
            world.AttachCharacterMotorFactory(_ => motor);
            CoreAiLocalPlayer.Connect(world, Host());
            Advance(world, 3);
            RbxHumanoid humanoid = CoreAiLocalPlayer.FindHumanoid(world);
            Assert.IsNotNull(humanoid, "the connected local player has a character");
            ChatCommandRegistry registry = new();
            using CoreAiWorldChatCommands commands = new(registry, null, Host, () => CoreAiLocalPlayer.FindHumanoid(world));

            ChatCommandResult on = Run(registry, "fly", out RecordingMessages _);
            Assert.IsTrue(humanoid.IsFlying);
            ChatCommandResult off = Run(registry, "fly", out RecordingMessages _);

            StringAssert.StartsWith("Flying.", on.Reply);
            Assert.AreEqual("Walking.", off.Reply);
            Assert.IsFalse(humanoid.IsFlying);
        }

        [Test]
        public void Composed_TheModsScopeAddsTheCommandsToThePlayersChat_AndUndoWithNothingAnswers()
        {
            ChatCommandRegistry registry = new();
            ContainerBuilder builder = NewModsContainer();
            builder.RegisterInstance<IChatCommandRegistry>(registry);
            builder.RegisterCoreAiWorldChatCommands();

            using (IObjectResolver container = builder.Build())
            {
                Assert.IsTrue(registry.TryResolve("undo", out _));
                Assert.IsTrue(registry.TryResolve("redo", out _));
                Assert.IsTrue(registry.TryResolve("fly", out _));
                ChatCommandResult result = Run(registry, "undo", out RecordingMessages _);
                Assert.AreEqual(ChatCommandOutcome.Failed, result.Outcome);
                StringAssert.Contains("Nothing to undo", result.Reply);
            }

            Assert.IsFalse(registry.TryResolve("undo", out _), "a disposed mods scope takes its commands with it");
        }

        [Test]
        public void Composed_WithoutAChat_RegistersNoCommands()
        {
            ContainerBuilder builder = NewModsContainer();
            builder.RegisterCoreAiWorldChatCommands();

            using IObjectResolver container = builder.Build();
            Assert.DoesNotThrow(() => container.Resolve<CoreAiWorldChatCommandsInstaller.CoreAiWorldChatCommandsHolder>());
            Assert.IsNull(container.Resolve<CoreAiWorldChatCommandsInstaller.CoreAiWorldChatCommandsHolder>().Commands);
        }

        // ==================== Local player and default content ====================

        [Test]
        public async Task LocalPlayer_JoinsTheFirstWorld_AndAgainTheWorldALoadPublishes()
        {
            using IObjectResolver container = NewModsContainer().Build();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            CoreAiPublishedWorldWatcher watcher = new();

            Assert.IsTrue(watcher.TryTakeNewWorld(session, out LuaCsRbxApiBindings first));
            Assert.IsNotNull(CoreAiLocalPlayer.Connect(first, Host()));
            Assert.IsFalse(watcher.TryTakeNewWorld(session, out _), "the same world is applied once");
            Assert.IsTrue(first.Players.TryGetByActorId("local", out RbxPlayer _));
            Assert.AreEqual("local", first.LocalPlayerActorId);

            await ReplaceWorldAsync(session);

            Assert.IsTrue(watcher.TryTakeNewWorld(session, out LuaCsRbxApiBindings second));
            Assert.IsFalse(second.Players.TryGetByActorId("local", out RbxPlayer _),
                "a replaced world starts without the local player");
            Assert.IsNotNull(CoreAiLocalPlayer.Connect(second, Host()));
            Assert.IsTrue(second.Players.TryGetByActorId("local", out RbxPlayer _));
        }

        [Test]
        public async Task DefaultContent_BuildsTheBaseplateAndPad_AsModOwnedParts_ThatTheUndoHistoryNeverSees()
        {
            using IObjectResolver container = NewModsContainer().Build();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            IRbxWorldUndoService undo = container.Resolve<IRbxWorldUndoService>();
            string source = CoreAiDefaultWorldContent.BuildSource(new Vector3(64f, 4f, 64f), new Color32(91, 91, 91, 255),
                true, new Vector3(12f, 1f, 12f), new Color32(163, 162, 165, 255));

            Assert.IsTrue(CoreAiDefaultWorldContent.LoadInto(session, Host(), source));
            Assert.IsFalse(CoreAiDefaultWorldContent.LoadInto(session, Host(), source), "loaded once per world");

            RbxInstance baseplate = Workspace(session).FindFirstChild(CoreAiDefaultWorldContent.BaseplateName);
            Assert.IsNotNull(baseplate);
            Assert.IsNotNull(Workspace(session).FindFirstChild(CoreAiDefaultWorldContent.SpawnPadName));
            Assert.IsTrue(session.CurrentRbxApi.Registry.TryGetRecord(baseplate.Id, out InstanceRecord record));
            Assert.AreEqual(CoreAiDefaultWorldContent.ModId, record.OwnerModId,
                "mod-owned: never stored in a world package, rebuilt by its mod");
            Assert.AreEqual(0, undo.GetStatus("local").UndoCount, "/undo can never remove the floor");

            await ReplaceWorldAsync(session);
            Assert.IsNull(Workspace(session).FindFirstChild(CoreAiDefaultWorldContent.BaseplateName),
                "the preset mod is not part of the world package");
            Assert.IsTrue(CoreAiDefaultWorldContent.LoadInto(session, Host(), source));
            Assert.IsNotNull(Workspace(session).FindFirstChild(CoreAiDefaultWorldContent.BaseplateName),
                "every published world gets the content again");
        }

        [Test]
        public void DefaultContent_AWorldWithItsOwnBaseplate_GetsNoSecondOne()
        {
            using IObjectResolver container = NewModsContainer().Build();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            session.CurrentConcreteRuntime.LoadMod(Host(), "own-floor", @"
                local floor = Instance.new('Part')
                floor.Name = 'Baseplate'
                floor.Anchored = true
                floor.Parent = workspace", LuaCapabilities.All, false);

            CoreAiDefaultWorldContent.LoadInto(session, Host(), CoreAiDefaultWorldContent.BuildSource(
                new Vector3(64f, 4f, 64f), new Color32(1, 2, 3, 255), false, Vector3.one, new Color32(0, 0, 0, 255)));

            int baseplates = 0;
            foreach (RbxInstance child in Workspace(session).GetChildren())
            {
                baseplates += child.Name == CoreAiDefaultWorldContent.BaseplateName ? 1 : 0;
            }

            Assert.AreEqual(1, baseplates);
            Assert.IsNull(Workspace(session).FindFirstChild(CoreAiDefaultWorldContent.SpawnPadName), "the pad was switched off");
        }

        [Test]
        public void DefaultContent_Source_IsCultureInvariant_AndPutsTheBaseplateTopAtZero()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                string source = CoreAiDefaultWorldContent.BuildSource(new Vector3(2048f, 16f, 2048f),
                    new Color32(91, 91, 91, 255), true, new Vector3(12f, 1f, 12f), new Color32(163, 162, 165, 255));

                StringAssert.Contains("ensure('Baseplate', 2048, 16, 2048, -8, 91, 91, 91)", source);
                StringAssert.Contains("ensure('SpawnPad', 12, 1, 12, 0.5, 163, 162, 165)", source);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        // ==================== Autopilot and villager brains ====================

        [Test]
        public void Autopilot_WithNoDecisionEndpoint_AnswersWithTheFallbackAndItsReason()
        {
            ChatCommandRegistry registry = new();
            ContainerBuilder builder = NewModsContainer();
            builder.RegisterInstance<IChatCommandRegistry>(registry);
            CoreAI.Decisions.CoreAiDecisionsInstaller.RegisterCoreAiDecisions(builder);
            using IObjectResolver container = builder.Build();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            session.CurrentRbxApi.AttachCharacterMotorFactory(_ => new FlyingMotor());
            CoreAiLocalPlayer.Connect(session.CurrentRbxApi, Host());
            Advance(session.CurrentRbxApi, 3);
            Assert.IsNotNull(CoreAiLocalPlayer.FindHumanoid(session.CurrentRbxApi), "the local character spawned");

            Assert.IsTrue(registry.TryResolve("autopilot", out IChatCommand autopilot), "/autopilot is registered");
            ChatCommandResult result = autopilot.Execute(new ChatCommandContext(
                new ChatCommandInvocation("autopilot", "on", new[] { "on" }, "/autopilot on"), registry,
                new RecordingMessages(), '/'));

            Assert.AreEqual(ChatCommandOutcome.Handled, result.Outcome, result.Reply);
            StringAssert.Contains("fallback", result.Reply, "it says the built-in fallback decides");
            StringAssert.Contains("because", result.Reply, "and why no decision model is used");
        }

        [Test]
        public async Task Autopilot_TicksOnTheFirstWorld_AndOnEveryWorldALoadStages()
        {
            ContainerBuilder builder = NewModsContainer();
            CoreAI.Decisions.CoreAiDecisionsInstaller.RegisterCoreAiDecisions(builder);
            using IObjectResolver container = builder.Build();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            CoreAiWorldAgents agents = container.Resolve<CoreAiWorldAgents>();

            Assert.IsNotNull(agents.Autopilot, "the decision block gives the autopilot");
            Assert.IsTrue(agents.IsAttached(session.CurrentRbxApi.Scheduler), "the first world ticks it");
            await ReplaceWorldAsync(session);
            Assert.IsTrue(agents.IsAttached(session.CurrentRbxApi.Scheduler), "a staged world ticks it too");
        }

        [Test]
        public async Task Autopilot_AModsRequest_IsFollowedOnTheHeartbeat_OfTheFirstWorldAndOfAStagedOne()
        {
            ContainerBuilder builder = NewModsContainer();
            CoreAI.Decisions.CoreAiDecisionsInstaller.RegisterCoreAiDecisions(builder);
            using IObjectResolver container = builder.Build();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            CoreAI.Decisions.AutopilotController autopilot = container.Resolve<CoreAiWorldAgents>().Autopilot.Controller;

            RbxPlayer first = SpawnLocalCharacter(session.CurrentRbxApi);
            Assert.IsTrue(RequestAutopilot(first));
            Assert.IsFalse(autopilot.Engaged, "a mod's request waits for the world's heartbeat");
            Advance(session.CurrentRbxApi, 1);
            Assert.IsTrue(autopilot.Engaged, "the first world's heartbeat ticks the autopilot");

            await ReplaceWorldAsync(session);
            RbxPlayer second = SpawnLocalCharacter(session.CurrentRbxApi);
            Assert.IsFalse(autopilot.Engaged, "a world change hands control back");
            Assert.IsTrue(RequestAutopilot(second));
            Advance(session.CurrentRbxApi, 1);
            Assert.IsTrue(autopilot.Engaged, "a staged world's heartbeat ticks it too");
        }

        private static RbxPlayer SpawnLocalCharacter(LuaCsRbxApiBindings world)
        {
            world.AttachCharacterMotorFactory(_ => new FlyingMotor());
            RbxPlayer player = CoreAiLocalPlayer.Connect(world, Host());
            Advance(world, 3);
            Assert.IsNotNull(CoreAiLocalPlayer.FindHumanoid(world), "the local character spawned");
            return player;
        }

        private static bool RequestAutopilot(RbxPlayer player)
        {
            return CoreAI.Mods.Rbx.Agents.RbxCharacterQueries.TryWriteAttribute(player,
                CoreAI.Mods.Rbx.Agents.RbxAgentAttributes.Autopilot, true);
        }

        [Test]
        public void Agents_WithoutTheDecisionBlock_NeitherAutopilotNorWorldIsBuilt()
        {
            ChatCommandRegistry registry = new();
            ContainerBuilder builder = NewModsContainer();
            builder.RegisterInstance<IChatCommandRegistry>(registry);

            using IObjectResolver container = builder.Build();
            CoreAiWorldAgents agents = container.Resolve<CoreAiWorldAgents>();

            Assert.IsNull(agents.Autopilot);
            Assert.IsNull(agents.BrainHost);
            Assert.IsFalse(registry.TryResolve("autopilot", out _), "no /autopilot without a decision block");
        }

        // ==================== Mod folders: one list per mod store ====================

        [Test]
        public async Task ModFolders_AFolderTheAiCreates_IsTheWorldsAndTheHubsList_AndSurvivesASaveAndLoad()
        {
            AgentMemoryPolicy policy = new();
            ContainerBuilder builder = NewModsContainer();
            builder.RegisterInstance(policy);
            using IObjectResolver container = builder.Build();
            CoreAI.Mods.Folders.IModFolderListStore folders = container.Resolve<CoreAI.Mods.Folders.IModFolderListStore>();
            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            LuaModsLlmTool tool = FindProgrammerTool<LuaModsLlmTool>(policy, "manage_mods");
            Assert.AreSame(folders, session.ModFolderListStore, "the world session saves the same list");

            Newtonsoft.Json.Linq.JObject created = Newtonsoft.Json.Linq.JObject.Parse(
                await tool.ExecuteAsync("create_folder", folder: "Houses"));
            Assert.IsTrue(created.Value<bool>("success"), created.ToString());
            CollectionAssert.Contains(folders.Load(), "Houses", "manage_mods wrote the shared list");
#if COREAI_HAS_HUB
            CoreAI.Ai.Hub.HubModsPage page = new(new CoreAI.Ai.Hub.LuaCsModRuntimeHubService(
                container.Resolve<ILuaModRuntime>(), Host(), container.Resolve<ILuaModSourceStore>(),
                LuaCapabilities.All, false, null), 300, folders, null);
            page.CreatePageContent();
            CollectionAssert.Contains(page.FolderService.BuildTree().GetFolderPaths(), "Houses",
                "the Hub tree given the shared list shows the AI's empty folder");
#endif

            RbxWorldPackagePayload saved = session.CaptureCurrent();
            CollectionAssert.Contains(saved.ModFolders, "Houses", "an empty folder travels in the world package");
            folders.Save(Array.Empty<string>());
            RbxWorldLoadResult loaded = await session.LoadConfirmedAsync(saved);

            Assert.IsTrue(loaded.Success, loaded.Error);
            CollectionAssert.Contains(folders.Load(), "Houses", "loading the world puts its folders back");
        }

        private static T FindProgrammerTool<T>(AgentMemoryPolicy policy, string name) where T : class, ILlmTool
        {
            foreach (ILlmTool tool in policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer))
            {
                if (tool != null && tool.Name == name)
                {
                    return tool as T;
                }
            }

            Assert.Fail("the Programmer has no " + name);
            return null;
        }

        // ==================== Helpers ====================

        private static ActorContext Host()
        {
            return CoreServicesInstaller.DefaultLocalHostIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static ChatCommandResult Run(IChatCommandRegistry registry, string name, out RecordingMessages messages)
        {
            messages = new RecordingMessages();
            Assert.IsTrue(registry.TryResolve(name, out IChatCommand command), "/" + name + " is registered");
            ChatCommandContext context = new(new ChatCommandInvocation(name, "", null, "/" + name), registry, messages, '/');
            return command.Execute(context);
        }

        private static RbxInstance Workspace(RbxWorldRuntimeSessionController session)
        {
            return session.CurrentRbxApi.Game.FindFirstChildOfClass("Workspace");
        }

        private static void Advance(LuaCsRbxApiBindings world, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                world.Scheduler.Advance(1d / 60d);
            }
        }

        private static async Task ReplaceWorldAsync(RbxWorldRuntimeSessionController session)
        {
            LuaCsRbxApiBindings before = session.CurrentRbxApi;
            RbxWorldLoadResult loaded = await session.LoadConfirmedAsync(session.CaptureCurrent());
            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreNotSame(before, session.CurrentRbxApi);
        }

        private static ContainerBuilder NewModsContainer()
        {
            ContainerBuilder builder = new();
            builder.RegisterCoreAiInstantEditLoop(new CoreAiInstantEditSettings(), () => false);
            builder.RegisterCoreAiMods(
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null,
                worldSessionSourceStore: new P3MemorySourceStore(),
                worldPackageStore: new P3MemoryPackageStore());
            builder.RegisterInstance<ICoreAISettings>(new CoreAISettingsOptions());
            builder.RegisterInstance<IGameLogger>(new SilentLogger());
            builder.RegisterInstance<Logging.ILog>(Logging.NullLog.Instance);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterInstance<ILuaModStore>(new RbxApi.Acceptance.Mvp1AcceptanceMemoryStore());
            return builder;
        }

        private sealed class FakeUndo : IRbxWorldUndoService
        {
            public RbxWorldUndoStatus Status { get; set; } = new("local", 0, 0, false, false, "");

            public RbxWorldUndoResult Result { get; set; } =
                new(true, RbxWorldUndoResult.UndoneStatus, "Undone.", "local", 0, 1);

            public int UndoCalls { get; private set; }

            public int RedoCalls { get; private set; }

            public ActorContext LastCaller { get; private set; }

            public int Capacity => 10;

            public event Action<string> HistoryChanged
            {
                add { }
                remove { }
            }

            public RbxWorldUndoStatus GetStatus(string actorId)
            {
                return Status;
            }

            public UniTask<RbxWorldUndoResult> UndoAsync(ActorContext caller, CancellationToken cancellationToken = default)
            {
                UndoCalls++;
                LastCaller = caller;
                return UniTask.FromResult(Result);
            }

            public UniTask<RbxWorldUndoResult> RedoAsync(ActorContext caller, CancellationToken cancellationToken = default)
            {
                RedoCalls++;
                LastCaller = caller;
                return UniTask.FromResult(Result);
            }
        }

        private sealed class RecordingMessages : IChatOverlayMessages
        {
            public readonly List<string> Lines = new();

            public void PostSystemMessage(string text)
            {
                Lines.Add(text);
            }

            public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
            {
                Lines.Add(text);
            }
        }

        private sealed class FlyingMotor : IRbxCharacterMotor
        {
            private bool _flying;

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => true;

            public bool IsFlying => _flying;

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
            }

            public bool SetFlying(bool flying)
            {
                _flying = flying;
                return true;
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
