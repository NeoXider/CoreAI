using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// Lifecycle of a Team-style sub-agent (a member of a conversation's <see cref="SubAgentTeam"/>).
    /// <see cref="Finished"/>, <see cref="Failed"/> and <see cref="Stopped"/> are idle: a message continues the
    /// member with its conversation.
    /// </summary>
    public enum SubAgentMemberState
    {
        /// <summary>Has work (its task or a message) and waits for a free model slot.</summary>
        Queued = 0,

        /// <summary>A turn runs: model requests and tool calls.</summary>
        Working = 1,

        /// <summary>Its last turn answered; it keeps its conversation and waits for a message.</summary>
        Finished = 2,

        /// <summary>Its last turn failed (provider error, deadline); a message continues it.</summary>
        Failed = 3,

        /// <summary>Stopped by the lead or by the player's Stop; a message continues it.</summary>
        Stopped = 4
    }

    /// <summary>What one transcript entry of a member records.</summary>
    public enum SubAgentTranscriptKind
    {
        /// <summary>The lead's task (the first turn's input).</summary>
        Task = 0,

        /// <summary>A message the lead sent.</summary>
        Message = 1,

        /// <summary>The member's answer at the end of a turn.</summary>
        Answer = 2,

        /// <summary>Why a turn ended without an answer.</summary>
        Failure = 3,

        /// <summary>One tool step of a running turn, as the host forwarded it.</summary>
        Tool = 4,

        /// <summary>The member was stopped.</summary>
        Stop = 5
    }

    /// <summary>One immutable entry of a member's transcript.</summary>
    public sealed class SubAgentTranscriptEntry
    {
        internal SubAgentTranscriptEntry(int seq, int turn, SubAgentTranscriptKind kind, string text)
        {
            Seq = seq;
            Turn = turn;
            Kind = kind;
            Text = text ?? "";
        }

        /// <summary>Position in the member's transcript, from 1; a read cursor compares against it.</summary>
        public int Seq { get; }

        /// <summary>The turn the entry belongs to (the turn it starts, for a task or a message).</summary>
        public int Turn { get; }

        /// <summary>What the entry records.</summary>
        public SubAgentTranscriptKind Kind { get; }

        /// <summary>The text.</summary>
        public string Text { get; }

        /// <summary>True for what the member itself produced (answers, failures, tool steps, its stop).</summary>
        public bool IsMemberOutput => Kind != SubAgentTranscriptKind.Task && Kind != SubAgentTranscriptKind.Message;
    }

    /// <summary>An immutable view of one member, for the HUD, the chat and tests.</summary>
    public sealed class SubAgentMemberSnapshot
    {
        internal SubAgentMemberSnapshot(SubAgentMember member)
        {
            Name = member.Name;
            SubAgentId = member.Definition.Id;
            Label = member.Label;
            State = member.State;
            Turns = member.Turns;
            PendingMessages = member.Inbox.Count;
            Steps = member.Steps;
            LastAnswer = member.LastAnswer;
            LastError = member.LastError;
            TraceId = member.TraceId;
            LatestSeq = member.Transcript.Count == 0 ? 0 : member.Transcript[member.Transcript.Count - 1].Seq;
        }

        /// <summary>The name the lead addresses it by (<c>builder-1</c>).</summary>
        public string Name { get; }

        /// <summary>Its sub-agent type (<see cref="SubAgentDefinition.Id"/>).</summary>
        public string SubAgentId { get; }

        /// <summary>Short label for the HUD.</summary>
        public string Label { get; }

        /// <summary>Current state.</summary>
        public SubAgentMemberState State { get; }

        /// <summary>Turns started so far.</summary>
        public int Turns { get; }

        /// <summary>Messages waiting for its next turn.</summary>
        public int PendingMessages { get; }

        /// <summary>Tool steps of its current (or last) turn.</summary>
        public int Steps { get; }

        /// <summary>Its last answer (clipped to the definition's result size); empty before the first.</summary>
        public string LastAnswer { get; }

        /// <summary>Why its last turn failed; empty otherwise.</summary>
        public string LastError { get; }

        /// <summary>Trace id of its current (or last) turn; empty before the first.</summary>
        public string TraceId { get; }

        /// <summary>Sequence number of its newest transcript entry; 0 when empty.</summary>
        public int LatestSeq { get; }

        /// <summary>True when it waits for a message (finished, failed or stopped).</summary>
        public bool IsIdle => State >= SubAgentMemberState.Finished;
    }

    /// <summary>What a message sent with <c>agent_send</c> did.</summary>
    public enum SubAgentDelivery
    {
        /// <summary>Not delivered (the call was refused).</summary>
        None = 0,

        /// <summary>The member was busy: the message waits and starts its next turn when the current one ends.</summary>
        Queued = 1,

        /// <summary>The member was idle: a new turn started with its conversation.</summary>
        Started = 2
    }

    /// <summary>Which control produced a <see cref="SubAgentControlResult"/>.</summary>
    public enum SubAgentControlAction
    {
        /// <summary><c>agent_start</c>.</summary>
        Start = 0,

        /// <summary><c>agent_send</c>.</summary>
        Send = 1,

        /// <summary><c>agent_read</c> with an agent.</summary>
        Read = 2,

        /// <summary><c>agent_read</c> without an agent.</summary>
        List = 3,

        /// <summary><c>agent_stop</c>.</summary>
        Stop = 4
    }

    /// <summary>
    /// The typed outcome of one Team-style control call. <see cref="ToToolResultJson"/> is what the lead reads:
    /// <c>ok</c> first, a refusal as <c>ok:false</c> with <c>error</c> and <c>message</c>, and the fields of the
    /// action that answered.
    /// </summary>
    public sealed class SubAgentControlResult
    {
        /// <summary>True when the control did what was asked.</summary>
        public bool Ok { get; internal set; }

        /// <summary>The control that answered.</summary>
        public SubAgentControlAction Action { get; internal set; }

        /// <summary>Why it was refused; <see cref="SubAgentErrorCode.None"/> when <see cref="Ok"/>.</summary>
        public SubAgentErrorCode ErrorCode { get; internal set; }

        /// <summary>Explanation of a refusal.</summary>
        public string Message { get; internal set; } = "";

        /// <summary>The member's name; empty for a list.</summary>
        public string Agent { get; internal set; } = "";

        /// <summary>The member's state after the call.</summary>
        public SubAgentMemberState State { get; internal set; }

        /// <summary>The member's state before a stop.</summary>
        public SubAgentMemberState WasState { get; internal set; }

        /// <summary>What a send did.</summary>
        public SubAgentDelivery Delivery { get; internal set; }

        /// <summary>Turns started so far.</summary>
        public int Turns { get; internal set; }

        /// <summary>Messages waiting for its next turn.</summary>
        public int PendingMessages { get; internal set; }

        /// <summary>The member's own output after the read cursor, newest kept within the read size.</summary>
        public string Output { get; internal set; } = "";

        /// <summary>Pass back to a later read to get only newer output.</summary>
        public int Cursor { get; internal set; }

        /// <summary>Characters of older output a read left out.</summary>
        public int Skipped { get; internal set; }

        /// <summary>The last finished answer a stop returns.</summary>
        public string Answer { get; internal set; } = "";

        /// <summary>Tool steps of the turn a stop cancelled.</summary>
        public int Steps { get; internal set; }

        /// <summary>Every member, for a list.</summary>
        public IReadOnlyList<SubAgentMemberSnapshot> Members { get; internal set; } =
            Array.Empty<SubAgentMemberSnapshot>();

        /// <summary>The stable wire name of <paramref name="state"/>.</summary>
        public static string StateWireName(SubAgentMemberState state)
        {
            switch (state)
            {
                case SubAgentMemberState.Queued: return "queued";
                case SubAgentMemberState.Working: return "working";
                case SubAgentMemberState.Finished: return "finished";
                case SubAgentMemberState.Failed: return "failed";
                default: return "stopped";
            }
        }

        /// <summary>The JSON tool result, with <paramref name="news"/> (completion notices) appended when any.</summary>
        public string ToToolResultJson(IReadOnlyList<string> news = null)
        {
            JObject json = new() { ["ok"] = Ok };
            if (!Ok)
            {
                json["error"] = SubAgentErrorCodes.ToWireName(ErrorCode);
                json["message"] = Message ?? "";
                if (!string.IsNullOrEmpty(Agent))
                {
                    json["agent"] = Agent;
                }
            }
            else
            {
                switch (Action)
                {
                    case SubAgentControlAction.Start:
                        json["agent"] = Agent;
                        json["state"] = StateWireName(State);
                        break;
                    case SubAgentControlAction.Send:
                        json["agent"] = Agent;
                        json["delivery"] = Delivery == SubAgentDelivery.Started ? "started" : "queued";
                        json["state"] = StateWireName(State);
                        break;
                    case SubAgentControlAction.Read:
                        json["agent"] = Agent;
                        json["state"] = StateWireName(State);
                        json["turns"] = Turns;
                        json["pending"] = PendingMessages;
                        json["output"] = Output ?? "";
                        json["cursor"] = Cursor;
                        if (Skipped > 0)
                        {
                            json["skipped"] = Skipped;
                        }

                        break;
                    case SubAgentControlAction.List:
                        JArray agents = new();
                        foreach (SubAgentMemberSnapshot member in Members)
                        {
                            agents.Add(new JObject
                            {
                                ["agent"] = member.Name,
                                ["type"] = member.SubAgentId,
                                ["label"] = member.Label,
                                ["state"] = StateWireName(member.State),
                                ["turns"] = member.Turns,
                                ["pending"] = member.PendingMessages
                            });
                        }

                        json["agents"] = agents;
                        break;
                    case SubAgentControlAction.Stop:
                        json["agent"] = Agent;
                        json["was"] = StateWireName(WasState);
                        json["state"] = StateWireName(State);
                        json["answer"] = Answer ?? "";
                        json["steps"] = Steps;
                        break;
                }
            }

            if (news != null && news.Count > 0)
            {
                json["news"] = new JArray(news);
            }

            return json.ToString(Formatting.None);
        }

        internal static SubAgentControlResult Refused(
            SubAgentControlAction action,
            SubAgentErrorCode code,
            string message,
            string agent = "")
        {
            return new SubAgentControlResult
            {
                Ok = false,
                Action = action,
                ErrorCode = code,
                Message = message ?? "",
                Agent = agent ?? ""
            };
        }
    }

    /// <summary>A member's completion, waiting to reach the lead.</summary>
    public sealed class SubAgentNotice
    {
        internal SubAgentNotice(string agent, bool ok, string text)
        {
            Agent = agent ?? "";
            Ok = ok;
            Text = text ?? "";
        }

        /// <summary>The member that settled.</summary>
        public string Agent { get; }

        /// <summary>True when it finished with an answer; false when it failed.</summary>
        public bool Ok { get; }

        /// <summary>The line the lead reads: <c>builder-1 finished: ...</c>.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// A lead turn a sub-agent's completion asks for while no lead turn runs (a wake turn). The host runs
    /// <see cref="Text"/> as a chat turn of <see cref="RoleId"/> with <see cref="TraceId"/> through its own chat
    /// pipeline; the orchestrator driver recognises the trace and treats the turn as a wake. A host that cannot run
    /// it hands it back with <see cref="OrchestratorChatDriver.ReturnWake"/>.
    /// </summary>
    public sealed class LeadWake
    {
        internal LeadWake(string conversationKey, string roleId, string traceId, string text,
            IReadOnlyList<SubAgentNotice> notices)
        {
            ConversationKey = conversationKey ?? "";
            RoleId = roleId ?? "";
            TraceId = traceId ?? "";
            Text = text ?? "";
            Notices = notices ?? Array.Empty<SubAgentNotice>();
        }

        /// <summary>The conversation the wake belongs to.</summary>
        public string ConversationKey { get; }

        /// <summary>The lead's role.</summary>
        public string RoleId { get; }

        /// <summary>Trace id the host must run the turn with.</summary>
        public string TraceId { get; }

        /// <summary>The turn's user message: the notices and what to do with them.</summary>
        public string Text { get; }

        /// <summary>The notices the wake carries.</summary>
        public IReadOnlyList<SubAgentNotice> Notices { get; }
    }

    /// <summary>Mutable state of one member; touched only under its team's lock.</summary>
    internal sealed class SubAgentMember
    {
        public SubAgentMember(string name, SubAgentDefinition definition, string label, SubAgentParentContext parent)
        {
            Name = name;
            Definition = definition;
            Label = label;
            Parent = parent;
        }

        public readonly string Name;
        public readonly SubAgentDefinition Definition;
        public readonly string Label;
        public readonly List<SubAgentTranscriptEntry> Transcript = new();
        public readonly List<string> Inbox = new();
        public readonly Queue<OrchestrationBudgetLedger> Reservations = new();
        public SubAgentParentContext Parent;
        public SubAgentMemberState State = SubAgentMemberState.Queued;
        public string PendingTask = "";
        public int Turns;
        public int Steps;
        public string LastAnswer = "";
        public string LastError = "";
        public string TraceId = "";
        public LlmRequestProgress Progress;
        public CancellationTokenSource Activity;
        public bool LoopRunning;
        public int NextSeq;

        public void Append(SubAgentTranscriptKind kind, string text, int turn)
        {
            NextSeq++;
            Transcript.Add(new SubAgentTranscriptEntry(NextSeq, turn, kind, text));
        }
    }
}
