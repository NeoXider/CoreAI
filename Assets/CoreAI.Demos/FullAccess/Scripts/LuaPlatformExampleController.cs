#if COREAI_LUA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using UnityEngine;
using VContainer;

namespace CoreAI.Demos
{
    /// <summary>
    /// GUI-less scene driver for "Lua as a second game language": creates every Lua script itself and
    /// loads it into <see cref="ILuaModRuntime"/> — a two-mod platform self-test (timers, tick alias,
    /// variables/closures, store, cross-mod events, coroutines) plus a self-playing 3D falling-blocks
    /// game ("Tetris") built entirely from one Lua mod on the Rbx API (Instance.new / Part), the
    /// production world surface. No LLM is involved; this is
    /// the deterministic reference the chat agent is later asked to reproduce via manage_mods tool calls.
    /// The retained-mode UI lives in <see cref="LuaPlatformHubPage"/>, which drives this component and
    /// reflects its live state through the public read-only properties below.
    /// WebGL driver entry points (SendMessage("LuaPlatformExample", …)): <see cref="RunSelfTest"/>,
    /// <see cref="StartTetris"/>, <see cref="StopTetris"/>, <see cref="DumpStatus"/> (alias
    /// <see cref="LogStatus"/>), <see cref="TetrisMove"/> (alias <see cref="NudgePiece"/>),
    /// <see cref="CreateWorldMarker"/>, <see cref="SaveWorld"/>,
    /// <see cref="RequestWorldLoad"/>, and <see cref="DumpWorldMarker"/>. There is intentionally no
    /// confirmation bypass: requested loads can be approved or rejected only in the player Hub UI.
    /// </summary>
    public sealed class LuaPlatformExampleController : MonoBehaviour
    {
        private const string SelfTestAId = "platform_selftest_a";
        private const string SelfTestBId = "platform_selftest_b";
        private const string TetrisId = "tetris3d";
        private const string BrowserMarkerPrefix = "CoreAI_WebGL_Marker_";
        private const int MaximumMarkerInputLength = 32;
        private const int MaximumSlotInputLength = 64;

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        private ILuaModRuntime _mods;
        private LuaTool.ILuaExecutor _worldLuaExecutor;
        private IRbxWorldRuntimeService _worldRuntimeService;
        private ActorContext _actorContext;
        private string _status = "Waiting for CoreAI scope.";
        private string _selfTestSummary = "Self-test not run yet.";
        private readonly List<string> _selfTestLines = new();
        private string _tetrisHud = "";
        private bool _pendingSelfTestUnload;
        private bool _worldDriverBusy;

        /// <summary>Current one-line driver status (loading, self-test progress, Tetris state).</summary>
        public string Status => _status;

        /// <summary>Latest self-test verdict line (PASS/FAIL summary or "not run yet").</summary>
        public string SelfTestSummary => _selfTestSummary;

        /// <summary>Latest Tetris HUD line reported by the mod, or an empty string when idle.</summary>
        public string TetrisHud => _tetrisHud;

        /// <summary>Per-check self-test report lines, most recent run.</summary>
        public IReadOnlyList<string> SelfTestLines => _selfTestLines;

        /// <summary>True once the mods runtime has been resolved and the driver is ready to accept commands.</summary>
        public bool IsReady => _mods != null;

        /// <summary>True while the Tetris mod is loaded and playing.</summary>
        public bool IsTetrisRunning => _mods != null && _mods.IsLoaded(_actorContext, TetrisId);

        private IEnumerator Start()
        {
            yield return null;

            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found; example disabled.";
                Debug.LogError($"[LuaPlatformExample] {_status}");
                enabled = false;
                yield break;
            }

            IObjectResolver luaContainer = CoreAiDemoScope.ResolveModsContainer(coreAiScope);

            IActorIdentityProvider actorIdentityProvider = luaContainer.Resolve<IActorIdentityProvider>();
            _actorContext = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            _mods = luaContainer.Resolve<ILuaModRuntime>();
            _worldLuaExecutor = luaContainer.ResolveOrDefault<LuaTool.ILuaExecutor>();
            _worldRuntimeService = luaContainer.ResolveOrDefault<IRbxWorldRuntimeService>();
            _mods.AddModReportEmittedListener(_actorContext, OnModReport);
            bool tetrisAlreadyRunning = _mods.IsLoaded(_actorContext, TetrisId);
            if (tetrisAlreadyRunning)
            {
                // The mod store rehydrated the game from a previous session — that IS the restart
                // test. Just re-enable report logging (it is not persisted).
                _mods.SetModReportLoggingEnabled(_actorContext, TetrisId, true);
            }

