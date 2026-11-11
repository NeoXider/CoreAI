using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>The optional services the repair loop works with; every one may be null.</summary>
    public sealed class LuaModSelfRepairPorts
    {
        /// <summary>
        /// What a repair turn costs on the backend the repairing role uses; read whenever an incident opens. Null is
        /// <see cref="LuaModRepairBackendCost.Unknown"/>, so <see cref="LuaModRuntimeRepairMode.ByBackend"/> asks first.
        /// </summary>
        public Func<LuaModRepairBackendCost> BackendCost { get; set; }

        /// <summary>
        /// The undo history's repair face: a repair joins the change it repairs, so one <c>/undo</c> takes back both.
        /// Null records a repair as an ordinary AI change.
        /// </summary>
        public IRbxWorldRepairUndo Undo { get; set; }

        /// <summary>
        /// Pauses a mod the way the runtime's quarantine does (mod id, reason); true when it paused it. Called from
        /// <see cref="LuaModSelfRepairCoordinator.Tick"/>. Null never pauses a mod.
        /// </summary>
        public Func<string, string, bool> Pause { get; set; }

        /// <summary>
        /// Appends one system-event note to the repairing role's chat history (stored out of the player's sight);
        /// null keeps no note.
        /// </summary>
        public Action<string> HistoryNote { get; set; }
    }

    /// <summary>
    /// Drives the runtime repair loop: listens to the mod runtime's handler errors and source loads, opens incidents
    /// through <see cref="LuaModRuntimeRepairPolicy"/>, decides which failing mods it may rewrite (AI-written only by
    /// default, <see cref="LuaModAuthorshipTracker"/>) and whether it may spend a model's tokens without asking
    /// (<see cref="LuaModRuntimeRepairMode.ByBackend"/>), asks the model that maintains mods for one repair turn at a
    /// time through an <see cref="ILuaModRepairTurnRunner"/>, verifies the fix by watching the mod's errors, and tells
    /// the player one short line per step. The model applies the fix itself with <c>manage_mods reload</c>, which runs
    /// through the shared world-mutation gate like a chat request; with an <see cref="IRbxWorldRepairUndo"/> the fix
    /// joins the change it repairs in the undo history. A mod it leaves alone while it fails all the time is paused,
    /// never unloaded. An <c>/undo</c> or <c>/redo</c> that puts back another version closes the incident, and the
    /// loop never repairs that version on its own. Engine-free: the host calls <see cref="Tick"/> once per frame.
    /// Events may arrive on any thread; turns start and mods are paused only from <see cref="Tick"/>.
    /// </summary>
    public sealed class LuaModSelfRepairCoordinator : IDisposable
    {
        /// <summary><c>ScriptName</c> of the repair lines this loop appends to the mod log.</summary>
        public const string RepairScriptName = LuaModConsoleModel.RepairScriptName;

        /// <summary>Characters of the system-event note a finished incident leaves in the role's history.</summary>
        public const int MaximumHistoryNoteCharacters = 480;

        /// <summary>The words that open that note.</summary>
        public const string HistoryNotePrefix = "system_event runtime_repair:";

        private const double MissingEndGraceSeconds = 5d;
        private const int MaximumErrorCharacters = 140;
        private const string PausedSentence =
            " Its code is paused and its objects stay; saving it in the Hub (Mods) runs it again.";

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
        private readonly LuaModSelfRepairPorts _ports;
        private readonly List<string> _queue = new();
        private readonly HashSet<string> _consented = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ModState> _states = new(StringComparer.Ordinal);
        private readonly Queue<double> _turnStarts = new();
        private readonly List<PendingEnd> _pendingEnds = new();
        private readonly List<LuaModRepairIncident> _fixedScratch = new();
        private readonly List<Post> _posts = new();
        private readonly List<string> _pendingPauses = new();
        private readonly Action<string, string, int> _onErrored;
        private readonly Action<string, string, LuaCapabilities> _onLoaded;
        private readonly Action<string, string, LuaCapabilities> _onUnloaded;
        private readonly Action<string> _onWorldReplaced;
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
        /// <param name="ports">The backend cost, the undo history, the pause and the history note; optional.</param>
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
            ILog log = null,
            LuaModSelfRepairPorts ports = null)
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
            _ports = ports ?? new LuaModSelfRepairPorts();
            _onErrored = OnHandlerErrored;
            _onLoaded = OnSourceLoaded;
            _onUnloaded = OnSourceUnloaded;
            _onWorldReplaced = OnWholeWorldReplaced;
            _runtime.AddModHandlerErroredListener(_actor, _onErrored);
            _runtime.AddModSourceLoadedListener(_actor, _onLoaded);
            _runtime.AddModSourceUnloadedListener(_actor, _onUnloaded);
            _authorship.WholeWorldReplaced += _onWorldReplaced;
        }

        /// <summary>The decision core, for a HUD or a test.</summary>
        public LuaModRuntimeRepairPolicy Policy => _policy;

        /// <summary>Who wrote each mod's source.</summary>
        public LuaModAuthorshipTracker Authorship => _authorship;

        /// <summary>The numbers in use.</summary>
        public LuaModRuntimeRepairOptions Options => _policy.Options;

        /// <summary>
        /// The mode the loop acts in now: <see cref="LuaModRuntimeRepairOptions.Mode"/>, with
        /// <see cref="LuaModRuntimeRepairMode.ByBackend"/> resolved by the backend's cost.
        /// </summary>
        public LuaModRuntimeRepairMode EffectiveMode =>
            LuaModRepairBackendCostRules.EffectiveMode(Options.Mode, BackendCost());

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

        /// <summary>
        /// Raised with every line the loop tells, in the wording the mod log has (the error, the mod id and the console
        /// and Hub hints), also when no chat is connected. The chat gets a short line that names the mod by its header
        /// name instead (<see cref="PlayerNameOf"/>).
        /// </summary>
        public event Action<string, string> PlayerLinePosted;

        /// <summary>
        /// Advances the loop: applies ended turns, accepts verified fixes, stops a turn past its timeout, starts the
        /// next due repair when the runner is free, and pauses the mods it decided to pause. Call once per frame from
        /// the main thread.
        /// </summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            double now = _clock();
            string[] pauses = null;
            lock (_gate)
            {
                ApplyEndedTurnsLocked(now);
                _fixedScratch.Clear();
                _policy.Tick(now, _fixedScratch);
                foreach (LuaModRepairIncident incident in _fixedScratch)
                {
                    OnFixedLocked(incident);
                }

                WatchRunningTurnLocked(now);
                StartNextLocked(now);
                if (_pendingPauses.Count > 0)
                {
                    pauses = _pendingPauses.ToArray();
                    _pendingPauses.Clear();
                }
            }

            ApplyPauses(pauses);
            FlushPosts();
        }

        /// <summary>
        /// The player asked for a repair of <paramref name="modId"/> (<c>/fixmod</c>): a failing mod is queued whatever
        /// the mode, the scope and the backend say. Returns the reply for the chat.
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
                StateOf(id).Watch = false;
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
            _authorship.WholeWorldReplaced -= _onWorldReplaced;
            RunningTurn running;
            lock (_gate)
            {
                running = _running;
                _running = null;
                _queue.Clear();
            }

            running?.CloseWindow();
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
                        OnIncidentOpenedLocked(incident, false, now);
                        break;
                    case LuaModRepairErrorOutcome.Relapsed:
                        Tell(id, LuaLogLevel.Warn, "'" + id + "' fails again: " + Describe(incident) + ".",
                            "Something in this world stopped working again (" + DisplayNameLocked(id) + "). The AI is fixing it.");
                        Enqueue(id);
                        break;
                    case LuaModRepairErrorOutcome.AttemptFailed:
                        Tell(id, LuaLogLevel.Warn, "'" + id + "' still fails: " + Describe(incident) + "; the AI tries again in " +
                                                   Seconds(incident.NextAttemptAt - now) + ".", StillFixingLine(DisplayNameLocked(id)));
                        Enqueue(id);
                        break;
                    case LuaModRepairErrorOutcome.GaveUp:
                        OnGaveUpLocked(incident, now);
                        break;
                    case LuaModRepairErrorOutcome.Counted:
                        WatchBleedingLocked(incident, now);
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
                bool playerMove = _authorship.IsPlayerMoveRunning;
                string request = repairingThis || playerMove ? "" : SafePlayerRequest();
                _authorship.NoteSourceLoaded(id, source, request, out bool writtenByAiNow);
                if (repairingThis && writtenByAiNow)
                {
                    _policy.NoteFixLanded(id, _clock());
                    return;
                }

                if (playerMove && _authorship.IsPutBackByPlayer(id))
                {
                    TakeBackLocked(id, true);
                    return;
                }

                _policy.NoteReplaced(id);
                _consented.Remove(id);
                _queue.Remove(id);
                StateOf(id).Watch = false;
            }

            FlushPosts();
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
                    _states.Remove(id);
                }
            }
        }

        /// <summary>
        /// An <c>/undo</c>, a <c>/redo</c> or a world load replaced the whole world. The restored mods start in a new
        /// runtime before it is published, so no load or unload of theirs was reported: every mod the loop knows is
        /// compared with what runs now. A mod that is gone is forgotten; a mod whose source the player's move changed
        /// is taken back (its incident closes and the loop leaves that version alone); one a world load changed is
        /// treated as replaced.
        /// </summary>
        private void OnWholeWorldReplaced(string trigger)
        {
            if (_disposed)
            {
                return;
            }

            bool byPlayer = LuaModAuthorshipTracker.IsPlayerMove(trigger);
            lock (_gate)
            {
                HashSet<string> ids = new(StringComparer.Ordinal);
                foreach (LuaModRepairIncident incident in _policy.Incidents)
                {
                    ids.Add(incident.ModId);
                }

                foreach (string id in _authorship.TrackedModIds)
                {
                    ids.Add(id);
                }

                if (_running != null)
                {
                    ids.Add(_running.ModId);
                }

                foreach (LuaModInfo info in SafeListMods())
                {
                    if (info != null && !string.IsNullOrEmpty(info.Id))
                    {
                        ids.Add(info.Id);
                    }
                }

                foreach (string id in ids)
                {
                    ReconcileLocked(id, byPlayer);
                }
            }

            FlushPosts();
        }

        private void ReconcileLocked(string id, bool byPlayer)
        {
            bool loaded;
            string source = "";
            try
            {
                loaded = _runtime.IsLoaded(_actor, id) && _runtime.TryGetModSource(_actor, id, out source);
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading '" + id + "' after a world replacement failed: " + ex.Message, LogTag.Lua);
                return;
            }

            bool repairingThis = _running != null && string.Equals(_running.ModId, id, StringComparison.Ordinal);
            if (!loaded)
            {
                if (repairingThis || IsActiveLocked(id))
                {
                    TakeBackLocked(id, byPlayer);
                }

                _policy.Forget(id);
                _states.Remove(id);
                _authorship.NoteSourceGone(id);
                return;
            }

            if (!_authorship.NoteSourcePutBack(id, source, byPlayer))
            {
                return;
            }

            if (byPlayer)
            {
                TakeBackLocked(id, true);
                return;
            }

            if (repairingThis)
            {
                StopRunningLocked();
            }

            _policy.CloseTakenBack(id);
            _consented.Remove(id);
            _queue.Remove(id);
            StateOf(id).Watch = false;
        }

        /// <summary>
        /// Closes the incident of a mod the player's move changed or removed: a running repair is stopped, a queued or
        /// scheduled one dropped, and the player is told only when a repair was under way.
        /// </summary>
        private void TakeBackLocked(string id, bool byPlayer)
        {
            bool repairingThis = _running != null && string.Equals(_running.ModId, id, StringComparison.Ordinal);
            bool wasActive = repairingThis || IsActiveLocked(id);
            if (repairingThis)
            {
                StopRunningLocked();
            }

            _policy.CloseTakenBack(id);
            _consented.Remove(id);
            _queue.Remove(id);
            ModState state = StateOf(id);
            state.Watch = false;
            if (wasActive)
            {
                Say(id, LuaLogLevel.Print, "Stopped repairing '" + id + "': " + (byPlayer ? "your /undo or /redo" : "a world load") +
                                           " took that change back, and the AI leaves the version it put back alone. /fixmod " +
                                           id + " asks the AI to fix it.");
            }
        }

        private bool IsActiveLocked(string id)
        {
            LuaModRepairPhase phase = _policy.PhaseOf(id);
            return _queue.Contains(id) || phase == LuaModRepairPhase.Repairing || phase == LuaModRepairPhase.Backoff;
        }

        /// <summary>Stops the running turn because its mod was taken back: no attempt is counted, no line follows.</summary>
        private void StopRunningLocked()
        {
            RunningTurn running = _running;
            if (running == null)
            {
                return;
            }

            running.TakenBack = true;
            running.CancelRequested = true;
            running.CloseWindow();
        }

        private void OnIncidentOpenedLocked(LuaModRepairIncident incident, bool consented, double now)
        {
            string id = incident.ModId;
            ModState state = StateOf(id);
            state.ResetForIncident();
            string failing = "Mod '" + id + "' is failing: " + Describe(incident) + ".";
            if (Options.Mode == LuaModRuntimeRepairMode.Off && !consented)
            {
                Tell(id, LuaLogLevel.Warn, failing, StoppedLine(DisplayNameLocked(id), false));
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (_runner == null || !_runner.IsModelAvailable)
            {
                ReportOfflineLocked(incident, failing, now);
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.Offline);
                return;
            }

            if (!consented && _authorship.IsPutBackByPlayer(id))
            {
                ReportOnlyLocked(incident, failing, "It is the version your /undo or /redo put back, so the AI leaves it alone.",
                    "/fixmod " + id + " asks the AI to fix it.", now);
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (!consented && Options.Scope == LuaModRuntimeRepairScope.AiWrittenOnly &&
                _authorship.AuthorOf(id) != LuaModAuthor.Ai)
            {
                ReportOnlyLocked(incident, failing, (IsBundled(id) ? "It is a bundled sample" : "The AI did not write it") +
                                                    ", so the AI leaves it alone.",
                    "/fixmod " + id + " asks the AI to fix it.", now);
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (!consented && EffectiveMode == LuaModRuntimeRepairMode.AskFirst)
            {
                bool byBackend = Options.Mode == LuaModRuntimeRepairMode.ByBackend;
                string reason = !byBackend
                    ? "The AI repairs it when you ask."
                    : BackendCost() == LuaModRepairBackendCost.Paid
                        ? "Your AI backend is paid, so nothing is sent to it until you ask."
                        : "Your AI backend may be paid, so nothing is sent to it until you ask.";
                ReportOnlyLocked(incident, failing, reason,
                    "/fixmod " + id + (byBackend ? " repairs it." : " asks the AI to fix it."), now);
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.NotAllowed);
                return;
            }

            if (!_queue.Contains(id) && _queue.Count >= Options.MaxQueuedMods)
            {
                ReportOnlyLocked(incident, failing, "The AI is busy with other failing mods.", "/fixmod " + id + " queues it.",
                    now);
                _policy.ParkDetected(id, LuaModRepairGiveUpReason.TooManyFailing);
                return;
            }

            Tell(id, LuaLogLevel.Warn, failing,
                "Something in this world stopped working (" + DisplayNameLocked(id) + "). The AI is fixing it.");
            Enqueue(id);
        }

        /// <summary>
        /// One line for a failing mod the loop does not repair now, and the mod is paused when it fails all the time;
        /// a mod that fails now and then keeps running and is watched, so it is paused once it fails all the time.
        /// The chat keeps the <c>/fixmod</c> hint: it is the player's one way to let the AI repair such a mod.
        /// </summary>
        private void ReportOnlyLocked(LuaModRepairIncident incident, string failing, string reason, string hint, double now)
        {
            bool paused = PauseIfBleedingLocked(incident, now);
            Tell(incident.ModId, LuaLogLevel.Warn, failing + " " + reason + (paused ? PausedSentence : "") + " " + hint,
                StoppedLine(DisplayNameLocked(incident.ModId), paused) + " " + hint);
            StateOf(incident.ModId).Watch = !paused;
        }

        /// <summary>
        /// The Offline backend: one sentence per session, every later failing mod only in the mod log; a mod that fails
        /// all the time is paused all the same.
        /// </summary>
        private void ReportOfflineLocked(LuaModRepairIncident incident, string failing, double now)
        {
            string id = incident.ModId;
            bool paused = PauseIfBleedingLocked(incident, now);
            StateOf(id).Watch = !paused;
            if (_offlineSaid)
            {
                AppendLogLocked(id, LuaLogLevel.Warn, failing + (paused ? PausedSentence : ""));
                return;
            }

            _offlineSaid = true;
            Tell(id, LuaLogLevel.Warn, failing + " The AI is offline, so nothing repairs failing mods" +
                                       (_ports.Pause != null ? "; one that keeps failing is paused and its objects stay" : "") +
                                       ". The mod console (/console) lists them; fix them in the Hub (Mods).",
                StoppedLine(DisplayNameLocked(id), paused));
        }

        private void OnGaveUpLocked(LuaModRepairIncident incident, double now)
        {
            string id = incident.ModId;
            ModState state = StateOf(id);
            _queue.Remove(id);
            _consented.Remove(id);
            string outcome = "gave up after " + incident.Attempt.ToString(CultureInfo.InvariantCulture) + " tries";
            switch (incident.GiveUpReason)
            {
                case LuaModRepairGiveUpReason.IncidentsExhausted:
                {
                    bool paused = PauseIfBleedingLocked(incident, now);
                    state.Watch = !paused;
                    Tell(id, LuaLogLevel.Warn, "'" + id + "' keeps failing (" + Describe(incident) +
                                               "); the AI stops repairing it for this session." +
                                               (paused ? PausedSentence : "") + " /fixmod " + id + " tries again.",
                        GaveUpLine(DisplayNameLocked(id), paused));
                    break;
                }
                case LuaModRepairGiveUpReason.StoppedByPlayer:
                    outcome = "stopped by the player";
                    Say(id, LuaLogLevel.Print, "Stopped repairing '" + id + "'. /fixmod " + id + " starts again.");
                    break;
                case LuaModRepairGiveUpReason.Offline:
                {
                    outcome = "the AI could not be reached";
                    bool paused = PauseIfBleedingLocked(incident, now);
                    state.Watch = !paused;
                    string line = "Could not reach the AI to fix '" + id + "'." + (paused ? PausedSentence : "") +
                                  " Fix it in the Hub (Mods).";
                    if (_offlineSaid)
                    {
                        AppendLogLocked(id, LuaLogLevel.Warn, line);
                    }
                    else
                    {
                        Tell(id, LuaLogLevel.Warn, line, GaveUpLine(DisplayNameLocked(id), paused));
                    }

                    _offlineSaid = true;
                    break;
                }
                default:
                {
                    bool paused = PauseIfBleedingLocked(incident, now);
                    state.Watch = !paused;
                    Tell(id, LuaLogLevel.Warn, "Could not fix '" + id + "' after " +
                                               incident.Attempt.ToString(CultureInfo.InvariantCulture) + " tries." +
                                               (paused ? PausedSentence : " It keeps running.") +
                                               " /fixmod " + id + " tries again; " + GaveUpUndoWay(state.UndoRecording),
                        GaveUpLine(DisplayNameLocked(id), paused));
                    break;
                }
            }

            NoteHistoryLocked(id, state, outcome);
        }

        private void OnFixedLocked(LuaModRepairIncident incident)
        {
            string id = incident.ModId;
            ModState state = StateOf(id);
            _consented.Remove(id);
            string repaired = "";
            try
            {
                if (!_runtime.TryGetModSource(_actor, id, out repaired))
                {
                    repaired = "";
                }
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading the repaired source of '" + id + "' failed: " + ex.Message, LogTag.Lua);
            }

            LuaModRepairSourceComparison comparison = LuaModRepairChangeReport.Compare(state.FailingSource, repaired);
            string undoWay = LuaModRepairChangeReport.UndoWay(state.UndoRecording);
            Tell(id, LuaLogLevel.Print, LuaModRepairChangeReport.BuildFixedLine(id, state.LastSummary, comparison, undoWay),
                LuaModRepairChangeReport.BuildFixedLine(PlayerNameOf(LuaModHeader.Parse(repaired ?? "", id).Name, id),
                    state.LastSummary, comparison, undoWay));
            NoteHistoryLocked(id, state, comparison.IsSuspicious ? "errors stopped, but the fix removed code" : "fixed");
        }

        /// <summary>
        /// A paused-or-not decision for a mod the loop leaves alone: true when the mod is paused now (by the runtime's
        /// own quarantine, or by the pause this call schedules for the next <see cref="Tick"/>).
        /// </summary>
        private bool PauseIfBleedingLocked(LuaModRepairIncident incident, double now)
        {
            if (incident == null)
            {
                return false;
            }

            if (IsQuarantined(incident.ModId))
            {
                return true;
            }

            if (_ports.Pause == null || !_policy.IsFailingContinuously(incident.ModId, now))
            {
                return false;
            }

            if (!_pendingPauses.Contains(incident.ModId))
            {
                _pendingPauses.Add(incident.ModId);
            }

            return true;
        }

        /// <summary>A left-alone mod that keeps failing: once it fails all the time, it is paused with one line.</summary>
        private void WatchBleedingLocked(LuaModRepairIncident incident, double now)
        {
            if (incident == null || incident.Phase != LuaModRepairPhase.GaveUp)
            {
                return;
            }

            ModState state = StateOf(incident.ModId);
            if (!state.Watch || _ports.Pause == null || !_policy.IsFailingContinuously(incident.ModId, now))
            {
                return;
            }

            state.Watch = false;
            if (IsQuarantined(incident.ModId))
            {
                return;
            }

            _pendingPauses.Add(incident.ModId);
            string line = "'" + incident.ModId + "' fails all the time now (" + Describe(incident) + ")." + PausedSentence +
                          " /fixmod " + incident.ModId + " asks the AI to fix it.";
            if (incident.GiveUpReason == LuaModRepairGiveUpReason.Offline && _offlineSaid)
            {
                AppendLogLocked(incident.ModId, LuaLogLevel.Warn, line);
            }
            else
            {
                Tell(incident.ModId, LuaLogLevel.Warn, line, StoppedLine(DisplayNameLocked(incident.ModId), true) +
                                                             " /fixmod " + incident.ModId + " asks the AI to fix it.");
            }
        }

        private void ApplyPauses(string[] pauses)
        {
            if (pauses == null || _ports.Pause == null)
            {
                return;
            }

            foreach (string id in pauses)
            {
                try
                {
                    _ports.Pause(id, "the AI's self-repair left it alone while it kept failing");
                }
                catch (Exception ex)
                {
                    _log.Warn("[LuaModSelfRepair] Pausing '" + id + "' failed: " + ex.Message, LogTag.Lua);
                }
            }
        }

        /// <summary>
        /// One system-event note in the repairing role's history for an incident in which the model changed the mod, so
        /// a later chat turn knows what it changed; the repair requests themselves never enter the history.
        /// </summary>
        private void NoteHistoryLocked(string id, ModState state, string outcome)
        {
            if (_ports.HistoryNote == null || !state.FixLanded)
            {
                return;
            }

            state.FixLanded = false;
            string summary = state.LastSummary.Length > 0 ? state.LastSummary : "not described";
            string note = HistoryNotePrefix + " mod '" + id + "' failed while the game ran (" +
                          Clip(SingleLine(state.FirstError), MaximumErrorCharacters) + "); the AI repaired it with " +
                          "manage_mods reload, outcome: " + outcome + ". Its last change: " + summary;
            if (note.Length > MaximumHistoryNoteCharacters)
            {
                note = note.Substring(0, MaximumHistoryNoteCharacters - 3) + "...";
            }

            try
            {
                _ports.HistoryNote(note);
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Writing the repair note to the history failed: " + ex.Message, LogTag.Lua);
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

                FinishRunningLocked(pending.Outcome, now);
            }
        }

        private void FinishRunningLocked(LuaModRepairTurnOutcome result, double now)
        {
            RunningTurn running = _running;
            _running = null;
            running.CloseWindow();
            string id = running.ModId;
            if (running.TakenBack)
            {
                return;
            }

            LuaModRepairTurnEnd end = result.End;
            if (running.TimedOut && end == LuaModRepairTurnEnd.Cancelled)
            {
                end = LuaModRepairTurnEnd.TimedOut;
            }

            ModState state = StateOf(id);
            LuaModRepairIncident incident = _policy.Get(id);
            bool changed = incident != null && incident.FixLanded;
            state.LastReloaded = changed;
            state.LastSummary = changed ? LuaModRepairChangeReport.ExtractSummary(result.Answer) : "";
            state.LastAskedError = running.AskedError;
            state.LastAskedSite = running.AskedSite;
            if (changed)
            {
                state.FixLanded = true;
                RbxWorldRepairRecording recording = running.Window?.Recording ?? RbxWorldRepairRecording.None;
                if (recording != RbxWorldRepairRecording.None)
                {
                    state.UndoRecording = recording;
                }
            }

            LuaModRepairAttemptOutcome outcome = _policy.EndAttempt(id, end, now);
            switch (outcome)
            {
                case LuaModRepairAttemptOutcome.RetryScheduled:
                    string message = result.Message ?? "";
                    string why = end == LuaModRepairTurnEnd.TimedOut
                        ? "The repair of '" + id + "' took too long"
                        : end == LuaModRepairTurnEnd.Failed && !changed
                            ? "The AI could not fix '" + id + "'" + (message.Length > 0 ? " (" + message + ")" : "")
                            : !changed
                                ? "The AI did not change '" + id + "'"
                                : "'" + id + "' still fails: " + Describe(incident);
                    Tell(id, LuaLogLevel.Warn, why + "; it tries again in " + Seconds(incident.NextAttemptAt - now) + ".",
                        StillFixingLine(DisplayNameLocked(id)));
                    Enqueue(id);
                    break;
                case LuaModRepairAttemptOutcome.GaveUp:
                    OnGaveUpLocked(incident, now);
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

            if (running.CancelRequested && !running.CancelSent)
            {
                running.CancelSent = true;
                running.Handle?.Cancel();
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
                    FinishRunningLocked(new LuaModRepairTurnOutcome(
                        running.TimedOut ? LuaModRepairTurnEnd.TimedOut : LuaModRepairTurnEnd.Cancelled), now);
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
                    LuaModRepairIncident parked = _policy.Get(id);
                    _policy.ParkDetected(id, LuaModRepairGiveUpReason.Offline);
                    ReportOfflineLocked(parked, "Mod '" + id + "' is failing: " + Describe(parked) + ".", now);
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
                ModState state = StateOf(id);
                LuaModRepairTurnRequest request = BuildRequestLocked(incident, incident.Attempt + 1, state, out string source);
                if (incident.Attempt == 0)
                {
                    state.FailingSource = source;
                    state.FirstError = incident.LastError ?? "";
                }

                RunningTurn turn = new()
                {
                    ModId = id,
                    StartedAt = now,
                    AskedError = incident.LastError ?? "",
                    AskedSite = incident.LastSite,
                    Window = OpenRepairWindowLocked(id)
                };

                // WHY: the attempt begins before the runner is called: a runner may run the whole turn, the model's
                // reload included, inside TryStart, and that reload must find the mod in its running attempt.
                _policy.TryBeginAttempt(id, now, out int attempt);
                _running = turn;
                ILuaModRepairTurnHandle handle;
                try
                {
                    handle = _runner.TryStart(request, outcome => OnTurnEnded(turn, outcome));
                }
                catch (Exception ex)
                {
                    _log.Warn("[LuaModSelfRepair] The turn runner threw for '" + id + "': " + ex.Message, LogTag.Lua);
                    handle = null;
                }

                if (handle == null)
                {
                    _running = null;
                    turn.CloseWindow();
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

        /// <summary>
        /// Opens the undo history's repair window for a turn. An automatic repair of a source the AI wrote names the
        /// change that wrote it, so the repair can join it; a mod the player wrote or put back, and every repair the
        /// player asked for on such a mod, names none and stays its own entry.
        /// </summary>
        private IRbxWorldRepairWindow OpenRepairWindowLocked(string id)
        {
            if (_ports.Undo == null)
            {
                return null;
            }

            long repaired = _authorship.AuthorOf(id) == LuaModAuthor.Ai && !_authorship.IsPutBackByPlayer(id)
                ? _authorship.AiSequenceFor(id)
                : 0L;
            try
            {
                return _ports.Undo.BeginRepair(_actor.ActorId ?? "", repaired, id);
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Opening the undo history's repair window failed: " + ex.Message, LogTag.Lua);
                return null;
            }
        }

        private LuaModRepairTurnRequest BuildRequestLocked(LuaModRepairIncident incident, int attempt, ModState state,
            out string source)
        {
            string id = incident.ModId;
            source = "";
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

            bool hasPrevious = attempt > 1;
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
                PlayerRequest = _authorship.RequestFor(id),
                PreviousReloaded = hasPrevious && state.LastReloaded,
                PreviousSummary = hasPrevious ? state.LastSummary : "",
                PreviousError = hasPrevious ? state.LastAskedError : "",
                PreviousSite = hasPrevious ? state.LastAskedSite : default
            };

            return new LuaModRepairTurnRequest
            {
                ModId = id,
                Attempt = attempt,
                MaxAttempts = Options.MaxAttemptsPerIncident,
                RoleId = BuiltInAgentRoleIds.Programmer,
                Text = LuaModRepairPromptBuilder.Build(context),
                Announcement = "AI is fixing '" + PlayerNameOf(LuaModHeader.Parse(source ?? "", id).Name, id) + "' (try " +
                               attempt.ToString(CultureInfo.InvariantCulture) + " of " +
                               Options.MaxAttemptsPerIncident.ToString(CultureInfo.InvariantCulture) + "). /stop cancels.",
                TraceId = Guid.NewGuid().ToString("N"),
                HistoryMode = AiTaskHistoryMode.None
            };
        }

        private void OnTurnEnded(RunningTurn turn, LuaModRepairTurnOutcome outcome)
        {
            lock (_gate)
            {
                _pendingEnds.Add(new PendingEnd(turn, outcome));
            }
        }

        private LuaModRepairBackendCost BackendCost()
        {
            Func<LuaModRepairBackendCost> probe = _ports.BackendCost;
            if (probe == null)
            {
                return LuaModRepairBackendCost.Unknown;
            }

            try
            {
                return probe();
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Reading the backend's cost failed: " + ex.Message, LogTag.Lua);
                return LuaModRepairBackendCost.Unknown;
            }
        }

        private IReadOnlyList<LuaModInfo> SafeListMods()
        {
            try
            {
                return _runtime.ListMods(_actor) ?? (IReadOnlyList<LuaModInfo>)Array.Empty<LuaModInfo>();
            }
            catch (Exception ex)
            {
                _log.Warn("[LuaModSelfRepair] Listing mods failed: " + ex.Message, LogTag.Lua);
                return Array.Empty<LuaModInfo>();
            }
        }

        private bool IsQuarantined(string id)
        {
            foreach (LuaModInfo info in SafeListMods())
            {
                if (info != null && string.Equals(info.Id, id, StringComparison.Ordinal))
                {
                    return info.Quarantined;
                }
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

        private ModState StateOf(string id)
        {
            if (!_states.TryGetValue(id, out ModState state))
            {
                state = new ModState();
                _states[id] = state;
            }

            return state;
        }

        private void Say(string modId, LuaLogLevel level, string line)
        {
            Tell(modId, level, line, line);
        }

        /// <summary>
        /// A line in two wordings: the detailed one (the error, the mod id, the console and Hub hints) for the mod log
        /// and <see cref="PlayerLinePosted"/>, and the player's short one for the chat.
        /// </summary>
        private void Tell(string modId, LuaLogLevel level, string line, string chatLine)
        {
            AppendLogLocked(modId, level, line);
            _posts.Add(new Post(modId, line, chatLine, true));
        }

        /// <summary>The name the player knows a running mod by (<see cref="PlayerNameOf"/>); its id when it has none.</summary>
        private string DisplayNameLocked(string id)
        {
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
                _log.Warn("[LuaModSelfRepair] Reading the name of '" + id + "' failed: " + ex.Message, LogTag.Lua);
                source = "";
            }

            return PlayerNameOf(LuaModHeader.Parse(source ?? "", id).Name, id);
        }

        /// <summary>
        /// The mod's header name as a player reads it: a series prefix before the first <c>" - "</c> is left out
        /// (<c>Block World 9 - Falling blocks</c> is <c>Falling blocks</c>); an empty name is the mod's id.
        /// </summary>
        public static string PlayerNameOf(string headerName, string modId)
        {
            string name = (headerName ?? "").Trim();
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0 && name.Substring(dash + 3).Trim().Length > 0)
            {
                name = name.Substring(dash + 3).Trim();
            }

            return name.Length > 0 ? name : (modId ?? "").Trim();
        }

        private static string StoppedLine(string name, bool paused)
        {
            return "Something in this world stopped working (" + name + ")" + (paused ? " and was paused" : "") +
                   ". Everything else keeps going.";
        }

        private static string StillFixingLine(string name)
        {
            return "The AI is still fixing " + name + ".";
        }

        private static string GaveUpLine(string name, bool paused)
        {
            return name + (paused ? " is paused" : " keeps running") + "; it could not be fixed this time.";
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
                        _messages?.PostSystemMessage(post.ChatLine);
                    }

                    PlayerLinePosted?.Invoke(post.ModId, post.Line);
                }
                catch (Exception ex)
                {
                    _log.Warn("[LuaModSelfRepair] Posting a line failed: " + ex.Message, LogTag.Lua);
                }
            }
        }

        private static string GaveUpUndoWay(RbxWorldRepairRecording recording)
        {
            switch (recording)
            {
                case RbxWorldRepairRecording.JoinedRepairedChange:
                    return "/undo takes back your request together with the AI's tries.";
                case RbxWorldRepairRecording.OwnEntry:
                    return "/undo takes back the AI's last try.";
                default:
                    return "/undo takes back the AI's change.";
            }
        }

        /// <summary>
        /// A line for the player: the detailed wording and the chat's short one. A turn's announcement is not posted to
        /// the chat by a chat runner's turn, which shows it as the host turn's own line.
        /// </summary>
        private readonly struct Post
        {
            public Post(string modId, string line, bool toChat) : this(modId, line, line, toChat)
            {
            }

            public Post(string modId, string line, string chatLine, bool toChat)
            {
                ModId = modId ?? "";
                Line = line ?? "";
                ChatLine = chatLine ?? Line;
                ToChat = toChat;
            }

            public string ModId { get; }

            public string Line { get; }

            public string ChatLine { get; }

            public bool ToChat { get; }
        }

        private static string Describe(LuaModRepairIncident incident)
        {
            if (incident == null)
            {
                return "an error";
            }

            string where = incident.LastSite.Describe(incident.ModId);
            string error = Clip(incident.LastError ?? "", MaximumErrorCharacters);
            return where.Length > 0 ? error + " (" + where + ")" : error;
        }

        private static string Clip(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length) + "...";
        }

        private static string SingleLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Seconds(double seconds)
        {
            return Math.Max(0d, Math.Ceiling(seconds)).ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>What the loop remembers of one mod between the attempts of an incident.</summary>
        private sealed class ModState
        {
            public string FailingSource = "";
            public string FirstError = "";
            public string LastSummary = "";
            public bool LastReloaded;
            public string LastAskedError = "";
            public LuaModErrorSite LastAskedSite;
            public RbxWorldRepairRecording UndoRecording;
            public bool FixLanded;
            public bool Watch;

            public void ResetForIncident()
            {
                FailingSource = "";
                FirstError = "";
                LastSummary = "";
                LastReloaded = false;
                LastAskedError = "";
                LastAskedSite = default;
                UndoRecording = RbxWorldRepairRecording.None;
                FixLanded = false;
                Watch = false;
            }
        }

        private sealed class RunningTurn
        {
            public string ModId = "";
            public double StartedAt;
            public double NotRunningSince = -1d;
            public bool TimedOut;
            public bool TakenBack;
            public bool CancelRequested;
            public bool CancelSent;
            public string AskedError = "";
            public LuaModErrorSite AskedSite;
            public ILuaModRepairTurnHandle Handle;
            public IRbxWorldRepairWindow Window;

            public void CloseWindow()
            {
                IRbxWorldRepairWindow window = Window;
                if (window == null)
                {
                    return;
                }

                try
                {
                    window.Dispose();
                }
                catch (Exception)
                {
                    // WHY: closing a window only removes it from the history's list; a host's own history may throw,
                    // and the repair must still end.
                }
            }
        }

        private readonly struct PendingEnd
        {
            public PendingEnd(RunningTurn turn, LuaModRepairTurnOutcome outcome)
            {
                Turn = turn;
                Outcome = outcome;
            }

            public RunningTurn Turn { get; }

            public LuaModRepairTurnOutcome Outcome { get; }
        }
    }
}
