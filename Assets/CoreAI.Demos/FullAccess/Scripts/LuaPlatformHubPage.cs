#if COREAI_LUA
using System;
using System.Collections.Generic;
using CoreAI.Hub;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// UI Toolkit Hub page for the Lua platform example (replaces the old F6 IMGUI window). It drives a
    /// GUI-less <see cref="LuaPlatformExampleController"/> — the component that owns the self-test / Tetris
    /// Lua and the WebGL SendMessage entry points — and reflects its live status (self-test verdict, per-
    /// check lines, Tetris HUD) by polling it on a scheduled interval. The driver is supplied by a host
    /// provider, so the page renders a setup note when no driver is present.
    /// </summary>
    public sealed class LuaPlatformHubPage : HubPageBase
    {
        /// <summary>Default registry id for the Lua platform page.</summary>
        public const string DefaultPageId = "coreai.demo.fullaccess.luaplatform";

        private readonly Func<LuaPlatformExampleController> _driverProvider;

        private LuaPlatformExampleController _driver;
        private Label _statusLabel;
        private Label _selfTestLabel;
        private Label _tetrisLabel;
        private ScrollView _linesScroll;
        private Button _startButton;
        private Button _stopButton;
        private IVisualElementScheduledItem _tick;

        /// <param name="driverProvider">Resolves the scene's Lua platform driver (may return null).</param>
        public LuaPlatformHubPage(
            Func<LuaPlatformExampleController> driverProvider,
            string pageId = DefaultPageId,
            string displayName = "Lua Platform",
            int order = 20)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Lua Platform" : displayName,
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
            ScrollView scroll = DemoHubWidgets.CreatePage("Lua Platform Example", out VisualElement body);

            _driver = TryResolveDriver();
            if (_driver == null)
            {
                body.Add(DemoHubWidgets.MakeNote(
                    "No LuaPlatformExampleController driver was found in the scene. Add one (it also owns " +
                    "the WebGL SendMessage entry points), then reopen this tab."));
                return scroll;
            }

            body.Add(DemoHubWidgets.MakeBody(
                "A deterministic, no-LLM reference: a platform self-test plus a self-playing 3D Tetris mod, " +
                "both authored entirely in Lua on the mods runtime."));

            _statusLabel = DemoHubWidgets.MakeBody(_driver.Status);
            body.Add(_statusLabel);

            body.Add(DemoHubWidgets.MakeSection("Controls"));
            VisualElement buttons = DemoHubWidgets.MakeButtonRow();
            buttons.Add(DemoHubWidgets.MakePrimaryButton("Run self-test", () => _driver.RunSelfTest()));
            _startButton = DemoHubWidgets.MakeButton("Start Tetris", () =>
            {
                _driver.StartTetris();
                RefreshLive();
            });
            _stopButton = DemoHubWidgets.MakeButton("Stop Tetris", () =>
            {
                _driver.StopTetris();
                RefreshLive();
            });
            buttons.Add(_startButton);
            buttons.Add(_stopButton);
            buttons.Add(DemoHubWidgets.MakeButton("Nudge left", () => _driver.TetrisMove("-1")));
            buttons.Add(DemoHubWidgets.MakeButton("Nudge right", () => _driver.TetrisMove("1")));
            body.Add(buttons);

            body.Add(DemoHubWidgets.MakeSection("Self-test"));
            _selfTestLabel = DemoHubWidgets.MakeBody(_driver.SelfTestSummary);
            body.Add(_selfTestLabel);
            _tetrisLabel = DemoHubWidgets.MakeBody("");
            body.Add(_tetrisLabel);

            _linesScroll = new ScrollView(ScrollViewMode.Vertical);
            _linesScroll.style.maxHeight = 150f;
            _linesScroll.style.marginTop = 4f;
            body.Add(_linesScroll);

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

            if (_selfTestLabel != null)
            {
                _selfTestLabel.text = $"Self-test: {_driver.SelfTestSummary}";
            }

            if (_tetrisLabel != null)
            {
                string hud = _driver.TetrisHud;
                _tetrisLabel.text = string.IsNullOrEmpty(hud) ? "" : $"Tetris: {hud}";
                _tetrisLabel.style.display =
                    string.IsNullOrEmpty(hud) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            bool running = _driver.IsTetrisRunning;
            if (_startButton != null)
            {
                _startButton.text = running ? "Restart Tetris" : "Start Tetris";
            }

            _stopButton?.SetEnabled(running);
            RenderSelfTestLines();
        }

        private void RenderSelfTestLines()
        {
            if (_linesScroll == null)
            {
                return;
            }

            IReadOnlyList<string> lines = _driver.SelfTestLines;
            if (_linesScroll.childCount == lines.Count)
            {
                return;
            }

            _linesScroll.Clear();
            foreach (string line in lines)
            {
                _linesScroll.Add(DemoHubWidgets.MakeBody(line));
            }
        }

        private LuaPlatformExampleController TryResolveDriver()
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
