using System;
using System.Collections;
using UnityEngine;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// Runs the P19 reference scenes in a Standalone or WebGL player and writes (or prints) the same JSON
    /// report as the Editor fixtures. Add it to a scene, or let it start itself: a player started with
    /// <c>-coreaiPerf &lt;scenarios&gt;</c> (Standalone), with <c>COREAI_PERF_SCENARIOS</c> set, or opened with
    /// <c>?coreaiPerf=&lt;scenarios&gt;</c> in its URL (WebGL) creates one after the first scene loads.
    /// </summary>
    /// <remarks>
    /// The assembly is compiled into a player only when the build defines <c>COREAI_PERF_SCENARIOS</c> (or
    /// includes tests), so an ordinary game build never carries the runner. See
    /// dev-docs/PERF_BASELINE_2026-11.md for the build and run commands.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("CoreAI/Diagnostics/Perf Scenario Runner")]
    public sealed class CoreAiPerfScenarioRunner : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Comma-separated scenario ids or 'all'; overridden by -coreaiPerf / COREAI_PERF_SCENARIOS / ?coreaiPerf=.")]
        private string scenarios = "all";

        [SerializeField]
        [Tooltip("Run as soon as the component starts. Off: call Run() yourself.")]
        private bool runOnStart = true;

        private bool _running;

        /// <summary>The report of the last finished run, or null.</summary>
        public PerfRunReport LastReport { get; private set; }

        /// <summary>Where the last report was written; empty when it was only printed.</summary>
        public string LastReportPath { get; private set; } = "";

        /// <summary>True while a run is in progress.</summary>
        public bool IsRunning => _running;

        /// <summary>Options from the command line, the environment and the page URL; the inspector list is the fallback.</summary>
        public PerfRunnerOptions ReadOptions()
        {
            PerfRunnerOptions options = PerfRunnerOptions.Read(
                Environment.GetCommandLineArgs(),
                Environment.GetEnvironmentVariable,
                Application.platform == RuntimePlatform.WebGLPlayer ? Application.absoluteURL : null);
            if (!options.Requested && !string.IsNullOrWhiteSpace(scenarios))
            {
                options.Scenarios = scenarios;
            }

            return options;
        }

        /// <summary>Starts a run with <see cref="ReadOptions"/> unless one is already running.</summary>
        public void Run()
        {
            if (_running)
            {
                return;
            }

            StartCoroutine(RunRoutine(ReadOptions()));
        }

        private void Start()
        {
            if (runOnStart)
            {
                Run();
            }
        }

        private IEnumerator RunRoutine(PerfRunnerOptions options)
        {
            _running = true;
            string harness = Application.platform == RuntimePlatform.WebGLPlayer ? "unity-webgl" : "unity-player";
            if (Application.isEditor)
            {
                harness = "unity-editor-runner";
            }

            PerfRunReport report = null;
            try
            {
                yield return PerfScenarioRunner.RunAll(options, harness, finished => report = finished);
                LastReport = report;
                LastReportPath = PerfScenarioRunner.Publish(report, options.OutputPath);
            }
            finally
            {
                _running = false;
            }

            if (options.QuitWhenDone && !Application.isEditor && Application.platform != RuntimePlatform.WebGLPlayer)
            {
                Application.Quit(0);
            }
        }

        /// <summary>
        /// Creates a runner in a player that was asked for a run. Never in the Editor, where the [Explicit]
        /// PlayMode fixtures drive the same scenes and an inherited environment variable must not start a
        /// second run during ordinary tests.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartWhenRequested()
        {
            if (Application.isEditor || FindAnyObjectByType<CoreAiPerfScenarioRunner>() != null)
            {
                return;
            }

            PerfRunnerOptions options = PerfRunnerOptions.Read(
                Environment.GetCommandLineArgs(),
                Environment.GetEnvironmentVariable,
                Application.platform == RuntimePlatform.WebGLPlayer ? Application.absoluteURL : null);
            if (!options.Requested)
            {
                return;
            }

            GameObject host = new("CoreAI Perf Scenario Runner");
            DontDestroyOnLoad(host);
            host.AddComponent<CoreAiPerfScenarioRunner>();
        }
    }
}