            _status = tetrisAlreadyRunning
                ? "Ready. Tetris mod restored from a previous session and running."
                : "Ready. Run the self-test or start Tetris.";
            Debug.Log($"[LuaPlatformExample] {_status}");
        }

        private void OnDestroy()
        {
            if (_mods != null)
            {
                _mods.RemoveModReportEmittedListener(_actorContext, OnModReport);
            }
        }

        private void Update()
        {
            if (_pendingSelfTestUnload)
            {
                // Unload outside the report callback: the callback fires from inside the runtime's
                // Tick dispatch and unloading mid-dispatch is legal but needlessly re-entrant.
                _pendingSelfTestUnload = false;
                _mods.UnloadMod(_actorContext, SelfTestAId);
                _mods.UnloadMod(_actorContext, SelfTestBId);
            }
        }

        /// <summary>Loads the self-test mod pair and waits for their verdict reports (~2 s).</summary>
        public void RunSelfTest()
        {
            if (_mods == null)
            {
                return;
            }

            _selfTestLines.Clear();
            _selfTestSummary = "Self-test running...";
            try
            {
                // ForgetMod also purges stale persisted copies from older builds; a persisted A
                // would autoload before B on restart and fail its load-time mods_get.
                _mods.ForgetMod(_actorContext, SelfTestAId);
                _mods.ForgetMod(_actorContext, SelfTestBId);

                // B first so it is already listening when A emits ping from its load chunk.
                _mods.LoadMod(_actorContext, SelfTestBId, SelfTestBSource, LuaCapabilities.All, false);
                _mods.LoadMod(_actorContext, SelfTestAId, SelfTestASource, LuaCapabilities.All, false);
                _mods.SetModReportLoggingEnabled(_actorContext, SelfTestAId, true);
                _mods.SetModReportLoggingEnabled(_actorContext, SelfTestBId, true);
                _status = "Self-test mods loaded; verdict arrives in ~2 s.";
            }
            catch (System.Exception ex)
            {
                _selfTestSummary = $"Self-test LOAD FAILED: {ex.Message}";
                _status = _selfTestSummary;
                Debug.LogError($"[LuaPlatformExample] {_selfTestSummary}");
            }
        }

        /// <summary>Loads (or reloads) the Tetris mod. Persisted: it must survive a restart.</summary>
        public void StartTetris()
        {
            if (_mods == null)
            {
                return;
            }

            try
            {
                if (_mods.IsLoaded(_actorContext, TetrisId))
                {
                    _mods.ReloadMod(_actorContext, TetrisId, TetrisSource);
                }
                else
                {
                    _mods.LoadMod(_actorContext, TetrisId, TetrisSource, LuaCapabilities.All, true);
                }

                _mods.SetModReportLoggingEnabled(_actorContext, TetrisId, true);
                _status = "Tetris mod loaded. It plays itself; watch the board.";
            }
            catch (System.Exception ex)
            {
                _status = $"Tetris load failed: {ex.Message}";
                Debug.LogError($"[LuaPlatformExample] {_status}");
            }
        }

        /// <summary>Unloads the Tetris mod (marks it inactive for the persistence host).</summary>
        public void StopTetris()
        {
            if (_mods != null && _mods.UnloadMod(_actorContext, TetrisId))
            {
                _status = "Tetris mod unloaded.";
            }
        }

        /// <summary>Logs the current status — polled from the WebGL page console.</summary>
        public void DumpStatus()
        {
            Debug.Log($"[LuaPlatformExample] STATUS status={_status} | selftest={_selfTestSummary} | " +
                      $"tetris={(_mods != null && _mods.IsLoaded(_actorContext, TetrisId) ? "running" : "stopped")} {_tetrisHud}");
        }

        /// <summary>WebGL SendMessage alias for <see cref="DumpStatus"/>.</summary>
        public void LogStatus()
        {
            DumpStatus();
        }

        /// <summary>Nudges the falling piece: payload "-1" left, "1" right (named-event demo).</summary>
        public void TetrisMove(string delta)
        {
            _mods?.EmitEvent(_actorContext, "tetris_move", delta ?? "0");
        }

        /// <summary>WebGL SendMessage alias for <see cref="TetrisMove"/>.</summary>
        public void NudgePiece(string delta)
        {
            TetrisMove(delta);
        }

        /// <summary>Creates one world-owned marker through the production execute_lua mutation path.</summary>
        public void CreateWorldMarker(string markerName)
        {
            CreateWorldMarkerAsync(markerName).Forget();
        }

