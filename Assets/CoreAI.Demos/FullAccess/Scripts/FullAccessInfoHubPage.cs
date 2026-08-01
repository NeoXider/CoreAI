#if COREAI_LUA
using System;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// UI Toolkit replacement for the old F7 IMGUI info panel of the Full Access demo: renders the
    /// instructions and a live "TargetCube position" row inside a Hub tab. The cube position updates on a
    /// scheduled interval while the page is active; the Transform is supplied by the host as a provider so
    /// the page tolerates a not-yet-created or destroyed cube.
    /// </summary>
    public sealed class FullAccessInfoHubPage : HubPageBase
    {
        /// <summary>Default registry id for the Full Access info page.</summary>
        public const string DefaultPageId = "coreai.demo.fullaccess.info";

        private readonly Func<Transform> _targetCubeProvider;

        private Label _positionLabel;
        private IVisualElementScheduledItem _positionTick;

        /// <param name="targetCubeProvider">Returns the live TargetCube transform (may return null).</param>
        public FullAccessInfoHubPage(
            Func<Transform> targetCubeProvider,
            string pageId = DefaultPageId,
            string displayName = "Full Access",
            int order = 0)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Full Access" : displayName,
                order)
        {
            _targetCubeProvider = targetCubeProvider;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public override void OnDeactivated()
        {
            _positionTick?.Pause();
        }

        /// <inheritdoc />
        public override void OnActivated()
        {
            _positionTick?.Resume();
            UpdatePosition();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            _positionTick?.Pause();
            _positionTick = null;
        }

        private object Build()
        {
            ScrollView scroll = DemoHubWidgets.CreatePage("Full Access Demo", out VisualElement body);

            body.Add(DemoHubWidgets.MakeBody(
                "Enable Full Lua on CoreAILifetimeScope so Programmer mods can reach this scene."));
            body.Add(DemoHubWidgets.MakeBody(
                "The scene starts empty. Assign a TargetCube in the inspector (or let a mod spawn one) to " +
                "reach it via unity_find / unity_set_member; the row below reads '-' until one exists."));
            body.Add(DemoHubWidgets.MakeBody(
                "Private members need 'Enable Full Lua Private Access' (off by default)."));

            body.Add(DemoHubWidgets.MakeSection("Scene target"));
            body.Add(DemoHubWidgets.MakeRow("TargetCube position", "-", out _positionLabel));

            body.Add(DemoHubWidgets.MakeNote(
                "The Programmer, SmartChat and AINpc agents can be switched from the chat panel's " +
                "agent dropdown, enabled for this demo at startup."));

            // WHY: schedule.Execute drives the live position read on the panel's own scheduler, so there
            // is no per-frame Update and it pauses with the page (see OnActivated/OnDeactivated).
            _positionTick = scroll.schedule.Execute(UpdatePosition).Every(100);
            UpdatePosition();
            return scroll;
        }

        private void UpdatePosition()
        {
            if (_positionLabel == null)
            {
                return;
            }

            Transform cube = _targetCubeProvider?.Invoke();
            if (cube == null)
            {
                _positionLabel.text = "(no TargetCube)";
                return;
            }

            Vector3 p = cube.position;
            _positionLabel.text = $"({p.x:0.##}, {p.y:0.##}, {p.z:0.##})";
        }
    }
}
#endif
