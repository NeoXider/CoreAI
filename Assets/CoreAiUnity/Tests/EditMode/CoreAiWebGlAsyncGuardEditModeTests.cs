using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Source-level guards for the two async primitives that silently never resume on WebGL/Emscripten:
    /// <c>UniTask.SwitchToThreadPool()</c> (no thread pool in the browser) and <c>Task.Delay</c>
    /// (needs <c>System.Threading.Timer</c>). Both hang the caller forever instead of throwing, so the
    /// only cheap way to keep them out is to pin the guard in a test.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiWebGlAsyncGuardEditModeTests
    {
        private const string SwitchToThreadPoolCall = "UniTask.SwitchToThreadPool()";
        private const string WebGlGuard = "#if !UNITY_WEBGL || UNITY_EDITOR";

        private static string RuntimeRoot => Path.Combine(Application.dataPath, "CoreAiUnity", "Runtime");

        [Test]
        public void SwitchToThreadPool_EveryCallSite_IsWebGlGuarded()
        {
            List<string> unguarded = new();

            foreach (string file in Directory.GetFiles(RuntimeRoot, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(SwitchToThreadPoolCall) || lines[i].TrimStart().StartsWith("//"))
                    {
                        continue;
                    }

                    if (!IsWebGlGuarded(lines, i))
                    {
                        unguarded.Add($"{Path.GetFileName(file)}:{i + 1}");
                    }
                }
            }

            CollectionAssert.IsEmpty(
                unguarded,
                "WebGL has no thread pool: awaiting SwitchToThreadPool there never resumes and the whole " +
                $"tool call hangs until the request timeout. Wrap each call site in '{WebGlGuard}'. Unguarded: " +
                string.Join(", ", unguarded));
        }

        [Test]
        public void LlmClientRegistry_OwnedHostDrain_DoesNotUseTaskDelay()
        {
            string source = File.ReadAllText(Path.Combine(
                RuntimeRoot, "Source", "Features", "Llm", "Infrastructure", "LlmClientRegistry.cs"));

            StringAssert.DoesNotContain(
                "await Task.Delay(",
                source,
                "Task.Delay is built on System.Threading.Timer, which does not exist on WebGL: the owned-host " +
                "drain loop would never resume, HostReleaseTask would never complete and the next activation " +
                "would hang in WaitingForHttp forever. Use UniTask.Delay instead.");
            StringAssert.Contains("await UniTask.Delay(", source);
        }

        [Test]
        public void LlmClientRegistry_ReleaseWithNothingToRelease_SkipsTheDrainLoop()
        {
            string source = File.ReadAllText(Path.Combine(
                RuntimeRoot, "Source", "Features", "Llm", "Infrastructure", "LlmClientRegistry.cs"));

            int exchangeIndex = source.IndexOf(
                "Interlocked.Exchange(ref runtime.ReleaseOwnedHostAsync, null)",
                System.StringComparison.Ordinal);
            int drainIndex = source.IndexOf(
                "while (Volatile.Read(ref runtime.InFlightRequests) > 0)",
                System.StringComparison.Ordinal);

            Assert.Greater(exchangeIndex, 0, "Owned-host release delegate exchange not found.");
            Assert.Greater(drainIndex, 0, "Owned-host drain loop not found.");
            Assert.Less(
                exchangeIndex,
                drainIndex,
                "The release delegate must be claimed BEFORE the drain loop, so a deactivation with no owned " +
                "host returns immediately instead of polling (which never ends on WebGL).");
        }

        /// <summary>Whether an open <c>#if !UNITY_WEBGL || UNITY_EDITOR</c> region covers the given line.</summary>
        private static bool IsWebGlGuarded(IReadOnlyList<string> lines, int lineIndex)
        {
            int depth = 0;
            for (int i = lineIndex - 1; i >= 0; i--)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("#endif"))
                {
                    depth++;
                }
                else if (trimmed.StartsWith("#if"))
                {
                    if (depth == 0)
                    {
                        return trimmed.StartsWith(WebGlGuard);
                    }

                    depth--;
                }
            }

            return false;
        }
    }
}
