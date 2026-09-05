using UnityEngine;
#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Demos.Shared;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using UnityEngine.SceneManagement;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// Demo driver for the "Director AI" ambient pattern: an agent with NO chat box. On a timer it
    /// gathers a compact world snapshot (player position, tracked object counts), sends it to a
    /// game-director agent built with <c>AgentBuilder</c>, and lets the model either act through its
    /// tools (<c>world_command</c> wraps the standard world-command executor) or reply with a short
    /// directive. Replying <c>PASS</c> means "nothing needed right now".
    /// Requires a configured LLM backend (CoreAISettings: LLMUnity model or HTTP API).
    /// </summary>
    public sealed class DirectorAiDemoController : MonoBehaviour
    {
#if COREAI_LLM
        private const string PassDirective = "PASS";
        private const float ActionWindowSeconds = 60f;

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [Tooltip("Seconds between world observations sent to the director.")]
        [SerializeField]
        private float observationIntervalSeconds = 20f;

        [Tooltip("Agent role id registered for the director in CoreAIAgent.Policy.")]
        [SerializeField]
        private string directorRoleId = "Director";

        [Tooltip("Per-reply output token budget for the director. 0 = unlimited.")]
        [SerializeField]
        private int maxOutputTokens = 256;

        [Tooltip("Master switch: when off, no observations are sent (component stays alive).")]
        [SerializeField]
        private bool directorEnabled = true;

        [Tooltip("Hard cap on director requests within any rolling 60s window. 0 = uncapped.")]
        [SerializeField]
        private int maxActionsPerMinute = 3;

        [Tooltip(
            "Optional tags whose object counts are added to every observation (must exist in the Tag Manager), e.g. Enemy, Pickup.")]
        [SerializeField]
        private string[] trackedTags = Array.Empty<string>();

        private AgentConfig _director;
        private CoreAiDemoPanel _panel;
        private readonly Queue<float> _recentRequestTimes = new();
        private readonly StringBuilder _promptBuilder = new(256);
        private Transform _player;
        private string _guiLine = "";
        private string _lastDirective = "(none yet)";
        private int _observationCount;
        private float _timer;
        private bool _busy;
        private CancellationTokenSource _lifetimeCts;

        private void OnEnable()
        {
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
        }

        private void OnDisable()
        {
            _lifetimeCts?.Cancel();
            _busy = false;
        }

        private void OnDestroy()
        {
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }

        private void Start()
        {
            _panel = CoreAiDemoPanel.Create(
                "CoreAI — Director AI",
                "Ambient agent with no chat box: observes on a timer and acts through world_command.");

            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _panel.Log("CoreAILifetimeScope not found in scene; demo is inactive.");
                Debug.LogError("[DirectorAiDemo] CoreAILifetimeScope not found in scene; demo is inactive.");
                enabled = false;
                return;
            }

            if (CoreAIAgent.Policy == null || CoreAIAgent.Orchestrator == null)
            {
                _panel.Log("LLM module is not initialized; demo is inactive.");
                Debug.LogWarning(
                    "[DirectorAiDemo] LLM module is not initialized (CoreAIAgent facade is empty); demo is inactive.");
                enabled = false;
                return;
            }

            try
            {
                // world_command wraps the world-command executor registered by the standard scope, so the
                // director acts through the exact same audited pipeline as the chat-driven demos.
                IObjectResolver container = coreAiScope.Container;
                WorldLlmTool worldTool = new(
                    container.Resolve<ICoreAiWorldCommandExecutor>(),
                    container.Resolve<ICoreAISettings>(),
                    container.Resolve<IGameLogger>());

                _director = new AgentBuilder(directorRoleId)
                    .WithSystemPrompt(
                        "You are the Game Director, an ambient AI that quietly shapes a running game. " +
                        "There is no chat window and no player talking to you. On a schedule you receive one " +
                        "[Observation] line with a compact world snapshot. If the moment would benefit from " +
                        "direction (pacing, variety, reward, tension), act through your available tools - " +
                        "world_command can spawn, move, recolor, or destroy objects. Prefer at most one or two " +
                        "small actions per observation. If nothing is needed, reply with exactly PASS. " +
                        "Otherwise reply with one short directive line describing what you did and why.")
                    .WithTool(worldTool)
                    .WithMode(AgentMode.ToolsAndChat)
                    .WithMaxOutputTokens(maxOutputTokens > 0 ? maxOutputTokens : (int?)null)
                    .Build();
                _director.ApplyToPolicy(CoreAIAgent.Policy);
            }
            catch (Exception ex)
            {
                _panel.Log($"Failed to build the director agent: {ex.Message}");
                Debug.LogError($"[DirectorAiDemo] Failed to build the director agent: {ex.Message}");
                enabled = false;
                return;
            }

            RefreshGuiLine();
            Debug.Log(
                $"[DirectorAiDemo] Director '{directorRoleId}' registered. Observing every {observationIntervalSeconds:0.#}s.");
        }

        private void Update()
        {
            if (_director == null || !directorEnabled || _busy)
            {
                // Never issue a new observation while the previous request is in flight.
                return;
            }

            _timer += Time.deltaTime;
            if (_timer < Mathf.Max(1f, observationIntervalSeconds))
            {
                return;
            }

            if (!TryConsumeActionBudget())
            {
                // Cap reached: keep the timer primed and retry once the rolling window frees up.
                return;
            }

            _timer = 0f;
            SendObservationAsync(BuildObservationPrompt());
        }

        /// <summary>
        /// Enforces the actions-per-minute cap over a rolling 60-second window of issued requests.
        /// </summary>
        private bool TryConsumeActionBudget()
        {
            float now = Time.time;
            while (_recentRequestTimes.Count > 0 && now - _recentRequestTimes.Peek() > ActionWindowSeconds)
            {
                _recentRequestTimes.Dequeue();
            }

            if (maxActionsPerMinute > 0 && _recentRequestTimes.Count >= maxActionsPerMinute)
            {
                return false;
            }

            _recentRequestTimes.Enqueue(now);
            return true;
        }

        /// <summary>
        /// Gathers the cheap world snapshot. Replace or extend this method to point the director at
        /// your own game state (wave number, player health, quest flags, ...).
        /// </summary>
        private string BuildObservationPrompt()
        {
            _promptBuilder.Length = 0;
            _promptBuilder.Append("[Observation] t=")
                .Append(Time.time.ToString("0", CultureInfo.InvariantCulture))
                .Append('s');

            Transform player = FindPlayer();
            if (player != null)
            {
                Vector3 p = player.position;
                _promptBuilder.Append("; player=(")
                    .Append(p.x.ToString("0.0", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(p.y.ToString("0.0", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(p.z.ToString("0.0", CultureInfo.InvariantCulture)).Append(')');
            }
            else
            {
                _promptBuilder.Append("; player=none");
            }

            _promptBuilder.Append("; sceneRootObjects=").Append(SceneManager.GetActiveScene().rootCount);

            for (int i = 0; i < trackedTags.Length; i++)
            {
                string tag = trackedTags[i];
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                int count = CountTag(tag);
                if (count >= 0)
                {
                    _promptBuilder.Append("; ").Append(tag).Append('=').Append(count);
                }
            }

            _promptBuilder.Append(". Act via your tools if the moment needs direction; otherwise reply PASS.");
            return _promptBuilder.ToString();
        }

        private Transform FindPlayer()
        {
            if (_player != null)
            {
                return _player;
            }

            try
            {
                GameObject player = GameObject.FindWithTag("Player");
                _player = player != null ? player.transform : null;
            }
            catch (UnityException)
            {
                // "Player" tag not defined in this project; the snapshot simply reports player=none.
            }

            return _player;
        }

        private static int CountTag(string tag)
        {
            try
            {
                return GameObject.FindGameObjectsWithTag(tag).Length;
            }
            catch (UnityException)
            {
                return -1; // Undefined tag: skip it instead of spamming exceptions every interval.
            }
        }

        private async void SendObservationAsync(string prompt)
        {
            CancellationToken cancellationToken = _lifetimeCts?.Token ?? CancellationToken.None;
            _busy = true;
            _observationCount++;
            RefreshGuiLine();
            try
            {
                // Same orchestrator path as the other demos: AgentConfig -> CoreAIAgent.Orchestrator.
                string directive = await _director.AskAsync(prompt, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                HandleDirective(directive);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Scene unload/disable owns this cancellation. Do not let a late director response
                // apply world_command side effects to the next scene or emit a false error.
            }
            catch (Exception ex)
            {
                _lastDirective = $"Error: {ex.Message}";
                Debug.LogError($"[DirectorAiDemo] Observation #{_observationCount} failed: {ex}");
            }
            finally
            {
                if (this != null && !cancellationToken.IsCancellationRequested)
                {
                    _busy = false;
                    RefreshGuiLine();
                }
            }
        }

        private void HandleDirective(string text)
        {
            string directive = string.IsNullOrWhiteSpace(text)
                ? "(empty response - check LLM backend)"
                : text.Trim();
            _lastDirective = directive;

            if (string.Equals(directive, PassDirective, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[DirectorAiDemo] Observation #{_observationCount}: director passed.");
            }
            else
            {
                Debug.Log($"[DirectorAiDemo] Observation #{_observationCount} directive: {directive}");
            }
        }

        /// <summary>Rebuilds the panel's status line, only called when state actually changes.</summary>
        private void RefreshGuiLine()
        {
            string state = _busy ? "observing..." : directorEnabled ? "idle" : "disabled";
            _guiLine =
                $"Director AI [{directorRoleId}] obs #{_observationCount} ({state})\nLast directive: {_lastDirective}";
            _panel.SetLog(_guiLine);
        }
#else
        private void Start()
        {
            Debug.LogWarning(
                "[DirectorAiDemo] COREAI_LLM is not set; the LLM module is disabled and the Director demo is inactive.");
            enabled = false;
        }
#endif
    }
}