        /// <summary>Saves the current world into a create-once manual slot.</summary>
        public void SaveWorld(string slot)
        {
            SaveWorldAsync(slot).Forget();
        }

        /// <summary>Requests a manual load that can be completed only through the player Hub UI.</summary>
        public void RequestWorldLoad(string slot)
        {
            RequestWorldLoadAsync(slot).Forget();
        }

        /// <summary>Logs whether the world-owned projection currently contains the named marker.</summary>
        public void DumpWorldMarker(string markerName)
        {
            DumpWorldMarkerAsync(markerName).Forget();
        }

        private async UniTaskVoid CreateWorldMarkerAsync(string markerName)
        {
            if (!TryNormalizeDriverInput(
                    markerName,
                    MaximumMarkerInputLength,
                    out string normalizedMarker))
            {
                LogWorldDriverFailure("WORLD_MARKER_CREATE", "invalid-name");
                return;
            }

            if (!TryEnterWorldDriver("WORLD_MARKER_CREATE", true, false))
            {
                return;
            }

            string durableName = BrowserMarkerPrefix + normalizedMarker;
            Debug.Log("[LuaPlatformExample] WORLD_MARKER_CREATE requested name=" + durableName);
            try
            {
                string code =
                    "local marker = workspace:FindFirstChild('" + durableName + "')\n" +
                    "if marker == nil then\n" +
                    "  marker = Instance.new('Folder')\n" +
                    "  marker.Name = '" + durableName + "'\n" +
                    "  marker.Parent = workspace\n" +
                    "end\n" +
                    "return marker.Name";
                CancellationToken cancellationToken = this.GetCancellationTokenOnDestroy();
                LuaTool.LuaResult result = await _worldLuaExecutor.ExecuteAsync(
                    code,
                    cancellationToken);
                if (result != null && result.Success)
                {
                    Debug.Log("[LuaPlatformExample] WORLD_MARKER_CREATE success name=" + durableName);
                }
                else
                {
                    LogWorldDriverFailure("WORLD_MARKER_CREATE", "executor-rejected");
                }
            }
            catch (OperationCanceledException)
            {
                LogWorldDriverFailure("WORLD_MARKER_CREATE", "cancelled");
            }
            catch (Exception ex)
            {
                LogWorldDriverFailure("WORLD_MARKER_CREATE", ex.GetType().Name);
            }
            finally
            {
                _worldDriverBusy = false;
            }
        }

        private async UniTaskVoid SaveWorldAsync(string slot)
        {
            if (!TryNormalizeDriverInput(slot, MaximumSlotInputLength, out string normalizedSlot))
            {
                LogWorldDriverFailure("WORLD_SAVE", "invalid-slot");
                return;
            }

            if (!TryEnterWorldDriver("WORLD_SAVE", false, true))
            {
                return;
            }

            Debug.Log("[LuaPlatformExample] WORLD_SAVE requested slot=" + normalizedSlot);
            try
            {
                CancellationToken cancellationToken = this.GetCancellationTokenOnDestroy();
                RbxWorldPackageWriteResult result = await _worldRuntimeService.SaveManualAsync(
                    _actorContext,
                    normalizedSlot,
                    cancellationToken);
                if (result != null && result.Success)
                {
                    Debug.Log("[LuaPlatformExample] WORLD_SAVE success slot=" + normalizedSlot);
                }
                else
                {
                    LogWorldDriverFailure(
                        "WORLD_SAVE",
                        SanitizeLogReason(result?.Error, "store-rejected"));
                }
            }
            catch (OperationCanceledException)
            {
                LogWorldDriverFailure("WORLD_SAVE", "cancelled");
            }
            catch (Exception ex)
            {
                LogWorldDriverFailure("WORLD_SAVE", ex.GetType().Name);
            }
            finally
            {
                _worldDriverBusy = false;
            }
        }

