using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Npc;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>How a <see cref="BlockWorldTestWorld"/> is composed.</summary>
    internal sealed class BlockWorldTestOptions
    {
        /// <summary>The game's AI-from-Lua setting; null leaves the AI surface off the world entirely.</summary>
        public CoreAiLuaAiOptions Ai { get; set; } = new() { AllowAgentsFromLua = false };

        /// <summary>The model behind the villagers; null is the scope with no model pipeline.</summary>
        public IAiOrchestrationService Npc { get; set; }

        /// <summary>The model behind <c>TextGenerator</c>; null answers every request as a provider error.</summary>
        public IRbxTextGenerationBackend Text { get; set; }

        /// <summary>The per-resume instruction budget of every handler; 0 keeps CoreAI's default.</summary>
        public int BudgetPerResume { get; set; }

        /// <summary>Attach a physics port that casts against Terrain cells and unrotated Parts.</summary>
        public bool Physics { get; set; } = true;
    }

    /// <summary>
    /// The Block World demo's mods in one headless world over the production mod stack: the real Lua VM,
    /// the real Rbx bindings with <c>workspace.Terrain</c>, the P18 UI surface over the fake host, the P12 AI
    /// surface over scripted models, an in-memory input source and a physics port that answers
    /// <c>workspace:Raycast</c> from the Terrain store, as the chunk colliders do in Unity.
    /// </summary>
    internal sealed class BlockWorldTestWorld : IDisposable
    {
        /// <summary>One frame at 60 Hz.</summary>
        public const double FrameSeconds = 1d / 60d;

        /// <summary>Roblox <c>Enum.KeyCode.One</c>.</summary>
        public const int KeyOne = 49;

        public const int KeyE = 101;
        public const int KeyG = 103;
        public const int KeyL = 108;
        public const int KeyP = 112;
        public const int KeyW = 119;

        /// <summary>The camera-aim helper mod: reads two workspace attributes and points a Scriptable camera.</summary>
        public const string AimModId = "test_blockworld_aim";

        private const string AimSource = @"
local camera = workspace.CurrentCamera
game:GetService('RunService').Heartbeat:Connect(function()
  local from = workspace:GetAttribute('TestAimFrom')
  local at = workspace:GetAttribute('TestAimAt')
  if from and at then
    camera.CameraType = Enum.CameraType.Scriptable
    camera.CFrame = CFrame.lookAt(from, at)
  end
end)";

        private readonly LuaCsCoroutineBudgetSettings _budget;

        public BlockWorldTestWorld(BlockWorldTestOptions options = null)
        {
            options ??= new BlockWorldTestOptions();
            _budget = options.BudgetPerResume > 0
                ? new LuaCsCoroutineBudgetSettings(options.BudgetPerResume, LuaCsCoroutineHandle.DefaultResumeTimeoutMs)
                : new LuaCsCoroutineBudgetSettings();
            Focus = new InputFocusState();
            Input = new InMemoryInputSource { FocusState = Focus };
            Roblox = new LuaCsRbxApiBindings(inputSource: Input, coroutineResumeBudget: _budget);
            Workspace = Roblox.Game.FindFirstChildOfClass("Workspace");
            Terrain = (RbxTerrain)Workspace.FindFirstChildOfClass(RbxTerrain.ClassNameValue);
            Assert.IsNotNull(Terrain, "every world has a workspace.Terrain");
            if (options.Physics)
            {
                Roblox.WorldPhysics.AttachPort(new TerrainAndPartsPhysicsPort(Roblox, Terrain));
            }

            Logger = new RecordingGameLogger();
            ModLog = new LuaLogService();
            ModLog.EntryAppended += Logger.Record;
            Store = new UxmlServiceTestWorld.MemoryStore();
            Action<LuaCsApiRegistry, LuaCapabilities> aiHook = null;
            if (options.Ai != null)
            {
                Npc = options.Npc ?? UnavailableAiOrchestrationService.Instance;
                Text = options.Text ?? new ScriptedTextBackend { FailAll = true };
                Director = new NpcDirector(Npc, null, new NpcDirectorOptions(), new StoppedClock());
                aiHook = CoreAiLuaAiBindings.Attach(Roblox, options.Ai, Npc, Director, null, null,
                    new NpcSpeechAnchorRegistry(), null, Text, null);
            }

            Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = Logger,
                Log = Logger,
                LogService = ModLog,
                ModStore = Store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = Roblox,
                CoroutineResumeBudget = _budget,
                AdditionalGameplayBindings = aiHook
            });
            Ui = new FakeRbxUiBackend();
            Surface = new RuntimeUiLuaSurface(Ui);
            Surface.AttachTo(Roblox);
        }

        public InputFocusState Focus { get; }

        public InMemoryInputSource Input { get; }

        public LuaCsRbxApiBindings Roblox { get; }

        public RbxInstance Workspace { get; }

        public RbxTerrain Terrain { get; }

        public RecordingGameLogger Logger { get; }

        /// <summary>The mods' own log (print, warn and runtime errors, as get_mod_logs reads it).</summary>
        public LuaLogService ModLog { get; }

        public UxmlServiceTestWorld.MemoryStore Store { get; }

        public LuaCsModStack Stack { get; }

        public FakeRbxUiBackend Ui { get; }

        public RuntimeUiLuaSurface Surface { get; }

        public IAiOrchestrationService Npc { get; }

        public IRbxTextGenerationBackend Text { get; }

        public NpcDirector Director { get; }

        /// <summary>The source of a bundled Block World mod, read from the checkout.</summary>
        public static string Source(string modId)
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (DirectoryInfo directory = new(start); directory != null; directory = directory.Parent)
                {
                    string path = Path.Combine(directory.FullName, "Assets", "CoreAIMods", "Runtime", "Resources",
                        "CoreAIMods", "BlockWorld", modId + ".lua");
                    if (File.Exists(path))
                    {
                        return File.ReadAllText(path);
                    }
                }
            }

            throw new FileNotFoundException("The bundled Block World mod was not found: " + modId);
        }

        /// <summary>Loads a bundled Block World mod by id, as the seeded store would.</summary>
        public void Load(string modId)
        {
            LoadSource(modId, Source(modId));
        }

        public void LoadSource(string modId, string source)
        {
            Stack.Runtime.LoadMod(modId, source, LuaCapabilities.All, persistToStore: false);
            Assert.IsTrue(Stack.Runtime.IsLoaded(modId), modId + " must load: " + Logger.Describe());
        }

        public void Frame(int count = 1, double seconds = FrameSeconds)
        {
            for (int frame = 0; frame < count; frame++)
            {
                Roblox.Scheduler.Advance(seconds);
            }
        }

        public void FrameUntil(Func<bool> done, int maxFrames, string what)
        {
            for (int frame = 0; frame < maxFrames && !done(); frame++)
            {
                Frame();
            }

            Assert.IsTrue(done(), what + " did not happen within " + maxFrames + " frames. " + Logger.Describe());
        }

        public object Attribute(string name)
        {
            return Workspace.GetAttribute(name);
        }

        public double Number(string name)
        {
            object value = Attribute(name);
            Assert.IsNotNull(value, "workspace attribute " + name + " is missing");
            return Convert.ToDouble(value);
        }

        public RbxVector3 Vector(string name)
        {
            object value = Attribute(name);
            Assert.IsInstanceOf<RbxVector3>(value, "workspace attribute " + name);
            return (RbxVector3)value;
        }

        public void SetAttribute(string name, object value)
        {
            Workspace.SetAttribute(name, value);
        }

        /// <summary>Connects the local player (its character loads with the default factory) and returns it.</summary>
        public CoreAI.Mods.Rbx.Instances.Networking.RbxPlayer ConnectLocalPlayer(string actorId = "p6-player")
        {
            Roblox.LocalPlayerActorId = actorId;
            CoreAI.Mods.Rbx.Instances.Networking.RbxPlayer player = Roblox.Players.EnsureActor(Roblox.Registry, actorId);
            Frame(2);
            Assert.IsNotNull(player.Character, "the local character loaded");
            return player;
        }

        /// <summary>Points the camera from <paramref name="from"/> at <paramref name="at"/> (studs) and settles two frames.</summary>
        public void Aim(RbxVector3 from, RbxVector3 at)
        {
            if (!Stack.Runtime.IsLoaded(AimModId))
            {
                LoadSource(AimModId, AimSource);
            }

            SetAttribute("TestAimFrom", from);
            SetAttribute("TestAimAt", at);
            Frame(2);
        }

        public void Tap(int keyCode)
        {
            Input.PressKey(keyCode);
            Frame();
            Input.ReleaseKey(keyCode);
            Frame();
        }

        /// <summary>Presses and releases a mouse button (0 = MouseButton1, 1 = MouseButton2).</summary>
        public void Click(int button)
        {
            Input.SetMouseButton(button, true);
            Frame();
            Input.SetMouseButton(button, false);
            Frame();
        }

        public void Wheel(float notches)
        {
            Input.ScrollWheel(notches);
            Frame();
        }

        public byte Material(int x, int y, int z)
        {
            return Terrain.Store.GetMaterial(x, y, z);
        }

        public static byte Slot(string materialName)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(materialName, out byte slot), materialName);
            return slot;
        }

        /// <summary>Fails with every error the mods logged, and when a mod was quarantined.</summary>
        public void AssertHealthy(params string[] modIds)
        {
            foreach (string modId in modIds)
            {
                Assert.IsTrue(Stack.Runtime.IsLoaded(modId), modId + " must stay loaded. " + Logger.Describe());
                foreach (LuaModInfo info in Stack.Runtime.ListMods())
                {
                    if (info.Id == modId)
                    {
                        Assert.IsFalse(info.Quarantined, modId + " was quarantined. " + Logger.Describe());
                    }
                }
            }

            CollectionAssert.IsEmpty(Logger.Errors, Logger.Describe());
        }

        public void Dispose()
        {
            try
            {
                Stack.Runtime.ShutdownWithoutPersistence();
            }
            finally
            {
                Surface.Dispose();
                Director?.Dispose();
                Roblox.Dispose();
            }
        }

        /// <summary>Every log line of the mod stack, with the errors and warnings kept apart.</summary>
        internal sealed class RecordingGameLogger : IGameLogger, ILog
        {
            public readonly List<string> Lines = new();
            public readonly List<string> Errors = new();
            public readonly List<string> Warnings = new();

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add(message ?? "");
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add(message ?? "");
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add(message ?? "");
                Warnings.Add(message ?? "");
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
                Lines.Add(message ?? "");
                Errors.Add(message ?? "");
            }

            public void Debug(string message, string tag = null)
            {
                Lines.Add(message ?? "");
            }

            public void Info(string message, string tag = null)
            {
                Lines.Add(message ?? "");
            }

            public void Warn(string message, string tag = null)
            {
                Lines.Add(message ?? "");
                Warnings.Add(message ?? "");
            }

            public void Error(string message, string tag = null)
            {
                Lines.Add(message ?? "");
                Errors.Add(message ?? "");
            }

            public void Record(LuaLogEntry entry)
            {
                string line = "[" + entry.ModId + "] " + entry.Message;
                Lines.Add(line);
                if (entry.Level == LuaLogLevel.Error || entry.Level == LuaLogLevel.RuntimeError)
                {
                    Errors.Add(line);
                }
                else if (entry.Level == LuaLogLevel.Warn)
                {
                    Warnings.Add(line);
                }
            }

            public bool Contains(string text)
            {
                foreach (string line in Lines)
                {
                    if (line.IndexOf(text, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            public string Describe()
            {
                return Errors.Count == 0 && Warnings.Count == 0
                    ? "(no errors logged)"
                    : "Errors:\n" + string.Join("\n", Errors) + "\nWarnings:\n" + string.Join("\n", Warnings);
            }
        }

        /// <summary>
        /// Answers rays from the Terrain store (the engine-free voxel march the chunk colliders agree with) and
        /// from the axis-aligned boxes of anchored Parts in the Workspace, nearest first.
        /// </summary>
        private sealed class TerrainAndPartsPhysicsPort : IRbxPhysicsPort
        {
            private readonly LuaCsRbxApiBindings _roblox;
            private readonly RbxTerrain _terrain;

            public TerrainAndPartsPhysicsPort(LuaCsRbxApiBindings roblox, RbxTerrain terrain)
            {
                _roblox = roblox;
                _terrain = terrain;
            }

            public event Action<InstanceId, InstanceId> ContactBegan
            {
                add { }
                remove { }
            }

            public event Action<InstanceId, InstanceId> ContactEnded
            {
                add { }
                remove { }
            }

            public void SetGravity(double studsPerSecondSquared)
            {
            }

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
                Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
            {
                hit = default;
                double best = double.MaxValue;
                if (isEligible(_terrain.Id)
                    && TerrainVoxelRaycaster.TryRaycast(_terrain.Store, originStuds, directionStuds,
                        out TerrainRaycastHit voxel))
                {
                    best = voxel.Distance;
                    hit = new RbxPhysicsRaycastHit(_terrain.Id, voxel.Position, voxel.Normal,
                        TerrainMaterials.ToMaterialId(voxel.Material), voxel.Distance);
                }

                RbxInstance workspace = _roblox.Game.FindFirstChildOfClass("Workspace");
                foreach (RbxInstance candidate in workspace.GetDescendants())
                {
                    if (candidate.ClassName != "Part" || !isEligible(candidate.Id)
                        || !_roblox.PartSink.TryGetPartProperties(candidate.Id, out PartProperties part))
                    {
                        continue;
                    }

                    if (TryHitBox(originStuds, directionStuds, part.Position, part.Size, out double distance,
                            out RbxVector3 normal) && distance < best)
                    {
                        best = distance;
                        double length = directionStuds.Magnitude;
                        RbxVector3 point = new(
                            (float)(originStuds.X + directionStuds.X / length * distance),
                            (float)(originStuds.Y + directionStuds.Y / length * distance),
                            (float)(originStuds.Z + directionStuds.Z / length * distance));
                        hit = new RbxPhysicsRaycastHit(candidate.Id, point, normal, part.Material, distance);
                    }
                }

                return best < double.MaxValue;
            }

            private static bool TryHitBox(RbxVector3 origin, RbxVector3 direction, RbxVector3 center,
                RbxVector3 size, out double distance, out RbxVector3 normal)
            {
                distance = 0d;
                normal = default;
                double length = direction.Magnitude;
                if (!(length > 0d))
                {
                    return false;
                }

                double[] o = { origin.X, origin.Y, origin.Z };
                double[] d = { direction.X / length, direction.Y / length, direction.Z / length };
                double[] min = { center.X - size.X / 2d, center.Y - size.Y / 2d, center.Z - size.Z / 2d };
                double[] max = { center.X + size.X / 2d, center.Y + size.Y / 2d, center.Z + size.Z / 2d };
                double near = 0d;
                double far = length;
                int axis = -1;
                double sign = 0d;
                for (int i = 0; i < 3; i++)
                {
                    if (Math.Abs(d[i]) < 1e-12)
                    {
                        if (o[i] < min[i] || o[i] > max[i])
                        {
                            return false;
                        }

                        continue;
                    }

                    double t1 = (min[i] - o[i]) / d[i];
                    double t2 = (max[i] - o[i]) / d[i];
                    double entrySign = -1d;
                    if (t1 > t2)
                    {
                        (t1, t2) = (t2, t1);
                        entrySign = 1d;
                    }

                    if (t1 > near)
                    {
                        near = t1;
                        axis = i;
                        sign = entrySign;
                    }

                    far = Math.Min(far, t2);
                    if (near > far)
                    {
                        return false;
                    }
                }

                if (axis < 0)
                {
                    return false;
                }

                distance = near;
                normal = new RbxVector3(axis == 0 ? (float)sign : 0f, axis == 1 ? (float)sign : 0f,
                    axis == 2 ? (float)sign : 0f);
                return true;
            }
        }

        private sealed class StoppedClock : INpcClock
        {
            public double NowSeconds => 0d;
        }
    }

    /// <summary>A text model that answers every request with <see cref="Reply"/>, or fails them all.</summary>
    internal sealed class ScriptedTextBackend : IRbxTextGenerationBackend
    {
        public readonly List<RbxTextGenerationRequest> Requests = new();

        public Func<RbxTextGenerationRequest, string> Reply = _ => "{\"goal\":\"explore\"}";

        public bool FailAll;

        public Task<RbxTextGenerationResult> GenerateAsync(RbxTextGenerationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (FailAll)
            {
                return Task.FromResult(RbxTextGenerationResult.Failed(RbxTextGenerationFailure.ProviderError,
                    "no model is configured"));
            }

            return Task.FromResult(new RbxTextGenerationResult
            {
                Ok = true,
                GeneratedText = Reply(request),
                Model = "scripted-model"
            });
        }
    }

    /// <summary>A villager model: answers every turn synchronously with <see cref="Reply"/>.</summary>
    internal sealed class ScriptedVillagerModel : IAiOrchestrationService, IAiTaskResultService
    {
        public readonly List<AiTaskRequest> Requests = new();

        public Func<AiTaskRequest, string> Reply = _ => "Hello.";

        public bool SupportsTaskResults => true;

        public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The typed path is used.");
        }

        public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(task);
            return Task.FromResult(new LlmCompletionResult { Ok = true, Content = Reply(task), Model = "scripted" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(task);
            await Task.CompletedTask;
            yield return new LlmStreamChunk { Text = Reply(task) };
            yield return new LlmStreamChunk { IsDone = true };
        }

        public void CancelTasks(string cancellationScope)
        {
        }
    }
}
