using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace CoreAI.Tests.EditMode
{
    public static class CoreAiBatchTestRunner
    {
        private const double TimeoutSeconds = 300d;
        private static double _startedAt;

        public static void RunEditMode()
        {
            Run(TestMode.EditMode);
        }

        public static void RunPlayMode()
        {
            Run(TestMode.PlayMode);
        }

        private static void Run(TestMode mode)
        {
            string[] args = Environment.GetCommandLineArgs();
            string namesCsv = ReadArg(args, "-coreAiTestNames");
            string output = ReadArg(args, "-coreAiTestResults");
            string[] names = namesCsv
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => s.Length > 0)
                .ToArray();

            if (names.Length == 0)
            {
                Debug.LogError("[CoreAiBatchTestRunner] -coreAiTestNames is required.");
                EditorApplication.Exit(2);
                return;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                output = Path.Combine(Path.GetTempPath(), "coreai-test-results.xml");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
            _startedAt = EditorApplication.timeSinceStartup;

            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            RunnerCallbacks callbacks = new(output);
            api.RegisterCallbacks(callbacks);
            EditorApplication.update += Watchdog;

            ExecutionSettings settings = new(new Filter
            {
                testMode = mode,
                testNames = names
            });

            Debug.Log($"[CoreAiBatchTestRunner] Running {mode} tests: {string.Join(", ", names)}");
            api.Execute(settings);
        }

        private static void Watchdog()
        {
            if (EditorApplication.timeSinceStartup - _startedAt < TimeoutSeconds)
            {
                return;
            }

            EditorApplication.update -= Watchdog;
            Debug.LogError($"[CoreAiBatchTestRunner] Timed out after {TimeoutSeconds:0} seconds.");
            EditorApplication.Exit(124);
        }

        private static string ReadArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1] ?? "";
                }
            }

            return "";
        }

        private sealed class RunnerCallbacks : ICallbacks
        {
            private readonly string _output;

            public RunnerCallbacks(string output)
            {
                _output = output;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                Debug.Log($"[CoreAiBatchTestRunner] Run started: {testsToRun?.FullName}");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                EditorApplication.update -= Watchdog;
                TestRunnerApi.SaveResultToFile(result, _output);
                int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
                Debug.Log(
                    $"[CoreAiBatchTestRunner] Run finished: total={total}, passed={result.PassCount}, " +
                    $"failed={result.FailCount}, skipped={result.SkipCount}, inconclusive={result.InconclusiveCount}, " +
                    $"state={result.ResultState}, results={_output}");

                if (result.FailCount > 0 || (result.ResultState ?? "").StartsWith("Failed", StringComparison.Ordinal))
                {
                    EditorApplication.Exit(1);
                    return;
                }

                EditorApplication.Exit(total > 0 ? 0 : 2);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if ((result.ResultState ?? "").StartsWith("Failed", StringComparison.Ordinal))
                {
                    Debug.LogError($"[CoreAiBatchTestRunner] Failed: {result.FullName}\n{result.Message}\n{result.StackTrace}");
                }
            }
        }
    }
}
