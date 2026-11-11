using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Folders;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// Mod folders wired into the Lua-CSharp runtime and the <c>manage_mods</c> tool: a loaded mod moves without
    /// running again, the tool lists by folder, loads into a folder, moves mods and folders, and every changing
    /// folder action spends the shared rate limit.
    /// </summary>
    [TestFixture]
    public sealed class ModFolderRuntimeWiringEditModeTests
    {
        private const string HeaderedSource =
            "--[[@coreai\nid: racer\nname: Racer\ncategory: Samples\n]]\nhooks_on('tick', function() end)\n";

        private SynchronizationContext _savedContext;
        private LocalActorIdentityProvider _identity;
        private ActorContext _actor;

        [SetUp]
        public void SetUp()
        {
            // WHY: the Lua-CSharp runtime bridges its async VM to a synchronous call site; without the Unity
            // context its continuations complete on the thread pool instead of deadlocking (see
            // LuaModsLlmToolEditModeTests).
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _identity = new LocalActorIdentityProvider("folder-tests");
            _actor = _identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private LuaModsLlmTool Tool(LuaCsModRuntime runtime, string defaultFolder = "",
            LuaGenerationRateLimiter limiter = null, bool allowManagement = true, IModFolderListStore folders = null)
        {
            return new LuaModsLlmTool(runtime, new FolderSettings(defaultFolder), NullLog.Instance, LuaCapabilities.All,
                allowManagement, _identity, BuiltInAgentRoleIds.Programmer, null, limiter, folders);
        }

        private static async Task<JObject> Run(LuaModsLlmTool tool, string action, string modId = null,
            string code = null, string folder = null, string target = null)
        {
            string json = await tool.ExecuteAsync(action, modId, code, folder: folder, target: target);
            return JObject.Parse(json);
        }

        private static void AssertOk(JObject result)
        {
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }

        private static void AssertCode(JObject result, string code)
        {
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.StartsWith(code + ":", result.Value<string>("message"));
        }

        [Test]
        public void Retitle_LoadedMod_ChangesOnlyTheHeaderAndDoesNotRunItAgain()
        {
            CountingSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            runtime.LoadMod(_actor, "racer", HeaderedSource);
            LuaModInfo before = runtime.ListMods(_actor)[0];
            store.Saved.Clear();
            string moved = ModFolderHeaderEditor.SetCategory(HeaderedSource, "Games/Arcade").Source;

            Assert.IsTrue(runtime.TryRetitleModSource(_actor, "racer", moved));

            LuaModInfo after = runtime.ListMods(_actor)[0];
            Assert.AreEqual("Games/Arcade", after.Category);
            Assert.AreEqual("Samples", before.Category);
            Assert.AreEqual(before.LoadedAtUtc, after.LoadedAtUtc, "a move must not reload the mod");
            Assert.AreEqual(before.HandlerCount, after.HandlerCount);
            Assert.IsTrue(runtime.TryGetModSource(_actor, "racer", out string live));
            Assert.AreEqual(moved, live);
            CollectionAssert.AreEqual(new[] { "racer" }, store.Saved);
            Assert.AreEqual("Games/Arcade", store.Manifests["racer"].Category);
        }

        [Test]
        public void Retitle_ACodeChange_IsRefused_AndTheLiveSourceStays()
        {
            LuaCsModRuntime runtime = new();
            runtime.LoadMod(_actor, "racer", HeaderedSource);
            string edited = ModFolderHeaderEditor.SetCategory(HeaderedSource, "Games").Source.Replace("tick", "frame");

            Assert.Throws<ArgumentException>(() => runtime.TryRetitleModSource(_actor, "racer", edited));
            Assert.IsFalse(runtime.TryRetitleModSource(_actor, "missing", edited));
            Assert.IsTrue(runtime.TryGetModSource(_actor, "racer", out string live));
            Assert.AreEqual(HeaderedSource, live);
        }

        [Test]
        public async Task Load_WithAFolder_WritesTheCategory_AndListShowsIt()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = Tool(runtime);

            AssertOk(await Run(tool, "load", "racer", "hooks_on('tick', function() end)", folder: "games / arcade"));
            AssertOk(await Run(tool, "load", "clock", "hooks_on('tick', function() end)"));
            JObject list = await Run(tool, "list");

            Assert.IsTrue(runtime.TryGetModSource(_actor, "racer", out string source));
            StringAssert.StartsWith("-- @coreai category: games/arcade\n", source);
            Dictionary<string, string> folders = new();
            foreach (JToken mod in (JArray)list["data"])
            {
                folders[mod.Value<string>("id")] = mod.Value<string>("folder");
            }

            Assert.AreEqual("games/arcade", folders["racer"]);
            Assert.AreEqual("", folders["clock"]);
            StringAssert.Contains("\"path\":\"games/arcade\"", list.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Test]
        public async Task Load_WithoutAFolder_KeepsTheSourceExactly_UnlessAHostSetsADefaultFolder()
        {
            const string code = "hooks_on('tick', function() end)";
            LuaCsModRuntime plain = new();
            LuaCsModRuntime defaulted = new();

            AssertOk(await Run(Tool(plain), "load", "a", code));
            AssertOk(await Run(Tool(defaulted, "AI/Made"), "load", "a", code));
            AssertOk(await Run(Tool(defaulted, "AI/Made"), "load", "b", HeaderedSource.Replace("racer", "b")));

            Assert.IsTrue(plain.TryGetModSource(_actor, "a", out string kept));
            Assert.AreEqual(code, kept);
            Assert.AreEqual("AI/Made", defaulted.ListMods(_actor)[0].Category);
            Assert.AreEqual("Samples", defaulted.ListMods(_actor)[1].Category, "a header's own category wins");
        }

        [Test]
        public async Task Load_WithAnInvalidFolder_FailsWithTheCode_AndLoadsNothing()
        {
            LuaCsModRuntime runtime = new();

            AssertCode(await Run(Tool(runtime), "load", "a", "print(1)", folder: "Games/[x]"), "INVALID_CHARACTER");
            Assert.AreEqual(0, runtime.ListMods(_actor).Count);
        }

        [Test]
        public async Task Move_ALoadedMod_ChangesItsFolderWithoutReloading()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = Tool(runtime);
            AssertOk(await Run(tool, "load", "racer", HeaderedSource));
            DateTime loadedAt = runtime.ListMods(_actor)[0].LoadedAtUtc;

            JObject moved = await Run(tool, "move", "racer", folder: "Games/Arcade");

            AssertOk(moved);
            Assert.AreEqual("Games/Arcade", moved["data"].Value<string>("path"));
            Assert.AreEqual("Samples", moved["data"].Value<string>("previous_path"));
            Assert.AreEqual("Games/Arcade", runtime.ListMods(_actor)[0].Category);
            Assert.AreEqual(loadedAt, runtime.ListMods(_actor)[0].LoadedAtUtc);
            AssertCode(await Run(tool, "move", "racer", folder: "a//b"), "EMPTY_SEGMENT");
            AssertCode(await Run(tool, "move", "ghost", folder: "Games"), "MOD_NOT_FOUND");
        }

        [Test]
        public async Task FolderActions_CreateRenameMoveDelete_WithNamedRefusals()
        {
            LuaCsModRuntime runtime = new();
            InMemoryModFolderListStore folders = new();
            LuaModsLlmTool tool = Tool(runtime, folders: folders);
            AssertOk(await Run(tool, "load", "racer", HeaderedSource));

            AssertOk(await Run(tool, "create_folder", folder: "Archive/2025"));
            AssertCode(await Run(tool, "create_folder", folder: "archive/2025"), "FOLDER_ALREADY_EXISTS");
            AssertOk(await Run(tool, "rename_folder", folder: "Samples", target: "Games"));
            AssertCode(await Run(tool, "move_folder", folder: "Games", target: "Games"), "MOVE_INTO_ITSELF");
            AssertOk(await Run(tool, "move_folder", folder: "Games", target: "Archive"));
            AssertCode(await Run(tool, "delete_folder", folder: "Archive"), "FOLDER_NOT_EMPTY");
            AssertOk(await Run(tool, "delete_folder", folder: "Archive/2025"));
            JObject branch = await Run(tool, "list", folder: "Archive");

            Assert.AreEqual("Archive/Games", runtime.ListMods(_actor)[0].Category);
            Assert.AreEqual(1, ((JArray)branch["data"]).Count);
            AssertCode(await Run(tool, "list", folder: "Nowhere"), "FOLDER_NOT_FOUND");
            CollectionAssert.DoesNotContain(folders.Load(), "Archive/2025");
        }

        [Test]
        public async Task ChangingFolderActions_SpendTheSharedRateLimit_AndListDoesNot()
        {
            LuaCsModRuntime runtime = new();
            LuaGenerationRateLimiter limiter = new(1, 60d, () => 0d);
            LuaModsLlmTool tool = Tool(runtime, limiter: limiter);

            AssertOk(await Run(tool, "create_folder", folder: "Archive"));
            JObject refused = await Run(tool, "create_folder", folder: "Other");
            JObject listed = await Run(tool, "list");

            Assert.AreEqual("rate_limited", refused.Value<string>("status"), refused.ToString());
            AssertOk(listed);
        }

        [Test]
        public async Task ReadOnlyAgent_CannotMoveOrCreateFolders()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = Tool(runtime, allowManagement: false);

            JObject result = await Run(tool, "create_folder", folder: "Archive");

            Assert.IsFalse(result.Value<bool>("success"));
            StringAssert.Contains("read-only", result.Value<string>("message"));
        }

        private sealed class FolderSettings : ICoreAISettings
        {
            public FolderSettings(string defaultFolder)
            {
                AiModsDefaultFolder = defaultFolder;
            }

            public string AiModsDefaultFolder { get; }

            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }

        private sealed class CountingSourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

            public Dictionary<string, LuaModManifest> Manifests { get; } = new(StringComparer.Ordinal);

            public List<string> Saved { get; } = new();

            public void Save(string id, string source, LuaModManifest manifest)
            {
                Saved.Add(id);
                _sources[id] = source;
                Manifests[id] = manifest;
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                bool found = _sources.TryGetValue(id, out source);
                Manifests.TryGetValue(id, out manifest);
                return found;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return new List<LuaModManifest>(Manifests.Values);
            }

            public void SetActive(string id, bool active)
            {
                if (Manifests.TryGetValue(id, out LuaModManifest manifest))
                {
                    manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _sources.Remove(id);
                Manifests.Remove(id);
            }
        }
    }
}
