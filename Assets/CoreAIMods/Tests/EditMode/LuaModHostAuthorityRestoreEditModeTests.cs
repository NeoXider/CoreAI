using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A stored mod starts after a restart with the rights it had when it was loaded: one the trusted host loaded (as the
    /// AI's tools do) writes the host-protected Workspace again, and one a restricted actor loaded, under the same actor
    /// id, is still refused (the negative twin: no mod gains rights by being restored). And a restore that keeps going
    /// past a mod that throws while it starts runs the rest and names that mod; without it the whole restore fails.
    /// </summary>
    public sealed class LuaModHostAuthorityRestoreEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        /// <summary>A store that keeps each manifest as JSON, as a file store does, so a restart reads what disk holds.</summary>
        private sealed class JsonMemoryStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, string Manifest)> _entries = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = (source, JsonConvert.SerializeObject(manifest));
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out (string Source, string Manifest) entry))
                {
                    source = entry.Source;
                    manifest = JsonConvert.DeserializeObject<LuaModManifest>(entry.Manifest);
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach ((string Source, string Manifest) entry in _entries.Values)
                {
                    manifests.Add(JsonConvert.DeserializeObject<LuaModManifest>(entry.Manifest));
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (TryLoad(id, out string source, out LuaModManifest manifest) && manifest != null)
                {
                    manifest.Active = active;
                    Save(id, source, manifest);
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }

            public string Json(string id)
            {
                return _entries.TryGetValue(id, out (string Source, string Manifest) entry) ? entry.Manifest : "";
            }
        }

        private static ActorContext Host()
        {
            return CoreServicesInstaller.DefaultLocalHostIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>A fresh world over <paramref name="store"/>, as a start of the game builds it: an ACL world and its host.</summary>
        private static LuaCsModStack Start(ILuaModSourceStore store)
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion, worldId: "w");
            ActorContext host = Host();
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                ModSourceStore = store,
                AutoPersistMods = true,
                RbxApi = new LuaCsRbxApiBindings(registry),
                LocalActorResolver = () => host
            });
        }

        private static object WorkspaceAttribute(LuaCsModStack stack, string name)
        {
            return stack.GameplayBindings.RbxApi.Game.FindFirstChildOfClass("Workspace").GetAttribute(name);
        }

        private const string WritesTheWorkspace =
            "pcall(function() workspace:SetAttribute('TestMark', true) end)";

        [Test]
        public void AModTheHostLoaded_WritesTheWorkspaceAgain_AfterARestart()
        {
            JsonMemoryStore store = new();
            LuaCsModStack before = Start(store);
            before.Runtime.LoadMod(Host(), "ai_mod", WritesTheWorkspace);
            object markBefore = WorkspaceAttribute(before, "TestMark");
            store.TryLoad("ai_mod", out _, out LuaModManifest stored);

            LuaCsModStack after = Start(store);
            after.Runtime.RehydrateFromStore(LuaCapabilities.All);
            LuaCsModStack restored = Start(store);
            restored.Runtime.RehydrateExactOrThrow(LuaCapabilities.All, false, true, out IReadOnlyList<string> _);

            Assert.AreEqual(true, markBefore, "the host's mod writes the Workspace when it is loaded");
            Assert.AreEqual(Host().ActorId, stored.OwnerActorId);
            Assert.IsTrue(stored.OwnerHadHostAuthority, "the store keeps that the host loaded it");
            Assert.AreEqual(true, WorkspaceAttribute(after, "TestMark"), "and after a restart it does so again");
            Assert.AreEqual(true, WorkspaceAttribute(restored, "TestMark"), "also from a world package's restore");
        }

        [Test]
        public void NegativeTwin_AModARestrictedActorLoaded_IsStillRefusedAfterARestart_UnderTheSameActorId()
        {
            JsonMemoryStore store = new();
            LuaCsModStack before = Start(store);
            ActorContext restricted = Host().NarrowGrants(ActorGrantSet.Create(new[] { "mods" }));
            before.Runtime.LoadMod(restricted, "player_mod", WritesTheWorkspace);
            object markBefore = WorkspaceAttribute(before, "TestMark");
            store.TryLoad("player_mod", out _, out LuaModManifest stored);

            LuaCsModStack after = Start(store);
            after.Runtime.RehydrateFromStore(LuaCapabilities.All);
            LuaCsModStack restored = Start(store);
            restored.Runtime.RehydrateExactOrThrow(LuaCapabilities.All, false, true, out IReadOnlyList<string> _);

            Assert.AreEqual(Host().ActorId, restricted.ActorId, "the same actor id as the host");
            Assert.IsNull(markBefore, "a restricted actor's mod is refused the Workspace when it is loaded");
            Assert.IsFalse(stored.OwnerHadHostAuthority);
            StringAssert.DoesNotContain("OwnerHadHostAuthority", store.Json("player_mod"), "written only when true");
            Assert.IsTrue(after.Runtime.IsLoaded("player_mod"), "it still starts");
            Assert.IsNull(WorkspaceAttribute(after, "TestMark"), "but gains nothing by the restart");
            Assert.IsNull(WorkspaceAttribute(restored, "TestMark"), "nor by a world package's restore");
        }

        private static void Store(JsonMemoryStore store, string id, string source)
        {
            store.Save(id, source, new LuaModManifest
            {
                Id = id,
                Name = id,
                Capabilities = LuaCapabilities.All.ToString(),
                Active = true
            });
        }

        [Test]
        public void ARestoreThatKeepsGoing_RunsTheRest_AndNamesTheModThatFailed()
        {
            JsonMemoryStore store = new();
            Store(store, "good", "workspace:SetAttribute('TestGood', true)");
            Store(store, "bad", "error('broken at the start')");

            LuaCsModStack restored = Start(store);
            int started = restored.Runtime.RehydrateExactOrThrow(LuaCapabilities.All, false, true, true,
                out IReadOnlyList<string> notStarted);

            Assert.AreEqual(1, started);
            Assert.IsTrue(restored.Runtime.IsLoaded("good"), "the rest of the world runs");
            Assert.AreEqual(true, WorkspaceAttribute(restored, "TestGood"));
            Assert.IsFalse(restored.Runtime.IsLoaded("bad"), "the failing mod stays stopped");
            Assert.AreEqual(1, notStarted.Count);
            StringAssert.StartsWith("bad (failed: ", notStarted[0]);
            StringAssert.Contains("broken at the start", notStarted[0]);
            Assert.IsTrue(store.TryLoad("bad", out string source, out _) && source.Contains("broken"),
                "and is kept in the store");
        }

        [Test]
        public void NegativeTwin_AStrictRestore_StillFailsWhenAModFails()
        {
            JsonMemoryStore store = new();
            Store(store, "good", "workspace:SetAttribute('TestGood', true)");
            Store(store, "bad", "error('broken at the start')");

            LuaCsModStack restored = Start(store);

            Assert.Catch<Exception>(() =>
                restored.Runtime.RehydrateExactOrThrow(LuaCapabilities.All, false, true, out IReadOnlyList<string> _));
        }
    }
}
