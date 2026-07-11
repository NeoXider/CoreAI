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

        private void OnAiCommand(ApplyAiGameCommand cmd)
        {
            string src = string.IsNullOrWhiteSpace(cmd.SourceTag) ? "" : $" [{cmd.SourceTag}]";
            _visible.Add($"{cmd.CommandTypeId}{src}: {cmd.JsonPayload}");
            while (_visible.Count > 48)
            {
                _visible.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            if (!showGui)
            {
                return;
            }

            StringBuilder sb = new();
            IAiPermissions activePermissions = _runtimePermissions ?? permissions;
            if (activePermissions != null)
            {
                sb.AppendLine(
                    $"AI perms: C={activePermissions.AllowCreator} A={activePermissions.AllowAnalyzer} M={activePermissions.AllowCoreMechanic}");
            }

            foreach (string line in _visible)
            {
                sb.AppendLine(line);
            }

            const float w = 520f;
            GUI.Box(new Rect(10, 10, w, 220), "CoreAI - live log (MVP)");
            GUI.Label(new Rect(20, 35, w - 20, 200), sb.ToString());
        }
    }
}
