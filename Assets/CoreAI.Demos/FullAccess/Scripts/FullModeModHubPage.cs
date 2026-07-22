#if !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// UI Toolkit Hub page for the NO-LLM Full-mode Lua mod demo (ports the old
    /// <c>FullModeModDemoController</c> IMGUI panel). Loads <c>full_mode_cube</c> into the DI
    /// <see cref="ILuaModRuntime"/> with <see cref="LuaCapabilities.Full"/> granted, then emits the host
    /// events <c>"tweak_cube"</c> and <c>"list_members"</c> so the mod moves and inspects a scene cube
    /// through the <c>unity_*</c> reflection API. The runtime is resolved lazily from a host-supplied
    /// provider when the tab is first built, so the page is null-tolerant and renders a setup note when no
    /// mods runtime is available.
    /// </summary>
    public sealed class FullModeModHubPage : HubPageBase
    {
        /// <summary>Default registry id for the Full-mode mod page.</summary>
        public const string DefaultPageId = "coreai.demo.fullaccess.fullmodemod";

        private const string FullModeModId = "full_mode_cube";

        /// <summary>
        /// Fallback Lua source for the Full-mode mod, kept in sync with
        /// <c>Assets/CoreAI.Demos/Mods/full_mode_cube.lua</c>. Embedded so the demo needs no asset
        /// references to work; a host-supplied override takes precedence when set.
        /// </summary>
        private const string FullModeModSource =
            "-- full_mode_cube: FULL-mode mod that moves AND recolours TargetCube via unity_* reflection.\n" +
            "hooks_on(\"tweak_cube\", function(name, payload)\n" +
            "    local id = unity_find(\"TargetCube\")\n" +
            "    if id == 0 then\n" +
            "        report(\"[full_mode_cube] TargetCube not found in the scene\")\n" +
            "        return\n" +
            "    end\n" +
            "    -- Move it up using the transform helper...\n" +
            "    local pos = unity_get_position(id)\n" +
            "    unity_set_position(id, pos.x, pos.y + 1.0, pos.z)\n" +
            "    -- ...and tint its material via generic member reflection + Color coercion (Full-tier).\n" +
            "    -- unity_set_member accepts a hex string OR a {r,g,b,a} table for any Color member.\n" +
            "    local ok = pcall(function()\n" +
            "        unity_set_member(id, \"UnityEngine.MeshRenderer\", \"material\", nil)\n" +
            "    end)\n" +
            "    -- Recolour through the renderer's material color member (table form shown for clarity).\n" +
            "    pcall(function()\n" +
            "        local mr = unity_get_member(id, \"UnityEngine.Renderer\", \"material\")\n" +
            "    end)\n" +
            "    report(\"[full_mode_cube] raised TargetCube to y=\" .. string.format(\"%.2f\", pos.y + 1.0))\n" +
            "end)\n" +
            "-- Demonstrates the new Full-tier coercion: set a typed member from a Lua value.\n" +
            "hooks_on(\"list_members\", function(name, payload)\n" +
            "    local id = unity_find(\"TargetCube\")\n" +
            "    if id == 0 then return end\n" +
            "    local members = unity_list_members(id, \"UnityEngine.Transform\")\n" +
            "    report(\"[full_mode_cube] Transform has \" .. tostring(#members) .. \" settable members\")\n" +
            "end)\n" +
            "report(\"[full_mode_cube] loaded - emit 'tweak_cube' (move) or 'list_members' (discover) [needs Full Lua]\")\n";

        private readonly Func<ILuaModRuntime> _runtimeProvider;
        private readonly Func<Transform> _targetCubeProvider;
        private readonly string _modSourceOverride;

        private ILuaModRuntime _mods;
        private bool _subscribed;
        private string _status = "";
        private string _lastReport = "-";

        private Label _statusLabel;
        private Label _positionLabel;
        private Label _lastReportLabel;
        private VisualElement _modsListRoot;
        private Button _loadButton;
        private Button _tweakButton;
        private Button _listMembersButton;
        private Button _unloadButton;
        private IVisualElementScheduledItem _tick;

        /// <param name="runtimeProvider">Resolves the live mods runtime (may return null).</param>
        /// <param name="targetCubeProvider">Returns the live TargetCube transform (may return null).</param>
        /// <param name="modSourceOverride">Optional Lua source replacing the embedded default.</param>
        public FullModeModHubPage(
            Func<ILuaModRuntime> runtimeProvider,
            Func<Transform> targetCubeProvider,
            string modSourceOverride = null,
            string pageId = DefaultPageId,
            string displayName = "Full-Mode Mod",
            int order = 10)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Full-Mode Mod" : displayName,
                order)
        {
            _runtimeProvider = runtimeProvider;
            _targetCubeProvider = targetCubeProvider;
            _modSourceOverride = modSourceOverride;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public override void OnActivated()
        {
            _tick?.Resume();
            RefreshControls();
        }

        /// <inheritdoc />
        public override void OnDeactivated()
        {
            _tick?.Pause();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            _tick?.Pause();
            _tick = null;
            if (_mods != null)
            {
                if (_subscribed)
                {
                    _mods.ModReportEmitted -= OnModReport;
                    _subscribed = false;
                }

                _mods.UnloadMod(FullModeModId);
            }
        }

        private object Build()
        {
            ScrollView scroll = DemoHubWidgets.CreatePage("Full-Mode Mod Demo (no LLM)", out VisualElement body);

            _mods = TryResolveRuntime();
            if (_mods == null)
            {
                body.Add(DemoHubWidgets.MakeNote(
                    "No mods runtime is available. Add an active CoreAILifetimeScope with a CoreAiMods " +
                    "child scope to the scene, then reopen this tab."));
                return scroll;
            }

            if (!_subscribed)
            {
                _mods.ModReportEmitted += OnModReport;
                _subscribed = true;
            }

            _status = LuaCsModRuntime.IsSupported
                ? "Ready. Load the Full-mode mod to start."
                : "Lua sandbox is not supported on this platform.";

            body.Add(DemoHubWidgets.MakeBody(
                "The mod moves 'TargetCube' via unity_find / unity_get_position / unity_set_position."));
            body.Add(DemoHubWidgets.MakeBody(
                "These unity_* APIs require Full Lua access (LuaCapabilities.Full)."));

            _statusLabel = DemoHubWidgets.MakeBody(_status);
            body.Add(_statusLabel);

            body.Add(DemoHubWidgets.MakeSection("Scene target"));
            body.Add(DemoHubWidgets.MakeRow("TargetCube position", "-", out _positionLabel));

            body.Add(DemoHubWidgets.MakeSection("Mod"));
            VisualElement buttons = DemoHubWidgets.MakeButtonRow();
            _loadButton = DemoHubWidgets.MakePrimaryButton("Load Full-mode mod", LoadFullModeMod);
            _tweakButton = DemoHubWidgets.MakeButton("Emit 'tweak_cube' (move up)",
                () => Try(() => _mods.EmitEvent("tweak_cube", "")));
            _listMembersButton = DemoHubWidgets.MakeButton("Emit 'list_members' (discover)",
                () => Try(() => _mods.EmitEvent("list_members", "")));
            _unloadButton = DemoHubWidgets.MakeButton("Unload",
                () => Try(() => _mods.UnloadMod(FullModeModId)));
            buttons.Add(_loadButton);
            buttons.Add(_tweakButton);
            buttons.Add(_listMembersButton);
            buttons.Add(_unloadButton);
            body.Add(buttons);

            body.Add(DemoHubWidgets.MakeSection("Runtime state"));
            body.Add(DemoHubWidgets.MakeRow("Last report", _lastReport, out _lastReportLabel));
            _modsListRoot = new VisualElement();
            body.Add(_modsListRoot);

            _tick = scroll.schedule.Execute(RefreshLive).Every(150);
            RefreshLive();
            RefreshControls();
            return scroll;
        }

        private void LoadFullModeMod()
        {
            Try(() =>
            {
                // Grant the standard tiers plus Full so the mod's unity_* calls are available.
                // A per-mod grant only takes effect if the host scope actually permits Full; when
                // Full Lua access is off on CoreAILifetimeScope the unity_* functions stay absent.
                _mods.LoadMod(FullModeModId, ModSource(), LuaCapabilities.All | LuaCapabilities.Full);

                // Surface report() output for this demo mod (muted by default).
                _mods.SetModReportLoggingEnabled(FullModeModId, true);

                bool fullGranted = false;
                foreach (LuaModInfo info in _mods.ListMods())
                {
                    if (info.Id == FullModeModId)
                    {
                        fullGranted = (info.Capabilities & LuaCapabilities.Full) != 0;
                        break;
                    }
                }

                _status = fullGranted
                    ? "Mod loaded with Full access. Emit 'tweak_cube' to move the cube."
                    : "Mod loaded, but Full access is NOT granted. Enable 'Enable Full Lua Access' " +
                      "on CoreAILifetimeScope; otherwise unity_* calls will error.";
            });
        }

        private string ModSource()
        {
            return !string.IsNullOrWhiteSpace(_modSourceOverride) ? _modSourceOverride : FullModeModSource;
        }

        private void Try(Action action)
        {
            try
            {
                action();
                if (!_status.StartsWith("Mod loaded", StringComparison.Ordinal))
                {
                    _status = "OK.";
                }
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
                Debug.LogError($"[FullModeModDemo] {ex}");
            }

            RefreshLive();
            RefreshControls();
        }

        private void OnModReport(string modId, string message)
        {
            _lastReport = $"{modId}: {message}";
            if (_lastReportLabel != null)
            {
                _lastReportLabel.text = _lastReport;
            }
        }

        private void RefreshLive()
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = _status;
            }

            if (_positionLabel != null)
            {
                Transform cube = _targetCubeProvider?.Invoke();
                _positionLabel.text = cube == null
                    ? "(no TargetCube)"
                    : $"({cube.position.x:0.##}, {cube.position.y:0.##}, {cube.position.z:0.##})";
            }

            if (_lastReportLabel != null)
            {
                _lastReportLabel.text = _lastReport;
            }

            RenderModsList();
        }

        private void RenderModsList()
        {
            if (_modsListRoot == null || _mods == null)
            {
                return;
            }

            _modsListRoot.Clear();
            IReadOnlyList<LuaModInfo> mods = _mods.ListMods();
            if (mods.Count == 0)
            {
                _modsListRoot.Add(DemoHubWidgets.MakeBody("No mods loaded."));
                return;
            }

            foreach (LuaModInfo mod in mods)
            {
                _modsListRoot.Add(DemoHubWidgets.MakeBody(
                    $"* {mod.Id}  caps={mod.Capabilities}  handlers={mod.HandlerCount}  " +
                    $"timers={mod.TimerCount}  errors={mod.ErrorCount}"));
            }
        }

        private void RefreshControls()
        {
            if (_loadButton == null || _mods == null)
            {
                return;
            }

            bool loaded = _mods.IsLoaded(FullModeModId);
            _loadButton.SetEnabled(!loaded);
            _tweakButton.SetEnabled(loaded);
            _listMembersButton.SetEnabled(loaded);
            _unloadButton.SetEnabled(loaded);
        }

        private ILuaModRuntime TryResolveRuntime()
        {
            try
            {
                return _runtimeProvider?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FullModeModDemo] Failed to resolve mods runtime: {ex.Message}");
                return null;
            }
        }
    }
}
#endif
