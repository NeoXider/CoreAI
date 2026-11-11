using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Logging;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>
    /// Drives the runtime repair loop: listens to the mod runtime's handler errors and source loads, opens incidents
    /// through <see cref="LuaModRuntimeRepairPolicy"/>, decides which failing mods it may rewrite (AI-written only by
    /// default, <see cref="LuaModAuthorshipTracker"/>), asks the model that maintains mods for one repair turn at a time
    /// through an <see cref="ILuaModRepairTurnRunner"/>, verifies the fix by watching the mod's errors, and tells the
    /// player one short line per step. The model applies the fix itself with <c>manage_mods reload</c>, which runs
    /// through the shared world-mutation gate exactly like a chat request, so the fix is backed up, recorded as one AI
    /// change and taken back by <c>/undo</c>; the coordinator never writes a mod. Engine-free: the host calls
    /// <see cref="Tick"/> once per frame. Events may arrive on any thread; turns start only from <see cref="Tick"/>.
    /// </summary>
    public sealed class LuaModSelfRepairCoordinator : IDisposable
    {
        /// <summary><c>ScriptName</c> of the repair lines this loop appends to the mod log.</summary>
        public const string RepairScriptName = LuaModConsoleModel.RepairScriptName;

        private const double MissingEndGraceSeconds = 5d;
        private const int MaximumErrorCharacters = 140;

        private readonly object _gate = new();
        private readonly ILuaModRuntime _runtime;
        private readonly ActorContext _actor;
        private readonly LuaModRuntimeRepairPolicy _policy;
        private readonly LuaModAuthorshipTracker _authorship;
        private readonly ILuaModRepairTurnRunner _runner;
        private readonly ILuaLogService _logs;
        private readonly IChatOverlayMessages _messages;
        private readonly ILuaModSourceStore _store;
        private readonly Func<double> _clock;
        private readonly Func<string> _playerRequest;
        private readonly ILog _log;
        private readonly List<string> _queue = new();
        private readonly HashSet<string> _consented = new(StringComparer.Ordinal);
        private readonly Queue<double> _turnStarts = new();
        private readonly List<PendingEnd> _pendingEnds = new();
        private readonly List<LuaModRepairIncident> _fixedScratch = new();
        private readonly List<Post> _posts = new();
        private readonly Action<string, string, int> _onErrored;
        private readonly Action<string, string, LuaCapabilities> _onLoaded;
        private readonly Action<string, string, LuaCapabilities> _onUnloaded;
        private RunningTurn _running;
        private bool _offlineSaid;
        private double _stormNoticeUntil = double.NegativeInfinity;
        private bool _disposed;

        /// <param name="runtime">The mod runtime to watch.</param>
        /// <param name="actor">The trusted host actor the listeners and reads run as (the Programmer's actor).</param>
        /// <param name="options">The loop's numbers; null uses the defaults.</param>
        /// <param name="authorship">Who wrote each mod's source; null treats every mod as not AI-written.</param>
        /// <param name="runner">Runs the repair turns; null means no model, so nothing is repaired.</param>
        /// <param name="logs">The mod log: read for the repair request, written with the repair lines; optional.</param>
        /// <param name="messages">The in-world chat feed for the player's lines; optional.</param>
        /// <param name="store">The mod store, read to tell a bundled sample from a player's mod; optional.</param>
        /// <param name="clock">Monotonic seconds; null uses <see cref="Environment.TickCount"/>.</param>
        /// <param name="playerRequest">The player's current chat request, recorded with an AI-written mod; optional.</param>
        /// <param name="log">Diagnostics; optional.</param>
        public LuaModSelfRepairCoordinator(
            ILuaModRuntime runtime,
            ActorContext actor,
            LuaModRuntimeRepairOptions options = null,
            LuaModAuthorshipTracker authorship = null,
            ILuaModRepairTurnRunner runner = null,
            ILuaLogService logs = null,
            IChatOverlayMessages messages = null,
            ILuaModSourceStore store = null,
            Func<double> clock = null,
            Func<string> playerRequest = null,
            ILog log = null)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _actor = actor;
            _policy = new LuaModRuntimeRepairPolicy(options);
            _authorship = authorship ?? new LuaModAuthorshipTracker();
            _runner = runner;
            _logs = logs;
            _messages = messages;
            _store = store;
            _clock = clock ?? (() => Environment.TickCount / 1000d);
            _playerRequest = playerRequest;
            _log = log ?? NullLog.Instance;
            _onErrored = OnHandlerErrored;
            _onLoaded = OnSourceLoaded;
            _onUnloaded = OnSourceUnloaded;
            _runtime.AddModHandlerErroredListener(_actor, _onErrored);
            _runtime.AddModSourceLoadedListener(_actor, _onLoaded);
            _runtime.AddModSourceUnloadedListener(_actor, _onUnloaded);
        }

        /// <summary>The decision core, for a HUD or a test.</summary>
        public LuaModRuntimeRepairPolicy Policy => _policy;

        /// <summary>Who wrote each mod's source.</summary>
        public LuaModAuthorshipTracker Authorship => _authorship;

        /// <summary>The numbers in use.</summary>
        public LuaModRuntimeRepairOptions Options => _policy.Options;

        /// <summary>The mod a repair turn runs for; empty when none runs.</summary>
        public string RepairingModId
        {
            get
            {
                lock (_gate)
                {
                    return _running?.ModId ?? "";
                }
            }
        }

        /// <summary>Mods waiting for a repair turn, in order.</summary>
        public IReadOnlyList<string> Queue
        {
            get
            {
                lock (_gate)
                {
                    return _queue.ToArray();
                }
            }
        }

        /// <summary>Raised with every line the loop tells the player (also when no chat is connected).</summary>
        public event Action<string, string> PlayerLinePosted;

        /// <summary>
        /// Advances the loop: applies ended turns, accepts verified fixes, stops a turn past its timeout and starts the
        /// next due repair when the runner is free. Call once per frame from the main thread.
        /// </summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            double now = _clock();
            lock (_gate)
            {
                ApplyEndedTurnsLocked(now);
                _fixedScratch.Clear();
                _policy.Tick(now, _fixedScratch);
                foreach (LuaModRepairIncident incident in _fixedScratch)
                {
                    _consented.Remove(incident.ModId);
                    Say(incident.ModId, LuaLogLevel.Print, "'" + incident.ModId + "' is fixed: no errors for " +
                        Seconds(Options.VerifySeconds) + ".");
                }

                WatchRunningTurnLocked(now);
                StartNextLocked(now);
            }

            FlushPosts();
        }

        /// <summary>
        /// The player asked for a repair of <paramref name="modId"/> (<c>/fixmod</c>): a failing mod is queued whatever
        /// the mode and scope say. Returns the reply for the chat.
        /// </summary>
        public string RequestRepair(string modId)
        {
            string id = (modId ?? "").Trim();
            if (id.Length == 0)
            {
                return "Usage: /fixmod <mod id>.";
            }

            if (!_runtime.IsLoaded(_actor, id))
            {
                return "No mod '" + id + "' is running.";
            }

            if (_runner == null || !_runner.IsModelAvailable)
            {
                return "The AI is offline, so it cannot fix '" + id + "'.";
            }

            lock (_gate)
            {
                if (_running != null && string.Equals(_running.ModId, id, StringComparison.Ordinal))
                {
                    return "The AI is already fixing '" + id + "'.";
                }

                if (!_policy.Consent(id))
                {
                    LuaModRepairPhase phase = _policy.PhaseOf(id);
                    return phase == LuaModRepairPhase.Verifying
                        ? "'" + id + "' was just fixed and is being checked."
                        : "'" + id + "' has not failed since it last loaded; nothing to fix.";
                }

                _consented.Add(id);
                if (!_queue.Contains(id))
                {
                    _queue.Add(id);
                }

                return "Queued '" + id + "' for repair.";
            }
        }

        /// <summary>Stops listening and cancels a running repair turn.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _runtime.RemoveModHandlerErroredListener(_actor, _onErrored);
            _runtime.RemoveModSourceLoadedListener(_actor, _onLoaded);
            _runtime.RemoveModSourceUnloadedListener(_actor, _onUnloaded);
            RunningTurn running;
            lock (_gate)
            {
                running = _running;
                _running = null;
                _queue.Clear();
            }

            running?.Handle?.Cancel();
        }

        private void OnHandlerErrored(string modId, string error, int consecutiveCount)
        {
            if (_disposed)
            {
                return;
            }

            string id = (modId ?? "").Trim();
            double now = _clock();
            lock (_gate)
            {
                LuaModRepairErrorOutcome outcome = _policy.RecordError(id, error, consecutiveCount, now);
                LuaModRepairIncident incident = _policy.Get(id);
                switch (outcome)
                {
                    case LuaModRepairErrorOutcome.IncidentOpened:
                        OnIncidentOpenedLocked(incident, false);
                        break;
                    case LuaModRepairErrorOutcome.Relapsed:
                        Say(id, LuaLogLevel.Warn, "'" + id + "' fails again: " + Describe(incident) + ".");
                        Enqueue(id);
                        break;
                    case LuaModRepairErrorOutcome.AttemptFailed:
                        Say(id, LuaLogLevel.Warn, "'" + id + "' still fails: " + Describe(incident) + "; the AI tries again in " +
                                                  Seconds(incident.NextAttemptAt - now) + ".");
                        Enqueue(id);
                        break;
                    case LuaModRepairErrorOutcome.GaveUp:
                        OnGaveUpLocked(incident);
                        break;
                }
            }

            FlushPosts();
        }

        private void OnSourceLoaded(string modId, string source, LuaCapabilities capabilities)
        {
            if (_disposed)
            {
                return;
            }

            string id = (modId ?? "").Trim();
            lock (_gate)
            {
                bool repairingThis = _running != null && string.Equals(_running.ModId, id, StringComparison.Ordinal);
                string request = repairingThis ? "" : SafePlayerRequest();
                _authorship.NoteSourceLoaded(id, source, request, out bool writtenByAiNow);
                if (repairingThis && writtenByAiNow)
                {
                    _policy.NoteFixLanded(id, _clock());
                    return;
                }

                _policy.NoteReplaced(id);
                _consented.Remove(id);
                _queue.Remove(id);
            }
        }

        private void OnSourceUnloaded(string modId, string source, LuaCapabilities capabilities)
        {
            if (_disposed)
            {
                return;
            }

            string id = (modId ?? "").Trim();
            lock (_gate)
            {
                _queue.Remove(id);
                _consented.Remove(id);
                if (_running == null || !string.Equals(_running.ModId, id, StringComparison.Ordinal))
                {
                    _policy.Forget(id);
                }
            }
        }

        private void OnIncidentOpenedLocked(LuaModRepairIncident incident, bool consented)
        {
            string id = incident.ModId;
            string failing = "Mod '" + id + "' is failing: " + Describe(incident) + ".";
            if (Options.Mode == LuaModRuntimeRepairMode.Off && !consented)
            {
                Say(id, LuaLogLevel.Warn, failing);
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (_runner == null || !_runner.IsModelAvailable)
            {
                Say(id, LuaLogLevel.Warn, _offlineSaid
                    ? failing
                    : failing + " The AI is offline, so nothing repairs it; fix it in the Hub (Mods).");
                _offlineSaid = true;
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.Offline);
                return;
            }

            if (!consented && Options.Scope == LuaModRuntimeRepairScope.AiWrittenOnly &&
                _authorship.AuthorOf(id) != LuaModAuthor.Ai)
            {
                Say(id, LuaLogLevel.Warn, failing + " " + (IsBundled(id) ? "It is a bundled sample" : "The AI did not write it") +
                                          ", so the AI leaves it alone; /fixmod " + id + " asks the AI to fix it.");
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (!consented && Options.Mode == LuaModRuntimeRepairMode.AskFirst)
            {
                Say(id, LuaLogLevel.Warn, failing + " /fixmod " + id + " asks the AI to fix it.");
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (!_queue.Contains(id) && _queue.Count >= Options.MaxQueuedMods)
            {
                Say(id, LuaLogLevel.Warn, failing + " The AI is busy with other failing mods; /fixmod " + id + " queues it.");
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.TooManyFailing);
                return;
            }

            Say(id, LuaLogLevel.Warn, failing);
            Enqueue(id);
        }

        private void OnGaveUpLocked(LuaModRepairIncident incident)
        {
            string id = incident.ModId;
            _queue.Remove(id);
            _consented.Remove(id);
            switch (incident.GiveUpReason)
            {
                case LuaModRepairGiveUpReason.IncidentsExhausted:
                    Say(id, LuaLogLevel.Warn, "'" + id + "' keeps failing (" + Describe(incident) +
                                              "); the AI stops repairing it for this session. /fixmod " + id +
                                              " tries again.");
                    break;
                case LuaModRepairGiveUpReason.StoppedByPlayer:
                    Say(id, LuaLogLevel.Print, "Stopped repairing '" + id + "'. /fixmod " + id + " starts again.");
                    break;
                case LuaModRepairGiveUpReason.Offline:
                    Say(id, LuaLogLevel.Warn, "Could not reach the AI to fix '" + id + "'; fix it in the Hub (Mods).");
                    _offlineSaid = true;
                    break;
                default:
                    Say(id, LuaLogLevel.Warn, "Could not fix '" + id + "' after " +
                                              incident.Attempt.ToString(CultureInfo.InvariantCulture) + " tries" +
                                              (IsQuarantined(id) ? "; its handlers stay paused" : "") +
                                              ". /undo takes back the AI's change; /fixmod " + id + " tries again.");
                    break;
            }
        }

        private void Enqueue(string id)
        {
            if (!_queue.Contains(id))
            {
                _queue.Add(id);
            }
        }

        private void ApplyEndedTurnsLocked(double now)
        {
            if (_pendingEnds.Count == 0)
            {
                return;
            }

            PendingEnd[] ended = _pendingEnds.ToArray();
            _pendingEnds.Clear();
            foreach (PendingEnd pending in ended)
            {
                if (_running == null || !ReferenceEquals(_running, pending.Turn))
                {
                    continue;
                }

                FinishRunningLocked(pending.End, pending.Message, now);
            }
        }

        private void FinishRunningLocked(LuaModRepairTurnEnd end, string message, double now)
        {
            RunningTurn running = _running;
            _running = null;
            if (running.TimedOut && end == LuaModRepairTurnEnd.Cancelled)
            {
                end = LuaModRepairTurnEnd.TimedOut;
            }

            string id = running.ModId;
            LuaModRepairIncident incident = _policy.Get(id);
            bool changed = incident != null && incident.FixLanded;
            LuaModRepairAttemptOutcome outcome = _policy.EndAttempt(id, end, now);
            switch (outcome)
            {
                case LuaModRepairAttemptOutcome.RetryScheduled:
                    string why = end == LuaModRepairTurnEnd.TimedOut
                        ? "The repair of '" + id + "' took too long"
                        : end == LuaModRepairTurnEnd.Failed && !changed
                            ? "The AI could not fix '" + id + "'" + (message.Length > 0 ? " (" + message + ")" : "")
                            : !changed
                                ? "The AI did not change '" + id + "'"
                                : "'" + id + "' still fails: " + Describe(incident);
                    Say(id, LuaLogLevel.Warn, why + "; it tries again in " + Seconds(incident.NextAttemptAt - now) + ".");
                    Enqueue(id);
                    break;
                case LuaModRepairAttemptOutcome.GaveUp:
                    OnGaveUpLocked(incident);
                    break;
            }
        }

        private void WatchRunningTurnLocked(double now)
        {
            RunningTurn running = _running;
            if (running == null)
            {
                return;
            }

            if (!running.TimedOut && now - running.StartedAt > Options.TurnTimeoutSeconds)
            {
                running.TimedOut = true;
                running.Handle?.Cancel();
            }

            if (running.Handle != null && !running.Handle.IsRunning)
            {
                if (running.NotRunningSince < 0d)
                {
                    running.NotRunningSince = now;
                }
                else if (now - running.NotRunningSince > MissingEndGraceSeconds)
                {
                    FinishRunningLocked(running.TimedOut ? LuaModRepairTurnEnd.TimedOut : LuaModRepairTurnEnd.Cancelled, "", now);
                }
            }
        }

        private void StartNextLocked(double now)
        {
            if (_running != null || _queue.Count == 0 || _runner == null)
            {
                return;
            }

            while (_turnStarts.Count > 0 && now - _turnStarts.Peek() > Options.TurnWindowSeconds)
            {
                _turnStarts.Dequeue();
            }

            for (int index = 0; index < _queue.Count; index++)
            {
                string id = _queue[index];
                if (!_policy.IsAttemptDue(id, now))
                {
                    LuaModRepairPhase phase = _policy.PhaseOf(id);
                    if (phase != LuaModRepairPhase.Backoff)
                    {
                        _queue.RemoveAt(index);
                        index--;
                    }

                    continue;
                }

                if (!_runner.IsModelAvailable)
                {
                    _queue.RemoveAt(index);
                    _policy.ParkDetected(id, LuaModRepairGiveUpReason.Offline);
                    Say(id, LuaLogLevel.Warn, _offlineSaid
                        ? "The AI is offline; '" + id + "' is not repaired."
                        : "The AI is offline, so nothing repairs '" + id + "'; fix it in the Hub (Mods).");
                    _offlineSaid = true;
                    return;
                }

                if (_runner.IsBusy)
                {
                    return;
                }

                if (_turnStarts.Count >= Options.MaxTurnsPerWindow)
                {
                    if (now >= _stormNoticeUntil)
                    {
                        double resume = _turnStarts.Peek() + Options.TurnWindowSeconds - now;
                        _stormNoticeUntil = now + resume;
                        Say(id, LuaLogLevel.Warn, "Self-repair pauses for " + Seconds(resume) +
                                                  ": too many repairs in a short time.");
                    }

                    return;
                }

                LuaModRepairIncident incident = _policy.Get(id);
                LuaModRepairTurnRequest request = BuildRequestLocked(incident, incident.Attempt + 1);
                RunningTurn turn = new() { ModId = id, StartedAt = now };

                // WHY the attempt begins before the runner is called: a runner may run the whole turn, the model's
                // reload included, inside TryStart, and that reload must find the mod in its running attempt.
                _policy.TryBeginAttempt(id, now, out int attempt);
                _running = turn;
                ILuaModRepairTurnHandle handle;
                try
                {
                    handle = _runner.TryStart(request, (end, message) => OnTurnEnded(turn, end, message));
                }
                catch (Exception ex)
                {
                    _log.Warn("[LuaModSelfRepair] The turn runner threw for '" + id + "': " + ex.Message, LogTag.Lua);
                    handle = null;
                }

                if (handle == null)
                {
                    _running = null;
                    _pendingEnds.RemoveAll(pending => ReferenceEquals(pending.Turn, turn));
                    _policy.AbortAttempt(id);
                    return;
                }

                turn.Handle = handle;
                _turnStarts.Enqueue(now);
                _queue.RemoveAt(index);
                AppendLogLocked(id, LuaLogLevel.Print, "AI is fixing '" + id + "' (try " +
                                                       attempt.ToString(CultureInfo.InvariantCulture) + " of " +
                                                       Options.MaxAttemptsPerIncident.ToString(CultureInfo.InvariantCulture) + ").");
                _posts.Add(new Post(id, request.Announcement, !(_runner is ChatOverlayLuaModRepairTurnRunner)));
                return;
            }
        }

        private LuaModRepairTurnRequest BuildRequestLocked(LuaModRepairIncident incident, int attempt)
        {
            string id = incident.ModId;
            string source = "";
            try
            {
                if (!_runtime.TryGetModSource(_actor, id, out source))
                {
                    source = "";
                }
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading the source of '" + id + "' failed: " + ex.Message, LogTag.Lua);
            }

            IReadOnlyList<LuaLogEntry> recent = Array.Empty<LuaLogEntry>();
            try
            {
                recent = _logs?.Query(new LuaLogQuery { ModId = id, MaxCount = 12 }) ?? Array.Empty<LuaLogEntry>();
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading the log of '" + id + "' failed: " + ex.Message, LogTag.Lua);
            }

            LuaModRepairContext context = new()
            {
                ModId = id,
                Attempt = attempt,
                MaxAttempts = Options.MaxAttemptsPerIncident,
                Trigger = incident.Trigger,
                Error = incident.LastError,
                Site = incident.LastSite,
                Source = source ?? "",
                RecentLogs = recent,
                PlayerRequest = _authorship.RequestFor(id)
            };

            return new LuaModRepairTurnRequest
            {
                ModId = id,
                Attempt = attempt,
                MaxAttempts = Options.MaxAttemptsPerIncident,
                RoleId = BuiltInAgentRoleIds.Programmer,
                Text = LuaModRepairPromptBuilder.Build(context),
                Announcement = "AI is fixing '" + id + "' (try " + attempt.ToString(CultureInfo.InvariantCulture) + " of " +
                               Options.MaxAttemptsPerIncident.ToString(CultureInfo.InvariantCulture) + "). /stop cancels.",
                TraceId = Guid.NewGuid().ToString("N")
            };
        }

        private void OnTurnEnded(RunningTurn turn, LuaModRepairTurnEnd end, string message)
        {
            lock (_gate)
            {
                _pendingEnds.Add(new PendingEnd(turn, end, message ?? ""));
            }
        }

        private bool IsQuarantined(string id)
        {
            try
            {
                foreach (LuaModInfo info in _runtime.ListMods(_actor))
                {
                    if (info != null && string.Equals(info.Id, id, StringComparison.Ordinal))
                    {
                        return info.Quarantined;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Listing mods failed: " + ex.Message, LogTag.Lua);
            }

            return false;
        }

        private bool IsBundled(string id)
        {
            if (_store == null)
            {
                return false;
            }

            try
            {
                return _store.TryLoad(id, out _, out LuaModManifest manifest) && manifest != null &&
                       !string.IsNullOrEmpty(manifest.Origin);
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading the stored manifest of '" + id + "' failed: " + ex.Message,
                    LogTag.Lua);
                return false;
            }
        }

        private string SafePlayerRequest()
        {
            if (_playerRequest == null || !_authorship.IsAiMutationRunning)
            {
                return "";
            }

            try
            {
                return _playerRequest() ?? "";
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading the player's request failed: " + ex.Message, LogTag.Lua);
                return "";
            }
        }

        private void Say(string modId, LuaLogLevel level, string line)
        {
            AppendLogLocked(modId, level, line);
            _posts.Add(new Post(modId, line, true));
        }

        private void AppendLogLocked(string modId, LuaLogLevel level, string line)
        {
            if (_logs == null)
            {
                return;
            }

            try
            {
                _logs.Append(new LuaLogEntry
                {
                    ModId = modId ?? "",
                    Level = level,
                    Message = line,
                    ScriptName = RepairScriptName
                });
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Appending a repair line failed: " + ex.Message, LogTag.Lua);
            }
        }

        private void FlushPosts()
        {
            Post[] posts;
            lock (_gate)
            {
                if (_posts.Count == 0)
                {
                    return;
                }

                posts = _posts.ToArray();
                _posts.Clear();
            }

            foreach (Post post in posts)
            {
                try
                {
                    if (post.ToChat)
                    {
                        _messages?.PostSystemMessage(post.Line);
                    }

                    PlayerLinePosted?.Invoke(post.ModId, post.Line);
                }
                catch (Exception ex)
                {
                    _log.Warn("[LuaModSelfRepair] Posting a line failed: " + ex.Message, LogTag.Lua);
                }
            }
        }

        /// <summary>
        /// A line for the player. A turn's announcement is not posted to the chat by a chat runner's turn, which shows
        /// it as the host turn's own line.
        /// </summary>
        private readonly struct Post
        {
            public Post(string modId, string line, bool toChat)
            {
                ModId = modId ?? "";
                Line = line ?? "";
                ToChat = toChat;
            }

            public string ModId { get; }

            public string Line { get; }

            public bool ToChat { get; }
        }

        private static string Describe(LuaModRepairIncident incident)
        {
            if (incident == null)
            {
                return "an error";
            }

            string error = incident.LastError ?? "";
            if (error.Length > MaximumErrorCharacters)
            {
                error = error.Substring(0, MaximumErrorCharacters) + "...";
            }

            string where = incident.LastSite.Describe(incident.ModId);
            return where.Length > 0 ? error + " (" + where + ")" : error;
        }

        private static string Seconds(double seconds)
        {
            return Math.Max(0d, Math.Ceiling(seconds)).ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        private sealed class RunningTurn
        {
            public string ModId = "";
            public double StartedAt;
            public double NotRunningSince = -1d;
            public bool TimedOut;
            public ILuaModRepairTurnHandle Handle;
        }

        private readonly struct PendingEnd
        {
            public PendingEnd(RunningTurn turn, LuaModRepairTurnEnd end, string message)
            {
                Turn = turn;
                End = end;
                Message = message;
            }

            public RunningTurn Turn { get; }

            public LuaModRepairTurnEnd End { get; }

            public string Message { get; }
        }
    }
}
