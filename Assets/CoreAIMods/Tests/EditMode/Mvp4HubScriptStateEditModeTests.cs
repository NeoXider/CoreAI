#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// MVP4 step C1-5 on the Hub's Mods page: every record carries the mod's script context and run state, a loaded
    /// one the path of its <c>script</c>, all read from the runtime and the store listing the page already reads (never
    /// a store read of its own); deleting a declared mod releases its world-owned script instance, and deleting a mod
    /// that declares nothing releases nothing. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class Mvp4HubScriptStateEditModeTests
    {
        private const string ServerHeader = "--[[@coreai\nid: door\nname: Door\ncontext: server\n]]\nlocal n = 1\n";

        private SynchronizationContext _savedContext;
        private LuaCsRbxApiBindings _roblox;
        private CountingStore _store;
        private LuaCsModRuntimeHubService _hub;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _roblox = new LuaCsRbxApiBindings();
            _store = new CountingStore();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentGameLogger(),
                ModSourceStore = _store,
                RbxApi = _roblox
            });
            // WHY the host identity: the Hub service observes mod reports, which needs unrestricted host authority.
            ActorContext actor = CoreAI.Composition.CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            _hub = new LuaCsModRuntimeHubService(stack.Runtime, actor, _store, LuaCapabilities.All, false, true);
        }

        [TearDown]
        public void TearDown()
        {
            _hub?.Dispose();
            _roblox?.Dispose();
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private HubModRecord Record(string id)
        {
            foreach (HubModRecord record in _hub.ListMods())
            {
                if (record.Id == id)
                {
                    return record;
                }
            }

            Assert.Fail("the Hub lists no mod '" + id + "'");
            return null;
        }

        [Test]
        public void C1_5_Hub_ALoadedDeclaredMod_ShowsItsContextRunStateAndScript()
        {
            _hub.SaveOrReload("door", ServerHeader);
            int loadsBefore = _store.Loads;

            HubModRecord door = Record("door");

            Assert.AreEqual("server", door.Context);
            Assert.AreEqual("running", door.RunState);
            Assert.AreEqual("ServerScriptService.door", door.ScriptPath);
            Assert.AreEqual(loadsBefore, _store.Loads, "the listing reads no stored source for these fields");
        }

        [Test]
        public void C1_5_Hub_Negative_AStoredModThatIsNotLoaded_ShowsItsStoredContextAsUnloaded()
        {
            _store.Save("hud", "local n = 1", new LuaModManifest { Id = "hud", Name = "Hud", Context = "client" });
            _store.Save("stuck", "local n = 1",
                new LuaModManifest { Id = "stuck", Name = "Stuck", SuspendedAfterBudgetTrips = true });

            HubModRecord hud = Record("hud");
            HubModRecord stuck = Record("stuck");

            Assert.AreEqual("client", hud.Context);
            Assert.AreEqual("unloaded", hud.RunState);
            Assert.AreEqual("", hud.ScriptPath);
            Assert.AreEqual("legacy", stuck.Context);
            Assert.AreEqual("suspended", stuck.RunState);
        }

        [Test]
        public void C1_5_Hub_Delete_ReleasesTheDeclaredModsScriptInstance()
        {
            _hub.SaveOrReload("door", ServerHeader);
            Assert.IsTrue(_roblox.ScriptInstances.TryGetBoundScript("door", out RbxLuaSourceContainer script));

            _hub.Delete("door");

            Assert.IsTrue(script.IsDestroyed, "a default-placed, empty script instance goes with its deleted mod");
            Assert.IsFalse(_roblox.ScriptInstances.TryGetBoundScript("door", out _));
            Assert.IsFalse(_store.TryLoad("door", out _, out _));
        }

        [Test]
        public void C1_5_Hub_Delete_Negative_OfALegacyMod_ReleasesNothingAndDeletesOnce()
        {
            _hub.SaveOrReload("legacy", "--[[@coreai\nid: legacy\nname: Legacy\n]]\nlocal n = 1\n");
            int deletesBefore = _store.Deletes;

            _hub.Delete("legacy");

            Assert.AreEqual(deletesBefore + 1, _store.Deletes, "the Hub's own delete, and nothing else");
            Assert.IsNull(_roblox.Game.FindFirstChildOfClass("ServerScriptService").FindFirstChild("legacy"));
        }

        private sealed class CountingStore : ILuaModSourceStore, ILuaModSourceStrictWriter, ILuaModSourceStrictRemover
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

            public int Loads { get; private set; }

            public int Deletes { get; private set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = (source, manifest);
            }

            public void SaveStrict(string id, string source, LuaModManifest manifest)
            {
                Save(id, source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                Loads++;
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach ((string Source, LuaModManifest Manifest) entry in _entries.Values)
                {
                    manifests.Add(entry.Manifest);
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                Deletes++;
                _entries.Remove(id);
            }

            public void DeleteStrict(string id)
            {
                Delete(id);
            }
        }

        private sealed class SilentGameLogger : IGameLogger
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
    }
}
#endif
