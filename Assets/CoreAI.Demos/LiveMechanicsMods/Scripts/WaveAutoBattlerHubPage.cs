#if COREAI_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Hub;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// UI Toolkit Hub page for the Wave Auto-Battler mods demo (replaces the old IMGUI panel). It reads a
    /// GUI-less <see cref="WaveAutoBattlerModsDemoController"/> — the component that runs the auto-battle
    /// simulation and declares the Lua logic slots — and reflects its live state (status, battle stats,
    /// per-slot override flags, loaded mods, battle log) by polling it on a scheduled interval. The driver
    /// is supplied by a host provider, so the page renders a setup note when no driver is present.
    /// </summary>
    public sealed class WaveAutoBattlerHubPage : HubPageBase
    {
        /// <summary>Default registry id for the auto-battler page.</summary>
        public const string DefaultPageId = "coreai.demo.wave.autobattler";

        private readonly Func<WaveAutoBattlerModsDemoController> _driverProvider;
        private readonly Dictionary<string, Label> _slotLabels = new();

        private WaveAutoBattlerModsDemoController _driver;
        private Label _statusLabel;
        private Label _heroStatsLabel;
        private Label _battleStatsLabel;
        private VisualElement _slotsRoot;
        private VisualElement _modsListRoot;
        private ScrollView _logScroll;
        private IVisualElementScheduledItem _tick;

        /// <param name="driverProvider">Resolves the scene's auto-battler driver (may return null).</param>
        public WaveAutoBattlerHubPage(
            Func<WaveAutoBattlerModsDemoController> driverProvider,
            string pageId = DefaultPageId,
            string displayName = "Auto-Battler",
            int order = 0)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Auto-Battler" : displayName,
                order)
        {
            _driverProvider = driverProvider;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public override void OnActivated()
        {
            _tick?.Resume();
            RefreshLive();
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
        }

        private object Build()
        {
            ScrollView scroll = DemoHubWidgets.CreatePage("Wave Auto-Battler Mods Demo", out VisualElement body);

            _driver = TryResolveDriver();
            if (_driver == null)
            {
                body.Add(DemoHubWidgets.MakeNote(
                    "No WaveAutoBattlerModsDemoController driver was found in the scene. Add one (it runs " +
                    "the battle simulation and declares the Lua slots), then reopen this tab."));
                return scroll;
            }

            body.Add(DemoHubWidgets.MakeBody(
                "A compact auto-battler where chat-created Lua mods change real combat rules: wave size, " +
                "enemy scaling, hero damage, regen and rewards. Ask chat to create or edit mods."));

            _statusLabel = DemoHubWidgets.MakeBody(_driver.Status);
            body.Add(_statusLabel);

            body.Add(DemoHubWidgets.MakeSection("Battle"));
            _heroStatsLabel = DemoHubWidgets.MakeBody("");
            body.Add(_heroStatsLabel);
            _battleStatsLabel = DemoHubWidgets.MakeBody("");
            body.Add(_battleStatsLabel);

            body.Add(DemoHubWidgets.MakeSection("Lua mod slots"));
            _slotsRoot = new VisualElement();
            body.Add(_slotsRoot);

            body.Add(DemoHubWidgets.MakeSection("Loaded mods"));
            _modsListRoot = new VisualElement();
            body.Add(_modsListRoot);

            body.Add(DemoHubWidgets.MakeSection("Battle log"));
            _logScroll = new ScrollView(ScrollViewMode.Vertical);
            _logScroll.style.maxHeight = 150f;
            _logScroll.style.marginTop = 4f;
            body.Add(_logScroll);

            _tick = scroll.schedule.Execute(RefreshLive).Every(200);
            RefreshLive();
            return scroll;
        }

        private void RefreshLive()
        {
            if (_driver == null)
            {
                return;
            }

            if (_statusLabel != null)
            {
                _statusLabel.text = _driver.Status;
            }

            bool ready = _driver.IsReady;
            if (_heroStatsLabel != null)
            {
                _heroStatsLabel.text = ready
                    ? $"Wave {_driver.Wave}   Hero Lvl {_driver.HeroLevel}   " +
                      $"HP {_driver.HeroHp:0.#}/{_driver.HeroMaxHp:0.#}   Gold {_driver.Gold}   XP {_driver.Xp:0.#}"
                    : "";
            }

            if (_battleStatsLabel != null)
            {
                _battleStatsLabel.text = ready
                    ? $"Enemies alive: {_driver.EnemiesAlive}   " +
                      $"Hero attack interval: {_driver.HeroAttackIntervalSeconds:0.##}s"
                    : "";
            }

            RenderSlots();
            RenderModsList();
            RenderBattleLog();
        }

        private void RenderSlots()
        {
            if (_slotsRoot == null)
            {
                return;
            }

            IReadOnlyList<WaveAutoBattlerModsDemoController.SlotView> slots = _driver.GetSlotViews();
            // WHY: the slot set is fixed, so rows are created once and only their value labels update.
            if (_slotsRoot.childCount != slots.Count)
            {
                _slotsRoot.Clear();
                _slotLabels.Clear();
                foreach (WaveAutoBattlerModsDemoController.SlotView slot in slots)
                {
                    _slotsRoot.Add(DemoHubWidgets.MakeRow(slot.Slot, "", out Label valueLabel));
                    _slotLabels[slot.Slot] = valueLabel;
                }
            }

            foreach (WaveAutoBattlerModsDemoController.SlotView slot in slots)
            {
                if (_slotLabels.TryGetValue(slot.Slot, out Label label))
                {
                    label.text = $"{slot.ValueText}  {(slot.Overridden ? "Lua override" : "C# default")}";
                    label.style.color = slot.Overridden ? DemoHubWidgets.Accent : DemoHubWidgets.Text;
                }
            }
        }

        private void RenderModsList()
        {
            if (_modsListRoot == null)
            {
                return;
            }

            _modsListRoot.Clear();
            IReadOnlyList<LuaModInfo> mods = _driver.LoadedMods;
            if (mods == null || mods.Count == 0)
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

        private void RenderBattleLog()
        {
            if (_logScroll == null)
            {
                return;
            }

            IReadOnlyList<string> log = _driver.BattleLog;
            _logScroll.Clear();
            if (log.Count == 0)
            {
                _logScroll.Add(DemoHubWidgets.MakeBody("Waiting for the battle to start..."));
                return;
            }

            foreach (string line in log)
            {
                _logScroll.Add(DemoHubWidgets.MakeBody(line));
            }
        }

        private WaveAutoBattlerModsDemoController TryResolveDriver()
        {
            try
            {
                return _driverProvider?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
