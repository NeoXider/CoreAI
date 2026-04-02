using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    public sealed class ArenaSurvivalHud : MonoBehaviour
    {
        private IArenaSessionView _session;
        private ArenaPlayerHealth _health;
        private ArenaCreatorWavePlanner _creatorPlanner;
        private ArenaAuxLlmEveryNWaves _auxLlm;
        private GUIStyle _labelStyle;

        public void Bind(
            IArenaSessionView session,
            ArenaPlayerHealth health,
            ArenaCreatorWavePlanner creatorPlanner = null,
            ArenaAuxLlmEveryNWaves auxLlm = null)
        {
            _session = session;
            _health = health;
            _creatorPlanner = creatorPlanner;
            _auxLlm = auxLlm;
        }

        private void OnGUI()
        {
            if (_session == null)
                return;
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    normal = { textColor = Color.white }
                };
            }

            const float pad = 12f;
            var style = _labelStyle;
            if (_session.RunEnded)
            {
                GUI.Label(new Rect(pad, pad, 900f, 32f), _session.PlayerWon ? "Победа — все волны пройдены!" : "Поражение", style);
                GUI.Label(new Rect(pad, pad + 28f, 900f, 32f), "R — перезапуск сцены", style);
                return;
            }

            var h = _health != null ? $"{_health.Current} / {_health.Max}" : "?";
            GUI.Label(new Rect(pad, pad, 900f, 32f), $"Волна {_session.CurrentWave}  |  HP {h}  |  Врагов: {_session.AliveEnemies}", style);
            var simHint = _session.IsAuthoritativeSimulation ? "" : "  [клиент: без симуляции врагов]";
            GUI.Label(new Rect(pad, pad + 28f, 900f, 32f), $"WASD — движение, Space / ЛКМ — удар{simHint}", style);
            var y = pad + 56f;
            if (_creatorPlanner != null && _creatorPlanner.IsAwaitingCreatorPlan)
            {
                GUI.Label(new Rect(pad, y, 900f, 28f), "ИИ думает… (план волны)", style);
                y += 26f;
            }

            if (_auxLlm != null && !string.IsNullOrEmpty(_auxLlm.StatusLine))
                GUI.Label(new Rect(pad, y, 900f, 28f), _auxLlm.StatusLine, style);
        }
    }
}