        private async UniTaskVoid RequestWorldLoadAsync(string slot)
        {
            if (!TryNormalizeDriverInput(slot, MaximumSlotInputLength, out string normalizedSlot))
            {
                LogWorldDriverFailure("WORLD_LOAD_REQUEST", "invalid-slot");
                return;
            }

            if (!TryEnterWorldDriver("WORLD_LOAD_REQUEST", false, true))
            {
                return;
            }

            Debug.Log("[LuaPlatformExample] WORLD_LOAD_REQUEST requested slot=" + normalizedSlot);
            try
            {
                CancellationToken cancellationToken = this.GetCancellationTokenOnDestroy();
                RbxWorldLoadRequest request = await _worldRuntimeService.RequestManualLoadAsync(
                    _actorContext,
                    normalizedSlot,
                    cancellationToken);
                Debug.Log(
                    "[LuaPlatformExample] WORLD_LOAD_REQUEST success slot=" + normalizedSlot
                    + " request=" + request.RequestId
                    + " world=" + request.WorldId
                    + " expires=" + request.ExpiresAtUtc.ToUniversalTime().ToString("O"));
            }
            catch (OperationCanceledException)
            {
                LogWorldDriverFailure("WORLD_LOAD_REQUEST", "cancelled");
            }
            catch (Exception ex)
            {
                LogWorldDriverFailure("WORLD_LOAD_REQUEST", ex.GetType().Name);
            }
            finally
            {
                _worldDriverBusy = false;
            }
        }

        private async UniTaskVoid DumpWorldMarkerAsync(string markerName)
        {
            if (!TryNormalizeDriverInput(
                    markerName,
                    MaximumMarkerInputLength,
                    out string normalizedMarker))
            {
                LogWorldDriverFailure("WORLD_MARKER_DUMP", "invalid-name");
                return;
            }

            if (!TryEnterWorldDriver("WORLD_MARKER_DUMP", false, true))
            {
                return;
            }

            string durableName = BrowserMarkerPrefix + normalizedMarker;
            try
            {
                CancellationToken cancellationToken = this.GetCancellationTokenOnDestroy();
                await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                RbxWorldPackagePayload payload = _worldRuntimeService.CaptureCurrent();
                int matches = 0;
                if (payload?.Tree?.Instances != null)
                {
                    for (int index = 0; index < payload.Tree.Instances.Count; index++)
                    {
                        if (string.Equals(
                                payload.Tree.Instances[index]?.Name,
                                durableName,
                                StringComparison.Ordinal))
                        {
                            matches++;
                        }
                    }
                }

                Debug.Log(
                    "[LuaPlatformExample] WORLD_MARKER_DUMP success name=" + durableName
                    + " present=" + (matches > 0 ? "true" : "false")
                    + " count=" + matches);
            }
            catch (OperationCanceledException)
            {
                LogWorldDriverFailure("WORLD_MARKER_DUMP", "cancelled");
            }
            catch (Exception ex)
            {
                LogWorldDriverFailure("WORLD_MARKER_DUMP", ex.GetType().Name);
            }
            finally
            {
                _worldDriverBusy = false;
            }
        }

        private bool TryEnterWorldDriver(
            string operation,
            bool requiresExecutor,
            bool requiresWorldRuntime)
        {
            if (_worldDriverBusy)
            {
                LogWorldDriverFailure(operation, "busy");
                return false;
            }

            if (requiresExecutor && _worldLuaExecutor == null)
            {
                LogWorldDriverFailure(operation, "executor-unavailable");
                return false;
            }

            if (requiresWorldRuntime && _worldRuntimeService == null)
            {
                LogWorldDriverFailure(operation, "world-service-unavailable");
                return false;
            }

            _worldDriverBusy = true;
            return true;
        }

        private static bool TryNormalizeDriverInput(
            string value,
            int maximumLength,
            out string normalized)
        {
            normalized = value?.Trim() ?? "";
            if (normalized.Length == 0 || normalized.Length > maximumLength)
            {
                normalized = "";
                return false;
            }

            for (int index = 0; index < normalized.Length; index++)
            {
                char character = normalized[index];
                bool allowed = character >= 'a' && character <= 'z'
                               || character >= 'A' && character <= 'Z'
                               || character >= '0' && character <= '9'
                               || character == '-'
                               || character == '_';
                if (!allowed)
                {
                    normalized = "";
                    return false;
                }
            }

            return true;
        }

        private static void LogWorldDriverFailure(string operation, string reason)
        {
            Debug.LogWarning(
                "[LuaPlatformExample] " + operation + " failure reason=" + reason);
        }

        private static string SanitizeLogReason(string reason, string fallback)
        {
            string source = string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
            int length = Math.Min(source.Length, 160);
            char[] sanitized = new char[length];
            for (int index = 0; index < length; index++)
            {
                char character = source[index];
                sanitized[index] = character >= 32 && character <= 126
                    ? character
                    : '_';
            }

            return new string(sanitized);
        }

