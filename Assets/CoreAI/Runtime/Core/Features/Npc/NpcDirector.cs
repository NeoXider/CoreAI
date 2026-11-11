using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;

namespace CoreAI.Ai.Npc
{
    /// <summary>The director's answer to one heard or asked line.</summary>
    public readonly struct NpcHearResult
    {
        /// <summary>Creates the answer.</summary>
        public NpcHearResult(NpcAddressDecision decision, long turnId, bool started)
        {
            Decision = decision;
            TurnId = turnId;
            Started = started;
        }

        /// <summary>Which NPC took the line, or why none did.</summary>
        public NpcAddressDecision Decision { get; }

        /// <summary>The turn that will answer; 0 when no NPC took the line.</summary>
        public long TurnId { get; }

        /// <summary>True when the turn started at once; false when it waits for a cooldown or a free slot.</summary>
        public bool Started { get; }

        /// <summary>True when an NPC took the line.</summary>
        public bool IsTaken => Decision.IsAddressed && TurnId > 0;
    }

    /// <summary>
    /// The NPCs of one world and the rules between them: who answers a heard line (exactly one NPC or none),
    /// per-NPC cooldowns, the world's calls-per-minute and concurrent-turn budgets, one in-flight turn per NPC
    /// (a newer line cancels the older), in-character fallback lines on failure, and the events quest rules
    /// react to. Call it from one thread at a time (the game thread). A turn's results are applied inside the
    /// director call that produced them or on the next <see cref="Tick"/>, never on a thread of their own, so
    /// <see cref="Tick"/> once per frame also starts turns whose cooldown passed and stops turns that ran out of
    /// time.
    /// </summary>
    public interface INpcDirector : IDisposable
    {
        /// <summary>An NPC took a line; raised before its turn starts.</summary>
        event Action<NpcAddressed> NpcAddressed;

        /// <summary>An NPC spoke: a reply, a fallback line or a scripted line.</summary>
        event Action<NpcSaid> NpcSaid;

        /// <summary>A model turn ended, whatever the outcome.</summary>
        event Action<NpcTurnEnded> TurnEnded;

        /// <summary>A heard line no NPC took.</summary>
        event Action<NpcLineIgnored> LineIgnored;

        /// <summary>The NPCs, in the order they were added.</summary>
        IReadOnlyList<NpcAgent> Agents { get; }

        /// <summary>Model turns running now.</summary>
        int RunningTurnCount { get; }

        /// <summary>Adds an NPC; its persona id must be new to this director.</summary>
        NpcAgent Add(NpcAgentDefinition definition);

        /// <summary>Removes an NPC, cancelling its turn; false when the id is unknown.</summary>
        bool Remove(string npcId);

        /// <summary>Finds an NPC by id.</summary>
        bool TryGet(string npcId, out NpcAgent agent);

        /// <summary>Routes a heard line through the arbiter; at most one NPC answers it.</summary>
        NpcHearResult Hear(NpcHeardLine line);

        /// <summary>Sends a line straight to one NPC (a script or an interaction), skipping the addressing rules but not the budgets.</summary>
        NpcHearResult Ask(string npcId, NpcHeardLine line);

        /// <summary>Makes an NPC say a fixed line without the model; false when the id is unknown or the text empty.</summary>
        bool Say(string npcId, string text);

        /// <summary>Applies results from other threads, starts waiting turns and stops expired ones. Call once per frame.</summary>
        void Tick();
    }

    /// <summary>
    /// The default <see cref="INpcDirector"/>. Turns run through <see cref="IAiOrchestrationService"/> with the typed
    /// result path (a failed turn is data, never an exception and never provider text in front of a player).
    /// Engine-free and WebGL-safe: no threads, no blocking waits; the turn timeout is checked on <see cref="Tick"/>.
    /// </summary>
    public sealed class NpcDirector : INpcDirector
    {
        private const double MinutesWindowSeconds = 60d;
        private const double FailureLogIntervalSeconds = 10d;

