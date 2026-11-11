using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Agents;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Tests.EditMode.RbxApi.Agents
{
    /// <summary>A motor that records what the Humanoid asks of it and stands where the test puts it.</summary>
    internal sealed class RecordingMotor : IRbxCharacterMotor
    {
        public readonly List<RbxVector3?> MoveTargets = new();

        public readonly List<RbxVector3> Moves = new();

        public int Jumps;

        public RbxVector3 Position { get; set; } = RbxVector3.Zero;

        public RbxVector3 MoveDirection => RbxVector3.Zero;

        public bool IsGrounded => true;

        public void SetWalkSpeed(double studsPerSecond)
        {
        }

        public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
            Jumps++;
        }

        public void MoveTo(RbxVector3? targetStuds)
        {
            MoveTargets.Add(targetStuds);
        }

        public void Move(RbxVector3 directionStuds)
        {
            Moves.Add(directionStuds);
        }

        public RbxVector3? LastTarget => MoveTargets.Count == 0 ? null : MoveTargets[MoveTargets.Count - 1];
    }

    /// <summary>An input source the test presses keys on; the focus state is the world's own.</summary>
    internal sealed class PressedKeys : IInputSource
    {
        public readonly HashSet<int> Keys = new();

        public readonly HashSet<int> Buttons = new();

        public RbxVector2 Stick = RbxVector2.Zero;

        public InputFocusState Focus = new();

        public IInputFocusState FocusState => Focus;

        public void CollectPressedKeyCodes(ICollection<int> buffer)
        {
            foreach (int key in Keys)
            {
                buffer.Add(key);
            }
        }

        public bool IsKeyCodeDown(int keyCodeValue)
        {
            return Keys.Contains(keyCodeValue);
        }

        public bool IsMouseButtonDown(int button)
        {
            return Buttons.Contains(button);
        }

        public RbxVector2 GetMouseLocation()
        {
            return RbxVector2.Zero;
        }

        public bool TryReadThumbstick(int stick, out RbxVector2 position)
        {
            position = stick == 1 ? Stick : RbxVector2.Zero;
            return stick == 1;
        }
    }

    /// <summary>A headless Rbx world with a pose store standing in for the part sink.</summary>
    internal sealed class AgentsWorld
    {
        public const int KeyW = 119;
        public const int KeyT = 116;

        public readonly Dictionary<InstanceId, RbxCFrame> Poses = new();

        public AgentsWorld(bool partSink = true)
        {
            Registry = new InstanceRegistry();
            Game = DataModelBootstrap.CreateGame(Registry);
            Workspace = Registry.WorldRoot;
            Players = (RbxPlayers)Game.FindFirstChildOfClass("Players");
            Input = new PressedKeys();
            ((RbxUserInputService)Game.FindFirstChildOfClass("UserInputService")).AttachInputSource(Input);
            World = partSink
                ? new DelegateRbxCharacterWorld(() => Game,
                    part => Poses.TryGetValue(part.Id, out RbxCFrame pose) ? pose : (RbxCFrame?)null,
                    (part, pose) => Poses[part.Id] = pose)
                : new DelegateRbxCharacterWorld(() => Game);
        }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; set; }

        public RbxInstance Workspace { get; }

        public RbxPlayers Players { get; }

        public PressedKeys Input { get; }

        public IRbxCharacterWorld World { get; }

        public RbxPlayer Player { get; private set; }

        public RbxHumanoid PlayerHumanoid { get; private set; }

        public RecordingMotor PlayerMotor { get; private set; }

        public RbxPlayer AddPlayer(string actorId, RbxVector3 at)
        {
            Player = Players.EnsureActor(Registry, actorId);
            RbxInstance character = RbxCharacterFactory.Load(Registry, Workspace, Player);
            PlayerHumanoid = (RbxHumanoid)character.FindFirstChildOfClass("Humanoid");
            RbxInstance root = character.FindFirstChild("HumanoidRootPart");
            PlayerMotor = new RecordingMotor { Position = at };
            PlayerHumanoid.AttachHost(null, PlayerMotor, root);
            Place(root, at);
            return Player;
        }

        public RbxInstance AddVillager(string name, RbxVector3 at, out RbxHumanoid humanoid, out RecordingMotor motor)
        {
            RbxInstance model = Registry.Create("Model");
            model.Name = name;
            RbxInstance root = Registry.Create("Part");
            root.Name = "HumanoidRootPart";
            root.Parent = model;
            humanoid = (RbxHumanoid)Registry.Create("Humanoid");
            humanoid.DisplayName = name;
            humanoid.Parent = model;
            model.Parent = Workspace;
            motor = new RecordingMotor { Position = at };
            humanoid.AttachHost(null, motor, root);
            Place(root, at);
            return model;
        }

        public void Place(RbxInstance part, RbxVector3 at)
        {
            Poses[part.Id] = RbxCFrame.FromPosition(at);
        }

        public void MovePlayer(RbxVector3 at)
        {
            PlayerMotor.Position = at;
            Place(PlayerHumanoid.RootPart, at);
        }

        public static ModScheduler Scheduler()
        {
            return new ModScheduler(new NoThreads(), new RbxAccumulatingTimeSource());
        }

        private sealed class NoThreads : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }
    }

    /// <summary>A decision client that answers synchronously by script, or fails like a refused route.</summary>
    internal sealed class AnsweringDecisionClient : IDecisionClient
    {
        public readonly List<DecisionRequest> Requests = new();

        public Func<DecisionRequest, DecisionResult> Answer;

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Answer != null
                ? Answer(request)
                : DecisionResult.Failure(LlmErrorCode.RoutingError, DecisionErrorReasons.NoRoute,
                    "No decision route matches consumer 'autopilot'.", "routing"));
        }

        /// <summary>A confident model answer choosing <paramref name="optionId"/>.</summary>
        public static DecisionResult Choose(DecisionRequest request, string optionId, double confidence = 0.9d)
        {
            DecisionQuestion question = request.Questions[0];
            int count = question.Options.Count;
            int chosen = question.IndexOf(optionId);
            double[] probabilities = new double[count];
            for (int i = 0; i < count; i++)
            {
                probabilities[i] = i == chosen ? confidence : (1d - confidence) / Math.Max(1, count - 1);
            }

            List<string> ids = new();
            foreach (DecisionOption option in question.Options)
            {
                ids.Add(option.Id);
            }

            DecisionAnswer answer = new(question.Id, question.Kind, ids, probabilities, chosen, confidence, true, null);
            return DecisionResult.Success(new[] { answer }, new LlmUsageRecord(), "d1-2B", "test");
        }
    }

    /// <summary>Time moves only when the test says so.</summary>
    internal sealed class StepClock : IDecisionClock
    {
        public double NowSeconds { get; set; }

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            return new TaskCompletionSource<bool>().Task;
        }
    }
}
