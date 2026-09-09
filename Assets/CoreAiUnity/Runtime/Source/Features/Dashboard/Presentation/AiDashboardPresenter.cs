using System.Collections.Generic;
using System.Text;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI.Presentation.AiDashboard
{
    /// <summary>
    /// Immediate-mode dashboard presenter for CoreAI runtime status and controls.
    /// </summary>
    public sealed class AiDashboardPresenter : MonoBehaviour
    {
        [Tooltip("Optional role-permission snapshot displayed in the overlay header.")]
        [SerializeField]
        private AiPermissionsAsset permissions;

        [Tooltip("Enable the IMGUI command log overlay.")]
        [SerializeField]
        private bool showGui = true;

        private readonly List<string> _visible = new();
        private IAiPermissions _runtimePermissions;

        // Cached overlay text and the inputs it was built from (see BuildOverlayText).
        private readonly StringBuilder _overlayScratch = new();
        private string _overlayText = string.Empty;
        private bool _overlayLinesDirty = true;
        private IAiPermissions _overlayPermissions;
        private bool _overlayAllowCreator;
        private bool _overlayAllowAnalyzer;
        private bool _overlayAllowCoreMechanic;

        /// <summary>
        /// Overrides Inspector-authored permissions with a Unity-free runtime snapshot.
        /// </summary>
        public void SetRuntimePermissions(IAiPermissions runtimePermissions)
        {
            _runtimePermissions = runtimePermissions;
        }

        private void OnEnable()
        {
            AiGameCommandRouter.CommandReceived += OnAiCommand;
        }

        private void OnDisable()
        {
            AiGameCommandRouter.CommandReceived -= OnAiCommand;
        }

        /// <summary>Appends one routed command to the visible tail. Internal so a test can feed commands directly.</summary>
        internal void OnAiCommand(ApplyAiGameCommand cmd)
        {
            string src = string.IsNullOrWhiteSpace(cmd.SourceTag) ? "" : $" [{cmd.SourceTag}]";
            _visible.Add($"{cmd.CommandTypeId}{src}: {cmd.JsonPayload}");
            while (_visible.Count > 48)
            {
                _visible.RemoveAt(0);
            }

            _overlayLinesDirty = true;
        }

        private void OnGUI()
        {
            if (!showGui)
            {
                return;
            }

            const float w = 520f;
            GUI.Box(new Rect(10, 10, w, 220), "CoreAI - live log (MVP)");
            GUI.Label(new Rect(20, 35, w - 20, 200), BuildOverlayText());
        }

        /// <summary>
        /// Text of the overlay: the permission header plus the last commands. Rebuilt only when a
        /// command arrived or the permission flags changed; otherwise the previous string is returned.
        /// <para>
        /// WHY: the old OnGUI built a fresh <see cref="StringBuilder"/>, re-appended up to 48 lines and
        /// called <c>ToString()</c> on EVERY repaint - several per frame - although the content changes
        /// only when a command is routed. The permission flags are compared by value, so a flag flipped
        /// on the asset still shows up on the next repaint exactly as before. Internal so a test can pin
        /// the reuse.
        /// </para>
        /// </summary>
        internal string BuildOverlayText()
        {
            IAiPermissions activePermissions = _runtimePermissions ?? permissions;
            bool allowCreator = activePermissions != null && activePermissions.AllowCreator;
            bool allowAnalyzer = activePermissions != null && activePermissions.AllowAnalyzer;
            bool allowCoreMechanic = activePermissions != null && activePermissions.AllowCoreMechanic;
            bool permissionsChanged = !ReferenceEquals(activePermissions, _overlayPermissions) ||
                                      allowCreator != _overlayAllowCreator ||
                                      allowAnalyzer != _overlayAllowAnalyzer ||
                                      allowCoreMechanic != _overlayAllowCoreMechanic;
            if (!_overlayLinesDirty && !permissionsChanged)
            {
                return _overlayText;
            }

            _overlayPermissions = activePermissions;
            _overlayAllowCreator = allowCreator;
            _overlayAllowAnalyzer = allowAnalyzer;
            _overlayAllowCoreMechanic = allowCoreMechanic;
            _overlayLinesDirty = false;

            StringBuilder sb = _overlayScratch;
            sb.Clear();
            if (activePermissions != null)
            {
                sb.AppendLine(
                    $"AI perms: C={allowCreator} A={allowAnalyzer} M={allowCoreMechanic}");
            }

            foreach (string line in _visible)
            {
                sb.AppendLine(line);
            }

            _overlayText = sb.ToString();
            return _overlayText;
        }
    }
}
