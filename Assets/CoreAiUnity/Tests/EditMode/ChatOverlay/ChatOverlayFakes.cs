using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using CoreAI.Messaging;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>A clock the test moves by hand.</summary>
    internal sealed class ManualChatClock : IChatOverlayClock
    {
        public double NowSeconds { get; set; } = 100d;

        public void Advance(double seconds)
        {
            NowSeconds += seconds;
        }
    }

    /// <summary>Records every call the controller makes on its view and raises the view's events on demand.</summary>
    internal sealed class FakeChatOverlayView : IChatOverlayView
    {
        public readonly List<ChatFeedLine> Added = new();
        public readonly List<ChatFeedLine> Removed = new();
        public readonly Dictionary<long, string> RenderedText = new();
        public readonly Dictionary<long, bool> Visible = new();
        public readonly List<string> StatusHistory = new();
        public int UpdateCalls;

        public event Action<string> Submitted;

        public event Action CloseRequested;

        public bool InputOpen { get; private set; }

        public string Prefill { get; private set; } = "";

        public string StatusText { get; private set; } = "";

        public AgentStatusKind StatusKind { get; private set; }

        public int SubmittedSubscribers => Submitted?.GetInvocationList().Length ?? 0;

        public void RaiseSubmitted(string text)
        {
            Submitted?.Invoke(text);
        }

        public void RaiseCloseRequested()
        {
            CloseRequested?.Invoke();
        }

        public void SetInputOpen(bool open, string prefill)
        {
            InputOpen = open;
            Prefill = prefill ?? "";
        }

        public void AddLine(ChatFeedLine line)
        {
            Added.Add(line);
            RenderedText[line.Id] = line.Text;
            Visible[line.Id] = true;
        }

        public void UpdateLine(ChatFeedLine line)
        {
            UpdateCalls++;
            RenderedText[line.Id] = line.Text;
        }

        public void RemoveLine(ChatFeedLine line)
        {
            Removed.Add(line);
            RenderedText.Remove(line.Id);
            Visible.Remove(line.Id);
        }

        public void SetLineVisible(ChatFeedLine line, bool visible)
        {
            Visible[line.Id] = visible;
        }

        public void SetStatus(AgentStatusKind kind, string text)
        {
            StatusKind = kind;
            StatusText = text ?? "";
            StatusHistory.Add(StatusText);
        }
    }

    /// <summary>
    /// A sender the test drives by hand: <see cref="Send"/> records the request and keeps the observer, and the
    /// test pushes chunks, completion or failure through it synchronously.
    /// </summary>
    internal sealed class ScriptedTurnSender : IChatOverlayTurnSender
    {
        public readonly List<ChatOverlayTurnRequest> Requests = new();
        public readonly List<string> StoppedRoles = new();

        public bool IsAvailable { get; set; } = true;

        public IChatOverlayTurnObserver Observer { get; private set; }

        public ScriptedTurn LastTurn { get; private set; }

        public Action<ChatOverlayTurnRequest, IChatOverlayTurnObserver> OnSend { get; set; }

        public IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer)
        {
            Requests.Add(request);
            Observer = observer;
            LastTurn = new ScriptedTurn();
            OnSend?.Invoke(request, observer);
            return LastTurn;
        }

        public void StopAgent(string roleId)
        {
            StoppedRoles.Add(roleId);
        }

        public void Stream(string text, bool startsNewMessage = false)
        {
            Observer.OnChunk(new LlmStreamChunk { Text = text, StartsNewMessage = startsNewMessage });
        }

        public void Complete()
        {
            Observer.OnChunk(new LlmStreamChunk { IsDone = true });
            Observer.OnCompleted();
        }

        public void Fail(ChatOverlayTurnFailureKind kind, string message = "boom")
        {
            Observer.OnFailed(new ChatOverlayTurnFailure(kind, message));
        }
    }

    internal sealed class ScriptedTurn : IChatOverlayTurn
    {
        public int CancelCalls { get; private set; }

        public void Cancel()
        {
            CancelCalls++;
        }
    }

    /// <summary>Tool events the test raises by hand.</summary>
    internal sealed class FakeToolEventSource : IAgentToolEventSource
    {
        public event Action<LlmToolCallStarted> ToolCallStarted;

        public event Action<LlmToolCallCompleted> ToolCallCompleted;

        public event Action<LlmToolCallFailed> ToolCallFailed;

        public void Start(string traceId, string tool)
        {
            ToolCallStarted?.Invoke(new LlmToolCallStarted(traceId, "Programmer", tool, "{}"));
        }

        public void Complete(string traceId, string tool)
        {
            ToolCallCompleted?.Invoke(new LlmToolCallCompleted(traceId, "Programmer", tool, "{}", "{}", 5d));
        }

        public void Fail(string traceId, string tool)
        {
            ToolCallFailed?.Invoke(new LlmToolCallFailed(traceId, "Programmer", tool, "{}", "error", 5d));
        }
    }

    /// <summary>A routing seam with fixed profiles that records assignments.</summary>
    internal sealed class FakeModelSelector : IChatModelSelector
    {
        public readonly Dictionary<string, string> Assigned = new(StringComparer.Ordinal);

        public bool IsAvailable { get; set; } = true;

        public List<ChatModelProfile> Profiles { get; } = new()
        {
            new ChatModelProfile("fast", "Fast local", "Ready"),
            new ChatModelProfile("smart", "Cloud smart", "Ready")
        };

        public IReadOnlyList<ChatModelProfile> GetProfiles()
        {
            return Profiles;
        }

        public string GetProfileForRole(string roleId)
        {
            return Assigned.TryGetValue(roleId, out string profile) ? profile : "";
        }

        public bool TryAssign(string roleId, string profileId, out string message)
        {
            Assigned[roleId] = profileId ?? "";
            message = "";
            return true;
        }
    }

    /// <summary>Builds a controller over fakes.</summary>
    internal sealed class ChatOverlayHarness : IDisposable
    {
        public ChatOverlayHarness(ChatOverlayOptions options = null, bool registerBuiltIns = true)
        {
            Options = options ?? new ChatOverlayOptions();
            Clock = new ManualChatClock();
            View = new FakeChatOverlayView();
            Sender = new ScriptedTurnSender();
            Registry = new ChatCommandRegistry();
            Agents = new ChatAgentSelector(BuiltInAgentRoleIds.SmartChat,
                () => new[] { BuiltInAgentRoleIds.SmartChat, BuiltInAgentRoleIds.Programmer });
            Models = new FakeModelSelector();
            ToolEvents = new FakeToolEventSource();
            Controller = new ChatOverlayController(Options, View, Sender, Registry, Agents, Clock, ToolEvents);
            if (registerBuiltIns)
            {
                BuiltIns = ChatOverlayBuiltInCommands.RegisterAll(Registry, Agents, Models, Controller);
            }
        }

        public ChatOverlayOptions Options { get; }

        public ManualChatClock Clock { get; }

        public FakeChatOverlayView View { get; }

        public ScriptedTurnSender Sender { get; }

        public ChatCommandRegistry Registry { get; }

        public ChatAgentSelector Agents { get; }

        public FakeModelSelector Models { get; }

        public FakeToolEventSource ToolEvents { get; }

        public ChatOverlayController Controller { get; }

        public IDisposable BuiltIns { get; }

        public ChatFeedLine LastLine => Controller.Feed.Lines.Count == 0
            ? null
            : Controller.Feed.Lines[Controller.Feed.Lines.Count - 1];

        public void Dispose()
        {
            BuiltIns?.Dispose();
            Controller.Dispose();
        }
    }
}
