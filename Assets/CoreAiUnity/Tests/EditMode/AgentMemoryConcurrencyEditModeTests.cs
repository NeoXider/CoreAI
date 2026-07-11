using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.AiMemory;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class AgentMemoryConcurrencyEditModeTests
    {
        private static string CreateTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAITestAgentMemoryConcurrency_" +
                                                           Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteTempRoot(string root)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        [Test]
        public async Task FileStore_MutateAsync_ConcurrentAppends_AllLinesSurvive()
        {
            string root = CreateTempRoot();
            try
            {
                const int writers = 32;
                const string roleId = "concurrent_memory_mutate";
                FileAgentMemoryStore store = new(null, root);
                TaskCompletionSource<bool> start = new();

                Task[] tasks = Enumerable.Range(0, writers)
                    .Select(i => Task.Run(async () =>
                    {
                        await start.Task.ConfigureAwait(false);
                        string line = $"fact_{i:D2}";
                        await store.MutateAsync(roleId, state =>
                        {
                            string current = state.Memory ?? "";
                            state.Memory = string.IsNullOrEmpty(current) ? line : current + "\n" + line;
                            return true;
                        }).ConfigureAwait(false);
                    }))
                    .ToArray();

                start.SetResult(true);
                await Task.WhenAll(tasks).ConfigureAwait(false);

                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsTrue(reloaded.TryLoad(roleId, out AgentMemoryState state));

                string[] lines = (state.Memory ?? "")
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();

                Assert.AreEqual(writers, lines.Length);
                for (int i = 0; i < writers; i++)
                {
                    Assert.AreEqual($"fact_{i:D2}", lines[i]);
                }
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public async Task MemoryTool_ConcurrentAppendCalls_AllLinesSurvive()
        {
            string root = CreateTempRoot();
            try
            {
                const int writers = 32;
                const string roleId = "concurrent_memory_tool_append";
                FileAgentMemoryStore store = new(null, root);
                MemoryTool tool = new(store, roleId);
                TaskCompletionSource<bool> start = new();

                Task[] tasks = Enumerable.Range(0, writers)
                    .Select(i => Task.Run(async () =>
                    {
                        await start.Task.ConfigureAwait(false);
                        await tool.ExecuteAsync("append", $"tool_fact_{i:D2}")
                            .ConfigureAwait(false);
                    }))
                    .ToArray();

                start.SetResult(true);
                await Task.WhenAll(tasks).ConfigureAwait(false);

                FileAgentMemoryStore reloaded = new(null, root);
                Assert.IsTrue(reloaded.TryLoad(roleId, out AgentMemoryState state));

                string[] lines = (state.Memory ?? "")
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();

                Assert.AreEqual(writers, lines.Length);
                for (int i = 0; i < writers; i++)
                {
                    Assert.AreEqual($"tool_fact_{i:D2}", lines[i]);
                }
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }
    }
}
