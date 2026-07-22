using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.LiveCheck
{
    /// <summary>
    /// LIVE check (environment-gated, never fails CI): proves a real small model — the local
    /// LM Studio <c>qwen_qwen3.5-4b</c> by default — can write working Lua against the Roblox MVP1
    /// surface as wired in production. Each test builds the REAL mod stack
    /// (<c>LuaCsModRuntimeFactory</c> + <c>LuaCsRobloxApiBindings</c> with WorldEdit), asks the model
    /// with a MINIMAL honest skill excerpt, runs the returned Lua through the real one-off
    /// <c>execute_lua</c> executor, and asserts on ACTUAL world state via the registry. One
    /// error-fed retry per scenario probes whether our <c>CODE | fix</c> error format helps the 4B
    /// self-correct. The whole flow lives in <see cref="RobloxApi4BLiveCheckRunner"/> so the
    /// out-of-Unity console harness runs the identical path.
    ///
    /// <para>Self-skips via <see cref="Assert.Ignore(string)"/> when the endpoint/model is
    /// unavailable, following the repo's live-test convention (see PlayModeOpenAiTestConfig).
    /// Point elsewhere with <c>COREAI_TEST_BASE_URL</c> / <c>COREAI_TEST_MODEL</c>.</para>
    /// </summary>
    [TestFixture]
    [Category("LiveLlm")]
    [Explicit("Live LM Studio 4B check — run manually; requires a local OpenAI-compatible endpoint.")]
    // WHY: NUnit's default 180s per-test timeout killed all three scenarios before the reasoning
    // model answered — a 4B reasoning model can burn several minutes in reasoning_content per call,
    // and a scenario is up to two calls. 30 min covers 2 x 600s HTTP timeouts plus execution.
    [Timeout(1_800_000)]
    public sealed class RobloxApi4BLiveCheckEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Detach Unity's SynchronizationContext so the executor's awaited continuations
        /// complete on the thread pool rather than deadlocking the editor thread (same hazard the
        /// sibling RobloxApiLuaBindingsEditModeTests guards against).</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            // WHY: this live gate asserts on world state and executor results, not console hygiene.
            // Orphaned continuations from an aborted previous run (NUnit [Timeout] aborts mid-await)
            // can log e.g. "SynchronizationContext may not be used as a TaskScheduler" during THIS
            // test and NUnit would fail it for an unrelated logged exception.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static async Task RequireEndpointAsync()
        {
            (bool available, string reason) = await RobloxApi4BLiveCheckRunner.ProbeAsync();
            if (!available)
            {
                Assert.Ignore(
                    "LIVE 4B check skipped — " + reason + ". Set COREAI_TEST_BASE_URL / " +
                    "COREAI_TEST_MODEL to point at a running OpenAI-compatible endpoint.");
            }
        }

        [Test]
        public async Task Part_Door_Tagged_Interactive()
        {
            await RunScenarioByIndex(0);
        }

        [Test]
        public async Task Folder_Loot_With_Three_Coins()
        {
            await RunScenarioByIndex(1);
        }

        [Test]
        public async Task Report_Workspace_FullName_And_Neon_Value()
        {
            await RunScenarioByIndex(2);
        }

        private static async Task RunScenarioByIndex(int index)
        {
            await RequireEndpointAsync();

            RobloxApi4BLiveCheckRunner.Scenario scenario =
                RobloxApi4BLiveCheckRunner.Scenarios()[index];
            RobloxApi4BLiveCheckRunner.ScenarioResult result =
                await RobloxApi4BLiveCheckRunner.RunScenarioAsync(scenario);

            TestContext.WriteLine(RobloxApi4BLiveCheckRunner.FormatTranscript(result));

            Assert.IsTrue(result.Passed,
                $"4B failed scenario '{scenario.Id}' after {result.Attempts.Count} attempt(s). " +
                "See the transcript above for the model's Lua and the world/exec mismatch.");
        }
    }
}