        private readonly IAiOrchestrationService _orchestrator;
        private readonly INpcSpeechSink _sink;
        private readonly NpcDirectorOptions _options;
        private readonly INpcClock _clock;
        private readonly INpcAddressArbiter _arbiter;
        private readonly INpcLineOfSight _lineOfSight;
        private readonly INpcActorContextFactory _actors;
        private readonly INpcTurnPromptBuilder _prompts;
        private readonly ITextModerationFilter _moderation;
        private readonly ILog _log;
        private readonly List<NpcAgent> _agents = new();
        private readonly Dictionary<string, NpcAgent> _byId = new(StringComparer.Ordinal);
        private readonly Queue<double> _callStamps = new();
        private readonly object _queueGate = new();
        private readonly List<Action> _queued = new();
        private readonly List<Action> _drained = new();
        private long _nextTurnId;
        private int _running;
        private double _lastFailureLogAt = double.NegativeInfinity;
        private int _entryThreadId;
        private int _entryDepth;
        private bool _disposed;

        /// <summary>Creates a director. Only <paramref name="orchestrator"/> is required.</summary>
        public NpcDirector(
            IAiOrchestrationService orchestrator,
            INpcSpeechSink sink = null,
            NpcDirectorOptions options = null,
            INpcClock clock = null,
            INpcAddressArbiter arbiter = null,
            INpcLineOfSight lineOfSight = null,
            INpcActorContextFactory actors = null,
            INpcTurnPromptBuilder prompts = null,
            ITextModerationFilter moderation = null,
            ILog log = null)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _sink = sink ?? new CompositeNpcSpeechSink();
            _options = options ?? new NpcDirectorOptions();
            _clock = clock ?? new StopwatchNpcClock();
            _arbiter = arbiter ?? new NpcAddressArbiter();
            _lineOfSight = lineOfSight ?? OpenNpcLineOfSight.Instance;
            _actors = actors ?? new NpcActorContextFactory();
            _prompts = prompts ?? new NpcTurnPromptBuilder();
            _moderation = moderation ?? PassThroughTextModerationFilter.Instance;
            _log = log ?? NullLog.Instance;
        }

        /// <inheritdoc />
        public event Action<NpcAddressed> NpcAddressed;

        /// <inheritdoc />
        public event Action<NpcSaid> NpcSaid;

        /// <inheritdoc />
        public event Action<NpcTurnEnded> TurnEnded;

        /// <inheritdoc />
        public event Action<NpcLineIgnored> LineIgnored;

        /// <summary>The options this director was built with.</summary>
        public NpcDirectorOptions Options => _options;

        /// <summary>The speech sink every line goes to.</summary>
        public INpcSpeechSink Sink => _sink;

        /// <inheritdoc />
        public IReadOnlyList<NpcAgent> Agents => _agents;

        /// <inheritdoc />
        public int RunningTurnCount => _running;

        /// <summary>Model calls started inside the current one-minute window.</summary>
        public int CallsInLastMinute
        {
            get
            {
                PruneCallStamps(_clock.NowSeconds);
                return _callStamps.Count;
            }
        }

        /// <inheritdoc />
        public NpcAgent Add(NpcAgentDefinition definition)
        {
            ThrowIfDisposed();
            NpcAgent agent = new(definition, _options.SourceTag);
            if (_byId.ContainsKey(agent.Id))
            {
                throw new InvalidOperationException($"An NPC with id '{agent.Id}' already exists.");
            }

            if (_options.MaxAgents > 0 && _agents.Count >= _options.MaxAgents)
            {
                throw new InvalidOperationException(
                    $"The NPC capacity of {_options.MaxAgents} is reached; remove an NPC before adding another.");
            }

            _agents.Add(agent);
            _byId.Add(agent.Id, agent);
            return agent;
        }

        /// <inheritdoc />
        public bool Remove(string npcId)
        {
            using EntryScope entry = Enter();
            if (_disposed || !_byId.TryGetValue((npcId ?? "").Trim(), out NpcAgent agent))
            {
                return false;
            }

            _byId.Remove(agent.Id);
            _agents.Remove(agent);
            agent.IsRemoved = true;
            DropPending(agent, NpcTurnOutcome.Cancelled);
            if (agent.ActiveRun != null)
            {
                CancelRun(agent.ActiveRun, NpcTurnOutcome.Cancelled, true);
            }

            StartWaitingTurns();
            return true;
        }