        private void OnModReport(string modId, string message)
        {
            if (modId == TetrisId)
            {
                _tetrisHud = message;
                return;
            }

            if (modId != SelfTestAId)
            {
                return;
            }

            _selfTestLines.Add(message);
            if (message.StartsWith("FAIL", System.StringComparison.Ordinal))
            {
                // Failing checks surface in the player log too - the panel is not reachable from
                // WebGL page scripting, the console is.
                Debug.LogWarning($"[LuaPlatformExample] SELFTEST check failed: {message}");
            }

            if (message.StartsWith("SELFTEST_DONE", System.StringComparison.Ordinal))
            {
                _pendingSelfTestUnload = true;
                bool pass = message.Contains("fails=0");
                _selfTestSummary = pass ? $"PASS — {message}" : $"FAIL — {message}";
                _status = "Self-test finished.";
                Debug.Log($"[LuaPlatformExample] SELFTEST {(pass ? "PASS" : "FAIL")}: {message}");
            }
        }

        /// <summary>Receiver half of the cross-mod event check: answers every ping with pong.</summary>
        private const string SelfTestBSource = @"
-- name: Platform Self-Test B
-- description: Replies 'pong' to every 'ping'; exports a variable, a table and functions for A.
hooks_on('ping', function(evt, payload)
  events_emit('pong', tostring(payload) .. '!')
end)

local hits = 0
mods_export('greeting', 'hello_from_b')
mods_export('config', { difficulty = 2, title = 'B' })
mods_export('add', function(a, b)
  hits = hits + 1
  return a + b
end)
mods_export('hits', function() return hits end)
";

