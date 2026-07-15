using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.ExampleGame.QwenDemo
{
    /// <summary>
    /// Demo 2 — "Магия из описания". The player describes a spell in their OWN words (RU or EN):
    /// «стена огня», «заморозь их», «ядовитый туман», «призови молнию с неба». Qwen3.5-0.8B, via CoreAI's
    /// native tool-calling, maps that free description to cast_spell(element, power). Justification: the
    /// description space is open — there is no rune table or switch that covers "wall of thorns that burns";
    /// only language understanding does. Guardrail (the mana question, answered live): mana lives in C#; if a
    /// cast costs more than is available, the tool VETOES it and the spell fizzles regardless of what the
    /// model chose. Distinct VFX per element + HUD time/tokens.
    /// </summary>
    public sealed class SpellcraftDemo : MonoBehaviour
    {
        private const float TargetWorldX = 5.5f;
        private const string RoleId = "DemoMage";

        private const string SystemPrompt =
            "You interpret spells for a wizard. The player describes a spell in their own words (any " +
            "language, including Russian). Call cast_spell EXACTLY ONCE with the closest element " +
            "(one of: fire, frost, storm, poison, arcane) and an integer power from 1 to 3 reflecting how " +
            "strong the description sounds. After the call, reply with ONE short line naming the spell.";

        private static readonly string[] Presets =
        {
            "стена огня",
            "заморозь их до костей",
            "ядовитый туман над врагом",
            "призови молнию с неба",
            "a huge arcane meteor"
        };

        private static readonly string[] Elements = { "fire", "frost", "storm", "poison", "arcane" };

        private MainThreadPump _pump;
        private Transform _root;
        private GameObject _caster, _dummy;

        private AgentConfig _agent;

        private readonly object _gate = new();
        private float _mana = 100f;
        private const float MaxMana = 100f;

        // WHY: Each spell leaves a distinct temporary state so its gameplay result is visible.
        private Color _dummyBaseColor;
        private Vector3 _dummyBaseScale;
        private Vector3 _dummyBasePos;
        private Color _dummyReactColor;
        private Coroutine _recoverCo;

        // WHY: The self-test needs the raw decision independently of mana vetoes and VFX.
        private readonly object _decGate = new();
        private string _lastDecision;

        private string _input = "стена огня";
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
            QwenFx.BuildStage(new Color(0.12f, 0.13f, 0.18f));
            _root = new GameObject("MageRoot").transform;
            _pump = gameObject.AddComponent<MainThreadPump>();

            _caster = QwenFx.Prim(PrimitiveType.Capsule, _root, new Vector3(-3.5f, 1f, 0),
                new Vector3(0.8f, 1.1f, 0.8f), new Color(0.6f, 0.5f, 0.9f), "маг");
            _dummy = QwenFx.Prim(PrimitiveType.Cube, _root, new Vector3(TargetWorldX, 1f, 0),
                new Vector3(1.4f, 2f, 1.4f), new Color(0.5f, 0.5f, 0.55f), "мишень");
            _dummyBaseColor = new Color(0.5f, 0.5f, 0.55f);
            _dummyBaseScale = _dummy.transform.localScale;
            _dummyBasePos = _dummy.transform.localPosition;

            _agent = new AgentBuilder(RoleId)
                .WithSystemPrompt(SystemPrompt)
                .WithMode(AgentMode.ToolsOnly)
                .WithoutChatHistory()
                .WithTemperature(0f)
                .WithTool(new DelegateLlmTool("cast_spell",
                    "Cast a spell. element is one of fire, frost, storm, poison, arcane. power is 1 (weak) " +
                    "to 3 (mighty). Choose the element and power that best fit the player's description.",
                    new Func<string, int, string>(CastSpell)))
                .Build();

            if (CoreAIAgent.Policy == null)
            {
                _disabledReason =
                    "CoreAI не инициализирован (нет CoreAILifetimeScope в сцене или не выбран бэкенд LLM).";
                Log(_disabledReason);
                return;
            }

            _agent.ApplyToPolicy(CoreAIAgent.Policy);
            Log("⏳ Загружаем Qwen и ждём HTTP-сервер llama.cpp…");
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
            Log("✅ Опиши заклинание словами (RU/EN) — модель выберет стихию и силу, код проверит ману.");
        }

        private void OnDestroy()
        {
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
        }

        private void Update()
        {
            // WHY: Mana regenerates on the main thread while worker-thread tools spend it under the same lock.
            lock (_gate)
            {
                _mana = Mathf.Min(MaxMana, _mana + Time.deltaTime * 8f);
            }
        }

        private string CastSpell(string element, int power)
        {
            if (!_toolTurnGuard.TryClaim())
            {
                return "This turn already used its single spell action.";
            }

            string el = NormalizeElement(element);
            int pow = Mathf.Clamp(power <= 0 ? 1 : power, 1, 3);
            int cost = pow * 20;

            // WHY: Determinism measures the model decision independently of the authoritative mana veto.
            lock (_decGate)
            {
                _lastDecision = $"{el}|{pow}";
            }

            bool ok;
            float manaNow;
            lock (_gate)
            {
                manaNow = _mana;
                ok = _mana >= cost;
                if (ok)
                {
                    _mana -= cost;
                }
            }

            if (!ok)
            {
                _pump.Enqueue(() =>
                {
                    QwenFx.Sparks(this, _root, _caster.transform.localPosition + Vector3.up, Color.gray, 6, 2f);
                    QwenFx.Label(_caster.transform, "маны нет!", new Color(1f, 0.5f, 0.5f), 1.6f);
                });
                return $"Not enough mana for {el} (need {cost}, have {(int)manaNow}). The spell fizzles.";
            }

            _pump.Enqueue(() => StartCoroutine(Effect(el, pow)));
            return $"Cast {el} at power {pow} (spent {cost} mana).";
        }

        private static string NormalizeElement(string element)
        {
            string e = (element ?? "").Trim().ToLowerInvariant();
            foreach (string opt in Elements)
            {
                if (e.Contains(opt) || opt.Contains(e))
                {
                    return opt;
                }
            }

            // WHY: Common synonyms keep off-vocabulary descriptions inside the supported element set.
            if (e.Contains("ice") || e.Contains("frozen") || e.Contains("cold") || e.Contains("лед") ||
                e.Contains("мороз"))
            {
                return "frost";
            }

            if (e.Contains("light") || e.Contains("thunder") || e.Contains("молни") || e.Contains("гром"))
            {
                return "storm";
            }

            if (e.Contains("burn") || e.Contains("flame") || e.Contains("огн") || e.Contains("пламя"))
            {
                return "fire";
            }

            if (e.Contains("venom") || e.Contains("яд") || e.Contains("токс"))
            {
                return "poison";
            }

            return "arcane";
        }

        private Color ElementColor(string el) => el switch
        {
            "fire" => new Color(1f, 0.45f, 0.12f),
            "frost" => new Color(0.55f, 0.85f, 1f),
            "storm" => new Color(1f, 0.95f, 0.35f),
            "poison" => new Color(0.55f, 0.95f, 0.25f),
            _ => new Color(0.75f, 0.5f, 1f)
        };

        private IEnumerator Effect(string el, int power)
        {
            Color c = ElementColor(el);
            Vector3 casterTop = _caster.transform.localPosition + Vector3.up * 1.2f;
            Vector3 target = _dummy.transform.localPosition + Vector3.up;
            float scale = 1f + 0.5f * (power - 1);

            switch (el)
            {
                case "fire":
                {
                    GameObject bolt = QwenFx.Bolt(_root, casterTop, c, 0.5f * scale);
                    yield return QwenFx.MoveTo(bolt.transform, target, 16f);
                    if (bolt != null)
                    {
                        UnityEngine.Object.Destroy(bolt);
                    }

                    yield return QwenFx.Flash(_root, target, c, 2.4f * scale, 0.4f);
                    QwenFx.Sparks(this, _root, target, c, 14 + power * 4, 6f);
                    QwenFx.Ring(this, _root, _dummy.transform.localPosition, c, 3f * scale);
                    ReactDummy(el, power);
                    break;
                }

                case "frost":
                {
                    GameObject bolt = QwenFx.Bolt(_root, casterTop, c, 0.45f);
                    yield return QwenFx.MoveTo(bolt.transform, target, 14f);
                    if (bolt != null)
                    {
                        UnityEngine.Object.Destroy(bolt);
                    }

                    for (int i = 0; i < 4 + power * 2; i++)
                    {
                        float ang = i / (float)(4 + power * 2) * Mathf.PI * 2f;
                        Vector3 p = _dummy.transform.localPosition +
                                    new Vector3(Mathf.Cos(ang), -0.2f, Mathf.Sin(ang)) * 1.1f;
                        QwenFx.Lingering(this, _root, PrimitiveType.Cube, p + Vector3.up * 0.4f,
                            new Vector3(0.18f, 1.1f, 0.18f), new Color(0.7f, 0.9f, 1f), 0.9f, 0.08f, 1.4f, 0.6f);
                    }

                    QwenFx.Lingering(this, _root, PrimitiveType.Cube, _dummy.transform.localPosition + Vector3.up,
                        Vector3.one * 2.1f, new Color(0.6f, 0.85f, 1f), 0.4f, 0.12f, 1.6f, 0.6f);
                    ReactDummy(el, power);
                    break;
                }

                case "storm":
                {
                    for (int i = 0; i < 2 + power; i++)
                    {
                        Vector3 sky = _dummy.transform.localPosition + Vector3.up * 7f +
                                      new Vector3(UnityEngine.Random.Range(-0.6f, 0.6f), 0,
                                          UnityEngine.Random.Range(-0.6f, 0.6f));
                        QwenFx.Beam(this, _root, sky, target, c, 0.22f, 2);
                        QwenFx.Sparks(this, _root, target, c, 8, 4.5f, 0.14f, 0.5f);
                        QwenFx.Paint(_dummy, new Color(1f, 1f, 0.6f));
                        yield return new WaitForSeconds(0.16f);
                        QwenFx.Paint(_dummy, new Color(0.5f, 0.5f, 0.55f));
                        yield return new WaitForSeconds(0.08f);
                    }

                    ReactDummy(el, power);
                    break;
                }

                case "poison":
                {
                    QwenFx.Lingering(this, _root, PrimitiveType.Sphere, target + Vector3.up * 0.4f,
                        Vector3.one * (2.2f + power * 0.4f), c, 0.45f, 0.5f, 2.2f, 1.0f, 40f);
                    for (int i = 0; i < 4 + power; i++)
                    {
                        Vector3 p = _dummy.transform.localPosition + new Vector3(
                            UnityEngine.Random.Range(-0.8f, 0.8f), 0.3f, UnityEngine.Random.Range(-0.8f, 0.8f));
                        GameObject bub = QwenFx.Bolt(_root, p, c, 0.25f);
                        StartCoroutine(QwenFx.MoveTo(bub.transform, p + Vector3.up * 2.2f, 1.2f));
                        UnityEngine.Object.Destroy(bub, 2f);
                        yield return new WaitForSeconds(0.1f);
                    }

                    ReactDummy(el, power);
                    break;
                }

                default:
                {
                    Vector3 sky = _dummy.transform.localPosition + Vector3.up * 9f;
                    GameObject rock = QwenFx.Prim(PrimitiveType.Sphere, _root, sky,
                        Vector3.one * (0.9f + 0.3f * power), new Color(0.4f, 0.2f, 0.5f), null, true);
                    QwenFx.Sparks(this, _root, sky, c, 6, 2f, 0.2f, 0.6f);
                    yield return QwenFx.MoveTo(rock.transform, _dummy.transform.localPosition + Vector3.up * 0.4f, 22f);
                    if (rock != null)
                    {
                        UnityEngine.Object.Destroy(rock);
                    }

                    yield return QwenFx.Flash(_root, _dummy.transform.localPosition + Vector3.up * 0.4f, c,
                        3.2f + power * 0.4f, 0.5f);
                    QwenFx.Sparks(this, _root, _dummy.transform.localPosition, c, 18 + power * 6, 8f, 0.22f, 1f);
                    QwenFx.Ring(this, _root, _dummy.transform.localPosition, c, 4f + power * 0.5f, 0.6f);
                    ReactDummy(el, power);
                    break;
                }
            }
        }

        /// <summary>Each element leaves the dummy in a DISTINCT state (colour + shape/pose), restored after 3s.</summary>
        private void ReactDummy(string el, int power)
        {
            if (_recoverCo != null)
            {
                StopCoroutine(_recoverCo);
            }

            // WHY: Reset first so repeated casts cannot compound scale and position offsets.
            _dummy.transform.localScale = _dummyBaseScale;
            _dummy.transform.localPosition = _dummyBasePos;

            switch (el)
            {
                case "fire":
                    _dummyReactColor = new Color(0.14f, 0.11f, 0.1f);
                    QwenFx.Paint(_dummy, _dummyReactColor);
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 p = _dummy.transform.localPosition + Vector3.up * 1.4f +
                                    new Vector3(UnityEngine.Random.Range(-0.4f, 0.4f), 0,
                                        UnityEngine.Random.Range(-0.4f, 0.4f));
                        GameObject smoke = QwenFx.Bolt(_root, p, new Color(0.3f, 0.3f, 0.3f), 0.4f);
                        StartCoroutine(QwenFx.MoveTo(smoke.transform, p + Vector3.up * 2.5f, 0.9f));
                        UnityEngine.Object.Destroy(smoke, 2.2f);
                    }

                    StartCoroutine(QwenFx.Shake(_dummy.transform, 0.18f, 0.4f));
                    break;

                case "frost":
                    _dummyReactColor = new Color(0.62f, 0.85f, 1f);
                    QwenFx.Paint(_dummy, _dummyReactColor);
                    _dummy.transform.localScale = _dummyBaseScale * 1.12f;
                    break;

                case "storm":
                    _dummyReactColor = new Color(0.85f, 0.9f, 1f);
                    QwenFx.Paint(_dummy, _dummyReactColor);
                    StartCoroutine(QwenFx.Shake(_dummy.transform, 0.12f, 0.6f));
                    break;

                case "poison":
                    _dummyReactColor = new Color(0.45f, 0.7f, 0.2f);
                    QwenFx.Paint(_dummy, _dummyReactColor);
                    _dummy.transform.localScale = new Vector3(_dummyBaseScale.x * 1.1f,
                        _dummyBaseScale.y * 0.65f, _dummyBaseScale.z * 1.1f);
                    _dummy.transform.localPosition = _dummyBasePos - Vector3.up * (_dummyBaseScale.y * 0.17f);
                    break;

                default:
                    _dummyReactColor = new Color(0.35f, 0.18f, 0.4f);
                    QwenFx.Paint(_dummy, _dummyReactColor);
                    _dummy.transform.localPosition = _dummyBasePos + new Vector3(1.2f + 0.3f * power, 0, 0);
                    StartCoroutine(QwenFx.Shake(_dummy.transform, 0.25f, 0.5f));
                    break;
            }

            _recoverCo = StartCoroutine(RecoverDummy());
        }

        /// <summary>Holds the hit state for 3 seconds, then smoothly restores the dummy to its base look.</summary>
        private IEnumerator RecoverDummy()
        {
            yield return new WaitForSeconds(3f);

            Vector3 rs = _dummy.transform.localScale;
            Vector3 rp = _dummy.transform.localPosition;
            Color rc = _dummyReactColor;
            float t = 0f;
            while (t < 0.5f)
            {
                t += Time.deltaTime;
                float k = t / 0.5f;
                QwenFx.Paint(_dummy, Color.Lerp(rc, _dummyBaseColor, k));
                _dummy.transform.localScale = Vector3.Lerp(rs, _dummyBaseScale, k);
                _dummy.transform.localPosition = Vector3.Lerp(rp, _dummyBasePos, k);
                yield return null;
            }

            QwenFx.Paint(_dummy, _dummyBaseColor);
            _dummy.transform.localScale = _dummyBaseScale;
            _dummy.transform.localPosition = _dummyBasePos;
            _recoverCo = null;
        }

        private void Submit()
        {
            if (_busy || !_ready || string.IsNullOrWhiteSpace(_input) ||
                QwenDemoState.HasBlockingError(_disabledReason))
            {
                return;
            }

            _busy = true;
            string desc = _input;
            Log("📝 описание: " + desc);
            _ = RunAsync(desc);
        }

        /// <summary>
        /// Determinism self-test: runs the SAME description N times and reports the distribution of the
        /// model's element|power choice. Greedy decode (temperature 0) should give one bucket = deterministic.
        /// Mana is refilled each run so it never becomes the hidden variable.
        /// </summary>
        private async void RunDeterminism(string desc, int n)
        {
            if (_busy || !_ready || string.IsNullOrWhiteSpace(desc) ||
                QwenDemoState.HasBlockingError(_disabledReason))
            {
                return;
            }

            _busy = true;
            Log($"🔁 тест детерминизма: «{desc}» ×{n}");
            Dictionary<string, int> tally = new();
            double sumMs = 0;
            int successful = 0;
            int failures = 0;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    lock (_gate)
                    {
                        _mana = MaxMana;
                    }

                    lock (_decGate)
                    {
                        _lastDecision = null;
                    }

                    int turn = _toolTurnGuard.BeginTurn();
                    LlmRunResult r;
                    try
                    {
                        r = await LlmMeter.RunAsync(RoleId, desc, 160, "cast_spell");
                    }
                    finally
                    {
                        _toolTurnGuard.EndTurn(turn);
                    }

                    _last = r;
                    string dec;
                    lock (_decGate)
                    {
                        dec = _lastDecision;
                    }

                    if (!string.IsNullOrEmpty(r.Error) || string.IsNullOrEmpty(dec))
                    {
                        failures++;
                        Log($"  #{i + 1}: ОШИБКА — {r.Error ?? "решение отсутствует"}  ({r.TotalMs:0} мс)");
                        continue;
                    }

                    tally[dec] = tally.TryGetValue(dec, out int c) ? c + 1 : 1;
                    sumMs += r.TotalMs;
                    successful++;
                    Log($"  #{i + 1}: {dec}  ({r.TotalMs:0} мс)");
                }
            }
            finally
            {
                _busy = false;
            }

            string dist = string.Join(", ",
                tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}×{kv.Key}"));
            bool deterministic = QwenDeterminismVerdict.Passed(n, successful, failures, tally.Count);
            double avg = successful > 0 ? sumMs / successful : 0;
            string verdict = deterministic
                ? "✅ ДЕТЕРМИНИРОВАНО"
                : failures > 0
                    ? "❌ ТЕСТ НЕ ПРОЙДЕН"
                    : "⚠ РАЗБРОС";
            Log($"{verdict}: {dist} · успешно {successful}/{n} · ошибок {failures} · среднее {avg:0} мс");
        }

        private async System.Threading.Tasks.Task RunAsync(string desc)
        {
            int turn = _toolTurnGuard.BeginTurn();
            try
            {
                _last = await LlmMeter.RunAsync(RoleId, desc, 160, "cast_spell");
                if (!string.IsNullOrEmpty(_last.Error))
                {
                    Log("✘ " + _last.Error);
                }
                else
                {
                    Log("🪄 " + (_last.Text.Length == 0 ? "(маг промолчал)" : _last.Text.Trim()));
                    Log(_last.HudLine());
                }
            }
            finally
            {
                _toolTurnGuard.EndTurn(turn);
                _busy = false;
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
            int mana;
            lock (_gate)
            {
                mana = (int)_mana;
            }

            QwenDemoLayout.Calculate(Screen.width, Screen.height, out Rect topPanel, out Rect logPanel);
            GUILayout.BeginArea(topPanel, GUI.skin.box);
            _controlsScroll = GUILayout.BeginScrollView(_controlsScroll);
            GUILayout.Label("<b>МАГИЯ ИЗ ОПИСАНИЯ — Qwen3.5-0.8B (нативные tool-calls)</b>",
                new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 });
            GUILayout.Label(
                _last == null ? "<b>⏱ ждём первое заклинание…</b>" : "<b>" + _last.HudLine() + "</b>",
                new GUIStyle(GUI.skin.label)
                    { richText = true, fontSize = 12, normal = { textColor = new Color(0.7f, 0.85f, 1f) } });
            GUILayout.Label($"🔷 мана (guard в коде): <b>{mana}/100</b>  — спелл 1/2/3 стоит 20/40/60", rich);

            if (QwenDemoState.HasBlockingError(_disabledReason))
            {
                GUILayout.Label("<color=#ff8080>" + _disabledReason + "</color>", rich);
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }

            if (!_ready)
            {
                GUILayout.Label("<color=#ffd166>⏳ Qwen/llama.cpp загружается; действия станут доступны после readiness.</color>", rich);
            }

            GUILayout.Space(4);
            _input = GUILayout.TextField(_input, GUILayout.Height(24));
            bool stackedButtons = QwenDemoLayout.StackActionButtons(topPanel.width);
            if (!stackedButtons)
            {
                GUILayout.BeginHorizontal();
            }

            GUI.enabled = !_busy && _ready;
            if (GUILayout.Button(_busy ? "⏳ маг творит…" : "🪄 КОЛДОВАТЬ", GUILayout.Height(28)))
            {
                Submit();
            }

            GUILayoutOption[] determinismOptions = stackedButtons
                ? new[] { GUILayout.Height(28) }
                : new[] { GUILayout.Width(120), GUILayout.Height(28) };
            if (GUILayout.Button("🔁 детерм. ×5", determinismOptions))
            {
                RunDeterminism(_input, 5);
            }

            GUILayoutOption[] manaOptions = stackedButtons
                ? new[] { GUILayout.Height(28) }
                : new[] { GUILayout.Width(60), GUILayout.Height(28) };
            if (GUILayout.Button("мана", manaOptions))
            {
                lock (_gate)
                {
                    _mana = MaxMana;
                }
            }

            GUI.enabled = true;
            if (!stackedButtons)
            {
                GUILayout.EndHorizontal();
            }

            GUILayout.Label("Примеры (RU/EN):", rich);
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
            GUILayout.Label("Журнал (стихия/сила + скорость/токены):");
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