        /// <inheritdoc />
        public bool TryGet(string npcId, out NpcAgent agent)
        {
            return _byId.TryGetValue((npcId ?? "").Trim(), out agent);
        }

        /// <inheritdoc />
        public NpcHearResult Hear(NpcHeardLine line)
        {
            using EntryScope entry = Enter();
            if (_disposed)
            {
                return Ignore(line, NpcAddressDecision.Ignored(NpcIgnoreReason.Disposed));
            }

            double now = _clock.NowSeconds;
            List<NpcAddressCandidate> candidates = new(_agents.Count);
            foreach (NpcAgent agent in _agents)
            {
                candidates.Add(new NpcAddressCandidate(agent.Id, agent.Names, agent.Addressing, agent.Position,
                    agent.ConversationPartnerId, agent.ConversationAtSeconds));
            }

            NpcAddressDecision decision = _arbiter.Decide(line, candidates, now, _lineOfSight);
            if (!decision.IsAddressed || !_byId.TryGetValue(decision.NpcId, out NpcAgent chosen))
            {
                return Ignore(line, decision.IsAddressed
                    ? NpcAddressDecision.Ignored(NpcIgnoreReason.UnknownNpc, decision.NpcId)
                    : decision);
            }

            return Accept(chosen, line, decision);
        }

        /// <inheritdoc />
        public NpcHearResult Ask(string npcId, NpcHeardLine line)
        {
            using EntryScope entry = Enter();
            if (_disposed)
            {
                return Ignore(line, NpcAddressDecision.Ignored(NpcIgnoreReason.Disposed));
            }

            if (line == null || string.IsNullOrWhiteSpace(line.Text))
            {
                return Ignore(line, NpcAddressDecision.Ignored(NpcIgnoreReason.EmptyLine, npcId));
            }

            if (!_byId.TryGetValue((npcId ?? "").Trim(), out NpcAgent agent))
            {
                return Ignore(line, NpcAddressDecision.Ignored(NpcIgnoreReason.UnknownNpc, npcId));
            }

            return Accept(agent, line, NpcAddressDecision.Addressed(agent.Id, NpcAddressReason.Interaction));
        }

        /// <inheritdoc />
        public bool Say(string npcId, string text)
        {
            using EntryScope entry = Enter();
            if (_disposed || !_byId.TryGetValue((npcId ?? "").Trim(), out NpcAgent agent))
            {
                return false;
            }

            string line = NpcText.CleanReply(text, agent.Name, agent.Budget.MaxReplyCharacters);
            if (line.Length == 0)
            {
                return false;
            }

            NpcSpeech speech = new(agent.Id, agent.Name, "", "", 0, agent.OriginTag);
            _sink.SpeechCompleted(speech, line, false);
            Raise(NpcSaid, new NpcSaid(agent.Id, agent.Name, "", "", line, false, true, 0, agent.OriginTag));
            return true;
        }

        /// <inheritdoc />
        public void Tick()
        {
            using EntryScope entry = Enter();
            if (_disposed)
            {
                return;
            }

            DrainQueued();
            double now = _clock.NowSeconds;
            if (_options.TurnTimeoutSeconds > 0d && _running > 0)
            {
                List<NpcTurnRun> expired = null;
                foreach (NpcAgent agent in _agents)
                {
                    NpcTurnRun run = agent.ActiveRun;
                    if (run != null && !run.Ended && now - run.StartedAt > _options.TurnTimeoutSeconds)
                    {
                        (expired ??= new List<NpcTurnRun>()).Add(run);
                    }
                }

                if (expired != null)
                {
                    foreach (NpcTurnRun run in expired)
                    {
                        CancelRun(run, NpcTurnOutcome.FallbackLine, false);
                        SpeakFallback(run.Agent, run.Pending, run.Speech, LlmErrorCode.Timeout);
                    }
                }
            }

            StartWaitingTurns();
        }

