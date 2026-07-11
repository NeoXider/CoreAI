#if !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Ai.LuaCs;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Session;
using Lua;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Shared infrastructure for the game-creation benchmark (G1 + G2): a per-scenario environment
    /// (sandboxed Lua executor with named logic slots + a recording world-command executor), a
    /// session-capturing LLM decorator (real token usage, tool-call counts, and the full transcript),
    /// a scenario contract, and a non-throwing coroutine runner that grades each run 0..100 and always
    /// produces a <see cref="ScenarioResult"/> — even on timeout/fault — so one bad scenario never aborts
    /// the suite.
    /// </summary>
    internal static class GameCreationBenchmarkHarness
    {
        // ---------------------------------------------------------------------------------------------
        //  Session-capturing LLM client
        // ---------------------------------------------------------------------------------------------

        /// <summary>One model turn captured at the client boundary.</summary>
        public sealed class CapturedTurn
        {
            public int Index;
            public string User = "";
            public string Assistant = "";
            public readonly List<LlmToolCallTrace> Tools = new();
            public int? PromptTokens;
            public int? CompletionTokens;
            public bool Ok = true;
            public string Error = "";
        }

        /// <summary>
        /// Wraps the real <see cref="ILlmClient"/> to record, per turn, the prompt/answer/tool-calls and
        /// provider token usage. This is the single source of truth for the benchmark's real metrics and
        /// for the "Full model session" transcript in the report.
        /// </summary>
        public sealed class SessionCapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            private string _systemPrompt = "";

            public SessionCapturingLlmClient(ILlmClient inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public readonly List<CapturedTurn> Turns = new();
            public int ToolCalls { get; private set; }
            public int FailedToolCalls { get; private set; }
            public int ProviderPromptTokens { get; private set; }
            public int ProviderCompletionTokens { get; private set; }
            public bool AnyProviderUsage { get; private set; }

            /// <summary>
            /// Wall-clock spent INSIDE the LLM completion calls (prefill + decode), summed across turns.
            /// Excludes tool execution, grading and orchestration. NOTE: this is the full provider-call span,
            /// NOT decode-only, so completion-tokens ÷ this reads LOWER than LM Studio's decode-only tok/s
            /// (LM Studio excludes prefill). See Docs/TOKENS_PER_SEC_FIX_PLAN.md.
            /// </summary>
            public double GenerationMs { get; private set; }

            /// <summary>Number of turns the provider returned as failed (Ok == false with an error).</summary>
            public int FailedTurnCount { get; private set; }

            /// <summary>The first provider error text seen, used to classify a transient failure.</summary>
            public string FirstProviderError { get; private set; } = "";

            /// <summary>True once the model produced any usable output (a successful answer or tool call).</summary>
            public bool AnyUsableOutput { get; private set; }

            public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

            public bool SupportsNativeToolCallingForRole(string agentRoleId)
            {
                return _inner.SupportsNativeToolCallingForRole(agentRoleId);
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                long t0 = Stopwatch.GetTimestamp();
                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                GenerationMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                Record(request, result);
                return result;
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                StringBuilder text = new();
                LlmStreamChunk last = null;
                long t0 = Stopwatch.GetTimestamp();
                await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        text.Append(chunk.Text);
                    }

                    last = chunk;
                    yield return chunk;
                }

                GenerationMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                // ExecutedToolCalls must be carried from the terminal chunk - without it, Record() never
                // sees any tool trace for a streamed turn, so ToolCalls/FailedToolCalls silently stay 0 no
                // matter what the model actually did. This path used to be effectively dead (benchmarks
                // forced non-streaming), which is exactly how the gap went unnoticed.
                Record(request, new LlmCompletionResult
                {
                    Ok = last?.Error == null,
                    Content = text.ToString(),
                    Error = last?.Error ?? "",
                    PromptTokens = last?.PromptTokens,
                    CompletionTokens = last?.CompletionTokens,
                    ExecutedToolCalls = last?.ExecutedToolCalls
                });
            }

            private void Record(LlmCompletionRequest request, LlmCompletionResult result)
            {
                if (!string.IsNullOrEmpty(request?.SystemPrompt))
                {
                    _systemPrompt = request.SystemPrompt;
                }

                CapturedTurn turn = new()
                {
                    Index = Turns.Count + 1,
                    User = request?.UserPayload ?? "",
                    Assistant = result?.Content ?? "",
                    Ok = result?.Ok ?? false,
                    Error = result?.Error ?? "",
                    PromptTokens = result?.PromptTokens,
                    CompletionTokens = result?.CompletionTokens
                };

                if (result?.ExecutedToolCalls != null)
                {
                    foreach (LlmToolCallTrace t in result.ExecutedToolCalls)
                    {
                        turn.Tools.Add(t);
                        ToolCalls++;
                        if (!t.Success)
                        {
                            FailedToolCalls++;
                        }
                    }
                }

                if (result?.PromptTokens.HasValue == true || result?.CompletionTokens.HasValue == true)
                {
                    AnyProviderUsage = true;
                    ProviderPromptTokens += result.PromptTokens ?? 0;
                    ProviderCompletionTokens += result.CompletionTokens ?? 0;
                }

                if (result != null && !result.Ok && !string.IsNullOrEmpty(result.Error))
                {
                    FailedTurnCount++;
                    if (string.IsNullOrEmpty(FirstProviderError))
                    {
                        FirstProviderError = result.Error;
                    }
                }

                if (result is { Ok: true }
                    && (!string.IsNullOrWhiteSpace(result.Content) || (result.ExecutedToolCalls?.Count ?? 0) > 0))
                {
                    AnyUsableOutput = true;
                }

                Turns.Add(turn);
            }

            /// <summary>Concatenated prompt text (system once + every user payload) for BPE estimation.</summary>
            public string PromptTextForEstimate()
            {
                StringBuilder sb = new();
                sb.Append(_systemPrompt).Append('\n');
                foreach (CapturedTurn t in Turns)
                {
                    sb.Append(t.User).Append('\n');
                }

                return sb.ToString();
            }

            /// <summary>
            /// Concatenated assistant output for BPE estimation. Includes the tool calls (name + arguments),
            /// not just prose — in tool-only runs (e.g. the castle) almost all generated tokens are tool-call
            /// JSON, so estimating from assistant text alone would massively undercount decode throughput.
            /// </summary>
            public string CompletionTextForEstimate()
            {
                StringBuilder sb = new();
                foreach (CapturedTurn t in Turns)
                {
                    sb.Append(t.Assistant).Append('\n');
                    foreach (LlmToolCallTrace tool in t.Tools)
                    {
                        sb.Append(tool.Name).Append(' ').Append(tool.Detail ?? "").Append('\n');
                    }
                }

                return sb.ToString();
            }

            public string BuildTranscript(string goal)
            {
                const int MaxContent = 4000;
                const int MaxDetail = 600;
                // The system prompt is not truncated to the same tight budget as per-turn content: it is
                // captured once per scenario (not per turn), so bloat risk is low, and an auditor reading
                // "why did the model behave this way" needs the exact instructions it received, not a
                // clipped preview.
                const int MaxSystemPrompt = 12000;
                StringBuilder sb = new();
                sb.AppendLine("```text");
                sb.AppendLine($"GOAL: {Truncate(goal, MaxContent)}");
                foreach (CapturedTurn t in Turns)
                {
                    sb.AppendLine();
                    sb.AppendLine($"--- turn {t.Index} ---");
                    if (!t.Ok && !string.IsNullOrEmpty(t.Error))
                    {
                        sb.AppendLine($"ERROR: {Truncate(t.Error, MaxDetail)}");
                    }

                    if (!string.IsNullOrWhiteSpace(t.Assistant))
                    {
                        sb.AppendLine($"ASSISTANT: {Truncate(t.Assistant, MaxContent)}");
                    }

                    foreach (LlmToolCallTrace tool in t.Tools)
                    {
                        string detail = string.IsNullOrEmpty(tool.Detail)
                            ? ""
                            : $" — {Truncate(tool.Detail, MaxDetail)}";
                        sb.AppendLine($"TOOL: {tool.Name} ({(tool.Success ? "ok" : "FAIL")}, " +
                                      $"{tool.DurationMs:0}ms, {tool.Source}){detail}");
                    }

                    if (t.PromptTokens.HasValue || t.CompletionTokens.HasValue)
                    {
                        sb.AppendLine($"USAGE: prompt={t.PromptTokens?.ToString() ?? "?"} " +
                                      $"completion={t.CompletionTokens?.ToString() ?? "?"}");
                    }
                }

                // Full system prompt, at the very end of the transcript (after every turn) so the top of
                // the block stays scannable (goal + turns) while the exact instructions that shaped the
                // model's behavior are still one scroll away for a real audit.
                if (!string.IsNullOrWhiteSpace(_systemPrompt))
                {
                    sb.AppendLine();
                    sb.AppendLine("--- system prompt ---");
                    sb.AppendLine(Truncate(_systemPrompt, MaxSystemPrompt));
                }

                sb.AppendLine("```");
                return sb.ToString();
            }

            private static string Truncate(string s, int max)
            {
                if (string.IsNullOrEmpty(s) || s.Length <= max)
                {
                    return s ?? "";
                }

                return s.Substring(0, max) + $"…[+{s.Length - max} chars]";
            }
        }

        // ---------------------------------------------------------------------------------------------
        //  Lua executor with named logic slots (G2 core)
        // ---------------------------------------------------------------------------------------------

        public sealed class BenchmarkLuaExecutor : LuaTool.ILuaExecutor
        {
            public readonly LuaCsSecureEnvironment Sandbox = new();
            public readonly LuaCsApiRegistry Registry = new();
            public readonly LuaCsLogicSlots LogicSlots = new();
            public int ExecutionCount;
            public int FailedExecutions;
            public string LastError = "";
            // Lua-CSharp state persists across chunks so a seeded formula (logic_define) stays installed
            // for later invocations, mirroring the reused MoonSharp Script this replaced.
            private LuaState _state;

            public BenchmarkLuaExecutor()
            {
                LogicSlots.RegisterApis(Registry);
            }

            public void DeclareSlot(string name)
            {
                LogicSlots.DeclareSlot(name);
            }

            public void Seed(string luaCode)
            {
                ExecuteAsync(luaCode, default).GetAwaiter().GetResult();
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken ct)
            {
                try
                {
                    _state ??= Sandbox.Create(Registry);
                    ExecutionCount++;
                    LuaValue[] results = Sandbox.RunChunk(_state, code, cancellationToken: ct);
                    return Task.FromResult(new LuaTool.LuaResult
                    {
                        Success = true,
                        Output = Summarize(results)
                    });
                }
                catch (Exception ex)
                {
                    FailedExecutions++;
                    LastError = ex.Message;
                    return Task.FromResult(new LuaTool.LuaResult { Success = false, Error = ex.Message });
                }
            }

            private static string Summarize(LuaValue[] results)
            {
                if (results == null || results.Length == 0)
                {
                    return "ok";
                }

                LuaValue first = results[0];
                return first.Type == LuaValueType.Nil ? "ok" : first.ToString();
            }

            public bool TryNumber(string slot, out double value, params object[] args)
            {
                return LogicSlots.TryInvokeNumber(slot, out value, args);
            }

            public bool TryBool(string slot, out bool value, params object[] args)
            {
                return LogicSlots.TryInvokeBool(slot, out value, args);
            }

            public bool TryString(string slot, out string value, params object[] args)
            {
                return LogicSlots.TryInvokeString(slot, out value, args);
            }
        }

        // ---------------------------------------------------------------------------------------------
        //  Recording world executor (G1 core)
        // ---------------------------------------------------------------------------------------------

        public sealed class RecordedWorldCommand
        {
            public string Action = "";
            public string TargetName = "";
            public string PrefabKeyOrName = "";
            public string StringValue = "";
            public float X, Y, Z;
            public float FloatValue;
            public float Fx, Fy, Fz;
            public float ScaleX, ScaleY, ScaleZ;
            public bool WorldPositionStays;
            public bool HasPosition, HasRotation, HasScale;
            public bool HasX, HasY, HasZ;
            public bool HasFx, HasFy, HasFz;
        }

        public class RecordingWorldExecutor : ICoreAiWorldCommandExecutor
        {
            public readonly List<RecordedWorldCommand> Commands = new();
            public int InvalidCommandCount { get; private set; }

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                try
                {
                    CoreAiWorldCommandEnvelope env =
                        Newtonsoft.Json.JsonConvert.DeserializeObject<CoreAiWorldCommandEnvelope>(cmd.JsonPayload);
                    if (env == null || string.IsNullOrWhiteSpace(env.action))
                    {
                        InvalidCommandCount++;
                        return true;
                    }

                    // A spawn with no name and no prefab is not a real object — count it as invalid.
                    if (string.Equals(env.action, "spawn", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(env.targetName)
                        && string.IsNullOrWhiteSpace(env.prefabKeyOrName))
                    {
                        InvalidCommandCount++;
                        return true;
                    }

                    RecordedWorldCommand recorded = new()
                    {
                        Action = env.action,
                        TargetName = env.targetName ?? "",
                        PrefabKeyOrName = env.prefabKeyOrName ?? "",
                        StringValue = env.stringValue ?? "",
                        X = env.x,
                        Y = env.y,
                        Z = env.z,
                        FloatValue = env.floatValue,
                        Fx = env.fx,
                        Fy = env.fy,
                        Fz = env.fz,
                        ScaleX = env.scaleX,
                        ScaleY = env.scaleY,
                        ScaleZ = env.scaleZ,
                        WorldPositionStays = env.worldPositionStays,
                        HasPosition = env.hasPosition,
                        HasRotation = env.hasRotation,
                        HasScale = env.hasScale,
                        HasX = env.hasX,
                        HasY = env.hasY,
                        HasZ = env.hasZ,
                        HasFx = env.hasFx,
                        HasFy = env.hasFy,
                        HasFz = env.hasFz
                    };
                    Commands.Add(recorded);
                    OnCommand(recorded);
                }
                catch (Exception ex)
                {
                    InvalidCommandCount++;
                    Debug.LogWarning($"[Benchmark] invalid world command: {ex.Message}");
                }

                return true;
            }

            /// <summary>Hook for subclasses (e.g. the visual executor) to react to a recorded command.</summary>
            protected virtual void OnCommand(RecordedWorldCommand cmd)
            {
            }

            public string[] LastListedAnimations => Array.Empty<string>();
            public List<Dictionary<string, object>> LastListedObjects => new();

            public int Count(string action)
            {
                int n = 0;
                foreach (RecordedWorldCommand c in Commands)
                {
                    if (string.Equals(c.Action, action, StringComparison.OrdinalIgnoreCase))
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// Records commands like the base executor AND instantiates real primitive GameObjects so the
        /// scene can be screenshotted. Recording is identical to the base, so scores never change.
        /// </summary>
        /// <summary>Spins a directional light's azimuth at a fixed elevation — a full circle every 3
        /// minutes — so a recorded build timelapse has moving shadows instead of a static sun.</summary>
        private sealed class LiveSunOrbit : UnityEngine.MonoBehaviour
        {
            private const float DegreesPerSecond = 360f / 180f;

            private void Update()
            {
                transform.Rotate(0f, DegreesPerSecond * UnityEngine.Time.deltaTime, 0f, UnityEngine.Space.World);
            }
        }

        public sealed class VisualBenchmarkWorldExecutor : RecordingWorldExecutor
        {
            private readonly Dictionary<string, UnityEngine.GameObject> _objects =
                new(StringComparer.OrdinalIgnoreCase);

            // Translucent placeholders for expected objects the model never spawned (added at capture time).
            private readonly List<UnityEngine.GameObject> _ghosts = new();

            /// <summary>Object names the scene should contain — drives the ✓/✗ status marker + correctness tint.</summary>
            public readonly HashSet<string> ExpectedNames = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>When true, per-object name labels are not drawn (free-build hero shots stay uncluttered).</summary>
            public bool HideLabels;

            public UnityEngine.Transform Root { get; }
            public int ObjectCount => _objects.Count;

            // Live preview camera + light so the Game view shows the model building the scene in real time
            // (objects pop in as commands stream), instead of staring at an empty view until the final shot.
            private UnityEngine.GameObject _liveCamGo;
            private UnityEngine.GameObject _liveLightGo;
            private UnityEngine.TextMesh _liveModelLabel;

            public VisualBenchmarkWorldExecutor()
            {
                Root = new UnityEngine.GameObject("BenchmarkScene").transform;
                CreateLivePreview();
            }

            private void CreateLivePreview()
            {
                try
                {
                    _liveLightGo = new UnityEngine.GameObject("BenchmarkLivePreviewLight");
                    UnityEngine.Light light = _liveLightGo.AddComponent<UnityEngine.Light>();
                    light.type = UnityEngine.LightType.Directional;
                    light.intensity = 1.3f;
                    // Same shadow tuning as the final "BenchmarkKey" shot light (see CaptureSceneScreenshot):
                    // a fresh AddComponent'd Light defaults to LightShadows.None, and the default bias
                    // peter-pans shadows on these ~1m objects, so the live Game view looked flat too.
                    light.shadows = UnityEngine.LightShadows.Soft;
                    light.shadowStrength = 1f;
                    light.shadowBias = 0.01f;
                    light.shadowNormalBias = 0.2f;
                    _liveLightGo.transform.rotation = UnityEngine.Quaternion.Euler(48f, -32f, 0f);
                    // Orbit the sun azimuth (elevation stays fixed, so the scene never goes dark) for a
                    // more watchable build timelapse — a full 360 deg sweep every 3 minutes.
                    _liveLightGo.AddComponent<LiveSunOrbit>();

                    _liveCamGo = new UnityEngine.GameObject("BenchmarkLivePreviewCamera");
                    UnityEngine.Camera cam = _liveCamGo.AddComponent<UnityEngine.Camera>();
                    cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
                    cam.backgroundColor = new UnityEngine.Color(0.10f, 0.11f, 0.13f);
                    cam.fieldOfView = 50f;
                    cam.nearClipPlane = 0.05f;
                    cam.farClipPlane = 500f;
                    // Frame the -9..9 build volume from a 3/4 angle so objects appear in place as they spawn.
                    cam.transform.position = new UnityEngine.Vector3(14f, 16f, -22f);
                    cam.transform.LookAt(new UnityEngine.Vector3(0f, 2f, 0f));

                    // HUD label rigidly parented to the camera (fixed local transform => fixed screen
                    // position) so a recorded sweep video always shows which model is currently building.
                    UnityEngine.GameObject labelGo = new("BenchmarkLiveModelLabel");
                    labelGo.transform.SetParent(cam.transform, false);
                    labelGo.transform.localPosition = new UnityEngine.Vector3(-3.7f, 2.35f, 6f);
                    labelGo.transform.localRotation = UnityEngine.Quaternion.identity;
                    _liveModelLabel = labelGo.AddComponent<UnityEngine.TextMesh>();
                    _liveModelLabel.text = "";
                    _liveModelLabel.characterSize = 0.035f;
                    _liveModelLabel.fontSize = 60;
                    _liveModelLabel.fontStyle = UnityEngine.FontStyle.Bold;
                    _liveModelLabel.anchor = UnityEngine.TextAnchor.UpperLeft;
                    _liveModelLabel.alignment = UnityEngine.TextAlignment.Left;
                    _liveModelLabel.color = UnityEngine.Color.white;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Benchmark] live preview setup failed: {ex.Message}");
                }
            }

            /// <summary>Turns off the live preview camera + light so they never affect the final screenshot lighting.</summary>
            public void HideLivePreview()
            {
                if (_liveCamGo != null)
                {
                    _liveCamGo.SetActive(false);
                }

                if (_liveLightGo != null)
                {
                    _liveLightGo.SetActive(false);
                }
            }

            /// <summary>Sets the "now benchmarking: model" HUD text shown in the live preview Game view,
            /// so a recorded multi-model sweep always shows which model is currently building.</summary>
            public void SetLiveModelLabel(string modelId)
            {
                if (_liveModelLabel != null)
                {
                    _liveModelLabel.text = string.IsNullOrEmpty(modelId) ? "" : $"Model: {modelId}";
                }
            }

            protected override void OnCommand(RecordedWorldCommand cmd)
            {
                try
                {
                    string action = (cmd.Action ?? "").ToLowerInvariant();
                    string name = (cmd.TargetName ?? "").Trim();
                    if (action == "spawn")
                    {
                        string key = !string.IsNullOrEmpty(name) ? name :
                            !string.IsNullOrWhiteSpace(cmd.PrefabKeyOrName) ? cmd.PrefabKeyOrName.Trim() : "obj";
                        if (!_objects.ContainsKey(key))
                        {
                            bool expected = ExpectedNames.Count == 0 || ExpectedNames.Contains(key);
                            UnityEngine.GameObject go = BuildVisual(
                                key, cmd.PrefabKeyOrName, new UnityEngine.Vector3(cmd.X, cmd.Y, cmd.Z),
                                expected, false, cmd.StringValue, cmd.WorldPositionStays);
                            ApplyInlineTransform(go, cmd, !string.IsNullOrWhiteSpace(cmd.StringValue)
                                && !cmd.WorldPositionStays);
                            _objects[key] = go;
                        }
                    }
                    else if (action == "move" && _objects.TryGetValue(name, out UnityEngine.GameObject mv) &&
                             mv != null)
                    {
                        mv.transform.position = new UnityEngine.Vector3(cmd.X, cmd.Y, cmd.Z);
                    }
                    else if (action == "destroy" && _objects.TryGetValue(name, out UnityEngine.GameObject d))
                    {
                        if (d != null)
                        {
                            UnityEngine.Object.DestroyImmediate(d);
                        }

                        _objects.Remove(name);
                    }
                    else if (action == "set_scale"
                             && _objects.TryGetValue(name, out UnityEngine.GameObject sc) && sc != null
                             && cmd.FloatValue > 0f)
                    {
                        // Honour the model's uniform scale so towers/walls vary in size (natural variety).
                        sc.transform.localScale =
                            UnityEngine.Vector3.one * UnityEngine.Mathf.Clamp(cmd.FloatValue, 0.05f, 50f);
                    }
                    else if (action == "rotate"
                             && _objects.TryGetValue(name, out UnityEngine.GameObject ro) && ro != null)
                    {
                        ro.transform.rotation = UnityEngine.Quaternion.Euler(cmd.Fx, cmd.Fy, cmd.Fz);
                    }
                    else if (action == "change"
                             && _objects.TryGetValue(name, out UnityEngine.GameObject ch) && ch != null)
                    {
                        bool hasParent = TryResolveParent(cmd.StringValue, out UnityEngine.Transform parent);
                        bool localTransform = hasParent && !cmd.WorldPositionStays;
                        if (localTransform)
                        {
                            ch.transform.SetParent(parent, false);
                        }

                        ApplyInlineTransform(ch, cmd, localTransform);
                        if (hasParent && cmd.WorldPositionStays)
                        {
                            ch.transform.SetParent(parent, true);
                        }
                    }
                    else if (action == "set_color"
                             && _objects.TryGetValue(name, out UnityEngine.GameObject col) && col != null
                             && !string.IsNullOrWhiteSpace(cmd.StringValue))
                    {
                        string hex = cmd.StringValue.Trim();
                        if (hex.Length > 0 && hex[0] != '#')
                        {
                            hex = "#" + hex;
                        }

                        if (UnityEngine.ColorUtility.TryParseHtmlString(hex, out UnityEngine.Color parsed))
                        {
                            foreach (UnityEngine.Renderer r in col.GetComponentsInChildren<UnityEngine.Renderer>())
                            {
                                TintRenderer(r, parsed);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Benchmark] visual command failed: {ex.Message}");
                }
            }

            private static void ApplyInlineTransform(
                UnityEngine.GameObject go, RecordedWorldCommand cmd, bool localTransform)
            {
                if (go == null || cmd == null)
                {
                    return;
                }

                if (cmd.HasPosition || cmd.X != 0f || cmd.Y != 0f || cmd.Z != 0f)
                {
                    UnityEngine.Vector3 position = new(cmd.X, cmd.Y, cmd.Z);
                    if (localTransform)
                    {
                        go.transform.localPosition = position;
                    }
                    else
                    {
                        go.transform.position = position;
                    }
                }

                if (cmd.HasRotation || cmd.Fx != 0f || cmd.Fy != 0f || cmd.Fz != 0f)
                {
                    UnityEngine.Quaternion rotation = UnityEngine.Quaternion.Euler(cmd.Fx, cmd.Fy, cmd.Fz);
                    if (localTransform)
                    {
                        go.transform.localRotation = rotation;
                    }
                    else
                    {
                        go.transform.rotation = rotation;
                    }
                }

                if (cmd.FloatValue > 0f || cmd.ScaleX > 0f || cmd.ScaleY > 0f || cmd.ScaleZ > 0f)
                {
                    go.transform.localScale = ResolveScale(cmd);
                }
            }

            private static UnityEngine.Vector3 ResolveScale(RecordedWorldCommand cmd)
            {
                float uniform = cmd.FloatValue > 0f
                    ? UnityEngine.Mathf.Clamp(cmd.FloatValue, 0.01f, 100f)
                    : 1f;
                if (cmd.ScaleX <= 0f && cmd.ScaleY <= 0f && cmd.ScaleZ <= 0f)
                {
                    return UnityEngine.Vector3.one * uniform;
                }

                return new UnityEngine.Vector3(
                    AxisScale(cmd.ScaleX, uniform),
                    AxisScale(cmd.ScaleY, uniform),
                    AxisScale(cmd.ScaleZ, uniform));
            }

            private static float AxisScale(float value, float fallback)
            {
                return value > 0f ? UnityEngine.Mathf.Clamp(value, 0.01f, 100f) : fallback;
            }

            // The model now chooses each object's primitive via prefabKey (cube/sphere/cylinder/capsule/
            // plane); the harness no longer guesses a shape from the object's name. ShapeFor maps that
            // key to a primitive + a sensible per-shape default scale, and HashColor gives each object a
            // stable distinct colour so the scene reads like a prototype instead of identical grey cubes.
            private static (UnityEngine.PrimitiveType prim, UnityEngine.Vector3 scale) ShapeFor(string prefabKey)
            {
                UnityEngine.Vector3 S(float x, float y, float z)
                {
                    return new Vector3(x, y, z);
                }

                if (!CoreAiPrimitiveFactory.TryGetPrimitiveType(
                        prefabKey, out UnityEngine.PrimitiveType prim))
                {
                    prim = UnityEngine.PrimitiveType.Cube;
                }

                switch (prim)
                {
                    case UnityEngine.PrimitiveType.Cylinder:
                        return (prim, S(0.6f, 1.3f, 0.6f));
                    case UnityEngine.PrimitiveType.Capsule:
                        return (prim, S(0.7f, 0.95f, 0.7f));
                    case UnityEngine.PrimitiveType.Plane:
                        return (prim, S(1.2f, 1f, 1.2f));
                    default: // Cube, Sphere
                        return (prim, S(0.9f, 0.9f, 0.9f));
                }
            }

            // Neutral stone for any object the model did not colour itself — colours are the model's call
            // (it tints via set_color). Plane/ground gets a muted earth tone in BuildVisual.
            private static readonly UnityEngine.Color DefaultObjectColor = new(0.62f, 0.63f, 0.67f);

            /// <summary>
            /// Spawns the model-chosen primitive (<paramref name="prefabKey"/>) for <paramref name="key"/>,
            /// tinted a stable hash colour. When <see cref="ExpectedNames"/> is set, unexpected/extra objects
            /// turn red and ghosts grey, and a ✓/✗ status label is drawn (unless <see cref="HideLabels"/>).
            /// </summary>
            private static float Safe(float v)
            {
                return UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(v), 0.05f);
            }

            private UnityEngine.GameObject BuildVisual(
                string key, string prefabKey, UnityEngine.Vector3 pos, bool expected, bool ghost,
                string parentName = "", bool worldPositionStays = false)
            {
                (UnityEngine.PrimitiveType prim, UnityEngine.Vector3 scale) = ShapeFor(prefabKey);
                bool isEmpty = string.Equals(prefabKey?.Trim(), "empty", StringComparison.OrdinalIgnoreCase);
                if (isEmpty)
                {
                    scale = UnityEngine.Vector3.one;
                }
                UnityEngine.GameObject go = isEmpty
                    ? new UnityEngine.GameObject()
                    : UnityEngine.GameObject.CreatePrimitive(prim);
                go.name = ghost ? $"ghost:{key}" : key;
                UnityEngine.Collider col = go.GetComponent<UnityEngine.Collider>();
                if (col != null)
                {
                    UnityEngine.Object.DestroyImmediate(col);
                }

                bool hasParent = TryResolveParent(parentName, out UnityEngine.Transform parent);
                go.transform.SetParent(hasParent ? parent : Root, hasParent && worldPositionStays);
                if (hasParent && !worldPositionStays)
                {
                    go.transform.localPosition = pos;
                }
                else
                {
                    go.transform.position = pos;
                }
                go.transform.localScale = scale;

                UnityEngine.Color objColor =
                    ghost ? new UnityEngine.Color(0.34f, 0.36f, 0.40f) :
                    ExpectedNames.Count > 0 && !expected ? new UnityEngine.Color(0.86f, 0.36f, 0.34f) :
                    prim == UnityEngine.PrimitiveType.Plane ? new UnityEngine.Color(0.42f, 0.46f, 0.40f) :
                    DefaultObjectColor;
                UnityEngine.Renderer rend = go.GetComponent<UnityEngine.Renderer>();
                if (rend != null)
                {
                    TintRenderer(rend, objColor);
                }

                if (HideLabels)
                {
                    return go;
                }

                string mark = ExpectedNames.Count == 0 ? "" : ghost || !expected ? "  ✗" : "  ✓";
                UnityEngine.Color labelColor =
                    ghost ? new UnityEngine.Color(0.72f, 0.74f, 0.78f) :
                    expected || ExpectedNames.Count == 0 ? UnityEngine.Color.white :
                    new UnityEngine.Color(1f, 0.62f, 0.58f);

                UnityEngine.GameObject labelGo = new("Label");
                labelGo.transform.SetParent(go.transform, false);
                // Counter the object's (often non-uniform) scale so the label text is never squashed, and
                // sit it a fixed world distance above the object's top regardless of that scale.
                labelGo.transform.localScale = new UnityEngine.Vector3(
                    1f / Safe(scale.x), 1f / Safe(scale.y), 1f / Safe(scale.z));
                labelGo.transform.localPosition =
                    new UnityEngine.Vector3(0f, (scale.y * 0.5f + 0.5f) / Safe(scale.y), 0f);
                UnityEngine.TextMesh tm = labelGo.AddComponent<UnityEngine.TextMesh>();
                tm.text = (ghost ? key + " (missing)" : key) + mark;
                tm.characterSize = 0.03f;
                tm.fontSize = 80;
                tm.fontStyle = UnityEngine.FontStyle.Bold;
                tm.anchor = UnityEngine.TextAnchor.LowerCenter;
                tm.alignment = UnityEngine.TextAlignment.Center;
                tm.color = labelColor;
                return go;
            }

            private bool TryResolveParent(string parentName, out UnityEngine.Transform parent)
            {
                parent = null;
                if (string.IsNullOrWhiteSpace(parentName)
                    || !_objects.TryGetValue(parentName.Trim(), out UnityEngine.GameObject parentObject)
                    || parentObject == null)
                {
                    return false;
                }

                parent = parentObject.transform;
                return true;
            }

            /// <summary>Adds a faint placeholder for every expected object the model never spawned, so the
            /// picture shows what is MISSING. Cosmetic, runs after grading.</summary>
            public void AddMissingGhosts()
            {
                foreach (UnityEngine.GameObject ghost in _ghosts)
                {
                    if (ghost != null)
                    {
                        UnityEngine.Object.DestroyImmediate(ghost);
                    }
                }

                _ghosts.Clear();

                if (ExpectedNames.Count == 0)
                {
                    return;
                }

                List<string> missing = new();
                foreach (string name in ExpectedNames)
                {
                    if (!_objects.ContainsKey(name))
                    {
                        missing.Add(name);
                    }
                }

                missing.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in missing)
                {
                    _ghosts.Add(BuildVisual(name, "", UnityEngine.Vector3.zero, true, true));
                }
            }

            /// <summary>
            /// Arranges the spawned objects on a tidy grid for the screenshot. Models often spawn everything
            /// at the same coordinates (positions are not graded in G1 — names and count are), which would
            /// stack every cube and label into an unreadable blob; laying them out makes the picture an
            /// "inventory" of what was built. Purely cosmetic — runs after grading, never affects the score.
            /// </summary>
            public void LayoutForCapture()
            {
                List<string> keys = new(_objects.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);

                List<UnityEngine.GameObject> live = new();
                foreach (string k in keys)
                {
                    if (_objects[k] != null)
                    {
                        live.Add(_objects[k]);
                    }
                }

                foreach (UnityEngine.GameObject g in _ghosts)
                {
                    if (g != null)
                    {
                        live.Add(g);
                    }
                }

                int n = live.Count;
                if (n == 0)
                {
                    return;
                }

                int cols = UnityEngine.Mathf.CeilToInt(UnityEngine.Mathf.Sqrt(n));
                int rows = UnityEngine.Mathf.CeilToInt(n / (float)cols);
                const float sp = 1.9f;
                float w = (cols - 1) * sp;
                float d = (rows - 1) * sp;

                for (int i = 0; i < n; i++)
                {
                    int c = i % cols;
                    int r = i / cols;
                    live[i].transform.position =
                        new UnityEngine.Vector3(c * sp - w * 0.5f, 0.5f, r * sp - d * 0.5f);
                }
            }

            public UnityEngine.Bounds ComputeBounds()
            {
                UnityEngine.Bounds b = new(UnityEngine.Vector3.zero, UnityEngine.Vector3.one);
                bool first = true;

                List<UnityEngine.GameObject> all = new(_objects.Values);
                all.AddRange(_ghosts);
                foreach (UnityEngine.GameObject go in all)
                {
                    if (go == null)
                    {
                        continue;
                    }

                    UnityEngine.Renderer r = go.GetComponent<UnityEngine.Renderer>();
                    if (r == null)
                    {
                        continue;
                    }

                    if (first)
                    {
                        b = r.bounds;
                        first = false;
                    }
                    else
                    {
                        b.Encapsulate(r.bounds);
                    }
                }

                return b;
            }

            /// <summary>Rotates every name label to face the capture camera so the text is readable.</summary>
            public void FaceCamera(UnityEngine.Camera cam)
            {
                if (cam == null || Root == null)
                {
                    return;
                }

                foreach (UnityEngine.TextMesh tm in Root.GetComponentsInChildren<UnityEngine.TextMesh>())
                {
                    tm.transform.rotation = UnityEngine.Quaternion.LookRotation(
                        tm.transform.position - cam.transform.position, UnityEngine.Vector3.up);
                }
            }

            public void Cleanup()
            {
                if (_liveCamGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(_liveCamGo);
                }

                if (_liveLightGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(_liveLightGo);
                }

                if (Root != null)
                {
                    UnityEngine.Object.DestroyImmediate(Root.gameObject);
                }

                _objects.Clear();
            }
        }

        // ---------------------------------------------------------------------------------------------
        //  Scenario contract
        // ---------------------------------------------------------------------------------------------

        public sealed class BenchmarkEnvironment
        {
            public BenchmarkEnvironment(ICoreAISettings settings, bool visual = false)
            {
                Settings = settings;
                bool canRender = UnityEngine.SystemInfo.graphicsDeviceType
                                 != UnityEngine.Rendering.GraphicsDeviceType.Null;
                World = visual && canRender ? new VisualBenchmarkWorldExecutor() : new RecordingWorldExecutor();
            }

            public ICoreAISettings Settings { get; }
            public BenchmarkLuaExecutor Lua { get; } = new();
            public RecordingWorldExecutor World { get; }
            public RecordingMemoryStore Memory { get; } = new();

            /// <summary>
            /// When set, the wall-clock instant the current scenario should finish by. The world tool feeds a
            /// live "time remaining" note to the model after each spawn so it can pace itself. Null = no clock.
            /// </summary>
            public DateTime? DeadlineUtc { get; set; }

            /// <summary>Live "X s left" note for tool results, or "" when no deadline is set or it has passed.</summary>
            public string TimeRemainingNote()
            {
                if (DeadlineUtc == null)
                {
                    return "";
                }

                double secsLeft = (DeadlineUtc.Value - DateTime.UtcNow).TotalSeconds;
                if (secsLeft <= 0)
                {
                    return "TIME IS UP — stop building now and finish.";
                }

                return $"~{secsLeft:0}s left to build — keep going, then stop when done.";
            }

            public LuaLlmTool LuaTool()
            {
                return new LuaLlmTool(Lua, Settings, CoreAI.Logging.NullLog.Instance);
            }

            public WorldLlmTool WorldTool()
            {
                return new WorldLlmTool(World, Settings, new NullGameLogger(), TimeRemainingNote);
            }

            /// <summary>
            /// The agent-vision tool (<c>camera_capture</c> etc.) so a scenario can let the model SEE its own
            /// build and refine it. Only meaningful in the visual world executor (which renders real
            /// GameObjects and a preview camera the service can resolve). Returns null otherwise, so a
            /// scenario can gate the tool on <c>CameraTool() != null</c>.
            /// </summary>
            public CoreAI.Vision.CameraLlmTool CameraTool(string agentRoleId)
            {
                if (World is not VisualBenchmarkWorldExecutor)
                {
                    return null;
                }

                return new CoreAI.Vision.CameraLlmTool(
                    new CoreAI.Vision.AgentCameraService(), agentRoleId);
            }
        }

        public sealed class RunObservation
        {
            public int Turns;
            public int ToolCalls;
            public int FailedToolCalls;
            public int InvalidCommands;
            public string FinalText = "";
            public double LatencyMs;

            /// <summary>Wall-clock spent inside LLM calls only (generation), excluding tool execution.</summary>
            public double GenerationMs;

            public bool TimedOut;
            public string Failure = "";
            public FailureAttribution Attribution = FailureAttribution.None;

            /// <summary>Per-turn captured tool calls (for cadence / one-tool-per-turn / forbidden-tool checks).</summary>
            public IReadOnlyList<CapturedTurn> CapturedTurns = Array.Empty<CapturedTurn>();

            /// <summary>Number of model turns whose tool calls include a tool named <paramref name="toolName"/>.</summary>
            public int TurnsUsingTool(string toolName)
            {
                int n = 0;
                foreach (CapturedTurn t in CapturedTurns)
                {
                    foreach (LlmToolCallTrace call in t.Tools)
                    {
                        if (string.Equals(call.Name, toolName, StringComparison.OrdinalIgnoreCase))
                        {
                            n++;
                            break;
                        }
                    }
                }

                return n;
            }

            /// <summary>True when the 1-based turn index <paramref name="turn"/> issued a call to <paramref name="toolName"/>.</summary>
            public bool TurnUsedTool(int turn, string toolName)
            {
                if (turn < 1 || turn > CapturedTurns.Count)
                {
                    return false;
                }

                foreach (LlmToolCallTrace call in CapturedTurns[turn - 1].Tools)
                {
                    if (string.Equals(call.Name, toolName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>Count of turns that issued more than one tool call (for one-tool-per-turn rules).</summary>
            public int TurnsWithMultipleToolCalls()
            {
                int n = 0;
                foreach (CapturedTurn t in CapturedTurns)
                {
                    if (t.Tools.Count > 1)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public sealed class ScenarioGrading
        {
            public readonly List<BenchmarkCheckpoint> Checkpoints = new();
            public readonly List<BenchmarkPenalty> Penalties = new();
            public int? HardCap;
            public double Bonus;

            public void Add(string id, string description, double weight, bool passed,
                bool mandatory = false, string detail = null,
                BenchmarkDimension dimension = BenchmarkDimension.TaskCompletion)
            {
                Checkpoints.Add(new BenchmarkCheckpoint(id, description, weight, passed, mandatory, detail,
                    dimension));
            }

            public void Penalty(string reason, double points)
            {
                Penalties.Add(new BenchmarkPenalty(reason, points));
            }

            /// <summary>
            /// Subtractive instruction-following: adds a compliance checkpoint (InstructionAdherence) that
            /// fails when the constraint is <paramref name="violated"/>, plus an optional per-occurrence
            /// penalty for repeated violations. Use alongside mandatory core-task checkpoints so that
            /// "doing nothing" can never score 100.
            /// </summary>
            public void Constraint(string id, string description, double weight, bool violated,
                double penaltyPerOccurrence = 0, int occurrences = 0)
            {
                Add(id, description, weight, !violated, dimension: BenchmarkDimension.InstructionAdherence,
                    detail: violated ? "violated" : null);
                if (occurrences > 0 && penaltyPerOccurrence > 0)
                {
                    Penalty($"{description} — violated x{occurrences}", penaltyPerOccurrence * occurrences);
                }
            }
        }

        public abstract class GameBenchmarkScenario
        {
            /// <summary>Appended to any Goal that asks the model to <c>logic_define</c> a slot. Careful models
            /// often try to self-verify by calling the slot name directly as a plain Lua global right after
            /// defining it — that always throws "attempt to call a nil value" (a slot is not a real global;
            /// only the harness can invoke it), which was inflating ToolCorrectness failures on models that
            /// double-check their own work. See also G4's equivalent local <c>VerificationNote</c>.</summary>
            public const string LuaVerificationNote =
                "After defining a slot, do not call the slot name directly as a global Lua function to " +
                "self-verify; the benchmark harness invokes registered logic slots with hidden samples.";

            public abstract string Id { get; }
            public abstract string Name { get; }
            public abstract string Group { get; }
            public abstract string Goal { get; }

            public virtual string RoleId => "GameMaster";

            public virtual string SystemPrompt =>
                "You are the GameMaster. Build exactly what the player asks using the available tools. " +
                "Prefer the smallest correct set of tool calls.";

            /// <summary>Token budget for the efficiency bonus (fewer tokens than this earns points).</summary>
            public virtual int TokenBudget => 1500;

            /// <summary>Time budget in ms for the efficiency bonus (faster than this earns points).</summary>
            public virtual double TimeBudgetMs => 25000;

            /// <summary>
            /// Per-agent output token cap. 0 = explicitly UNLIMITED (no max_tokens sent), which is the
            /// suite-wide default: OpenAI-compatible max_tokens counts REASONING tokens too, so any finite
            /// cap silently starves long-thinking models (observed live: glm-5.2 spent a whole 4800-token
            /// G6 cap on thinking — finish_reason=length, zero tool calls, empty scene). The per-scenario
            /// TimeoutSeconds is the real runaway guard; token appetite is priced by the efficiency bonus.
            /// </summary>
            public virtual int MaxOutputTokens => 0;

            /// <summary>Wall-clock timeout for one run of this scenario (seconds). Heavier scenarios override.</summary>
            public virtual float TimeoutSeconds => 200f;

            /// <summary>When true, world commands spawn real GameObjects and a screenshot is captured.</summary>
            public virtual bool CaptureScene => false;

            /// <summary>When true, the screenshot preserves model-authored positions instead of using the grid layout.</summary>
            public virtual bool FreeBuildLayout => false;

            /// <summary>
            /// When true, this run is deliberately excluded from the model's score (<see cref="FailureAttribution.NotGraded"/>) —
            /// still runs, still screenshots, just doesn't feed the aggregate. For a scenario whose prompt
            /// was fully overridden by an operator env var, so the built-in checkpoints no longer describe
            /// the task that was actually asked (see G6's <c>COREAI_BENCHMARK_FREEBUILD_PROMPT</c>).
            /// </summary>
            public virtual bool ExcludeFromScoring => false;

            /// <summary>
            /// Overrides how many times THIS scenario runs, independent of the suite-wide
            /// <c>COREAI_BENCHMARK_REPS</c>. <c>null</c> (default) = inherit the suite repetition count.
            /// A concrete value (typically 1) is for heavy one-off scenarios — visual hero builds (G6) and
            /// comprehensive/integration scenarios (G7) — that should never re-run just because the suite
            /// is averaging cheaper scenarios over several repetitions.
            /// </summary>
            public virtual int? RepsOverride => null;

            /// <summary>
            /// Per-scenario tool-call roundtrip cap, propagated to <see cref="AiTaskRequest.MaxToolCallRoundtrips"/>
            /// (the per-call override always wins over agent/global settings). <c>null</c> = inherit the
            /// benchmark default; <c>0</c> = UNLIMITED (the visual free-build must never be cut off mid-build).
            /// </summary>
            public virtual int? MaxToolCallRoundtripsOverride => null;

            /// <summary>Relative difficulty 1 (easiest) .. 5 (hardest). Drives ordering and the UI indicator.</summary>
            public virtual int Difficulty => 3;

            /// <summary>Object names the scene SHOULD contain — the screenshot tints these green, others red.</summary>
            public virtual IReadOnlyList<string> ExpectedSceneObjectNames => Array.Empty<string>();

            /// <summary>One line describing what this test checks (shown on the screenshot and in the report).</summary>
            public virtual string WhatItChecks => "";

            public virtual void Prepare(BenchmarkEnvironment env)
            {
            }

            public abstract AgentConfig BuildAgent(BenchmarkEnvironment env);

            public abstract ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run);
        }

        // ---------------------------------------------------------------------------------------------
        //  Orchestrator collaborators
        // ---------------------------------------------------------------------------------------------

        /// <summary>Records memory writes so instruction-following scenarios can assert "wrote to memory".</summary>
        public sealed class RecordingMemoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, AgentMemoryState> _states = new();

            /// <summary>Number of durable memory Save() calls observed.</summary>
            public int SaveCount { get; private set; }

            /// <summary>Number of chat-history append calls observed.</summary>
            public int AppendCount { get; private set; }

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return _states.TryGetValue(roleId ?? "", out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                _states[roleId ?? ""] = state;
                SaveCount++;
            }

            public void Clear(string roleId)
            {
                _states.Remove(roleId ?? "");
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                AppendCount++;
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        // ---------------------------------------------------------------------------------------------
        //  Runner
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Drives <paramref name="orch"/> through <see cref="AiOrchestrator.RunStreamingAsync"/> and
        /// discards the chunks — every real observation (turns, tool calls, tokens, transcript) is already
        /// captured identically to the non-streaming path by <c>SessionCapturingLlmClient.CompleteStreamingAsync</c>
        /// (the wrapped <see cref="ILlmClient"/>). This wrapper exists purely so the resulting
        /// <see cref="Task"/> has the exact same Faulted/Canceled/Exception semantics that
        /// <see cref="AiOrchestrator.RunTaskAsync"/> gave <see cref="RunScenario"/> before: an
        /// <c>await foreach</c> with <c>WithCancellation</c> naturally propagates cancellation and any
        /// unhandled exception the same way a faulted/cancelled <c>Task&lt;string&gt;</c> would, so the
        /// rest of <see cref="RunScenario"/>'s polling/classification logic needs no other change.
        /// <para>
        /// Benchmarks must exercise the SAME code path real players/production callers use. Production
        /// CoreAI consumers (e.g. <c>CoreAiChatService</c>) always stream; non-streaming
        /// (<see cref="ILlmClient.CompleteAsync"/>) is a test/automation convenience and must never be the
        /// only path a benchmark measures.
        /// </para>
        /// </summary>
        private static async Task DrainStreamingAsync(
            AiOrchestrator orch, AiTaskRequest request, CancellationToken cancellationToken)
        {
            await foreach (LlmStreamChunk _ in orch.RunStreamingAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                // Intentionally empty: SessionCapturingLlmClient already recorded this turn via the
                // wrapped ILlmClient.CompleteStreamingAsync. We only need to drive the enumerable to
                // completion so this method's Task carries the terminal fault/cancellation state.
            }

            // RunStreamingAsync catches cancellation internally and yields a terminal {IsDone=true,
            // Error="cancelled"} chunk instead of letting OperationCanceledException propagate, so the
            // `await foreach` above completes normally even when cancellationToken fired - unlike the old
            // RunTaskAsync, whose Task ended up Canceled. Re-throw here so this method's Task keeps the
            // same Canceled/RanToCompletion semantics RunScenario's task.IsCanceled check (below) relies on.
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Drives one scenario through the model and reports a graded <see cref="ScenarioResult"/> via
        /// <paramref name="onResult"/>. Never throws on model/timeout/fault — it records the failure and
        /// still grades + reports. Coroutine so it can await the model on the PlayMode loop.
        /// </summary>
        public static IEnumerator RunScenario(
            GameBenchmarkScenario scenario,
            ILlmClient client,
            ICoreAISettings settings,
            ITokenCounter tokenCounter,
            string modelId,
            float timeoutSeconds,
            Action<ScenarioResult> onResult)
        {
            // Scenario setup is harness territory: a throw here is a Framework failure, not a model one.
            BenchmarkEnvironment env = new(settings, scenario.CaptureScene)
            {
                // Tell the world tool when this scenario should finish, so it can feed the model a live
                // countdown after each spawn and pace an open-ended build. Free-build only (FreeBuildLayout,
                // e.g. G6): every world_command result would otherwise carry "keep going, then stop when
                // done" even for a fixed-count instruction-following scenario (e.g. G5's "exactly three
                // actions"), which actively nudges the model toward extra, unwanted spawns.
                DeadlineUtc = scenario.FreeBuildLayout
                    ? DateTime.UtcNow.AddSeconds(timeoutSeconds)
                    : (DateTime?)null
            };
            AgentConfig config = null;
            string setupError = null;
            try
            {
                scenario.Prepare(env);
                if (env.World is VisualBenchmarkWorldExecutor visSetup)
                {
                    foreach (string n in scenario.ExpectedSceneObjectNames)
                    {
                        visSetup.ExpectedNames.Add(n);
                    }

                    // Free-build hero shots (the castle) drop per-object labels — dozens of model-named
                    // objects would overlap into unreadable garble.
                    visSetup.HideLabels = scenario.FreeBuildLayout;
                    visSetup.SetLiveModelLabel(modelId);
                }

                config = scenario.BuildAgent(env);
            }
            catch (Exception ex)
            {
                setupError = ex.Message;
            }

            if (config == null)
            {
                (env.World as VisualBenchmarkWorldExecutor)?.Cleanup();
                onResult?.Invoke(FailedResult(scenario, modelId, FailureAttribution.Framework,
                    $"setup failed: {setupError}"));
                yield break;
            }

            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);

            SessionCapturingLlmClient capture = new(client);
            ListSink sink = new();
            AiOrchestrator orch = new(
                new SoloAuthorityHost(),
                capture,
                sink,
                new SessionTelemetryCollector(),
                new AiPromptComposer(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore()),
                env.Memory,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings);

            Stopwatch sw = Stopwatch.StartNew();
            RunObservation obs = new();

            using CancellationTokenSource cts = new();
            // Streaming, not RunTaskAsync: production callers always stream (see DrainStreamingAsync doc),
            // so the benchmark must exercise that same path rather than the non-streaming convenience.
            Task task = DrainStreamingAsync(orch, new AiTaskRequest
            {
                RoleId = scenario.RoleId,
                SystemPrompt = scenario.SystemPrompt,
                Hint = scenario.Goal,
                // Per-call override always wins over agent/global settings — this is the reliable channel
                // for the visual free-build to run with NO roundtrip cap (0 = unlimited), independent of
                // however the HTTP client's settings were built.
                MaxToolCallRoundtrips = scenario.MaxToolCallRoundtripsOverride
            }, cts.Token);

            // Non-throwing wait: poll, cancel on timeout, give cancellation a grace window — never Assert.
            while (!task.IsCompleted && sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                yield return null;
            }

            if (!task.IsCompleted)
            {
                cts.Cancel();
                double deadline = sw.Elapsed.TotalSeconds + 5.0;
                while (!task.IsCompleted && sw.Elapsed.TotalSeconds < deadline)
                {
                    yield return null;
                }
            }

            sw.Stop();
            obs.LatencyMs = sw.Elapsed.TotalMilliseconds;

            // A cancellation/timeout AFTER the model already built a scene is the build simply ending (the
            // per-scenario time budget or a single orchestrator turn was cancelled while a good scene already
            // exists), not an infrastructure failure: treat it as a CLEAN STOP — grade and screenshot what was
            // built, do NOT set Environment attribution and do NOT trigger the scenario retry (which would wipe
            // the scene and rebuild from scratch). A cancellation/timeout BEFORE anything was built keeps the
            // Environment+retry behaviour. "scene built" is defined exactly as the empty-response clean-stop below.
            bool sceneWasBuilt = env.World.Count("spawn") >= 1 || capture.ToolCalls >= 1;

            if (task.IsFaulted)
            {
                Exception baseEx = task.Exception?.GetBaseException();
                bool cancelStop = sceneWasBuilt
                                  && (baseEx is OperationCanceledException
                                      || baseEx is LlmOperationTimeoutException
                                      || IsCancellationError(baseEx?.Message));
                if (!cancelStop)
                {
                    obs.Attribution = ClassifyException(baseEx);
                    obs.Failure = baseEx?.Message ?? "faulted";
                }
            }
            else if (task.IsCanceled || !task.IsCompleted)
            {
                // The scenario time budget elapsed and we cancelled the orchestrator. If a scene already
                // exists, this is a clean stop — grade what was built, no failure/retry.
                obs.TimedOut = true;
                if (!sceneWasBuilt)
                {
                    obs.Attribution = FailureAttribution.Environment;
                    obs.Failure = "timed out";
                }
            }

            StringBuilder finalText = new();
            foreach (ApplyAiGameCommand item in sink.Items)
            {
                finalText.Append(item.JsonPayload).Append('\n');
            }

            obs.FinalText = finalText.ToString();
            obs.GenerationMs = capture.GenerationMs;
            obs.Turns = capture.Turns.Count;
            obs.CapturedTurns = capture.Turns;
            obs.ToolCalls = capture.ToolCalls;
            // `capture.FailedToolCalls` is the sole source: it counts every LlmToolCallTrace with
            // Success=false, and every real execute_lua invocation that ILuaExecutor.ExecuteAsync ever
            // runs (env.Lua.FailedExecutions) originates from exactly one such trace - there is no
            // scenario-setup Lua seeding that would fail outside a captured turn. Adding
            // env.Lua.FailedExecutions on top used to compensate for MeaiLlmClient dropping
            // ExecutedToolCalls on an empty final response (fixed - see MeaiLlmClient.CompleteAsync);
            // keeping the addition now double-counts every Lua tool failure.
            obs.FailedToolCalls = capture.FailedToolCalls;
            obs.InvalidCommands = env.World.InvalidCommandCount;

            // A mid-build empty/blank response AFTER the model has already built something is the weak
            // model signalling it is done, not an infrastructure failure: a long unbounded conversation
            // eventually yields a turn with no tool call and no visible text ("Empty response from LLM").
            // When a scene already exists (>=1 spawn or >=1 tool call), treat it as a CLEAN STOP — grade
            // and screenshot what was built, and do NOT trigger the scenario retry. A clearly transient
            // transport error (HTTP/crash/timeout) still falls through to the Environment branch below.
            // (sceneWasBuilt is computed above, where the fault/cancel clean-stop also uses it.)
            bool emptyResponseStop = capture.FailedTurnCount > 0
                                     && IsEmptyResponseError(capture.FirstProviderError)
                                     && !LooksTransient(capture.FirstProviderError)
                                     && sceneWasBuilt;

            // Same clean-stop for a cancellation/timeout that surfaced as an Ok=false provider result (rather
            // than a thrown fault): "A task was canceled." after a scene was built is the build ending, not an
            // infrastructure failure. This WINS over the transient/Environment classification below.
            bool cancellationStop = capture.FailedTurnCount > 0
                                    && IsCancellationError(capture.FirstProviderError)
                                    && sceneWasBuilt;

            // A provider/model crash that came back as a failed result (not a thrown fault) — model-load
            // crash, "model has crashed", HTTP 4xx/5xx — is an Environment failure, not a weak model.
            // Classify it (so it is retried and excluded from the model's score) when the error text looks
            // transient OR the run produced no usable output at all despite a failed turn.
            if (!emptyResponseStop && !cancellationStop
                                   && obs.Attribution == FailureAttribution.None && string.IsNullOrEmpty(obs.Failure)
                                   && capture.FailedTurnCount > 0
                                   && (LooksTransient(capture.FirstProviderError) || !capture.AnyUsableOutput))
            {
                obs.Attribution = FailureAttribution.Environment;
                obs.Failure = $"provider error: {capture.FirstProviderError}";
            }

            // Grading is harness territory too: protect the suite from a grader bug.
            ScenarioGrading grading;
            try
            {
                grading = scenario.Grade(env, obs);
            }
            catch (Exception ex)
            {
                (env.World as VisualBenchmarkWorldExecutor)?.Cleanup();
                onResult?.Invoke(FailedResult(scenario, modelId, FailureAttribution.Framework,
                    $"grading failed: {ex.Message}"));
                yield break;
            }

            // Harness-level error penalties on top of the scenario's own checkpoints. Kept small and
            // CAPPED: failed tool calls are already reflected in the ToolCorrectness dimension, and a model
            // that self-corrects (a failed call followed by a successful one) must not be tanked — the
            // scenario's outcome checkpoints are what decide the base.
            if (obs.FailedToolCalls > 0)
            {
                grading.Penalty($"{obs.FailedToolCalls} failed tool call(s)", Math.Min(2 * obs.FailedToolCalls, 8));
            }

            // Invalid (malformed) world commands are a harder error and never a normal recovery step.
            if (obs.InvalidCommands > 0)
            {
                grading.Penalty($"{obs.InvalidCommands} invalid world command(s)",
                    Math.Min(5 * obs.InvalidCommands, 15));
            }

            // An incomplete run (timeout/fault) cannot be a perfect build.
            if (!string.IsNullOrEmpty(obs.Failure))
            {
                grading.HardCap = Math.Min(grading.HardCap ?? 100, 60);
            }

            // Real token usage when the provider reports it; otherwise a labeled BPE estimate. Streaming
            // local backends (e.g. LM Studio) frequently under-report completion usage on tool-call turns,
            // which made decode tok/s read as ~0.3; guard against that by never trusting a provider completion
            // count that falls below a tokenizer estimate of everything the model generated (incl. tool calls).
            int estCompletion = tokenCounter.CountTokens(capture.CompletionTextForEstimate(), modelId);
            int promptTokens, completionTokens;
            bool fromProvider = capture.AnyProviderUsage;
            if (fromProvider)
            {
                promptTokens = capture.ProviderPromptTokens;
                completionTokens = Math.Max(capture.ProviderCompletionTokens, estCompletion);
            }
            else
            {
                promptTokens = tokenCounter.CountTokens(capture.PromptTextForEstimate(), modelId);
                completionTokens = estCompletion;
            }

            double totalTokens = promptTokens + completionTokens;
            GoalScore score = GoalScore.Compute(
                grading.Checkpoints, grading.Penalties, grading.Bonus, grading.HardCap,
                actualTokens: totalTokens, tokenBudget: scenario.TokenBudget,
                actualMs: obs.LatencyMs, timeBudgetMs: scenario.TimeBudgetMs);

            // A genuine crash/timeout attribution always wins — ExcludeFromScoring only downgrades a
            // CLEAN run (obs.Attribution == None) to "ran fine, just not comparable", never masks a
            // real framework/environment failure.
            FailureAttribution attribution = obs.Attribution == FailureAttribution.None && scenario.ExcludeFromScoring
                ? FailureAttribution.NotGraded
                : obs.Attribution;

            ScenarioResult result = new()
            {
                ScenarioId = scenario.Id,
                ScenarioName = scenario.Name,
                Group = scenario.Group,
                ModelId = modelId,
                Score = score,
                Attribution = attribution,
                Checkpoints = grading.Checkpoints,
                Penalties = grading.Penalties,
                Turns = obs.Turns,
                ToolCalls = obs.ToolCalls,
                FailedToolCalls = obs.FailedToolCalls,
                InvalidCommands = obs.InvalidCommands,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ProviderPromptTokens = fromProvider ? capture.ProviderPromptTokens : (int?)null,
                ProviderCompletionTokens = fromProvider ? capture.ProviderCompletionTokens : (int?)null,
                TokensFromProvider = fromProvider,
                LatencyMs = obs.LatencyMs,
                GenerationMs = obs.GenerationMs,
                CostKnown = false,
                TimedOut = obs.TimedOut,
                Failure = obs.Failure,
                SessionTranscript = capture.BuildTranscript(scenario.Goal)
            };

            Debug.Log($"[Benchmark] {scenario.Group}/{scenario.Name}: base={score.Base:0.#} " +
                      $"bonus={score.Bonus:0.#} (eff {score.EfficiencyBonus:0.#}) verdict={score.Classification} " +
                      $"turns={obs.Turns} tools={obs.ToolCalls} err={obs.FailedToolCalls + obs.InvalidCommands} " +
                      $"tok={totalTokens:0}{(fromProvider ? "" : "~")} {obs.LatencyMs:0}ms");

            result.WhatItChecks = scenario.WhatItChecks;

            // Capture a real screenshot of the built scene (world scenarios with a graphics device). The
            // baked header shows the scenario, its score and verdict, so the picture alone says how it did.
            if (env.World is VisualBenchmarkWorldExecutor vis)
            {
                // Capture when anything was built, or when the scenario expected objects (so a total
                // failure still produces a picture full of "missing" ghosts rather than no picture).
                if (scenario.CaptureScene && (vis.ObjectCount > 0 || vis.ExpectedNames.Count > 0))
                {
                    string verdict = score.Classification switch
                    {
                        BenchmarkClassification.Pass => "PASS",
                        BenchmarkClassification.Partial => "PARTIAL",
                        _ => "FAIL"
                    };
                    string header = $"{scenario.Name} — {score.Base:0}/100 {verdict}";
                    // Free-build hero shots get a second stats line showing the real effort behind the scene:
                    // how many tool-calls (build steps) and spawns the model issued, how long generation took,
                    // and how many tokens it cost — so the picture carries its own provenance.
                    string heroStats = null;
                    if (scenario.FreeBuildLayout)
                    {
                        int spawns = env.World.Count("spawn");
                        // Count camera/vision tool calls so the picture itself shows whether the model actually
                        // LOOKED at its build (image-feedback variant) or never used vision at all — "0 looks"
                        // makes an unused vision run obvious at a glance.
                        int cameraCalls = 0;
                        foreach (CapturedTurn capturedTurn in obs.CapturedTurns)
                        {
                            foreach (LlmToolCallTrace tr in capturedTurn.Tools)
                            {
                                if (tr.Name != null &&
                                    tr.Name.StartsWith("camera", System.StringComparison.OrdinalIgnoreCase))
                                {
                                    cameraCalls++;
                                }
                            }
                        }

                        double genSec = obs.GenerationMs / 1000.0;
                        double tokPerSec = genSec > 0.001 ? completionTokens / genSec : 0.0;
                        heroStats = $"{obs.ToolCalls} tool-calls · {spawns} spawns · {cameraCalls} camera looks · " +
                                    $"{genSec:0.#}s gen · {completionTokens} gen tokens" +
                                    $"{(fromProvider ? "" : "~")} · {tokPerSec:0.#} tok/s ({totalTokens:0} total)";
                    }

                    yield return CaptureSceneScreenshot(vis, modelId, header, scenario.WhatItChecks,
                        scenario.FreeBuildLayout, heroStats, png => result.SceneScreenshotPng = png);
                }

#if UNITY_EDITOR
                // Persist the built free-build scene (the castle) as a reusable, inspectable Unity prefab —
                // not just a flat screenshot — labelled with the model that authored it. Free-build only.
                if (scenario.FreeBuildLayout && vis.ObjectCount > 0)
                {
                    SaveCastlePrefab(vis, modelId, scenario.Id, score.Base);
                }
#endif

                // Always tear down the spawned scene, even when no screenshot was taken, so a visual
                // scenario never leaks its GameObjects into the next run.
                vis.Cleanup();
            }

            onResult?.Invoke(result);
        }

#if UNITY_EDITOR
        // Saves the built free-build scene (the castle) as a Unity prefab so the model's work is inspectable
        // and reusable beyond a flat screenshot, labelled with the model that authored it. Per-object colours
        // live in runtime MaterialPropertyBlocks (which do NOT serialize into a prefab), so they are baked into
        // real shared materials first. Editor-only: PrefabUtility / AssetDatabase do not exist in a player, and
        // benchmarks only ever run in the editor. Prefabs land in Assets/Benchmark/<model>/ (git-ignored,
        // OUTSIDE the benchmark package) as "<scenario>.prefab" with a Materials/ subfolder beside them.
        private static void SaveCastlePrefab(
            VisualBenchmarkWorldExecutor vis, string modelId, string scenarioId, double score)
        {
            try
            {
                if (vis == null || vis.Root == null)
                {
                    return;
                }

                UnityEngine.GameObject root = vis.Root.gameObject;

                // Written OUTSIDE the benchmark package (plain Assets/Benchmark/) so generated art never
                // ships with com.neoxider.coreaibenchmark. Per-model layout: GeneratedCastles/<model>/ holds the
                // prefab, with its own Materials/ subfolder.
                const string rootDir = "Assets/Benchmark";
                if (!UnityEditor.AssetDatabase.IsValidFolder(rootDir))
                {
                    UnityEditor.AssetDatabase.CreateFolder("Assets", "Benchmark");
                }

                string modelDir = $"{rootDir}/{FileSafe(modelId)}";
                if (!UnityEditor.AssetDatabase.IsValidFolder(modelDir))
                {
                    UnityEditor.AssetDatabase.CreateFolder(rootDir, FileSafe(modelId));
                }

                string matDir = $"{modelDir}/Materials";
                if (!UnityEditor.AssetDatabase.IsValidFolder(matDir))
                {
                    UnityEditor.AssetDatabase.CreateFolder(modelDir, "Materials");
                }

                // Bake MaterialPropertyBlock colours into real MATERIAL ASSETS (one per unique colour) so the
                // prefab keeps its palette: SaveAsPrefabAsset drops runtime-instance materials (the renderer
                // would come back null / render pink), so the shared material must be a project asset.
                UnityEngine.Shader lit = UnityEngine.Shader.Find("Universal Render Pipeline/Lit")
                                         ?? UnityEngine.Shader.Find("Standard");
                System.Collections.Generic.Dictionary<int, UnityEngine.Material> palette = new();
                UnityEngine.MaterialPropertyBlock mpb = new();
                foreach (UnityEngine.Renderer rend in root.GetComponentsInChildren<UnityEngine.Renderer>(true))
                {
                    if (rend == null)
                    {
                        continue;
                    }

                    rend.GetPropertyBlock(mpb);
                    UnityEngine.Color c = mpb.GetColor(BaseColorId);
                    if (c.a <= 0f)
                    {
                        c = mpb.GetColor(ColorId);
                    }

                    if (c.a <= 0f)
                    {
                        c = rend.sharedMaterial != null ? rend.sharedMaterial.color : UnityEngine.Color.gray;
                    }

                    UnityEngine.Color32 c32 = c;
                    int key = (c32.r << 16) | (c32.g << 8) | c32.b;
                    if (!palette.TryGetValue(key, out UnityEngine.Material mat))
                    {
                        mat = new UnityEngine.Material(lit);
                        if (mat.HasProperty(BaseColorId))
                        {
                            mat.SetColor(BaseColorId, c);
                        }

                        mat.color = c;
                        string matPath = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(
                            $"{matDir}/c_{c32.r:X2}{c32.g:X2}{c32.b:X2}.mat");
                        UnityEditor.AssetDatabase.CreateAsset(mat, matPath);
                        palette[key] = mat;
                    }

                    rend.sharedMaterial = mat;
                    rend.SetPropertyBlock(null);
                }

                // A self-identifying child so the saved prefab always says who built it and how it scored.
                UnityEngine.GameObject label = new($"BuiltBy_{FileSafe(modelId)}__{score:0}of100");
                label.transform.SetParent(root.transform, false);

                string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(
                    $"{modelDir}/{FileSafe(scenarioId)}.prefab");
                UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Benchmark] saved castle prefab (built by {modelId}, {score:0}/100): {path}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Benchmark] castle prefab save failed: {ex.Message}");
            }
        }

        private static string FileSafe(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "unknown";
            }

            System.Text.StringBuilder sb = new(s.Length);
            foreach (char ch in s)
            {
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' ? ch : '_');
            }

            return sb.ToString();
        }
#endif

        // 4K (2160p) output for every report image. The overlay (banner/caption/insets) is either
        // world-space or sized relative to these, so the whole card scales with them.
        private const int ShotWidth = 3840;

        private const int ShotHeight = 2160;

        // 16:9, two per column between the top/bottom bars. Each 960x540 inset spans a quarter of the
        // 4K frame PER AXIS (so 1/16 of the frame by area): at the old 676x380 the insets read as
        // blurry thumbnails on a 4K report (user-reported). Layout check at 3840x2160 (see
        // CompositeInsets): margins 48px, banner 453px, y1 = 1119, y2 = 531 -- the y2 inset's bottom
        // edge sits 531px above the frame bottom, clear of the hero center and the bottom caption bar.
        private const int InsetWidth = 960;
        private const int InsetHeight = 540;

        // Shared by the main and inset scene cameras: a soft daylight sky instead of the old
        // near-black void, so report shots read as daytime scenes.
        private static readonly UnityEngine.Color DaySkyColor = new(0.53f, 0.65f, 0.80f);

        /// <summary>
        /// Frames a camera over the spawned objects, renders to a 4K (3840x2160) RenderTexture with four
        /// inset views composited in — a gate-level close-up and a top-down overview in the RIGHT
        /// column, two close-up zoom shots at different magnifications in the LEFT column — and returns
        /// PNG bytes via <paramref name="onPng"/>. Fully defensive — any failure yields a null
        /// screenshot and never breaks the run.
        /// </summary>
        private static IEnumerator CaptureSceneScreenshot(
            VisualBenchmarkWorldExecutor vis, string model, string header, string subtitle, bool freeBuildLayout,
            string heroStats, Action<byte[]> onPng)
        {
            UnityEngine.GameObject camGo = null;
            UnityEngine.GameObject camBGo = null;
            UnityEngine.GameObject camCGo = null;
            UnityEngine.GameObject camDGo = null;
            UnityEngine.GameObject camEGo = null;
            UnityEngine.GameObject keyGo = null;
            UnityEngine.GameObject fillGo = null;
            UnityEngine.GameObject groundGo = null;
            UnityEngine.Camera cam = null;
            UnityEngine.RenderTexture rt = null;
            UnityEngine.RenderTexture rtB = null;
            UnityEngine.RenderTexture rtC = null;
            UnityEngine.RenderTexture rtD = null;
            UnityEngine.RenderTexture rtE = null;
            UnityEngine.Vector3 sceneCenter = UnityEngine.Vector3.zero;
            float sceneExt = 1.2f;
            UnityEngine.Rendering.AmbientMode prevAmbientMode = UnityEngine.RenderSettings.ambientMode;
            UnityEngine.Color prevAmbientLight = UnityEngine.RenderSettings.ambientLight;

            try
            {
                // Flat daylight ambient for the duration of the capture: with only a directional key,
                // views looking at the shadow side of the build read several stops darker than the hero
                // view — as if each inset had a different sun. A flat ambient floor lifts the unlit
                // faces so every angle reads as the same daytime scene. Restored in finally.
                UnityEngine.RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                UnityEngine.RenderSettings.ambientLight = new UnityEngine.Color(0.42f, 0.46f, 0.53f);

                // Switch off the live preview camera/light so only the capture rig lights the final shot.
                vis.HideLivePreview();

                if (!freeBuildLayout)
                {
                    vis.AddMissingGhosts();
                    vis.LayoutForCapture();
                }

                UnityEngine.Bounds bounds = vis.ComputeBounds();
                float ext = UnityEngine.Mathf.Max(bounds.extents.magnitude, 1.2f);
                sceneCenter = bounds.center;
                sceneExt = ext;

                camGo = new UnityEngine.GameObject("BenchmarkCamera");
                cam = camGo.AddComponent<UnityEngine.Camera>();
                cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
                // Daylight sky, not a void: the report shots must read as DAYTIME (the dark
                // near-black backdrop made every hero image look like a night scene).
                cam.backgroundColor = DaySkyColor;
                cam.fieldOfView = 50f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 500f;
                cam.allowMSAA = true;
                // 1.5x closer than the old (1.7, 1.5, -2.7)*ext framing — free-build scenes sit on a
                // large ground plate that inflates the bounds, which left the actual build tiny in the
                // middle of empty ground. Flipped 180° around the scene center (was +X,-Z): models
                // consistently build castles gate-forward toward -Z, so the old offset photographed
                // every scene from BEHIND (user-reported); from (-X,+Z) the hero shot faces the front.
                cam.transform.position =
                    bounds.center + new UnityEngine.Vector3(-ext * 1.13f, ext * 1.0f, ext * 1.8f);
                cam.transform.LookAt(bounds.center);

                // A grounded floor so the objects sit on a surface instead of floating in a void.
                groundGo = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Quad);
                groundGo.name = "BenchmarkGround";
                DestroyCollider(groundGo);
                groundGo.transform.position =
                    new UnityEngine.Vector3(bounds.center.x, bounds.min.y - 0.02f, bounds.center.z);
                groundGo.transform.rotation = UnityEngine.Quaternion.Euler(90f, 0f, 0f);
                float gsize = UnityEngine.Mathf.Max(bounds.size.x, bounds.size.z) + ext * 4f + 6f;
                groundGo.transform.localScale = new UnityEngine.Vector3(gsize, gsize, 1f);
                UnityEngine.Renderer gr = groundGo.GetComponent<UnityEngine.Renderer>();
                if (gr != null)
                {
                    // Light warm-grey ground for the daytime look — dark enough that white
                    // primitives and their shadows still separate from it.
                    TintRenderer(gr, new UnityEngine.Color(0.47f, 0.49f, 0.45f));
                }

                // Key + cool fill so the cubes read as 3D instead of flat silhouettes. A freshly
                // AddComponent'd Light defaults to LightShadows.None, so without setting it explicitly
                // the "3D" scene renders with no shadows at all — only the key casts (soft), so there is
                // one clean shadow direction instead of the fill adding a second, conflicting one.
                keyGo = new UnityEngine.GameObject("BenchmarkKey");
                UnityEngine.Light key = keyGo.AddComponent<UnityEngine.Light>();
                key.type = UnityEngine.LightType.Directional;
                key.intensity = 1.35f;
                key.color = new UnityEngine.Color(1.0f, 0.96f, 0.87f); // warm afternoon sun
                key.shadows = UnityEngine.LightShadows.Soft;
                // Full strength + a small bias: the default bias is tuned for room/level-scale scenes and
                // "peter-pans" (detaches/hides) the shadow on these ~1m benchmark objects, which was why
                // shadows looked barely-there even with shadows correctly enabled.
                key.shadowStrength = 1f;
                key.shadowBias = 0.01f;
                key.shadowNormalBias = 0.2f;
                // Afternoon-sun elevation: at the old 48 deg the shadows were shorter than the objects
                // (~0.9x height) and read as barely-there in the report; 33 deg throws ~1.5x-height
                // shadows across the ground — the screenshot light is static and independent of the
                // live-preview day/night orbit, so the report always gets "daytime" shadows.
                keyGo.transform.rotation = UnityEngine.Quaternion.Euler(33f, -40f, 0f);

                fillGo = new UnityEngine.GameObject("BenchmarkFill");
                UnityEngine.Light fill = fillGo.AddComponent<UnityEngine.Light>();
                fill.type = UnityEngine.LightType.Directional;
                fill.intensity = 0.7f; // brighter sky-bounce fill for the daytime look
                fill.color = new UnityEngine.Color(0.72f, 0.82f, 1.0f);
                fillGo.transform.rotation = UnityEngine.Quaternion.Euler(-15f, 150f, 0f);

                // Screen-aligned overlay (parented to the camera): a top results bar and a bottom caption
                // bar with solid backdrops, so the header and "what it checks" read as a clean card, not
                // text floating in the scene. Front-parallel quads never skew under perspective.
                const float zb = 1.5f;
                float halfH = zb * UnityEngine.Mathf.Tan(cam.fieldOfView * 0.5f * UnityEngine.Mathf.Deg2Rad);
                float fullW = 2f * halfH * (ShotWidth / (float)ShotHeight);
                UnityEngine.Transform p = cam.transform;
                UnityEngine.Color verdict = VerdictColor(header);

                float topH = 0.62f * halfH;
                float topY = halfH - topH * 0.5f;
                AddQuad(p, new UnityEngine.Vector3(0f, topY, zb), new UnityEngine.Vector2(fullW, topH),
                    new UnityEngine.Color(0.09f, 0.10f, 0.12f));
                AddQuad(p, new UnityEngine.Vector3(0f, halfH - 0.012f, zb - 0.01f),
                    new UnityEngine.Vector2(fullW, 0.024f), verdict);
                // Model name is the headline (so each scene/castle image says which model built it),
                // with the scenario + score + verdict as the line below. Long hyphenated model ids are
                // wrapped (and shrunk) so they never overflow the banner.
                string m = model ?? "";
                float mSize = m.Length > 42 ? 0.0064f : m.Length > 26 ? 0.0086f : 0.0112f;
                AddCameraText(p, WrapModel(m, 30), new UnityEngine.Vector3(0f, halfH - 0.085f, zb - 0.02f),
                    mSize, UnityEngine.Color.white, true);
                AddCameraText(p, WrapText(header ?? "", 58), new UnityEngine.Vector3(0f, halfH - 0.225f, zb - 0.02f),
                    0.0068f, verdict, true);

                // Stats line (free-build hero only): tool-calls · spawns · generation time · tokens, in a
                // muted colour just under the score line, so the effort behind the scene is on the image.
                if (!string.IsNullOrEmpty(heroStats))
                {
                    AddCameraText(p, WrapText(heroStats, 72), new UnityEngine.Vector3(0f, halfH - 0.315f, zb - 0.02f),
                        0.0044f, new UnityEngine.Color(0.78f, 0.82f, 0.88f), false);
                }

                string cap = WrapText(subtitle ?? "", 52);
                if (!string.IsNullOrEmpty(cap))
                {
                    float botH = 0.42f * halfH;
                    float botY = -halfH + botH * 0.5f;
                    AddQuad(p, new UnityEngine.Vector3(0f, botY, zb), new UnityEngine.Vector2(fullW, botH),
                        new UnityEngine.Color(0.09f, 0.10f, 0.12f));
                    AddCameraText(p, cap, new UnityEngine.Vector3(0f, botY, zb - 0.02f),
                        0.0072f, new UnityEngine.Color(0.84f, 0.86f, 0.90f), false);
                }

                rt = new UnityEngine.RenderTexture(ShotWidth, ShotHeight, 24) { antiAliasing = 8 };
                vis.FaceCamera(cam);
                cam.targetTexture = rt;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] screenshot setup failed: {ex.Message}");
            }

            // Frame 1: the main camera (with its banner overlay) renders to rt under the SRP render loop
            // (Camera.Render() is not supported under SRP, so we rely on the normal render loop).
            yield return new UnityEngine.WaitForEndOfFrame();

            UnityEngine.Texture2D texMain = ReadRenderTexture(rt);

            // Hide the main camera (and its parented banner quads) so the two inset cameras capture a clean
            // scene, then render the scene from two other angles for the composite.
            if (camGo != null)
            {
                camGo.SetActive(false);
            }

            try
            {
                // RIGHT column — top slot is a GATE-LEVEL close-up: models build the entrance facing +Z
                // (same convention as the hero camera flip), so stand just outside the front gate, low to
                // the ground, and look through the gap toward the keep. Uses the zoom extent floor so a
                // tiny scene still fits the narrow frustum. Below it stays the top-down overview.
                float gateExt = UnityEngine.Mathf.Max(sceneExt, 2.8f);
                rtB = new UnityEngine.RenderTexture(InsetWidth * 2, InsetHeight * 2, 24) { antiAliasing = 8 };
                camBGo = MakeInsetCamera("BenchmarkCameraB",
                    sceneCenter + new UnityEngine.Vector3(gateExt * 0.06f, gateExt * 0.22f, gateExt * 1.25f),
                    sceneCenter + new UnityEngine.Vector3(0f, gateExt * 0.08f, 0f), rtB, 42f);
                rtC = new UnityEngine.RenderTexture(InsetWidth * 2, InsetHeight * 2, 24) { antiAliasing = 8 };
                camCGo = MakeInsetCamera("BenchmarkCameraC",
                    sceneCenter + new UnityEngine.Vector3(sceneExt * 0.13f, sceneExt * 1.5f, -sceneExt * 0.35f),
                    sceneCenter, rtC);
                // LEFT column — two close-up "zoom" shots at different magnifications (narrow FOV, near,
                // low angle): detail views the wide hero framing can't show (larger models build
                // compositions worth zooming into). The zoom cameras get a HIGHER extent floor than the
                // scene's own 1.2 minimum: at 20-32 deg FOV and these offsets, a tiny scene (a lone 1m
                // cube) would not fit the frustum at all — found by the independent Codex audit.
                float zoomExt = UnityEngine.Mathf.Max(sceneExt, 2.8f);
                rtD = new UnityEngine.RenderTexture(InsetWidth * 2, InsetHeight * 2, 24) { antiAliasing = 8 };
                camDGo = MakeInsetCamera("BenchmarkCameraD",
                    sceneCenter + new UnityEngine.Vector3(zoomExt * 1.0f, zoomExt * 0.4f, -zoomExt * 1.3f),
                    sceneCenter, rtD, 32f);
                rtE = new UnityEngine.RenderTexture(InsetWidth * 2, InsetHeight * 2, 24) { antiAliasing = 8 };
                camEGo = MakeInsetCamera("BenchmarkCameraE",
                    sceneCenter + new UnityEngine.Vector3(-zoomExt * 0.9f, zoomExt * 0.55f, zoomExt * 1.15f),
                    sceneCenter, rtE, 20f);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] inset camera setup failed: {ex.Message}");
            }

            // Frame 2: the inset cameras render their small views.
            yield return new UnityEngine.WaitForEndOfFrame();

            byte[] png = null;
            UnityEngine.Texture2D texB = null;
            UnityEngine.Texture2D texC = null;
            UnityEngine.Texture2D texD = null;
            UnityEngine.Texture2D texE = null;
            try
            {
                if (texMain != null)
                {
                    texB = ReadRenderTextureDownscaled(rtB, InsetWidth, InsetHeight);
                    texC = ReadRenderTextureDownscaled(rtC, InsetWidth, InsetHeight);
                    texD = ReadRenderTextureDownscaled(rtD, InsetWidth, InsetHeight);
                    texE = ReadRenderTextureDownscaled(rtE, InsetWidth, InsetHeight);
                    CompositeInsets(texMain, texB, texC, texD, texE);
                    png = UnityEngine.ImageConversion.EncodeToPNG(texMain);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] screenshot composite failed: {ex.Message}");
                try
                {
                    if (texMain != null)
                    {
                        png = UnityEngine.ImageConversion.EncodeToPNG(texMain);
                    }
                }
                catch
                {
                    // Give up — a null png is handled downstream (report just omits the image).
                }
            }
            finally
            {
                // Unbind + destroy the cameras BEFORE their render textures, otherwise Unity logs
                // "Releasing render texture that is set as Camera.targetTexture!" at Error level, which the
                // test framework treats as a failure.
                if (cam != null)
                {
                    cam.targetTexture = null;
                }

                DestroyGo(camGo);
                DestroyGo(camBGo);
                DestroyGo(camCGo);
                DestroyGo(camDGo);
                DestroyGo(camEGo);
                DestroyTex(texMain);
                DestroyTex(texB);
                DestroyTex(texC);
                DestroyTex(texD);
                DestroyTex(texE);
                DestroyRt(rt);
                DestroyRt(rtB);
                DestroyRt(rtC);
                DestroyRt(rtD);
                DestroyRt(rtE);
                DestroyGo(keyGo);
                DestroyGo(fillGo);
                DestroyGo(groundGo);
                DestroyScratchMeshes();
                UnityEngine.RenderSettings.ambientMode = prevAmbientMode;
                UnityEngine.RenderSettings.ambientLight = prevAmbientLight;
            }

            onPng?.Invoke(png);
        }

        // --- Screenshot composite helpers ---------------------------------------------------------------

        /// <summary>
        /// Reads a render texture downscaled to w×h through a bilinear blit. The inset cameras
        /// render at 2× and land here: the supersampled downscale is what keeps the small side
        /// views crisp in the 4K composite (straight 1:1 renders read as blurry thumbnails —
        /// user-reported).
        /// </summary>
        private static UnityEngine.Texture2D ReadRenderTextureDownscaled(
            UnityEngine.RenderTexture src, int w, int h)
        {
            if (src == null)
            {
                return null;
            }

            UnityEngine.RenderTexture small = UnityEngine.RenderTexture.GetTemporary(w, h, 0);
            try
            {
                UnityEngine.Graphics.Blit(src, small);
                return ReadRenderTexture(small);
            }
            finally
            {
                UnityEngine.RenderTexture.ReleaseTemporary(small);
            }
        }

        private static UnityEngine.Texture2D ReadRenderTexture(UnityEngine.RenderTexture rt)
        {
            if (rt == null)
            {
                return null;
            }

            UnityEngine.RenderTexture prev = UnityEngine.RenderTexture.active;
            UnityEngine.Texture2D tex = null;
            try
            {
                UnityEngine.RenderTexture.active = rt;
                tex = new UnityEngine.Texture2D(rt.width, rt.height, UnityEngine.TextureFormat.RGB24, false);
                tex.ReadPixels(new UnityEngine.Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] render-texture read failed: {ex.Message}");
            }
            finally
            {
                UnityEngine.RenderTexture.active = prev;
            }

            return tex;
        }

        private static UnityEngine.GameObject MakeInsetCamera(
            string name, UnityEngine.Vector3 position, UnityEngine.Vector3 lookAt, UnityEngine.RenderTexture rt,
            float fieldOfView = 50f)
        {
            UnityEngine.GameObject go = new(name);
            UnityEngine.Camera c = go.AddComponent<UnityEngine.Camera>();
            c.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
            c.backgroundColor = DaySkyColor;
            c.fieldOfView = fieldOfView;
            c.nearClipPlane = 0.05f;
            c.farClipPlane = 500f;
            c.allowMSAA = true;
            go.transform.position = position;
            c.transform.LookAt(lookAt);
            c.targetTexture = rt;
            return go;
        }

        // Pastes the four extra views into the hero shot, stacked below the top banner: the gate-level
        // close-up and the top-down overview down the RIGHT column, the two close-up zoom
        // shots down the LEFT column. All metrics scale from the main image dimensions so the layout is
        // resolution-independent; two per column never reaches the bottom caption bar.
        private static void CompositeInsets(
            UnityEngine.Texture2D main, UnityEngine.Texture2D b, UnityEngine.Texture2D c,
            UnityEngine.Texture2D d, UnityEngine.Texture2D e)
        {
            if (main == null)
            {
                return;
            }

            const int iw = InsetWidth;
            const int ih = InsetHeight;
            int margin = UnityEngine.Mathf.RoundToInt(main.height * (16f / 720f));
            int bannerPx = UnityEngine.Mathf.RoundToInt(main.height * (151f / 720f)); // top results bar, kept clear
            int xLeft = margin;
            int xRight = main.width - margin - iw;
            int y1 = main.height - bannerPx - margin - ih;
            int y2 = y1 - margin - ih;
            PasteInset(main, b, xRight, y1, iw, ih); // gate-level close-up (front entrance)
            PasteInset(main, c, xRight, y2, iw, ih); // wide: top-down
            PasteInset(main, d, xLeft, y1, iw, ih); // zoom: close-up
            PasteInset(main, e, xLeft, y2, iw, ih); // zoom: tighter close-up
            main.Apply();
        }

        private static void PasteInset(
            UnityEngine.Texture2D main, UnityEngine.Texture2D inset, int x, int y, int w, int h)
        {
            if (main == null || inset == null || inset.width != w || inset.height != h)
            {
                return;
            }

            int border = UnityEngine.Mathf.Max(3, main.width / 640); // ~6px at 4K, matches the 3px-at-720p look
            int fx = UnityEngine.Mathf.Max(0, x - border);
            int fy = UnityEngine.Mathf.Max(0, y - border);
            int fw = UnityEngine.Mathf.Min(main.width - fx, w + 2 * border);
            int fh = UnityEngine.Mathf.Min(main.height - fy, h + 2 * border);
            UnityEngine.Color[] frame = new UnityEngine.Color[fw * fh];
            UnityEngine.Color frameColor = new(0.04f, 0.05f, 0.06f);
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = frameColor;
            }

            main.SetPixels(fx, fy, fw, fh, frame);

            if (x >= 0 && y >= 0 && x + w <= main.width && y + h <= main.height)
            {
                main.SetPixels(x, y, w, h, inset.GetPixels());
            }
        }

        private static void DestroyTex(UnityEngine.Texture2D tex)
        {
            if (tex != null)
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void DestroyRt(UnityEngine.RenderTexture rt)
        {
            if (rt != null)
            {
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static void DestroyGo(UnityEngine.GameObject go)
        {
            if (go != null)
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Renders a 4K (3840x2160) "model card" — a 6-axis radar of the benchmark dimensions plus a game-fitness
        /// bar per role and the headline score — so two models' cards can be compared at a glance (a strong
        /// model fills the hexagon; a weak one is small and spiky). Suite-level, so all six axes are present.
        /// </summary>
        public static IEnumerator CaptureModelCard(BenchmarkReport report, Action<byte[]> onPng)
        {
            UnityEngine.GameObject camGo = null;
            UnityEngine.Camera cam = null;
            UnityEngine.RenderTexture rt = null;

            try
            {
                camGo = new UnityEngine.GameObject("ModelCardCamera");
                cam = camGo.AddComponent<UnityEngine.Camera>();
                cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
                cam.backgroundColor = new UnityEngine.Color(0.07f, 0.08f, 0.10f);
                cam.fieldOfView = 50f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;
                cam.allowMSAA = true;
                cam.transform.position = UnityEngine.Vector3.zero;
                cam.transform.rotation = UnityEngine.Quaternion.identity;

                UnityEngine.Transform p = cam.transform;
                const float zb = 1.6f;
                float halfH = zb * UnityEngine.Mathf.Tan(cam.fieldOfView * 0.5f * UnityEngine.Mathf.Deg2Rad);

                // --- data ---
                Dictionary<BenchmarkDimension, double> dim = new();
                foreach (DimensionScore d in report.DimensionBreakdown())
                {
                    dim[d.Dimension] = d.Score;
                }

                BenchmarkDimension[] order =
                {
                    BenchmarkDimension.ToolCorrectness, BenchmarkDimension.IntentSequence,
                    BenchmarkDimension.TaskCompletion, BenchmarkDimension.Determinism,
                    BenchmarkDimension.Reasoning, BenchmarkDimension.InstructionAdherence
                };
                string[] axisLabels = { "Tool", "Intent", "Task", "Determ", "Reason", "Instr" };

                RoleFitness.Result fit = RoleFitness.Evaluate(report);

                // --- header ---
                // Long/hyphenated model ids (OpenRouter-style: "qwen3.6-27b-heretic-...-imatrix-max")
                // must never overflow the card at a fixed size, or they run off the frustum edges.
                // Shrink by length like the hero header does, but AddCameraText has no wrap/measure —
                // so a hard truncation is the actual backstop for arbitrarily long ids, not the size floor.
                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                string modelId = report.Metadata.ModelId ?? "";
                if (modelId.Length > 68)
                {
                    modelId = modelId.Substring(0, 65) + "…";
                }

                string headerLine = $"{modelId} — {report.SuiteBaseScore.ToString("0", inv)}/100";
                float headerSize = headerLine.Length > 60 ? 0.0060f
                    : headerLine.Length > 46 ? 0.0080f
                    : headerLine.Length > 34 ? 0.0100f
                    : 0.0135f;
                AddCameraText(p, headerLine,
                    new UnityEngine.Vector3(0f, halfH - 0.085f, zb - 0.05f), headerSize, UnityEngine.Color.white, true);
                bool anyAssessed = false;
                foreach (RoleFitness.RoleScore r in fit.Roles)
                {
                    if (r.Assessed)
                    {
                        anyAssessed = true;
                        break;
                    }
                }

                string fitTxt = anyAssessed
                    ? $"game-fit {fit.Overall.ToString("0.#", inv)}/10 · best {Shorten(fit.BestRole)}"
                    : "partial run";
                // Length varies with PASS/PARTIAL/FAIL digit counts and the best-role name — centered text
                // at a fixed size can overflow BOTH frustum edges for a long combination (e.g. double-digit
                // counts + "Programmer"), same failure mode as the header above — shrink by length too.
                string summaryLine =
                    $"PASS {report.PassCount}  ·  PARTIAL {report.PartialCount}  ·  FAIL {report.FailCount}      {fitTxt}";
                float summarySize = summaryLine.Length > 70 ? 0.0060f
                    : summaryLine.Length > 55 ? 0.0075f
                    : 0.0085f;
                AddCameraText(p, summaryLine,
                    new UnityEngine.Vector3(0f, halfH - 0.185f, zb - 0.05f), summarySize,
                    new UnityEngine.Color(0.62f, 0.66f, 0.72f), false);

                // --- radar (left) ---
                UnityEngine.GameObject radar = new("Radar");
                radar.transform.SetParent(p, false);
                radar.transform.localPosition = new UnityEngine.Vector3(-0.62f, -0.10f, zb);
                radar.transform.localRotation = UnityEngine.Quaternion.identity;
                UnityEngine.Transform rp = radar.transform;
                const float R = 0.40f;

                UnityEngine.Vector2[] dir = new UnityEngine.Vector2[6];
                UnityEngine.Vector2[] rim = new UnityEngine.Vector2[6];
                UnityEngine.Vector2[] data = new UnityEngine.Vector2[6];
                for (int i = 0; i < 6; i++)
                {
                    float ang = (90f - i * 60f) * UnityEngine.Mathf.Deg2Rad;
                    dir[i] = new UnityEngine.Vector2(UnityEngine.Mathf.Cos(ang), UnityEngine.Mathf.Sin(ang));
                    rim[i] = dir[i] * R;
                    double v = dim.TryGetValue(order[i], out double sv) ? sv : 0.0;
                    data[i] = dir[i] * (R * (float)UnityEngine.Mathf.Clamp01((float)v / 100f));
                }

                // Concentric grid rings at 25/50/75/100% + spokes, so the hexagon reads as a real radar.
                UnityEngine.Color gridOuter = new(0.34f, 0.37f, 0.43f);
                UnityEngine.Color gridInner = new(0.22f, 0.24f, 0.29f);
                float[] rings = { 0.25f, 0.5f, 0.75f, 1f };
                foreach (float frac in rings)
                {
                    bool outer = frac > 0.99f;
                    UnityEngine.Color rc = outer ? gridOuter : gridInner;
                    float th = outer ? 0.0035f : 0.002f;
                    for (int i = 0; i < 6; i++)
                    {
                        AddLine(rp, dir[i] * (R * frac), dir[(i + 1) % 6] * (R * frac), 0.02f, th, rc);
                    }
                }

                for (int i = 0; i < 6; i++)
                {
                    AddLine(rp, UnityEngine.Vector2.zero, rim[i], 0.018f, 0.002f, gridInner);
                }

                AddFilledPolygon(rp, data, 0.0f, new UnityEngine.Color(0.26f, 0.56f, 0.92f));
                for (int i = 0; i < 6; i++)
                {
                    AddLine(rp, data[i], data[(i + 1) % 6], -0.01f, 0.005f, new UnityEngine.Color(0.62f, 0.85f, 1f));
                }

                for (int i = 0; i < 6; i++)
                {
                    double v = dim.TryGetValue(order[i], out double sv) ? sv : 0.0;
                    UnityEngine.Vector2 lp = rim[i] * 1.34f;
                    AddCameraText(rp, $"{axisLabels[i]} {v:0}",
                        new UnityEngine.Vector3(lp.x, lp.y, -0.02f), 0.0072f,
                        new UnityEngine.Color(0.80f, 0.84f, 0.90f), false);
                }

                // The headline game-fit number, big, in the dead centre of the radar so it stands out.
                AddQuad(rp, new UnityEngine.Vector3(0f, 0f, -0.03f), new UnityEngine.Vector2(0.27f, 0.175f),
                    new UnityEngine.Color(0.05f, 0.06f, 0.08f));
                string centre = anyAssessed ? fit.Overall.ToString("0.#", inv) : "n/a";
                AddCameraText(rp, centre, new UnityEngine.Vector3(0f, 0.016f, -0.05f), 0.0135f,
                    UnityEngine.Color.white, true);
                AddCameraText(rp, "game-fit /10", new UnityEngine.Vector3(0f, -0.052f, -0.05f), 0.0047f,
                    new UnityEngine.Color(0.66f, 0.70f, 0.76f), false);

                // --- role bars (right) ---
                AddCameraText(p, "Game-fitness by role (0–10)",
                    new UnityEngine.Vector3(0.02f, 0.40f, zb - 0.05f), 0.0085f,
                    new UnityEngine.Color(0.62f, 0.66f, 0.72f), false, UnityEngine.TextAnchor.MiddleLeft);

                // barW is kept short of the ~1.326-unit perspective-frustum half-width so the rating
                // number (up to "10", drawn from barX0+barW+0.02) never gets clipped at the right edge.
                const float barX0 = 0.55f, barW = 0.48f;
                for (int j = 0; j < fit.Roles.Count && j < 6; j++)
                {
                    RoleFitness.RoleScore role = fit.Roles[j];
                    float y = 0.28f - j * 0.125f;
                    AddCameraText(p, Shorten(role.Role), new UnityEngine.Vector3(0.02f, y, zb - 0.05f),
                        0.0078f, UnityEngine.Color.white, false, UnityEngine.TextAnchor.MiddleLeft);

                    AddQuad(p, new UnityEngine.Vector3(barX0 + barW * 0.5f, y, zb),
                        new UnityEngine.Vector2(barW, 0.05f),
                        new UnityEngine.Color(0.16f, 0.17f, 0.20f));

                    if (role.Assessed)
                    {
                        float frac = UnityEngine.Mathf.Clamp01((float)role.Rating / 10f);
                        float fw = UnityEngine.Mathf.Max(barW * frac, 0.004f);
                        AddQuad(p, new UnityEngine.Vector3(barX0 + fw * 0.5f, y, zb - 0.01f),
                            new UnityEngine.Vector2(fw, 0.05f), RatingColor(role.Rating));
                        AddCameraText(p, role.Rating.ToString("0.#", inv),
                            new UnityEngine.Vector3(barX0 + barW + 0.02f, y, zb - 0.05f),
                            0.0078f, UnityEngine.Color.white, true, UnityEngine.TextAnchor.MiddleLeft);
                    }
                    else
                    {
                        AddCameraText(p, "n/a", new UnityEngine.Vector3(barX0 + 0.04f, y, zb - 0.05f),
                            0.0072f, new UnityEngine.Color(0.55f, 0.58f, 0.62f), false,
                            UnityEngine.TextAnchor.MiddleLeft);
                    }
                }

                rt = new UnityEngine.RenderTexture(ShotWidth, ShotHeight, 24) { antiAliasing = 8 };
                cam.targetTexture = rt;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] model-card setup failed: {ex.Message}");
            }

            yield return new UnityEngine.WaitForEndOfFrame();

            byte[] png = null;
            UnityEngine.Texture2D tex = null;
            UnityEngine.RenderTexture prevActive = UnityEngine.RenderTexture.active;
            try
            {
                if (cam != null && rt != null)
                {
                    UnityEngine.RenderTexture.active = rt;
                    tex = new UnityEngine.Texture2D(rt.width, rt.height, UnityEngine.TextureFormat.RGB24, false);
                    tex.ReadPixels(new UnityEngine.Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    png = UnityEngine.ImageConversion.EncodeToPNG(tex);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Benchmark] model-card capture failed: {ex.Message}");
            }
            finally
            {
                UnityEngine.RenderTexture.active = prevActive;
                if (cam != null)
                {
                    cam.targetTexture = null;
                }

                if (tex != null)
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }

                if (rt != null)
                {
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                if (camGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(camGo);
                }

                DestroyScratchMeshes();
            }

            onPng?.Invoke(png);
        }

        private static string Shorten(string role)
        {
            if (string.IsNullOrEmpty(role))
            {
                return "";
            }

            if (role.StartsWith("NPC"))
            {
                return "NPC";
            }

            if (role.StartsWith("Mechanic"))
            {
                return "Mechanic";
            }

            if (role.StartsWith("Scene"))
            {
                return "Tool Op";
            }

            if (role.StartsWith("Programmer"))
            {
                return "Programmer";
            }

            if (role.StartsWith("Orchestrator"))
            {
                return "Director";
            }

            if (role.StartsWith("QA"))
            {
                return "QA";
            }

            return role;
        }

        private static UnityEngine.Color RatingColor(double rating)
        {
            if (rating >= 8.0)
            {
                return new UnityEngine.Color(0.36f, 0.78f, 0.45f);
            }

            if (rating >= 6.5)
            {
                return new UnityEngine.Color(0.55f, 0.80f, 0.40f);
            }

            if (rating >= 4.0)
            {
                return new UnityEngine.Color(0.93f, 0.74f, 0.33f);
            }

            return new UnityEngine.Color(0.88f, 0.42f, 0.40f);
        }

        private static void AddCameraText(UnityEngine.Transform parent, string text,
            UnityEngine.Vector3 localPos, float size, UnityEngine.Color color, bool bold,
            UnityEngine.TextAnchor anchor = UnityEngine.TextAnchor.MiddleCenter)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            UnityEngine.GameObject go = new("OverlayText");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = UnityEngine.Quaternion.identity;
            UnityEngine.TextMesh tm = go.AddComponent<UnityEngine.TextMesh>();
            tm.text = text;
            tm.characterSize = size;
            tm.fontSize = 96;
            tm.anchor = anchor;
            tm.alignment = anchor == UnityEngine.TextAnchor.MiddleLeft
                ? UnityEngine.TextAlignment.Left
                : UnityEngine.TextAlignment.Center;
            tm.fontStyle = bold ? UnityEngine.FontStyle.Bold : UnityEngine.FontStyle.Normal;
            tm.color = color;
        }

        /// <summary>Adds a flat, screen-aligned coloured quad parented to <paramref name="parent"/>.</summary>
        private static void AddQuad(UnityEngine.Transform parent, UnityEngine.Vector3 localPos,
            UnityEngine.Vector2 size, UnityEngine.Color color)
        {
            UnityEngine.GameObject q = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Quad);
            q.name = "OverlayQuad";
            DestroyCollider(q);
            UnityEngine.Renderer r = q.GetComponent<UnityEngine.Renderer>();
            if (r != null)
            {
                r.sharedMaterial = MakeUnlitMaterial(color);
            }

            q.transform.SetParent(parent, false);
            q.transform.localPosition = localPos;
            q.transform.localRotation = UnityEngine.Quaternion.identity;
            q.transform.localScale = new UnityEngine.Vector3(size.x, size.y, 1f);
        }

        private static void DestroyCollider(UnityEngine.GameObject go)
        {
            UnityEngine.Collider col = go.GetComponent<UnityEngine.Collider>();
            if (col != null)
            {
                UnityEngine.Object.DestroyImmediate(col);
            }
        }

        private static readonly int BaseColorId = UnityEngine.Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = UnityEngine.Shader.PropertyToID("_Color");
        private static readonly UnityEngine.MaterialPropertyBlock TintBlock = new();

        private static readonly Dictionary<(UnityEngine.Color color, bool doubleSided), UnityEngine.Material>
            UnlitMaterialCache = new();

        private static readonly List<UnityEngine.Mesh> ScratchMeshes = new();

        /// <summary>Tints a renderer without touching renderer.material, which would instantiate a material.</summary>
        private static void TintRenderer(UnityEngine.Renderer r, UnityEngine.Color c)
        {
            if (r == null)
            {
                return;
            }

            TintBlock.Clear();
            r.GetPropertyBlock(TintBlock);
            TintBlock.SetColor(BaseColorId, c);
            TintBlock.SetColor(ColorId, c);
            r.SetPropertyBlock(TintBlock);
        }

        /// <summary>Tints a material across pipelines — URP/Lit uses <c>_BaseColor</c>, built-in uses <c>_Color</c>.</summary>
        private static void Tint(UnityEngine.Material m, UnityEngine.Color c)
        {
            if (m == null)
            {
                return;
            }

            if (m.HasProperty(BaseColorId))
            {
                m.SetColor(BaseColorId, c);
            }

            if (m.HasProperty(ColorId))
            {
                m.SetColor(ColorId, c);
            }
        }

        /// <summary>An unlit, solid-colour material for overlay bars — works under URP and the built-in pipeline.</summary>
        private static UnityEngine.Material MakeUnlitMaterial(UnityEngine.Color c, bool doubleSided = false)
        {
            (UnityEngine.Color color, bool doubleSided) key = (c, doubleSided);
            if (UnlitMaterialCache.TryGetValue(key, out UnityEngine.Material cached) && cached != null)
            {
                return cached;
            }

            UnityEngine.Shader s = UnityEngine.Shader.Find("Universal Render Pipeline/Unlit")
                                   ?? UnityEngine.Shader.Find("Unlit/Color")
                                   ?? UnityEngine.Shader.Find("Sprites/Default");
            UnityEngine.Material m = new(s);
            Tint(m, c);
            if (doubleSided && m.HasProperty("_Cull"))
            {
                m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            }

            UnlitMaterialCache[key] = m;
            return m;
        }

        /// <summary>A thin coloured line (a quad) from <paramref name="a"/> to <paramref name="b"/> in the parent's local XY.</summary>
        private static void AddLine(UnityEngine.Transform parent, UnityEngine.Vector2 a, UnityEngine.Vector2 b,
            float z, float thickness, UnityEngine.Color color)
        {
            UnityEngine.Vector2 mid = (a + b) * 0.5f;
            UnityEngine.Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1e-5f)
            {
                return;
            }

            float ang = UnityEngine.Mathf.Atan2(d.y, d.x) * UnityEngine.Mathf.Rad2Deg;
            UnityEngine.GameObject q = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Quad);
            q.name = "OverlayLine";
            DestroyCollider(q);
            UnityEngine.Renderer r = q.GetComponent<UnityEngine.Renderer>();
            if (r != null)
            {
                r.sharedMaterial = MakeUnlitMaterial(color);
            }

            q.transform.SetParent(parent, false);
            q.transform.localPosition = new UnityEngine.Vector3(mid.x, mid.y, z);
            q.transform.localRotation = UnityEngine.Quaternion.Euler(0f, 0f, ang);
            q.transform.localScale = new UnityEngine.Vector3(len, thickness, 1f);
        }

        /// <summary>A filled convex polygon (triangle fan from the centroid) in the parent's local XY.</summary>
        private static void AddFilledPolygon(UnityEngine.Transform parent, UnityEngine.Vector2[] pts,
            float z, UnityEngine.Color color)
        {
            if (pts == null || pts.Length < 3)
            {
                return;
            }

            UnityEngine.Vector3[] verts = new UnityEngine.Vector3[pts.Length + 1];
            verts[0] = UnityEngine.Vector3.zero;
            for (int i = 0; i < pts.Length; i++)
            {
                verts[i + 1] = new UnityEngine.Vector3(pts[i].x, pts[i].y, 0f);
            }

            int[] tris = new int[pts.Length * 3];
            for (int i = 0; i < pts.Length; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % pts.Length + 1;
            }

            UnityEngine.Mesh mesh = new() { vertices = verts, triangles = tris };
            mesh.RecalculateBounds();
            ScratchMeshes.Add(mesh);

            UnityEngine.GameObject go = new("OverlayPolygon");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new UnityEngine.Vector3(0f, 0f, z);
            go.transform.localRotation = UnityEngine.Quaternion.identity;
            go.AddComponent<UnityEngine.MeshFilter>().sharedMesh = mesh;
            go.AddComponent<UnityEngine.MeshRenderer>().sharedMaterial = MakeUnlitMaterial(color, true);
        }

        private static void DestroyScratchMeshes()
        {
            foreach (UnityEngine.Mesh mesh in ScratchMeshes)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            ScratchMeshes.Clear();
        }

        /// <summary>Word-wraps text into lines of at most <paramref name="maxChars"/> (TextMesh has no wrapping).</summary>
        private static string WrapText(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }

            StringBuilder sb = new();
            int lineLen = 0;
            foreach (string word in text.Split(' '))
            {
                if (lineLen > 0 && lineLen + 1 + word.Length > maxChars)
                {
                    sb.Append('\n');
                    lineLen = 0;
                }
                else if (lineLen > 0)
                {
                    sb.Append(' ');
                    lineLen++;
                }

                sb.Append(word);
                lineLen += word.Length;
            }

            return sb.ToString();
        }

        /// <summary>Wraps a long model id onto multiple lines, breaking after hyphens/underscores/spaces
        /// (model ids rarely contain spaces), so it never overflows the header banner.</summary>
        private static string WrapModel(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxChars)
            {
                return s;
            }

            StringBuilder sb = new();
            int lineLen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                sb.Append(c);
                lineLen++;
                if (lineLen >= maxChars && (c == '-' || c == '_' || c == ' ') && i < s.Length - 1)
                {
                    sb.Append('\n');
                    lineLen = 0;
                }
            }

            return sb.ToString();
        }

        private static UnityEngine.Color VerdictColor(string header)
        {
            if (string.IsNullOrEmpty(header))
            {
                return UnityEngine.Color.white;
            }

            if (header.EndsWith("PASS", StringComparison.Ordinal))
            {
                return new UnityEngine.Color(0.45f, 0.85f, 0.50f);
            }

            if (header.EndsWith("PARTIAL", StringComparison.Ordinal))
            {
                return new UnityEngine.Color(0.95f, 0.80f, 0.35f);
            }

            if (header.EndsWith("FAIL", StringComparison.Ordinal))
            {
                return new UnityEngine.Color(0.92f, 0.45f, 0.42f);
            }

            return UnityEngine.Color.white;
        }

        /// <summary>
        /// True when a provider error string looks like a transport/infrastructure failure (HTTP 4xx/5xx,
        /// model crash/load failure, timeout, connection, rate limit) rather than a model-quality issue.
        /// </summary>
        private static bool LooksTransient(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            string e = error.ToLowerInvariant();
            string[] signatures =
            {
                "http error", "http 4", "http 5", "status 4", "status 5", "crashed", "has crashed",
                "failed to load model", "model load", "loading model", "timeout", "timed out",
                "connection", "econnrefused", "rate limit", "429", "500", "502", "503", "504",
                "unavailable", "overloaded", "no healthy"
            };

            foreach (string s in signatures)
            {
                if (e.Contains(s))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when a failed turn carried an empty/blank-response error (<see cref="LlmErrorCode.EmptyResponse"/>,
        /// surfaced as "Empty response from LLM"). For a weak model on a long free-build this means "I'm done",
        /// not an infrastructure fault — see the empty-response clean-stop in RunScenario.
        /// </summary>
        private static bool IsEmptyResponseError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            return error.IndexOf("empty response", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True when an error string looks like a cooperative cancellation / time-budget cutoff
        /// ("A task was canceled.", "timed out", "timeout", …). For a model that has ALREADY built a scene
        /// this means the build ran out of time, not an infrastructure fault — see the cancellation clean-stop
        /// in RunScenario. Mirrors <see cref="IsEmptyResponseError"/>.
        /// </summary>
        private static bool IsCancellationError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            string e = error.ToLowerInvariant();
            string[] signatures = { "task was canceled", "canceled", "cancelled", "timed out", "timeout" };
            foreach (string s in signatures)
            {
                if (e.Contains(s))
                {
                    return true;
                }
            }

            return false;
        }

        private static FailureAttribution ClassifyException(Exception ex)
        {
            switch (ex)
            {
                case null:
                    return FailureAttribution.None;
                case LlmOperationTimeoutException:
                case OperationCanceledException:
                case LlmClientException:
                    // Transport / provider / model-load / cancellation — not the harness's fault.
                    return FailureAttribution.Environment;
                default:
                    // NullRef / InvalidOperation / Argument from our own code.
                    return FailureAttribution.Framework;
            }
        }

        private static ScenarioResult FailedResult(
            GameBenchmarkScenario scenario, string modelId, FailureAttribution attribution, string failure)
        {
            GoalScore zero = GoalScore.Compute(Array.Empty<BenchmarkCheckpoint>());
            return new ScenarioResult
            {
                ScenarioId = scenario.Id,
                ScenarioName = scenario.Name,
                Group = scenario.Group,
                ModelId = modelId,
                Score = zero,
                Attribution = attribution,
                Failure = failure,
                SessionTranscript = $"```text\n{failure}\n```"
            };
        }
    }
}
#endif
#endif
