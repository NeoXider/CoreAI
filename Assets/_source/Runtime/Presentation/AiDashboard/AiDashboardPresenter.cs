using System.Collections.Generic;
using System.Text;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI.Presentation.AiDashboard
{
    /// <summary>
    /// Минимальный on-screen лог для отладки очереди ИИ (MVP, IMGUI).
    /// </summary>
    public sealed class AiDashboardPresenter : MonoBehaviour
    {
        [SerializeField]
        private AiPermissionsAsset permissions;

        [SerializeField]
        private bool showGui = true;

        private readonly List<string> _visible = new List<string>();

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
            _visible.Add($"{cmd.CommandTypeId}: {cmd.JsonPayload}");
            while (_visible.Count > 48)
                _visible.RemoveAt(0);
        }

        private void OnGUI()
        {
            if (!showGui)
                return;

            var sb = new StringBuilder();
            if (permissions != null)
            {
                sb.AppendLine(
                    $"AI perms: C={permissions.AllowCreator} A={permissions.AllowAnalyzer} M={permissions.AllowCoreMechanic}");
            }

            foreach (var line in _visible)
                sb.AppendLine(line);

            const float w = 520f;
            GUI.Box(new Rect(10, 10, w, 220), "CoreAI — live log (MVP)");
            GUI.Label(new Rect(20, 35, w - 20, 200), sb.ToString());
        }
    }
}