        /// <summary>
        /// Emitter half: exercises variables/closures, strings, math, tables, coroutines, the store,
        /// mod_id, hooks_every timers, the hooks_on('tick') alias, and cross-mod events, then reports
        /// one PASS/FAIL line per check and a final SELFTEST_DONE verdict.
        /// </summary>
        private const string SelfTestASource = @"
-- name: Platform Self-Test A
-- description: Checks timers, tick alias, variables, store, events, coroutines; reports a verdict.
local checks = {}
local function mark(name, ok)
  checks[#checks + 1] = (ok and 'PASS ' or 'FAIL ') .. name
end

mark('locals_and_tables', (function()
  local t = { a = 1, b = 2 }
  t.c = t.a + t.b
  local sum = 0
  for _, v in pairs(t) do sum = sum + v end
  return t.c == 3 and sum == 6 and #('abc') == 3
end)())

mark('string_format', string.format('%03d:%s', 7, 'ok') == '007:ok')

mark('math', (function()
  local r = math.random(1, 6)
  return r >= 1 and r <= 6 and math.floor(3.7) == 3 and math.max(2, 9) == 9
end)())

mark('varargs', (function(...)
  local n = select('#', ...)
  local a, b = ...
  return n == 3 and a == 10 and b == 20
end)(10, 20, 30))

mark('coroutine', (function()
  local co = coroutine.create(function(x) coroutine.yield(x + 1) end)
  local ok, v = coroutine.resume(co, 41)
  return ok and v == 42
end)())

mark('input_api', (function()
  -- Bindings exist and unheld/unknown keys read false without throwing.
  return type(input_key) == 'function' and input_key('f13') == false
     and input_mouse_button(0) ~= nil and type(input_mouse_x()) == 'number'
     and type(input_axis('Horizontal')) == 'number'
end)())

store_set('selftest_key', 'hello_store')
mark('store_roundtrip', store_get('selftest_key') == 'hello_store')
mark('mod_id', mod_id() == 'platform_selftest_a')

-- Cross-mod surface: read another mod's exported variable/table, call its functions
-- (stateful - B counts the calls), enumerate its exports.
mark('cross_mod_get', mods_get('platform_selftest_b', 'greeting') == 'hello_from_b')
mark('cross_mod_get_table', (function()
  local cfg = mods_get('platform_selftest_b', 'config')
  return type(cfg) == 'table' and cfg.difficulty == 2 and cfg.title == 'B'
end)())
mark('cross_mod_call', mods_call('platform_selftest_b', 'add', 20, 22) == 42)
mark('cross_mod_call_state', (function()
  mods_call('platform_selftest_b', 'add', 1, 1)
  return mods_call('platform_selftest_b', 'hits') == 2
end)())
mark('cross_mod_list', (function()
  local names = mods_list_exports('platform_selftest_b')
  return type(names) == 'table' and #names == 4
end)())

local timer_ticks = 0
local tick_alias = 0
local pong_payload = nil

hooks_every(0.2, function()
  timer_ticks = timer_ticks + 1
end)

hooks_on('tick', function()
  tick_alias = tick_alias + 1
end)

hooks_on('pong', function(evt, payload)
  pong_payload = payload
end)

events_emit('ping', '42')

local reported = false
hooks_every(1.5, function()
  if reported then return end
  reported = true
  mark('hooks_every_timer', timer_ticks >= 5)
  -- Threshold is frame-rate tolerant: the alias fires at most once per rendered frame, and a
  -- throttled/background WebGL tab can run at a few fps. Repeated firing is what's asserted.
  mark('tick_alias_20hz', tick_alias >= 3)
  mark('cross_mod_events', pong_payload == '42!')
  local fails = 0
  for i = 1, #checks do
    report(checks[i])
    if string.sub(checks[i], 1, 4) == 'FAIL' then fails = fails + 1 end
  end
  report(string.format('SELFTEST_DONE checks=%d fails=%d', #checks, fails))
end)
";

        /// <summary>
        /// The whole game is this one mod: board state in Lua tables, gravity on a hooks_every timer,
        /// autoplay drift, line clears, and a score that persists in the mod store across restarts.
        /// World interaction is exclusively the Rbx API (Instance.new / Part / Folder,
        /// workspace.CurrentCamera) — the production surface; the coreai_world_* build bindings are
        /// withheld in production.
        /// </summary>
        private const string TetrisSource = @"
-- name: Tetris 3D
-- description: 3D falling-blocks game in one Lua mod. A/D move, W rotate, S soft-drop, Space hard-drop; autopilot after 5 s idle.
local W = 8
local H = 14
-- The Rbx API works in studs (RbxSpace: 1 stud = 0.28 m, locked). The board was authored in
-- meters for the old world API; M converts so the layout lands on the same world spots.
local M = 1 / 0.28
local OX = -16.0
local OY = 0.5
local OZ = 6.0

local SHAPES = {
  { cells = { {0,0},{1,0},{0,1},{1,1} }, color = '#FFD500', w = 2 },
  { cells = { {0,0},{1,0},{2,0},{3,0} }, color = '#00E5FF', w = 4 },
  { cells = { {0,0},{1,0},{2,0},{1,1} }, color = '#B84DFF', w = 3 },
  { cells = { {0,0},{0,1},{0,2},{1,0} }, color = '#FF8C00', w = 2 },
  { cells = { {0,0},{1,0},{1,1},{2,1} }, color = '#40D040', w = 3 },
}

local board = {}
for y = 1, H do board[y] = {} end

local piece = nil
local next_id = 0
local score = tonumber(store_get('tetris_score')) or 0
local total_lines = tonumber(store_get('tetris_lines')) or 0
local games = tonumber(store_get('tetris_games')) or 0

-- Per-load generation for every spawned name. A reload rebuilds the field from scratch; unique
-- names per generation make destroy+rebuild order-independent and keep the store demo alive.
local gen = (tonumber(store_get('tetris_gen')) or 0) + 1
store_set('tetris_gen', tostring(gen))
local ROOT = 'TetrisRoot_g' .. gen
local function nm(s) return 'tz' .. gen .. s end

local function world_x(x) return (OX + x) * M end
local function world_y(y) return (OY + (y - 1)) * M end
local function world_z() return OZ * M end

local function place_cell(p, x, y)
  p.Position = Vector3.new(world_x(x), world_y(y), world_z())
end

-- Build the playfield under one Folder the mod owns. Destroying the previous generation's root
-- removes its whole field (walls, active piece, landed cubes) in one call, so reloads never
-- leave orphans; unload/reload also sweeps mod-owned instances automatically.
local prev = workspace:FindFirstChild('TetrisRoot_g' .. (gen - 1))
if prev then prev:Destroy() end

local root = Instance.new('Folder')
root.Name = ROOT
root.Parent = workspace

local WALL_COLOR = Color3.fromHex('#4A4A55')

local function spawn_cube(name, x, y, size_m)
  local p = Instance.new('Part')
  p.Name = name
  p.Size = Vector3.new(size_m * M, size_m * M, size_m * M)
  p.Position = Vector3.new(x, y, world_z())
  p.Anchored = true
  p.Parent = root
  return p
end

for y = 1, H do
  local wl = spawn_cube(nm('_wl' .. y), world_x(-1), world_y(y), 0.95)
  local wr = spawn_cube(nm('_wr' .. y), world_x(W), world_y(y), 0.95)
  wl.Color = WALL_COLOR
  wr.Color = WALL_COLOR
end
for x = -1, W do
  local fc = spawn_cube(nm('_wf' .. (x + 2)), world_x(x), world_y(0), 0.95)
  fc.Color = WALL_COLOR
end

-- Four persistent cubes for the active piece, reused every piece and parked below the board.
local piece_parts = {}
for i = 1, 4 do
  piece_parts[i] = spawn_cube(nm('_a' .. i), world_x(0), -6 * M, 0.98)
end

local function occupied(x, y)
  if x < 0 or x >= W or y < 1 then return true end
  if y > H then return false end
  return board[y][x] ~= nil
end

local function can_place(cells, px, py)
  for i = 1, #cells do
    if occupied(px + cells[i][1], py + cells[i][2]) then return false end
  end
  return true
end

-- Visual position trails the logical cell (exponential lerp on a 20 Hz timer) so moves and
-- falling look animated instead of teleporting cell to cell.
local vis_x, vis_y = 0, 0
local last_vx, last_vy = 1e9, 1e9

local function draw_piece()
  for i = 1, #piece.cells do
    place_cell(piece_parts[i], vis_x + piece.cells[i][1], vis_y + piece.cells[i][2])
  end
end

local function publish_hud()
  store_set('tetris_score', tostring(score))
  store_set('tetris_lines', tostring(total_lines))
  store_set('tetris_games', tostring(games))
  report(string.format('HUD score=%d lines=%d games=%d', score, total_lines, games))
end

local function reset_board()
  for y = 1, H do
    for x = 0, W - 1 do
      local p = board[y][x]
      if p then
        p:Destroy()
        board[y][x] = nil
      end
    end
  end
end

local function clear_lines()
  local y = 1
  while y <= H do
    local full = true
    for x = 0, W - 1 do
      if board[y][x] == nil then full = false; break end
    end
    if full then
      for x = 0, W - 1 do
        board[y][x]:Destroy()
        board[y][x] = nil
      end
      for yy = y + 1, H do
        for x = 0, W - 1 do
          local p = board[yy][x]
          board[yy - 1][x] = p
          board[yy][x] = nil
          if p then place_cell(p, x, yy - 1) end
        end
      end
      total_lines = total_lines + 1
      score = score + 100
    else
      y = y + 1
    end
  end
end

local function lock_piece()
  for i = 1, #piece.cells do
    local x = piece.x + piece.cells[i][1]
    local y = piece.y + piece.cells[i][2]
    if y >= 1 and y <= H then
      next_id = next_id + 1
      local p = spawn_cube(nm('_c' .. next_id), world_x(x), world_y(y), 0.98)
      p.Color = Color3.fromHex(piece.color)
      board[y][x] = p
    end
    local parked = piece_parts[i].Position
    piece_parts[i].Position = Vector3.new(parked.X, -6 * M, parked.Z)
  end
  score = score + 4
  piece = nil
  clear_lines()
  publish_hud()
end

local function spawn_piece()
  local shape = SHAPES[math.random(1, #SHAPES)]
  piece = {
    cells = shape.cells,
    color = shape.color,
    x = math.random(0, W - shape.w),
    y = H,
    target = math.random(0, W - shape.w),
  }
  if not can_place(piece.cells, piece.x, piece.y) then
    -- Stack reached the top: fresh board, cumulative score survives in the store.
    games = games + 1
    reset_board()
    publish_hud()
  end
  vis_x, vis_y = piece.x, piece.y + 2
  for i = 1, #piece.cells do
    piece_parts[i].Color = Color3.fromHex(piece.color)
  end
  draw_piece()
end

-- Animation: ease the visual position toward the logical cell; skip world commands entirely
-- while nothing moves.
hooks_every(0.05, function()
  if piece == nil then return end
  vis_x = vis_x + (piece.x - vis_x) * 0.4
  vis_y = vis_y + (piece.y - vis_y) * 0.4
  if math.abs(piece.x - vis_x) < 0.02 then vis_x = piece.x end
  if math.abs(piece.y - vis_y) < 0.02 then vis_y = piece.y end
  if vis_x ~= last_vx or vis_y ~= last_vy then
    last_vx, last_vy = vis_x, vis_y
    draw_piece()
  end
end)

-- Player input (Gameplay-tier input_* API): A/D steer, W rotate, S soft-drop, Space hard-drop. Held keys
-- are polled at 20 Hz, which never misses like frame-edge checks would from a timer. Any input
-- pauses the autopilot; it resumes after ~5 s idle so the unattended demo keeps playing itself.
local idle = 1000
local move_cd = 0
local soft_drop = false
local space_was = false
local rot_was = false

-- W: rotate 90° clockwise around the piece's bounding box, normalized back to origin so
-- can_place / draw keep using non-negative offsets. Never mutates the shared SHAPES tables.
local function try_rotate()
  local rc = {}
  local minx, miny = 1e9, 1e9
  for i = 1, #piece.cells do
    local nx, ny = piece.cells[i][2], -piece.cells[i][1]
    rc[i] = { nx, ny }
    if nx < minx then minx = nx end
    if ny < miny then miny = ny end
  end
  for i = 1, #rc do
    rc[i][1] = rc[i][1] - minx
    rc[i][2] = rc[i][2] - miny
  end
  -- Wall kick: accept the first horizontal nudge that makes the rotation legal.
  for _, k in ipairs({ 0, -1, 1, -2, 2 }) do
    if can_place(rc, piece.x + k, piece.y) then
      piece.cells = rc
      piece.x = piece.x + k
      piece.target = piece.x
      draw_piece()
      return
    end
  end
end
hooks_every(0.05, function()
  idle = idle + 1
  if move_cd > 0 then move_cd = move_cd - 1 end
  soft_drop = input_key('s')
  if soft_drop then idle = 0 end
  local space_now = input_key('space')
  if piece == nil then
    space_was = space_now
    return
  end
  local dx = 0
  if input_key('a') then dx = -1 elseif input_key('d') then dx = 1 end
  if dx ~= 0 then
    idle = 0
    if move_cd == 0 and can_place(piece.cells, piece.x + dx, piece.y) then
      piece.x = piece.x + dx
      piece.target = piece.x
      move_cd = 3
    end
  end
  local rot_now = input_key('w')
  if rot_now and not rot_was then
    idle = 0
    try_rotate()
  end
  rot_was = rot_now
  if space_now and not space_was then
    -- Hard drop: release the piece straight to the floor and lock it.
    idle = 0
    while can_place(piece.cells, piece.x, piece.y - 1) do
      piece.y = piece.y - 1
    end
    vis_x, vis_y = piece.x, piece.y
    lock_piece()
  end
  space_was = space_now
end)

-- Gravity: one cell per 0.5 s, 5x with soft-drop (accumulator on a 0.1 s timer so the
-- speed-up needs no timer re-registration).
local fall_acc = 0
hooks_every(0.1, function()
  if piece == nil then
    spawn_piece()
    return
  end
  fall_acc = fall_acc + (soft_drop and 5 or 1)
  if fall_acc < 5 then return end
  fall_acc = 0
  if idle > 100 then
    if piece.x < piece.target and can_place(piece.cells, piece.x + 1, piece.y) then
      piece.x = piece.x + 1
    elseif piece.x > piece.target and can_place(piece.cells, piece.x - 1, piece.y) then
      piece.x = piece.x - 1
    end
  end
  if can_place(piece.cells, piece.x, piece.y - 1) then
    piece.y = piece.y - 1
  else
    lock_piece()
  end
end)

-- External control: the host or another mod can steer the piece via events_emit.
hooks_on('tetris_move', function(evt, payload)
  if piece == nil then return end
  local dx = tonumber(payload) or 0
  if dx ~= 0 and can_place(piece.cells, piece.x + dx, piece.y) then
    piece.x = piece.x + dx
    piece.target = piece.x
  end
end)

hooks_on('tetris_rotate', function(evt, payload)
  if piece ~= nil then try_rotate() end
end)

hooks_every(5.0, publish_hud)

-- Camera: slow orbit around the board, always facing its center. CFrame.lookAt replaces the old
-- euler parametrization; at h=7 / r=24 the look-down pitch lands within half a degree of rx=16,
-- so the framing matches the old orbit.
local cam = workspace.CurrentCamera
local cam_cx = (OX + (W - 1) / 2) * M
local cam_cy = (OY + H / 2) * M
local cam_r = 24.0 * M
local cam_h = 7.0 * M
local cam_a = 0.0
if cam then
  cam.CameraType = Enum.CameraType.Scriptable
  hooks_every(0.05, function()
    cam_a = cam_a + 0.010
    local eye = Vector3.new(
      cam_cx + cam_r * math.sin(cam_a),
      cam_cy + cam_h,
      world_z() - cam_r * math.cos(cam_a))
    cam.CFrame = CFrame.lookAt(eye, Vector3.new(cam_cx, cam_cy, world_z()))
  end)
end
";
    }
}
#else
using UnityEngine;

namespace CoreAI.Demos
{
    public sealed class LuaPlatformExampleController : MonoBehaviour
    {
        private void Start()
        {
            Debug.LogWarning("[LuaPlatformExample] Lua disabled; example inactive.");
            enabled = false;
        }
    }
}
#endif
