using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for the shipped Clicker, Lane Racer, and Tetris Lua resources, including their
    /// delivery headers and frame-driven collision and gravity behavior under the real Lua runtime.
    /// </summary>
    [TestFixture]
    public sealed class BundledLuaSamplesEditModeTests
    {
        private const int KeyA = 97;
        private const int KeyD = 100;
        private const int KeyS = 115;

        private SynchronizationContext _savedContext;
        private ILog _savedLog;
        private CapturingLog _capturingLog;

        [SetUp]
        public void SetUpHarnessEnvironment()
        {
            _savedContext = SynchronizationContext.Current;
            _savedLog = Log.Instance;
            _capturingLog = new CapturingLog();
            Log.Instance = _capturingLog;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreHarnessEnvironment()
        {
            Log.Instance = _savedLog;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (key.ModId == modId)
                    {
                        keys.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class CapturingGameLogger : IGameLogger
        {
            public readonly List<string> Messages = new();
            public readonly List<string> Errors = new();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Messages.Add(message ?? "");
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Messages.Add(message ?? "");
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Messages.Add(message ?? "");
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                string text = message ?? "";
                Messages.Add(text);
                Errors.Add(text);
            }
        }

        private sealed class CapturingLog : ILog
        {
            public readonly List<string> Errors = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
                Errors.Add(message ?? "");
            }
        }

        private sealed class RuntimeHarness
        {
            public RuntimeHarness()
            {
                Input = new InMemoryInputSource();
                InstanceRegistry registry = new(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
                RbxApi = new LuaCsRbxApiBindings(registry: registry, inputSource: Input);
                Logger = new CapturingGameLogger();
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = Logger,
                    ModStore = Store,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = RbxApi,
                    RegisterWorldEditBuildBindings = false
                });
                RbxApi.Scheduler.PhaseReached += PumpSchedulerPhase;
            }

            public InMemoryInputSource Input { get; }
            public LuaCsRbxApiBindings RbxApi { get; }
            public CapturingGameLogger Logger { get; }
            public MemoryStore Store { get; }
            public LuaCsModStack Stack { get; }

            public void AdvanceFrame(float deltaSeconds)
            {
                RbxApi.Scheduler.Advance(deltaSeconds);
            }

            private void PumpSchedulerPhase(SchedulerPhase phase, double deltaSeconds)
            {
                float frameDelta = (float)deltaSeconds;
                switch (phase)
                {
                    case SchedulerPhase.PreSimulation:
                        RbxApi.PumpPreSimulation(frameDelta);
                        return;
                    case SchedulerPhase.Heartbeat:
                        RbxApi.PumpHeartbeat(frameDelta);
                        return;
                    case SchedulerPhase.PreRender:
                        RbxApi.PumpPreRender(frameDelta);
                        return;
                }
            }
        }

        private static BundledMod FindBundledMod(IReadOnlyList<BundledMod> mods, string id)
        {
            foreach (BundledMod mod in mods)
            {
                if (mod.Id == id)
                {
                    return mod;
                }
            }

            Assert.Fail("Bundled resource is missing: " + id);
            return default;
        }

        private static string LoadSampleSource(string resourceName)
        {
            TextAsset asset = Resources.Load<TextAsset>("CoreAIMods/" + resourceName);
            Assert.IsNotNull(asset, "Bundled Lua resource is missing: " + resourceName);
            return asset.text;
        }

        private static void AssertHeader(BundledMod mod, string expectedId, string minimumVersion)
        {
            LuaModHeader header = LuaModHeader.Parse(mod.Source, mod.Id);
            Assert.AreEqual(expectedId, mod.Id);
            Assert.AreEqual(expectedId, header.Id);
            Assert.AreEqual(mod.Version, header.Version);
            Assert.GreaterOrEqual(BundledModSeeder.CompareVersions(header.Version, minimumVersion), 0,
                expectedId + " must not lose the version bump that delivers its bundled fixes.");
            Assert.AreEqual(LuaCapabilities.All.ToString(), header.Capabilities);
            Assert.IsFalse(header.Active);
        }

        private static RuntimeHarness LoadSource(string modId, string source)
        {
            RuntimeHarness harness = new();
            Assert.DoesNotThrow(() => harness.Stack.Runtime.LoadMod(
                modId, source, LuaCapabilities.All, persistToStore: false));
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded(modId), modId + " must load in the real Lua runtime.");
            return harness;
        }

        private static RuntimeHarness LoadSample(string modId, string resourceName)
        {
            return LoadSource(modId, LoadSampleSource(resourceName));
        }

        private static string ReplaceRequired(string source, string oldValue, string newValue)
        {
            string replaced = source.Replace(oldValue, newValue);
            Assert.AreNotEqual(source, replaced, "The bundled sample camera framing line changed unexpectedly.");
            return replaced;
        }

        private static RbxInstance Workspace(RuntimeHarness harness)
        {
            return harness.RbxApi.Game.FindFirstChildOfClass("Workspace");
        }

        private static RbxInstance RequiredChild(RbxInstance parent, string name)
        {
            RbxInstance child = parent.FindFirstChild(name);
            Assert.IsNotNull(child, "Expected Rbx child is missing: " + name);
            return child;
        }

        private static PartProperties PartProperties(RuntimeHarness harness, RbxInstance part)
        {
            bool found = harness.RbxApi.PartSink.TryGetPartProperties(part.Id, out PartProperties properties);
            Assert.IsTrue(found, "Part properties are missing for " + part.Name);
            return properties;
        }

        private static void AlignLaneWithBlock(RuntimeHarness harness, RbxInstance block)
        {
            float blockX = PartProperties(harness, block).Position.X;
            int key = blockX < -1f ? KeyA : blockX > 1f ? KeyD : 0;
            if (key == 0)
            {
                return;
            }

            harness.Input.PressKey(key);
            harness.AdvanceFrame(0f);
            harness.Input.ReleaseKey(key);
            harness.AdvanceFrame(0f);
        }

        private static List<RbxVector3> ActiveCellPositions(RuntimeHarness harness, RbxInstance root)
        {
            List<RbxVector3> positions = new();
            foreach (RbxInstance child in root.GetChildren())
            {
                if (child.Name != "Cell")
                {
                    continue;
                }

                positions.Add(PartProperties(harness, child).Position);
            }

            Assert.AreEqual(4, positions.Count, "The early-game board must contain exactly four active-piece cells.");
            return positions;
        }

        private static float ActiveCellAverageY(RuntimeHarness harness, RbxInstance root)
        {
            float total = 0f;
            List<RbxVector3> positions = ActiveCellPositions(harness, root);
            foreach (RbxVector3 position in positions)
            {
                total += position.Y;
            }

            return total / positions.Count;
        }

        private void AssertRuntimeHealthy(RuntimeHarness harness, string modId)
        {
            Assert.IsTrue(harness.Stack.Runtime.IsLoaded(modId), modId + " must remain loaded.");
            LuaModInfo info = null;
            foreach (LuaModInfo candidate in harness.Stack.Runtime.ListMods())
            {
                if (candidate.Id == modId)
                {
                    info = candidate;
                    break;
                }
            }

            Assert.IsNotNull(info, modId + " must be present in runtime diagnostics.");
            Assert.IsFalse(info.Quarantined, modId + " must not be quarantined by a handler error.");
            Assert.AreEqual(0, _capturingLog.Errors.Count, string.Join("\n", _capturingLog.Errors));
            Assert.AreEqual(0, harness.Logger.Errors.Count, string.Join("\n", harness.Logger.Errors));
            foreach (string message in harness.Logger.Messages)
            {
                StringAssert.DoesNotContain("NOT_IMPLEMENTED", message, modId + " reached a loud Rbx stub.");
            }
        }

        [Test]
        public void RealResources_LoadAndHeadersParseWithFixDeliveryVersions()
        {
            IReadOnlyList<BundledMod> mods = new ResourcesBundledModSource().Load();
            AssertHeader(FindBundledMod(mods, "sample_clicker"), "sample_clicker", "1.5.0");
            AssertHeader(FindBundledMod(mods, "sample_lane_racer"), "sample_lane_racer", "2.3.0");
            AssertHeader(FindBundledMod(mods, "sample_tetris3d"), "sample_tetris3d", "3.3.0");
        }

        [TestCase("sample_clicker", "sample_clicker")]
        [TestCase("sample_lane_racer", "sample_lane_racer")]
        [TestCase("sample_tetris3d", "sample_tetris3d")]
        public void RealSample_LoadsAndRunsWithoutLoudStub(string modId, string resourceName)
        {
            RuntimeHarness harness = LoadSample(modId, resourceName);
            Assert.DoesNotThrow(() => harness.AdvanceFrame(1f / 60f));
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void FrameHarness_HeartbeatHandlerRunsExactlyOncePerFrame()
        {
            const string modId = "heartbeat_exactly_once";
            RuntimeHarness harness = LoadSource(modId, @"
                local RunService = game:GetService('RunService')
                RunService.Heartbeat:Connect(function()
                    local count = tonumber(store_get('heartbeat_count')) or 0
                    store_set('heartbeat_count', tostring(count + 1))
                end)");

            harness.AdvanceFrame(0.25f);
            Assert.AreEqual("1", harness.Store.Get(modId, "heartbeat_count"),
                "One logical frame must invoke a Heartbeat handler exactly once.");
            harness.AdvanceFrame(0.25f);
            Assert.AreEqual("2", harness.Store.Get(modId, "heartbeat_count"),
                "Each additional logical frame must add exactly one Heartbeat invocation.");
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void LaneRacer_YawedCameraUsesProjectedLaneAndTrackAxes()
        {
            const string modId = "sample_lane_racer";
            string source = ReplaceRequired(
                LoadSampleSource("sample_lane_racer"),
                "cam.CFrame = CFrame.lookAt(Vector3.new(0, 9, 14), Vector3.new(0, 2, -18))",
                "cam.CFrame = CFrame.lookAt(Vector3.new(14, 9, 0), Vector3.new(-18, 2, 0))");
            RuntimeHarness harness = LoadSource(modId, source);
            RbxInstance root = RequiredChild(Workspace(harness), "LaneRacer");
            RbxInstance car = RequiredChild(root, "RacerCar");

            harness.AdvanceFrame(0.8f);
            RbxInstance block = RequiredChild(root, "Block");
            RbxVector3 blockBefore = PartProperties(harness, block).Position;
            harness.Input.PressKey(KeyD);
            harness.AdvanceFrame(0.1f);
            RbxVector3 blockAfter = PartProperties(harness, block).Position;
            RbxVector3 carAfter = PartProperties(harness, car).Position;

            Assert.AreEqual(-60f, blockBefore.X, 0.0001f,
                "The obstacle must spawn along the yawed camera's projected look vector.");
            Assert.AreEqual(-58f, blockAfter.X, 0.0001f,
                "The obstacle must advance along the yawed camera's projected look vector.");
            Assert.AreEqual(blockBefore.Z, blockAfter.Z, 0.0001f);
            Assert.AreEqual(0f, carAfter.X, 0.0001f);
            Assert.AreEqual(-4f, carAfter.Z, 0.0001f,
                "D must move the car along the yawed camera's projected right vector.");
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void LaneRacer_SweptCollisionDetectsObstacleThatCrossesCarBandInOneFrame()
        {
            const string modId = "sample_lane_racer";
            RuntimeHarness harness = LoadSample(modId, "sample_lane_racer");
            RbxInstance root = RequiredChild(Workspace(harness), "LaneRacer");
            RbxInstance car = RequiredChild(root, "RacerCar");

            harness.AdvanceFrame(0.8f);
            RbxInstance block = RequiredChild(root, "Block");
            AlignLaneWithBlock(harness, block);
            harness.AdvanceFrame(3.5f);

            Assert.IsFalse(PartProperties(harness, car).Anchored,
                "A block swept from distance 60 to -10 must crash instead of tunnelling through the car band.");
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void Tetris_YawedCameraUsesProjectedHorizontalAxis()
        {
            const string modId = "sample_tetris3d";
            string source = ReplaceRequired(
                LoadSampleSource("sample_tetris3d"),
                "cam.CFrame = CFrame.lookAt(BOARD_CENTER + Vector3.new(0, 2, 18), BOARD_CENTER)",
                "cam.CFrame = CFrame.lookAt(BOARD_CENTER + Vector3.new(18, 2, 0), BOARD_CENTER)");
            RuntimeHarness harness = LoadSource(modId, source);
            RbxInstance root = RequiredChild(Workspace(harness), "Tetris3D");

            harness.AdvanceFrame(0.0625f);
            List<RbxVector3> before = ActiveCellPositions(harness, root);
            harness.Input.PressKey(KeyD);
            harness.AdvanceFrame(0.0625f);
            List<RbxVector3> after = ActiveCellPositions(harness, root);

            for (int i = 0; i < before.Count; i++)
            {
                Assert.AreEqual(-0.5f, before[i].X, 0.0001f,
                    "Every active cell must lie in the yawed camera's board plane.");
                Assert.AreEqual(before[i].X, after[i].X, 0.0001f);
                Assert.AreEqual(before[i].Z - 1f, after[i].Z, 0.0001f,
                    "D must move each cell along the yawed camera's projected right vector.");
            }

            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void LaneRacer_NormalFrameOutsideCarBandDoesNotCrash()
        {
            const string modId = "sample_lane_racer";
            RuntimeHarness harness = LoadSample(modId, "sample_lane_racer");
            RbxInstance root = RequiredChild(Workspace(harness), "LaneRacer");
            RbxInstance car = RequiredChild(root, "RacerCar");

            harness.AdvanceFrame(0.8f);
            RbxInstance block = RequiredChild(root, "Block");
            AlignLaneWithBlock(harness, block);
            harness.AdvanceFrame(2.9f);
            harness.AdvanceFrame(1f / 60f);

            Assert.IsTrue(PartProperties(harness, car).Anchored,
                "A normal frame ending outside the car band must not report a collision.");
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void Tetris_GravityPreservesRemainderAcrossFrames()
        {
            const string modId = "sample_tetris3d";
            RuntimeHarness harness = LoadSample(modId, "sample_tetris3d");
            RbxInstance root = RequiredChild(Workspace(harness), "Tetris3D");

            harness.AdvanceFrame(0.4f);
            float before = ActiveCellAverageY(harness, root);
            harness.AdvanceFrame(0.3f);
            float afterFirstStep = ActiveCellAverageY(harness, root);
            harness.AdvanceFrame(0.5f);
            float afterSecondStep = ActiveCellAverageY(harness, root);

            Assert.AreEqual(before - 1f, afterFirstStep, 0.0001f);
            Assert.AreEqual(afterFirstStep - 1f, afterSecondStep, 0.0001f,
                "The first step must preserve its 0.1-second remainder for the next frame.");
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void Tetris_LongFrameCapsGravityAndDropsSurplusBank()
        {
            const string modId = "sample_tetris3d";
            RuntimeHarness harness = LoadSample(modId, "sample_tetris3d");
            RbxInstance root = RequiredChild(Workspace(harness), "Tetris3D");

            harness.AdvanceFrame(0.0625f);
            float before = ActiveCellAverageY(harness, root);
            Assert.DoesNotThrow(() => harness.AdvanceFrame(100f));
            float afterLongFrame = ActiveCellAverageY(harness, root);
            harness.AdvanceFrame(0f);
            float afterZeroFrame = ActiveCellAverageY(harness, root);
            harness.AdvanceFrame(0.138f);
            float afterPreservedSurplus = ActiveCellAverageY(harness, root);

            Assert.AreEqual(before - 8f, afterLongFrame, 0.0001f,
                "One long frame must execute at most eight gravity steps.");
            Assert.AreEqual(afterLongFrame, afterZeroFrame, 0.0001f,
                "Surplus banked time must not spill into following frames.");
            Assert.AreEqual(afterZeroFrame - 1f, afterPreservedSurplus, 0.0001f,
                "The modulo remainder from the capped frame must still contribute to the next gravity step.");
            AssertRuntimeHealthy(harness, modId);
        }

        [Test]
        public void Tetris_GravityModeSwitchPreservesPhaseWithoutBurstingRows()
        {
            const string modId = "sample_tetris3d";
            RuntimeHarness harness = LoadSample(modId, "sample_tetris3d");
            RbxInstance root = RequiredChild(Workspace(harness), "Tetris3D");

            harness.AdvanceFrame(0.59f);
            float bankedNormal = ActiveCellAverageY(harness, root);
            harness.Input.PressKey(KeyS);
            harness.AdvanceFrame(0.0005f);
            float switchedToSoft = ActiveCellAverageY(harness, root);
            harness.Input.ReleaseKey(KeyS);
            harness.AdvanceFrame(0f);
            float switchedBackToNormal = ActiveCellAverageY(harness, root);
            harness.AdvanceFrame(0.0045f);
            float completedPhase = ActiveCellAverageY(harness, root);

            Assert.AreEqual(bankedNormal, switchedToSoft, 0.0001f,
                "A sub-threshold soft frame must not release banked normal-gravity time.");
            Assert.AreEqual(bankedNormal, switchedBackToNormal, 0.0001f);
            Assert.AreEqual(bankedNormal - 0.072f, completedPhase, 0.0001f,
                "Switching gravity modes must preserve the normalized phase.");
            AssertRuntimeHealthy(harness, modId);
        }
    }
}