        /// <summary>Cancels every turn and forgets every NPC.</summary>
        public void Dispose()
        {
            using EntryScope entry = Enter();
            if (_disposed)
            {
                return;
            }

            foreach (NpcAgent agent in _agents.ToArray())
            {
                agent.IsRemoved = true;
                DropPending(agent, NpcTurnOutcome.Cancelled);
                if (agent.ActiveRun != null)
                {
                    CancelRun(agent.ActiveRun, NpcTurnOutcome.Cancelled, true);
                }
            }

            _disposed = true;
            _agents.Clear();
            _byId.Clear();
            lock (_queueGate)
            {
                _queued.Clear();
            }
        }

        private NpcHearResult Accept(NpcAgent agent, NpcHeardLine line, NpcAddressDecision decision)
        {
            string text = NpcText.Clamp((line.Text ?? "").Trim(), agent.Budget.MaxHeardCharacters);
            long turnId = ++_nextTurnId;
            double now = _clock.NowSeconds;
            Raise(NpcAddressed, new NpcAddressed(agent.Id, agent.Name, line.SpeakerId, line.EffectiveSpeakerName,
                text, decision.Reason, turnId, agent.OriginTag));
            if (agent.IsRemoved || _disposed)
            {
                return new NpcHearResult(decision, 0, false);
            }

            if (agent.ActiveRun != null)
            {
                CancelRun(agent.ActiveRun, NpcTurnOutcome.Superseded, true);
            }

            DropPending(agent, NpcTurnOutcome.Superseded);
            NpcPendingTurn pending = new(line, text, turnId, decision.Reason, now);
            if (!_moderation.TryFilter(text, TextModerationDirection.Input, out string filtered))
            {
                NpcSpeech refused = SpeechFor(agent, pending);
                _sink.SpeechStarted(refused);
                SpeakFallback(agent, pending, refused, LlmErrorCode.InvalidRequest);
                return new NpcHearResult(decision, turnId, true);
            }

            agent.Pending = new NpcPendingTurn(line, filtered ?? "", turnId, decision.Reason, now);
            bool started = TryStart(agent, now);
            if (started)
            {
                StartWaitingTurns();
            }

            return new NpcHearResult(decision, turnId, started);
        }

        private NpcHearResult Ignore(NpcHeardLine line, NpcAddressDecision decision)
        {
            Raise(LineIgnored, new NpcLineIgnored(line?.SpeakerId, line?.Text, decision.IgnoreReason, decision.NpcId));
            return new NpcHearResult(decision, 0, false);
        }

        private bool TryStart(NpcAgent agent, double now)
        {
            NpcPendingTurn pending = agent.Pending;
            if (pending == null || agent.ActiveRun != null || agent.IsRemoved || _disposed)
            {
                return false;
            }

            if (now - agent.LastTurnStartedAt < agent.Budget.CooldownSeconds)
            {
                return false;
            }

            if (_options.MaxConcurrentTurns > 0 && _running >= _options.MaxConcurrentTurns)
            {
                return false;
            }

            agent.Pending = null;
            agent.LastTurnStartedAt = now;
            NpcSpeech speech = SpeechFor(agent, pending);
            if (!TryTakeCall(now))
            {
                if (_options.WhenBudgetExceeded == NpcBudgetExceededBehavior.Silence)
                {
                    Raise(TurnEnded, new NpcTurnEnded(agent.Id, pending.TurnId, NpcTurnOutcome.Cancelled, "",
                        LlmErrorCode.QuotaExceeded));
                    return true;
                }

                _sink.SpeechStarted(speech);
                SpeakFallback(agent, pending, speech, LlmErrorCode.QuotaExceeded);
                return true;
            }

            AiTaskRequest request;
            try
            {
                request = BuildRequest(agent, pending);
            }
            catch (Exception ex)
            {
                LogFailure($"[Npc] Building the turn of '{agent.Id}' failed: {ex.GetType().Name}: {ex.Message}");
                _sink.SpeechStarted(speech);
                SpeakFallback(agent, pending, speech, LlmErrorCode.InvalidRequest);
                return true;
            }

            // WHY never disposed: a source with no timer and no wait handle needs no disposal, and disposing it
            // from the turn's own completion could run inside its Cancel() callbacks (a supersede that the
            // orchestrator observes synchronously). Dispose cancels every running turn explicitly instead.
            CancellationTokenSource cancellation = new();
            request.CallerCancellationToken = cancellation.Token;
            NpcTurnRun run = new(agent, pending, speech, cancellation, now);
            agent.ActiveRun = run;
            _running++;
            _sink.SpeechStarted(speech);
            _ = ExecuteAsync(run, request);
            return true;
        }

