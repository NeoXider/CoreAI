using System;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// SendMessage-compatible bridge for driving <see cref="CoreAiChatPanel"/> from host JavaScript
    /// in a WebGL player (or from any external harness that only has string entry points).
    /// Synthetic DOM events never reach Unity 6's Input System, so browser-side automation cannot
    /// click the chat UI; this driver is the supported way to submit a prompt from page JS:
    /// <c>unityInstance.SendMessage('CoreAiChatExternalDriver', 'SubmitPrompt', 'text')</c>.
    /// The turn result is logged to the console (and the browser console in WebGL), so headless
    /// harnesses can assert on the log stream alone.
    ///
    /// The driver spawns itself ONLY when the page URL contains <c>coreai-external-driver=1</c>
    /// (WebGL) or the environment variable <c>COREAI_EXTERNAL_DRIVER=1</c> is set (other players),
    /// so shipping builds are unaffected unless explicitly opted in.
    /// </summary>
    public sealed class CoreAiChatExternalDriver : MonoBehaviour
    {
        /// <summary>Fixed GameObject name so SendMessage targeting never guesses.</summary>
        public const string DriverObjectName = "CoreAiChatExternalDriver";

        private const string LogPrefix = "[CoreAiChatExternalDriver]";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawnWhenOptedIn()
        {
            if (!IsOptedIn())
            {
                return;
            }

            GameObject go = new(DriverObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<CoreAiChatExternalDriver>();
            Logging.Log.Instance.Info($"{LogPrefix} spawned (opt-in flag detected). " +
                                      "Submit prompts via SendMessage('" + DriverObjectName +
                                      "', 'SubmitPrompt', text).");
        }

        private static bool IsOptedIn()
        {
            string url = Application.absoluteURL ?? string.Empty;
            if (url.IndexOf("coreai-external-driver=1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            try
            {
                return Environment.GetEnvironmentVariable("COREAI_EXTERNAL_DRIVER") == "1";
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Logs every active Graphic/Renderer whose shader reports unsupported on this device -
        /// exactly the objects the player renders as magenta. SendMessage-compatible (arg unused).
        /// </summary>
        public void DumpUnsupportedShaders(string _ = null)
        {
            int hits = 0;
            foreach (UnityEngine.UI.Graphic g in FindObjectsByType<UnityEngine.UI.Graphic>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                Material m = g.materialForRendering;
                if (m != null && m.shader != null && !m.shader.isSupported)
                {
                    hits++;
                    Logging.Log.Instance.Warn(
                        $"{LogPrefix} [shader-diag] UNSUPPORTED {BuildPath(g.transform)} mat={m.name} shader={m.shader.name}");
                }
            }

            foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && m.shader != null && !m.shader.isSupported)
                    {
                        hits++;
                        Logging.Log.Instance.Warn(
                            $"{LogPrefix} [shader-diag] UNSUPPORTED {BuildPath(r.transform)} mat={m.name} shader={m.shader.name}");
                    }
                }
            }

            Logging.Log.Instance.Info($"{LogPrefix} [shader-diag] done, unsupported={hits}");

            static string BuildPath(Transform t)
            {
                return t.parent == null ? t.name : BuildPath(t.parent) + "/" + t.name;
            }
        }

        /// <summary>
        /// Staged Lua diagnostic for the WebGL "RuntimeError: null function" trap (no LLM involved).
        /// Runs six escalating stages (bare Script → sandbox → host callback → Full unity_* bindings),
        /// logging before/after each; the last "stage-N: begin" without a matching "ok" pinpoints the
        /// faulting layer even though a wasm trap halts the player. SendMessage-compatible (arg unused).
        /// </summary>
        public void RunLuaDiag(string _ = null)
        {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
            void Stage(string name, Action body)
            {
                Logging.Log.Instance.Info($"{LogPrefix} [diag] {name}: begin");
                try
                {
                    body();
                    Logging.Log.Instance.Info($"{LogPrefix} [diag] {name}: ok");
                }
                catch (Exception ex)
                {
                    Logging.Log.Instance.Warn(
                        $"{LogPrefix} [diag] {name}: MANAGED-FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            probe.name = "LuaDiagCube";
            try
            {
                MoonSharp.Interpreter.Script bare = null;
                Stage("s1-new-Script", () =>
                {
                    bare = new MoonSharp.Interpreter.Script(
                        MoonSharp.Interpreter.CoreModules.Preset_HardSandbox |
                        MoonSharp.Interpreter.CoreModules.Coroutine);
                });

                Stage("s2-bare-DoString", () =>
                {
                    MoonSharp.Interpreter.DynValue r = bare.DoString("return 1+1");
                    Logging.Log.Instance.Info($"{LogPrefix} [diag] s2 result={r.Number}");
                });

                Sandbox.SecureLuaEnvironment env = new();
                Stage("s3-sandbox-RunChunk", () =>
                {
                    MoonSharp.Interpreter.Script s = env.CreateScript(null);
                    MoonSharp.Interpreter.DynValue r = env.RunChunk(s, "return 2+2");
                    Logging.Log.Instance.Info($"{LogPrefix} [diag] s3 result={r.Number}");
                });

                Stage("s4-host-callback", () =>
                {
                    Sandbox.LuaApiRegistry reg = new();
                    reg.Register("host_add", (Func<double, double, double>)((a, b) => a + b));
                    MoonSharp.Interpreter.Script s = env.CreateScript(reg);
                    MoonSharp.Interpreter.DynValue r = env.RunChunk(s, "return host_add(2,3)");
                    Logging.Log.Instance.Info($"{LogPrefix} [diag] s4 result={r.Number}");
                });

                Stage("s5-unity_find", () =>
                {
                    Sandbox.LuaApiRegistry reg = new();
                    new Infrastructure.Lua.CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(reg);
                    MoonSharp.Interpreter.Script s = env.CreateScript(reg);
                    MoonSharp.Interpreter.DynValue r = env.RunChunk(s, "return unity_find('LuaDiagCube')");
                    Logging.Log.Instance.Info($"{LogPrefix} [diag] s5 id={r.Number}");
                });

                Stage("s6-unity_set_scale", () =>
                {
                    Sandbox.LuaApiRegistry reg = new();
                    new Infrastructure.Lua.CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(reg);
                    MoonSharp.Interpreter.Script s = env.CreateScript(reg);
                    env.RunChunk(s, "local id = unity_find('LuaDiagCube'); unity_set_scale(id, 2, 2, 2)");
                    Logging.Log.Instance.Info($"{LogPrefix} [diag] s6 scale={probe.transform.localScale}");
                });

                Logging.Log.Instance.Info($"{LogPrefix} [diag] ALL STAGES PASSED");
            }
            finally
            {
                Destroy(probe);
            }
#else
            Logging.Log.Instance.Warn($"{LogPrefix} RunLuaDiag ignored: Lua module not compiled in.");
#endif
        }

        /// <summary>
        /// Switches the LLM backend at runtime via <see cref="CoreAI.CoreAiBackend"/>.
        /// SendMessage-compatible: takes one JSON string
        /// <c>{"baseUrl":"...","apiKey":"...","model":"...","maxTokens":4096,"temperature":0.7,"timeoutSeconds":120}</c>
        /// (the last three optional; providers like Groq count <c>max_tokens</c> toward per-minute
        /// token limits, so capping it per backend matters) and calls
        /// <c>CoreAiBackend.ApplyHttpApi</c>. The outcome is logged (<c>backend-applied</c> /
        /// <c>backend-failed</c>); the API key is never echoed.
        /// </summary>
        public void ApplyBackendJson(string json)
        {
            try
            {
                Newtonsoft.Json.Linq.JObject o = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}");
                string baseUrl = (string)o["baseUrl"] ?? "";
                string apiKey = (string)o["apiKey"] ?? "";
                string model = (string)o["model"] ?? "";
                float? temperature = (float?)o["temperature"];
                int? timeoutSeconds = (int?)o["timeoutSeconds"];
                int? maxTokens = (int?)o["maxTokens"];
                bool hotSwapped = CoreAiBackend.ApplyHttpApi(
                    baseUrl, apiKey, model, temperature, timeoutSeconds, maxTokens);
                Logging.Log.Instance.Info($"{LogPrefix} backend-applied: baseUrl={baseUrl} model={model} " +
                                          $"maxTokens={(maxTokens.HasValue ? maxTokens.Value.ToString() : "keep")} " +
                                          $"hotSwapped={hotSwapped} keyLen={apiKey.Length}");
            }
            catch (Exception ex)
            {
                Logging.Log.Instance.Warn($"{LogPrefix} backend-failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Submits <paramref name="prompt"/> to the first active <see cref="CoreAiChatPanel"/> in the
        /// scene through the same turn pipeline the UI uses (streaming, tool calls, history).
        /// SendMessage-compatible: one string parameter, void return; completion is reported via logs
        /// (<c>turn-complete</c> with the assistant text, or <c>turn-failed</c>).
        /// </summary>
        public void SubmitPrompt(string prompt)
        {
            CoreAiChatPanel panel = FindFirstObjectByType<CoreAiChatPanel>();
            if (panel == null)
            {
                Logging.Log.Instance.Warn($"{LogPrefix} SubmitPrompt ignored: no CoreAiChatPanel in the scene.");
                return;
            }

            Logging.Log.Instance.Info(
                $"{LogPrefix} submitting prompt ({prompt?.Length ?? 0} chars) to '{panel.name}'.");
            SubmitAndReportAsync(panel, prompt).Forget();
        }

        private static async Task SubmitAndReportAsync(CoreAiChatPanel panel, string prompt)
        {
            try
            {
                string response = await panel.SubmitMessageFromExternalAsync(prompt);
                if (response == null)
                {
                    Logging.Log.Instance.Warn($"{LogPrefix} turn-failed: panel busy, canceled, or empty input.");
                }
                else
                {
                    Logging.Log.Instance.Info($"{LogPrefix} turn-complete ({response.Length} chars): {response}");
                }
            }
            catch (Exception ex)
            {
                Logging.Log.Instance.Warn($"{LogPrefix} turn-failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal static class CoreAiChatExternalDriverTaskExtensions
    {
        /// <summary>Fire-and-forget with observed exceptions (avoids unobserved-task noise).</summary>
        public static void Forget(this Task task)
        {
            task.ContinueWith(
                t => Logging.Log.Instance.Warn(
                    $"[CoreAiChatExternalDriver] unobserved failure: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}