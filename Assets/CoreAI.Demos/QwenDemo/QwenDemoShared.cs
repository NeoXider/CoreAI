using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace CoreAI.ExampleGame.QwenDemo
{
    /// <summary>Shared hot-reload-safe demo state predicates.</summary>
    public static class QwenDemoState
    {
        /// <summary>Unity hot reload may restore a null serialized string as empty.</summary>
        public static bool HasBlockingError(string error) => !string.IsNullOrWhiteSpace(error);
    }

    /// <summary>
    /// One measured LLM turn: text, latency split (TTFT / total), token usage and which tools fired.
    /// Populated from the streaming orchestrator so the HUD can show "за сколько и сколько токенов".
    /// </summary>
    public sealed class LlmRunResult
    {
        public string Text = "";
        public double TtftMs;
        public double TotalMs;
        public int? PromptTokens;
        public int? CompletionTokens;
        public int? TotalTokens;
        public bool CompletionTokensExact;
        public readonly List<string> Tools = new();
        public readonly List<LlmToolCallTrace> ToolCalls = new();
        public string Error;

        /// <summary>Decode-only tok/s (LM-Studio-comparable): completion ÷ (total − TTFT).</summary>
        public double TokensPerSecond
        {
            get
            {
                double decodeMs = TotalMs - TtftMs;
                if (!CompletionTokens.HasValue || CompletionTokens.Value <= 0 || decodeMs <= 1)
                {
                    return 0;
                }

                return CompletionTokens.Value / (decodeMs / 1000.0);
            }
        }

        public string HudLine()
        {
            if (!string.IsNullOrEmpty(Error))
            {
                return $"<color=#ff6666>ошибка: {Error}</color>";
            }

            string tok = CompletionTokens.HasValue
                ? $"{(CompletionTokensExact ? "" : "~")}{CompletionTokens} вых" +
                  (PromptTokens.HasValue ? $" · {PromptTokens} вх" : "")
                : "токены n/a";
            string tps = TokensPerSecond > 0 ? $" · {TokensPerSecond:0} ток/с" : "";
            string toolStr = Tools.Count > 0 ? $" · тулы: {string.Join(", ", Tools)}" : " · тулов нет";
            return $"⏱ первый токен {TtftMs:0} мс · ответ {TotalMs:0} мс · {tok}{tps}{toolStr}";
        }
    }

    /// <summary>
    /// Runs one orchestrator turn through the existing CoreAI LLM-for-Unity path and measures it.
    /// The role's tools must already be registered (AgentConfig.ApplyToPolicy) so native tool-calls fire.
    /// Executes through <see cref="CoreAIAgent.Orchestrator"/> — the SAME facade the tools are registered on
    /// (<see cref="CoreAIAgent.Policy"/>), so there is no risk of a two-facade policy mismatch.
    /// </summary>
    public static class LlmMeter
    {
        /// <summary>Both Qwen demos require a native tool call instead of accepting a text-only answer.</summary>
        public static LlmToolChoiceMode ToolChoiceMode => LlmToolChoiceMode.RequireAny;

        public static async Task<LlmRunResult> RunAsync(
            string roleId,
            string hint,
            int maxOutputTokens = 256,
            params string[] allowedToolNames)
        {
            LlmRunResult r = new();
            IAiOrchestrationService orchestrator = CoreAIAgent.Orchestrator;
            if (orchestrator == null)
            {
                r.Error = "CoreAIAgent.Orchestrator is null (CoreAI not initialized).";
                return r;
            }

            Stopwatch sw = Stopwatch.StartNew();
            StringBuilder sb = new();
            LlmStreamChunk last = null;
            IReadOnlyList<LlmToolCallTrace> lastToolCalls = null;

            try
            {
                AiTaskRequest task = new()
                {
                    RoleId = roleId,
                    Hint = hint,
                    MaxOutputTokens = maxOutputTokens,
                    CancellationScope = roleId,
                    ForcedToolMode = ToolChoiceMode
                };

                await foreach (LlmStreamChunk ch in orchestrator.RunStreamingAsync(task, CancellationToken.None))
                {
                    if (!string.IsNullOrEmpty(ch.Error))
                    {
                        r.Error = ch.Error;
                    }

                    if (r.TtftMs <= 0 && !string.IsNullOrEmpty(ch.Text))
                    {
                        r.TtftMs = sw.Elapsed.TotalMilliseconds;
                    }

                    if (!string.IsNullOrEmpty(ch.Text))
                    {
                        sb.Append(ch.Text);
                    }

                    // WHY: Usage and executed tools arrive on the terminal chunk.
                    if (ch.CompletionTokens.HasValue || ch.IsDone)
                    {
                        last = ch;
                    }

                    if (ch.ExecutedToolCalls != null && ch.ExecutedToolCalls.Count > 0)
                    {
                        lastToolCalls = ch.ExecutedToolCalls;
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            r.TotalMs = sw.Elapsed.TotalMilliseconds;
            r.Text = sb.ToString();

            if (last != null)
            {
                r.PromptTokens = last.PromptTokens;
                r.TotalTokens = last.TotalTokens;
                if (last.CompletionTokens.HasValue && last.CompletionTokens.Value > 0)
                {
                    r.CompletionTokens = last.CompletionTokens;
                    r.CompletionTokensExact = true;
                }

                if (lastToolCalls != null)
                {
                    foreach (LlmToolCallTrace t in lastToolCalls)
                    {
                        r.ToolCalls.Add(t);
                        r.Tools.Add($"{t.Name}({t.DurationMs:0}мс)");
                    }
                }
            }

            if (string.IsNullOrEmpty(r.Error))
            {
                r.Error = QwenToolContract.ValidateExactlyOne(r.ToolCalls, allowedToolNames);
            }

            // WHY: Some llama.cpp responses omit usage; a visibly approximate count is better than a blank HUD.
            if (!r.CompletionTokens.HasValue && r.Text.Length > 0)
            {
                r.CompletionTokens = Mathf.Max(1, Mathf.RoundToInt(r.Text.Length / 4f));
                r.CompletionTokensExact = false;
            }

            return r;
        }
    }

    /// <summary>Calculates non-overlapping HUD panels for compact and wide Game views.</summary>
    public static class QwenDemoLayout
    {
        public static void Calculate(float screenWidth, float screenHeight, out Rect top, out Rect log)
        {
            float margin = Mathf.Min(8f, Mathf.Max(0f, Mathf.Min(screenWidth, screenHeight) * 0.02f));
            float gap = Mathf.Min(8f, Mathf.Max(2f, screenHeight * 0.015f));
            float panelWidth = Mathf.Min(560f, Mathf.Max(1f, screenWidth - margin * 2f));
            float availableHeight = Mathf.Max(1f, screenHeight - margin * 2f - gap);
            float desiredLog = Mathf.Clamp(screenHeight * 0.36f, 88f, 202f);
            float logHeight = Mathf.Min(desiredLog, availableHeight * 0.42f);
            float topHeight = Mathf.Max(1f, availableHeight - logHeight);
            top = new Rect(margin, margin, panelWidth, topHeight);
            log = new Rect(margin, margin + topHeight + gap, panelWidth, logHeight);
        }

        public static bool StackActionButtons(float panelWidth) => panelWidth < 430f;
    }

    /// <summary>Validates the hard contract shared by the two ToolsOnly demos.</summary>
    public static class QwenToolContract
    {
        public static string ValidateExactlyOne(
            IReadOnlyList<LlmToolCallTrace> calls,
            IReadOnlyList<string> allowedToolNames)
        {
            int count = calls?.Count ?? 0;
            if (count == 0)
            {
                return "The model returned no tool call; this ToolsOnly turn was rejected.";
            }

            if (count != 1)
            {
                return $"The model returned {count} tool calls; exactly one is required.";
            }

            LlmToolCallTrace call = calls[0];
            if (!call.Success)
            {
                return $"Tool '{call.Name}' failed: {call.Detail}";
            }

            if (allowedToolNames != null && allowedToolNames.Count > 0 &&
                !allowedToolNames.Any(name => string.Equals(name, call.Name, StringComparison.Ordinal)))
            {
                return $"Unexpected tool '{call.Name}'.";
            }

            return null;
        }
    }

    /// <summary>Prevents repeated or parallel tool calls from applying more than one side effect per model turn.</summary>
    public sealed class QwenToolTurnGuard
    {
        private readonly object _gate = new();
        private int _turn;
        private bool _active;
        private bool _claimed;

        public int BeginTurn()
        {
            lock (_gate)
            {
                _turn++;
                _active = true;
                _claimed = false;
                return _turn;
            }
        }

        public bool TryClaim()
        {
            lock (_gate)
            {
                if (!_active || _claimed)
                {
                    return false;
                }

                _claimed = true;
                return true;
            }
        }

        public void EndTurn(int turn)
        {
            lock (_gate)
            {
                if (_turn == turn)
                {
                    _active = false;
                }
            }
        }
    }

    /// <summary>Pure verdict helper that cannot classify repeated tool failures as deterministic success.</summary>
    public static class QwenDeterminismVerdict
    {
        public static bool Passed(int requested, int successful, int failures, int distinctDecisions)
        {
            return requested > 0 && successful == requested && failures == 0 && distinctDecisions == 1;
        }
    }

    /// <summary>Waits for both the native LLMUnity host and its OpenAI-compatible HTTP socket.</summary>
    public static class QwenDemoReadiness
    {
        public static async Task<string> WaitUntilReadyAsync(
            float timeoutSeconds = 120f,
            CancellationToken cancellationToken = default)
        {
            float startedAt = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startedAt < Mathf.Max(1f, timeoutSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                object llm = FindLlmHost();
                if (llm != null)
                {
                    if (Read<bool>(llm, "failed"))
                    {
                        return "LLMUnity reported a native model startup failure.";
                    }

                    int port = Read<int>(llm, "port");
                    if (Read<bool>(llm, "started") && port > 0 &&
                        await ProbeHttpAsync(port, cancellationToken))
                    {
                        return null;
                    }
                }

                await Task.Delay(200, cancellationToken);
            }

            return $"LLMUnity did not become ready within {timeoutSeconds:0} seconds.";
        }

        private static object FindLlmHost()
        {
            Type llmType = null;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                llmType = assembly.GetType("LLMUnity.LLM", false);
                if (llmType != null)
                {
                    break;
                }
            }

            if (llmType == null)
            {
                return null;
            }

            object fallback = null;
            foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(llmType))
            {
                if (candidate is Component component && component.gameObject.scene.isLoaded)
                {
                    fallback ??= candidate;
                    string model = Read<string>(candidate, "model") ?? "";
                    if (model.IndexOf("Qwen3.5-0.8B", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return candidate;
                    }
                }
            }

            return fallback;
        }

        private static T Read<T>(object source, string memberName)
        {
            Type type = source.GetType();
            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && field.GetValue(source) is T fieldValue)
            {
                return fieldValue;
            }

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.GetValue(source) is T propertyValue ? propertyValue : default;
        }

        private static async Task<bool> ProbeHttpAsync(int port, CancellationToken cancellationToken)
        {
            using UnityWebRequest request = new(
                $"http://localhost:{port}/v1/chat/completions", UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 2;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            long status = request.responseCode;
            return status is >= 200 and < 500 && status is not 401 and not 403 and not 404;
        }
    }

    /// <summary>
    /// Drains actions onto the Unity main thread. LLM tool delegates run on MEAI's worker thread, so any
    /// GameObject/Transform work they trigger must be marshaled here (touching Unity APIs off-thread throws).
    /// </summary>
    public sealed class MainThreadPump : MonoBehaviour
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Enqueue(Action action)
        {
            if (action != null)
            {
                _queue.Enqueue(action);
            }
        }

        private void Update()
        {
            while (_queue.TryDequeue(out Action a))
            {
                try
                {
                    a();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[QwenDemo] main-thread action failed: " + ex);
                }
            }
        }
    }

    /// <summary>Compact runtime VFX from primitives (transparent + emissive, self-destructing). Shared by both demos.</summary>
    public static class QwenFx
    {
        public static Material Glow(GameObject go, Color c, float alpha = 1f)
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r == null)
            {
                return null;
            }

            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material m = new Material(sh);
            m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend"))
            {
                m.SetFloat("_Blend", 0f);
            }

            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_EMISSION");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.renderQueue = 3000;
            Color col = c;
            col.a = alpha;
            SetCol(m, col);
            m.SetColor("_EmissionColor", c * 2.2f);
            r.material = m;
            return m;
        }

        private static void SetCol(Material m, Color c)
        {
            m.SetColor(m.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", c);
        }

        private static Color GetCol(Material m)
        {
            return m.GetColor(m.HasProperty("_BaseColor") ? "_BaseColor" : "_Color");
        }

        public static GameObject Prim(PrimitiveType t, Transform parent, Vector3 pos, Vector3 scale, Color c,
            string label = null, bool glow = false, float alpha = 1f)
        {
            GameObject go = GameObject.CreatePrimitive(t);
            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                UnityEngine.Object.Destroy(col);
            }

            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            if (glow)
            {
                Glow(go, c, alpha);
            }
            else
            {
                Paint(go, c);
            }

            if (label != null)
            {
                Label(go.transform, label, Color.white, scale.y * 0.6f + 0.6f);
            }

            return go;
        }

        public static void Paint(GameObject go, Color c)
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r == null)
            {
                return;
            }

            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            r.material = new Material(sh);
            r.material.SetColor(r.material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", c);
        }

        public static TextMesh Label(Transform parent, string text, Color color, float y)
        {
            GameObject lgo = new GameObject("label");
            lgo.transform.SetParent(parent, false);
            lgo.transform.localPosition = Vector3.up * y;
            TextMesh tm = lgo.AddComponent<TextMesh>();
            tm.text = text;
            tm.fontSize = 44;
            tm.characterSize = 0.08f;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            lgo.AddComponent<Billboard>();
            return tm;
        }

        public static void Sparks(MonoBehaviour host, Transform root, Vector3 pos, Color c,
            int count = 14, float speed = 5f, float size = 0.18f, float life = 0.8f)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) * 0.7f + 0.5f;
                GameObject s = Prim(PrimitiveType.Sphere, root, pos, Vector3.one * size, c, null, true);
                host.StartCoroutine(FlyFade(s, dir * speed * UnityEngine.Random.Range(0.6f, 1.2f), 8f, life));
            }
        }

        public static System.Collections.IEnumerator Flash(Transform root, Vector3 pos, Color c,
            float maxSize = 2.2f, float dur = 0.45f)
        {
            GameObject f = Prim(PrimitiveType.Sphere, root, pos, Vector3.one * 0.2f, c, null, true, 0.9f);
            Material m = f.GetComponent<Renderer>().material;
            Color b = GetCol(m);
            float age = 0f;
            while (f != null && age < dur)
            {
                age += Time.deltaTime;
                float k = age / dur;
                f.transform.localScale = Vector3.one * Mathf.Lerp(0.2f, maxSize, k);
                Color col = b;
                col.a = 0.9f * (1f - k);
                SetCol(m, col);
                yield return null;
            }

            if (f != null)
            {
                UnityEngine.Object.Destroy(f);
            }
        }

        public static void Ring(MonoBehaviour host, Transform root, Vector3 pos, Color c,
            float maxRadius = 3.2f, float dur = 0.5f)
        {
            GameObject disc = Prim(PrimitiveType.Cylinder, root,
                new Vector3(pos.x, 0.06f, pos.z), new Vector3(0.4f, 0.03f, 0.4f), c, null, true, 0.85f);
            host.StartCoroutine(RingCo(disc, maxRadius, dur));
        }

        private static System.Collections.IEnumerator RingCo(GameObject disc, float maxRadius, float dur)
        {
            Material m = disc.GetComponent<Renderer>().material;
            Color b = GetCol(m);
            float age = 0f;
            while (disc != null && age < dur)
            {
                age += Time.deltaTime;
                float k = age / dur;
                float rad = Mathf.Lerp(0.4f, maxRadius, k);
                disc.transform.localScale = new Vector3(rad, 0.03f, rad);
                Color col = b;
                col.a = 0.85f * (1f - k);
                SetCol(m, col);
                yield return null;
            }

            if (disc != null)
            {
                UnityEngine.Object.Destroy(disc);
            }
        }

        public static void Beam(MonoBehaviour host, Transform root, Vector3 from, Vector3 to, Color c,
            float width = 0.22f, int flashes = 3)
        {
            Vector3 dir = to - from;
            float len = dir.magnitude;
            GameObject beam = Prim(PrimitiveType.Cube, root, (from + to) * 0.5f,
                new Vector3(width, width, len), c, null, true, 0.95f);
            if (dir.sqrMagnitude > 0.0001f)
            {
                beam.transform.localRotation = Quaternion.LookRotation(dir);
            }

            host.StartCoroutine(BeamCo(beam, flashes));
        }

        private static System.Collections.IEnumerator BeamCo(GameObject beam, int flashes)
        {
            Renderer r = beam.GetComponent<Renderer>();
            for (int i = 0; i < flashes && beam != null; i++)
            {
                r.enabled = true;
                yield return new WaitForSeconds(0.06f);
                if (beam != null)
                {
                    r.enabled = false;
                }

                yield return new WaitForSeconds(0.05f);
            }

            if (beam != null)
            {
                UnityEngine.Object.Destroy(beam);
            }
        }

        public static GameObject Lingering(MonoBehaviour host, Transform root, PrimitiveType t, Vector3 pos,
            Vector3 scale, Color c, float alpha, float grow, float hold, float fade, float spin = 0f)
        {
            GameObject go = Prim(t, root, pos, Vector3.one * 0.05f, c, null, true, alpha);
            host.StartCoroutine(LingerCo(go, scale, alpha, grow, hold, fade, spin));
            return go;
        }

        private static System.Collections.IEnumerator LingerCo(GameObject go, Vector3 scale, float alpha,
            float grow, float hold, float fade, float spin)
        {
            Material m = go.GetComponent<Renderer>().material;
            Color b = GetCol(m);
            float age = 0f;
            while (go != null && age < grow)
            {
                age += Time.deltaTime;
                go.transform.localScale = Vector3.Lerp(Vector3.one * 0.05f, scale, age / grow);
                if (spin != 0f)
                {
                    go.transform.Rotate(Vector3.up, spin * Time.deltaTime);
                }

                yield return null;
            }

            float held = 0f;
            while (go != null && held < hold)
            {
                held += Time.deltaTime;
                if (spin != 0f)
                {
                    go.transform.Rotate(Vector3.up, spin * Time.deltaTime);
                }

                yield return null;
            }

            float f = 0f;
            while (go != null && f < fade)
            {
                f += Time.deltaTime;
                Color col = b;
                col.a = alpha * (1f - f / fade);
                SetCol(m, col);
                yield return null;
            }

            if (go != null)
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        public static System.Collections.IEnumerator MoveTo(Transform t, Vector3 target, float speed)
        {
            while (t != null && (t.localPosition - target).sqrMagnitude > 0.01f)
            {
                t.localPosition = Vector3.MoveTowards(t.localPosition, target, speed * Time.deltaTime);
                yield return null;
            }
        }

        public static System.Collections.IEnumerator Shake(Transform t, float amount = 0.18f, float dur = 0.4f)
        {
            if (t == null)
            {
                yield break;
            }

            Vector3 home = t.localPosition;
            float age = 0f;
            while (t != null && age < dur)
            {
                age += Time.deltaTime;
                t.localPosition = home + (Vector3)UnityEngine.Random.insideUnitCircle * amount * (1f - age / dur);
                yield return null;
            }

            if (t != null)
            {
                t.localPosition = home;
            }
        }

        public static GameObject Bolt(Transform root, Vector3 pos, Color c, float size)
        {
            return Prim(PrimitiveType.Sphere, root, pos, Vector3.one * size, c, null, true);
        }

        private static System.Collections.IEnumerator FlyFade(GameObject go, Vector3 vel, float grav, float life)
        {
            Material m = go.GetComponent<Renderer>().material;
            Color b = GetCol(m);
            Vector3 s0 = go.transform.localScale;
            float age = 0f;
            while (go != null && age < life)
            {
                float dt = Time.deltaTime;
                age += dt;
                vel += Vector3.down * grav * dt;
                go.transform.localPosition += vel * dt;
                float k = age / life;
                go.transform.localScale = s0 * (1f - k);
                Color col = b;
                col.a = 1f - k;
                SetCol(m, col);
                yield return null;
            }

            if (go != null)
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        public static void BuildStage(Color ground)
        {
            if (GameObject.Find("QwenGround") == null)
            {
                GameObject g = GameObject.CreatePrimitive(PrimitiveType.Plane);
                g.name = "QwenGround";
                g.transform.localScale = new Vector3(4f, 1f, 4f);
                Paint(g, ground);
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }

            cam.transform.position = new Vector3(0f, 11f, -10f);
            cam.transform.rotation = Quaternion.Euler(45f, 0f, 0f);
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            cam.clearFlags = CameraClearFlags.SolidColor;

            if (UnityEngine.Object.FindAnyObjectByType<Light>() == null)
            {
                Light l = new GameObject("Sun").AddComponent<Light>();
                l.type = LightType.Directional;
                l.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
                l.intensity = 1.1f;
            }
        }
    }

    /// <summary>Keeps world-space labels facing the camera.</summary>
    public sealed class Billboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            }
        }
    }
}