        private void StartWaitingTurns()
        {
            if (_disposed)
            {
                return;
            }

            double now = _clock.NowSeconds;
            List<NpcAgent> waiting = null;
            foreach (NpcAgent agent in _agents)
            {
                if (agent.Pending != null && agent.ActiveRun == null)
                {
                    (waiting ??= new List<NpcAgent>()).Add(agent);
                }
            }

            if (waiting == null)
            {
                return;
            }

            waiting.Sort((a, b) => a.Pending.RequestedAt.CompareTo(b.Pending.RequestedAt));
            foreach (NpcAgent agent in waiting)
            {
                if (_options.MaxConcurrentTurns > 0 && _running >= _options.MaxConcurrentTurns)
                {
                    return;
                }

                TryStart(agent, now);
            }
        }

        private AiTaskRequest BuildRequest(NpcAgent agent, NpcPendingTurn pending)
        {
            return new AiTaskRequest
            {
                RoleId = agent.Persona.EffectiveRoleId,
                RoutingProfileId = (agent.Persona.RoutingProfileId ?? "").Trim(),
                RequestSystemInstructions = _prompts.BuildInstructions(agent, pending.Line),
                Hint = _prompts.BuildUserLine(agent, pending.Line, pending.Text),
                Priority = agent.Budget.QueuePriority,
                MaxOutputTokens = agent.Budget.MaxOutputTokens > 0 ? agent.Budget.MaxOutputTokens : (int?)null,
                CancellationScope = NpcMemoryScopes.CancellationScopeFor(agent.Id),
                SourceTag = agent.SourceTag,
                ActorContext = _actors.CreateTurnActor(agent, pending.Line),
                TraceId = "npc-" + agent.Id + "-" + pending.TurnId
            };
        }

