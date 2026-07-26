using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Demos.QwenDemo
{
    /// <summary>
    /// Demo 1 — "Wish Genie". The player makes a free-form wish (RU or EN). Qwen3.5-0.8B, through the
    /// existing CoreAI LLM-for-Unity native tool-calling path, interprets the wish and calls exactly one
    /// world tool. Justification: the input is unbounded natural language — no switch/state-machine can map
    /// wishes such as "make it rain frogs" to an action; understanding and a mischievous
    /// reading is the whole mechanic. Guardrail: a wish-charge budget lives in C# — when it hits zero the
    /// tools VETO the model's call. HUD shows time and tokens.
    /// </summary>
    public sealed class GenieDemo : MonoBehaviour
    {
        private const string RoleId = "DemoGenie";

        private const string SystemPrompt =
            "You are a mischievous genie bound to a lamp. The player makes a wish in their own words " +
            "(any language, including Russian). Grant it by calling EXACTLY ONE tool that best matches the " +
            "wish. Be playful: if a wish is greedy, vague, or rude, interpret it literally or mischievously. " +
            "After the tool call, reply with ONE short in-character sentence.";

        private static readonly string[] Presets =
        {
            "хочу гору золота",
            "призови дракона",
            "make it rain gold",
            "накажи вора, что украл лампу",
            "хочу стать бессмертным и править миром"
        };

        private MainThreadPump _pump;
        private Transform _root;
        private GameObject _lamp;
        private AgentConfig _agent;

        private readonly object _gate = new();
        private int _charges = 3;

        private string _input = "хочу гору золота";
        private LlmRunResult _last;
        private bool _busy;
        private readonly List<string> _log = new();
        private Vector2 _scroll;
        private Vector2 _controlsScroll;
        private string _disabledReason;
        private bool _ready;
        private readonly QwenToolTurnGuard _toolTurnGuard = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();

        private async void Start()
        {
            QwenFx.BuildStage(new Color(0.14f, 0.12f, 0.18f));
            _root = new GameObject("GenieRoot").transform;
            _pump = gameObject.AddComponent<MainThreadPump>();

            _lamp = QwenFx.Prim(PrimitiveType.Cylinder, _root, new Vector3(0, 0.5f, 0),
                new Vector3(1.4f, 0.5f, 1.4f), new Color(0.8f, 0.7f, 0.3f), "🪔 LAMP");
            QwenFx.Prim(PrimitiveType.Capsule, _root, new Vector3(-3f, 1f, -1.5f),
                new Vector3(0.7f, 1f, 0.7f), new Color(0.4f, 0.7f, 1f), "you");

            _agent = new AgentBuilder(RoleId)
                .WithSystemPrompt(SystemPrompt)
                .WithMode(AgentMode.ToolsOnly)
                .WithoutChatHistory()
                .WithTool(new DelegateLlmTool("grant_gold",
                    "Give the player gold coins. Use for wishes about money, riches, treasure.",
                    new Func<int, string>(GrantGold)))
                .WithTool(new DelegateLlmTool("summon",
                    "Summon a creature, ally or object into the world by name.",
                    new Func<string, string>(Summon)))
                .WithTool(new DelegateLlmTool("smite",
                    "Punish, curse or mischievously twist a target named by the player.",
                    new Func<string, string>(Smite)))
                .WithTool(new DelegateLlmTool("deny",
                    "Refuse a wish that is impossible or against the rules, with a short reason.",
                    new Func<string, string>(Deny)))
                .Build();

            if (CoreAIAgent.Policy == null)
            {
                _disabledReason =
                    "CoreAI is not initialized (CoreAILifetimeScope is missing from the scene or no LLM backend is selected).";
                Log(_disabledReason);
                return;
            }

            _agent.ApplyToPolicy(CoreAIAgent.Policy);
            Log("⏳ Loading Qwen and waiting for the llama.cpp HTTP server…");
            string readinessError;
            try
            {
                readinessError = await QwenDemoReadiness.WaitUntilReadyAsync(
                    cancellationToken: _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (this == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(readinessError))
            {
                _disabledReason = readinessError;
                Log("✘ " + readinessError);
                return;
            }

            _ready = true;
            Log("✅ The genie is ready. Make a wish in your own words and it will choose the tool.");
        }

        private void OnDestroy()
        {
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
        }

        // WHY: Tool delegates run on a worker thread, so guards stay here and visuals use the pump.

        private bool TrySpendCharge(out int left)
        {
            lock (_gate)
            {
                if (_charges <= 0)
                {
                    left = 0;
                    return false;
                }

                _charges--;
                left = _charges;
                return true;
            }
        }

        private string GrantGold(int amount)
        {
            if (!_toolTurnGuard.TryClaim())
            {
                return "This turn already used its single world action.";
            }

            if (!TrySpendCharge(out int left))
            {
                return "The lamp has gone cold — no wishes left. Refuse politely.";
            }

            // WHY: The model supplies a hint while authoritative game code owns the reward cap.
            int coins = Mathf.Clamp(amount <= 0 ? 20 : amount, 3, 40);
            if (_pump != null)
            {
                _pump.Enqueue(() =>
                {
                    for (int i = 0; i < coins; i++)
                    {
                        Vector3 p = new(UnityEngine.Random.Range(-2.5f, 2.5f), 6f + i * 0.15f,
                            UnityEngine.Random.Range(-1f, 2.5f));
                        GameObject c = QwenFx.Prim(PrimitiveType.Sphere, _root, p, Vector3.one * 0.28f,
                            new Color(1f, 0.84f, 0.2f), null, true);
                        StartCoroutine(QwenFx.MoveTo(c.transform, new Vector3(p.x, 0.3f, p.z), 9f));
                        Destroy(c, 4f);
                    }

                    QwenFx.Ring(this, _root, Vector3.zero, new Color(1f, 0.84f, 0.2f), 3f, 0.5f);
                });
            }

            return $"Granted {coins} gold coins raining down. Wishes left: {left}.";
        }

        private string Summon(string creature)
        {
            if (!_toolTurnGuard.TryClaim())
            {
                return "This turn already used its single world action.";
            }

            string name = string.IsNullOrWhiteSpace(creature) ? "creature" : creature.Trim();
            if (name.Length > 22)
            {
                name = name.Substring(0, 22);
            }

            if (!TrySpendCharge(out int left))
            {
                return "The lamp has gone cold — no wishes left. Refuse politely.";
            }

            if (_pump != null)
            {
                _pump.Enqueue(() =>
                {
                    Vector3 p = new(UnityEngine.Random.Range(-2f, 2f), 0.9f, 2.5f);
                    QwenFx.Sparks(this, _root, p, new Color(0.6f, 0.3f, 0.9f), 16, 5f);
                    QwenFx.Prim(PrimitiveType.Cube, _root, p, new Vector3(1.1f, 1.6f, 1.1f),
                        new Color(0.55f, 0.35f, 0.8f), "✨ " + name);
                });
            }

            return $"Summoned '{name}'. Wishes left: {left}.";
        }

        private string Smite(string target)
        {
            if (!_toolTurnGuard.TryClaim())
            {
                return "This turn already used its single world action.";
            }

            string name = string.IsNullOrWhiteSpace(target) ? "target" : target.Trim();
            if (name.Length > 22)
            {
                name = name.Substring(0, 22);
            }

            if (!TrySpendCharge(out int left))
            {
                return "The lamp has gone cold — no wishes left. Refuse politely.";
            }

            if (_pump != null)
            {
                _pump.Enqueue(() =>
                {
                    Vector3 p = new(UnityEngine.Random.Range(-2f, 2f), 1f, 2.5f);
                    GameObject victim = QwenFx.Prim(PrimitiveType.Capsule, _root, p, new Vector3(0.7f, 1f, 0.7f),
                        new Color(0.8f, 0.3f, 0.3f), "☄ " + name);
                    QwenFx.Beam(this, _root, p + Vector3.up * 6f, p + Vector3.up, new Color(1f, 0.4f, 0.2f), 0.25f, 3);
                    QwenFx.Sparks(this, _root, p, new Color(1f, 0.4f, 0.2f), 14, 5f);
                    StartCoroutine(QwenFx.Shake(victim.transform, 0.2f, 0.5f));
                    Destroy(victim, 3.5f);
                });
            }

            return $"Smote '{name}' with a bolt. Wishes left: {left}.";
        }

        private string Deny(string reason)
        {
            if (!_toolTurnGuard.TryClaim())
            {
                return "This turn already used its single world action.";
            }

            string r = string.IsNullOrWhiteSpace(reason) ? "that is beyond my power" : reason.Trim();
            if (_pump != null)
            {
                _pump.Enqueue(() => QwenFx.Label(_lamp.transform, "🚫", new Color(1f, 0.5f, 0.5f), 1.6f));
            }

            return $"Wish refused: {r}";
        }

        private void Submit()
        {
            if (_busy || !_ready || string.IsNullOrWhiteSpace(_input) ||
                QwenDemoState.HasBlockingError(_disabledReason))
            {
                return;
            }

            _busy = true;
            string wish = _input;
            Log("🙏 wish: " + wish);
            _ = RunAsync(wish);
        }

        private async System.Threading.Tasks.Task RunAsync(string wish)
        {
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            int turn = _toolTurnGuard.BeginTurn();
            try
            {
                LlmRunResult result = await LlmMeter.RunAsync(RoleId, wish, 200, cancellationToken,
                    "grant_gold", "summon", "smite", "deny");
                if (cancellationToken.IsCancellationRequested || this == null)
                {
                    return;
                }

                _last = result;
                if (!string.IsNullOrEmpty(_last.Error))
                {
                    Log("✘ " + _last.Error);
                }
                else
                {
                    Log("🧞 " + (_last.Text.Length == 0 ? "(the genie was silent)" : _last.Text.Trim()));
                    Log(_last.HudLine());
                }
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested && this != null)
                {
                    _toolTurnGuard.EndTurn(turn);
                    _busy = false;
                }
            }
        }

        private void Log(string s)
        {
            _log.Add(s);
            if (_log.Count > 30)
            {
                _log.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            GUIStyle rich = new(GUI.skin.label) { richText = true, wordWrap = true };

            QwenDemoLayout.Calculate(Screen.width, Screen.height, out Rect topPanel, out Rect logPanel);
            GUILayout.BeginArea(topPanel, GUI.skin.box);
            _controlsScroll = GUILayout.BeginScrollView(_controlsScroll);
            GUILayout.Label("<b>WISH GENIE — Qwen3.5-0.8B (native tool calls)</b>",
                new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 });
            GUILayout.Label(
                _last == null ? "<b>⏱ waiting for the first wish…</b>" : "<b>" + _last.HudLine() + "</b>",
                new GUIStyle(GUI.skin.label)
                    { richText = true, fontSize = 12, normal = { textColor = new Color(0.6f, 0.9f, 1f) } });
            GUILayout.Label($"🪔 wishes left (code-enforced limit): <b>{_charges}</b>", rich);

            if (QwenDemoState.HasBlockingError(_disabledReason))
            {
                GUILayout.Label("<color=#ff8080>" + _disabledReason + "</color>", rich);
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }

            if (!_ready)
            {
                GUILayout.Label("<color=#ffd166>⏳ Qwen/llama.cpp is loading; actions unlock after readiness.</color>",
                    rich);
            }

            GUILayout.Space(4);
            _input = GUILayout.TextField(_input, GUILayout.Height(24));
            bool stackedButtons = QwenDemoLayout.StackActionButtons(topPanel.width);
            if (!stackedButtons)
            {
                GUILayout.BeginHorizontal();
            }

            GUI.enabled = !_busy && _ready;
            if (GUILayout.Button(_busy ? "⏳ the genie is thinking…" : "🙏 MAKE A WISH", GUILayout.Height(28)))
            {
                Submit();
            }

            GUILayoutOption[] resetOptions = stackedButtons
                ? new[] { GUILayout.Height(28) }
                : new[] { GUILayout.Width(130), GUILayout.Height(28) };
            if (GUILayout.Button("reset lamp (+3)", resetOptions))
            {
                lock (_gate)
                {
                    _charges = 3;
                }
            }

            GUI.enabled = true;
            if (!stackedButtons)
            {
                GUILayout.EndHorizontal();
            }

            GUILayout.Label("Examples (RU/EN):", rich);
            GUI.enabled = !_busy && _ready;
            foreach (string p in Presets)
            {
                if (GUILayout.Button(p, GUILayout.Height(20)))
                {
                    _input = p;
                    Submit();
                }
            }

            GUI.enabled = true;

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUILayout.BeginArea(logPanel, GUI.skin.box);
            GUILayout.Label("Log (model decision + speed/tokens):");
            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = _log.Count - 1; i >= 0; i--)
            {
                GUILayout.Label(_log[i], rich);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