        private async Task ExecuteAsync(NpcTurnRun run, AiTaskRequest request)
        {
            CancellationToken token = run.Cancellation.Token;
            StringBuilder text = new(128);
            LlmErrorCode failure = LlmErrorCode.None;
            bool cancelled = false;
            try
            {
                if (!_options.UseStreaming && _orchestrator is IAiTaskResultService typed && typed.SupportsTaskResults)
                {
                    LlmCompletionResult result = await typed.RunTaskResultAsync(request, token);
                    if (result == null)
                    {
                        failure = LlmErrorCode.EmptyResponse;
                    }
                    else if (!result.Ok)
                    {
                        failure = result.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : result.ErrorCode;
                    }
                    else
                    {
                        text.Append(result.Content ?? "");
                    }
                }
                else
                {
                    await foreach (LlmStreamChunk chunk in _orchestrator.RunStreamingAsync(request, token))
                    {
                        if (chunk == null)
                        {
                            continue;
                        }

                        if (LlmTurnFailure.IsFailure(chunk))
                        {
                            failure = chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode;
                            break;
                        }

                        if (!string.IsNullOrEmpty(chunk.Text))
                        {
                            if (chunk.StartsNewMessage && text.Length > 0)
                            {
                                text.Append(' ');
                            }

                            text.Append(chunk.Text);
                            string soFar = text.ToString();
                            Dispatch(() => OnGrew(run, soFar));
                        }

                        if (chunk.IsDone)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex is LlmRequestFailedException typedFailure
                    ? typedFailure.ErrorCode
                    : LlmErrorCode.ProviderError;
                string reason = ex.GetType().Name;
                Dispatch(() => LogFailure($"[Npc] The turn of '{run.Agent.Id}' threw {reason} ({failure})."));
            }

            if (failure == LlmErrorCode.Cancelled || token.IsCancellationRequested)
            {
                cancelled = true;
            }

            string final = text.ToString();
            LlmErrorCode code = failure;
            bool wasCancelled = cancelled;
            Dispatch(() => OnFinished(run, final, code, wasCancelled));
        }

        private void OnGrew(NpcTurnRun run, string textSoFar)
        {
            if (run.Ended || _disposed)
            {
                return;
            }

            string shown = NpcText.CleanReply(textSoFar, run.Agent.Name, run.Agent.Budget.MaxReplyCharacters);
            if (shown.Length > 0)
            {
                _sink.SpeechGrew(run.Speech, shown);
            }
        }

        private void OnFinished(NpcTurnRun run, string raw, LlmErrorCode failure, bool cancelled)
        {
            if (run.Ended || _disposed)
            {
                return;
            }

            NpcAgent agent = run.Agent;
            if (cancelled)
            {
                CancelRun(run, NpcTurnOutcome.Cancelled, true);
                StartWaitingTurns();
                return;
            }

            EndRun(run);
            string reply = failure == LlmErrorCode.None
                ? NpcText.CleanReply(raw, agent.Name, agent.Budget.MaxReplyCharacters)
                : "";
            if (failure == LlmErrorCode.None && reply.Length == 0)
            {
                failure = LlmErrorCode.EmptyResponse;
            }

            if (failure == LlmErrorCode.None &&
                (!_moderation.TryFilter(reply, TextModerationDirection.Output, out reply) ||
                 string.IsNullOrWhiteSpace(reply)))
            {
                failure = LlmErrorCode.InvalidRequest;
            }

            if (failure != LlmErrorCode.None)
            {
                LogFailure($"[Npc] The turn of '{agent.Id}' failed ({failure}); a fallback line was spoken.");
                SpeakFallback(agent, run.Pending, run.Speech, failure);
            }
            else
            {
                Speak(agent, run.Pending, run.Speech, reply.Trim(), false, LlmErrorCode.None);
            }

            StartWaitingTurns();
        }

        private void SpeakFallback(NpcAgent agent, NpcPendingTurn pending, NpcSpeech speech, LlmErrorCode failure)
        {
            Speak(agent, pending, speech, agent.NextFallbackLine(_options), true, failure);
        }

        private void Speak(NpcAgent agent, NpcPendingTurn pending, NpcSpeech speech, string text, bool isFallback,
            LlmErrorCode failure)
        {
            agent.ConversationPartnerId = (pending.Line.SpeakerId ?? "").Trim();
            agent.ConversationAtSeconds = _clock.NowSeconds;
            _sink.SpeechCompleted(speech, text, isFallback);
            Raise(NpcSaid, new NpcSaid(agent.Id, agent.Name, pending.Line.SpeakerId, pending.Line.EffectiveSpeakerName,
                text, isFallback, false, pending.TurnId, agent.OriginTag));
            Raise(TurnEnded, new NpcTurnEnded(agent.Id, pending.TurnId,
                isFallback ? NpcTurnOutcome.FallbackLine : NpcTurnOutcome.Replied, text, failure));
        }

        private void CancelRun(NpcTurnRun run, NpcTurnOutcome outcome, bool raiseEnded)
        {
            if (run.Ended)
            {
                return;
            }

            EndRun(run);
            try
            {
                run.Cancellation.Cancel();
            }
            catch (AggregateException ex)
            {
                _log.Warn($"[Npc] Cancelling a turn ran a callback that threw: {ex.InnerException?.Message}",
                    LogTag.Llm);
            }

            if (!raiseEnded)
            {
                return;
            }

            _sink.SpeechCancelled(run.Speech);
            Raise(TurnEnded, new NpcTurnEnded(run.Agent.Id, run.Pending.TurnId, outcome, "", LlmErrorCode.Cancelled));
        }

        private void EndRun(NpcTurnRun run)
        {
            run.Ended = true;
            if (ReferenceEquals(run.Agent.ActiveRun, run))
            {
                run.Agent.ActiveRun = null;
            }

            _running = Math.Max(0, _running - 1);
        }

        private void DropPending(NpcAgent agent, NpcTurnOutcome outcome)
        {
            NpcPendingTurn pending = agent.Pending;
            if (pending == null)
            {
                return;
            }

            agent.Pending = null;
            Raise(TurnEnded, new NpcTurnEnded(agent.Id, pending.TurnId, outcome, "", LlmErrorCode.Cancelled));
        }

        private bool TryTakeCall(double now)
        {
            if (_options.MaxCallsPerMinute <= 0)
            {
                return true;
            }

            PruneCallStamps(now);
            if (_callStamps.Count >= _options.MaxCallsPerMinute)
            {
                return false;
            }

            _callStamps.Enqueue(now);
            return true;
        }

        private void PruneCallStamps(double now)
        {
            while (_callStamps.Count > 0 && now - _callStamps.Peek() >= MinutesWindowSeconds)
            {
                _callStamps.Dequeue();
            }
        }

        private static NpcSpeech SpeechFor(NpcAgent agent, NpcPendingTurn pending)
        {
            return new NpcSpeech(agent.Id, agent.Name, pending.Line.SpeakerId, pending.Line.EffectiveSpeakerName,
                pending.TurnId, agent.OriginTag);
        }

        /// <summary>
        /// Applies a turn result now when it arrives inside a director call on that call's thread (an orchestrator
        /// that completed synchronously), otherwise queues it for the next <see cref="Tick"/>.
        /// </summary>
        /// <remarks>
        /// WHY not "now when on the constructing thread": a continuation can run on that thread id between two
        /// director calls (a reused pool thread, or Unity resuming on the main thread), and applying it there
        /// would race with a director call made from elsewhere. Inside a call, on its thread, nothing else runs.
        /// </remarks>
        private void Dispatch(Action action)
        {
            if (Volatile.Read(ref _entryThreadId) == Environment.CurrentManagedThreadId)
            {
                RunContained(action);
                return;
            }

            lock (_queueGate)
            {
                if (!_disposed)
                {
                    _queued.Add(action);
                }
            }
        }

        private void DrainQueued()
        {
            lock (_queueGate)
            {
                if (_queued.Count == 0)
                {
                    return;
                }

                _drained.AddRange(_queued);
                _queued.Clear();
            }

            try
            {
                foreach (Action action in _drained)
                {
                    RunContained(action);
                }
            }
            finally
            {
                _drained.Clear();
            }
        }

        private void RunContained(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // WHY contained: these actions run from a fire-and-forget turn; an exception escaping one would
                // either vanish as an unobserved task fault or abort the host's frame inside Tick.
                _log.Warn($"[Npc] Applying a turn result threw: {ex.GetType().Name}: {ex.Message}", LogTag.Llm);
            }
        }

        private void LogFailure(string message)
        {
            double now = _clock.NowSeconds;
            if (now - _lastFailureLogAt < FailureLogIntervalSeconds)
            {
                return;
            }

            _lastFailureLogAt = now;
            _log.Warn(message, LogTag.Llm);
        }

        private void Raise<T>(Action<T> handler, T value)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                _log.Warn($"[Npc] An event handler threw: {ex.GetType().Name}: {ex.Message}", LogTag.Llm);
            }
        }

        private EntryScope Enter()
        {
            if (_entryDepth++ == 0)
            {
                Volatile.Write(ref _entryThreadId, Environment.CurrentManagedThreadId);
            }

            return new EntryScope(this);
        }

        private void Exit()
        {
            if (--_entryDepth <= 0)
            {
                _entryDepth = 0;
                Volatile.Write(ref _entryThreadId, 0);
            }
        }

        private readonly struct EntryScope : IDisposable
        {
            private readonly NpcDirector _owner;

            public EntryScope(NpcDirector owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                _owner?.Exit();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NpcDirector));
            }
        }
    }
}
