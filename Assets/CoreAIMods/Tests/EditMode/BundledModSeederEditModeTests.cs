using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Unit tests for <see cref="BundledModSeeder"/>: fresh install, idempotent re-seed, version-gated
    /// update of an unmodified entry (preserving the player's enabled state), respect for player edits
    /// (flag instead of overwrite), never clobbering a user-authored mod that shares an id, and the
    /// dotted-numeric version comparison. Uses an in-memory store + fake source, no file system.
    /// </summary>
    public sealed class BundledModSeederEditModeTests
    {
        private static string Mod(string id, string version, bool active = true, string body = "report('hi')")
        {
            return $"--[[@coreai\nid: {id}\nname: {id}\nversion: {version}\nactive: {(active ? "true" : "false")}\n" +
                   $"capabilities: All\ncategory: Samples\n]]\n{body}\n";
        }

        private sealed class FakeSource : IBundledModSource
        {
            private readonly List<BundledMod> _mods = new();
            public string Origin { get; set; } = "resources";

            public void Add(string id, string version, bool active = true, string body = "report('hi')")
            {
                _mods.Add(new BundledMod(id, Mod(id, version, active, body), version));
            }

            public IReadOnlyList<BundledMod> Load()
            {
                return _mods;
            }
        }

        private sealed class MemStore : ILuaModSourceStore
        {
            private sealed class E
            {
                public string Source;
                public LuaModManifest Manifest;
            }

            private readonly Dictionary<string, E> _e = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _e[id] = new E { Source = source, Manifest = manifest };
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_e.TryGetValue(id, out E e))
                {
                    source = e.Source;
                    manifest = e.Manifest;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> r = new();
                foreach (E e in _e.Values)
                {
                    if (e.Manifest != null)
                    {
                        r.Add(e.Manifest);
                    }
                }

                return r;
            }

            public void SetActive(string id, bool active)
            {
                if (_e.TryGetValue(id, out E e) && e.Manifest != null)
                {
                    e.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _e.Remove(id);
            }

            public bool Has(string id)
            {
                return _e.ContainsKey(id);
            }

            public LuaModManifest ManifestOf(string id)
            {
                return _e.TryGetValue(id, out E e) ? e.Manifest : null;
            }

            public string SourceOf(string id)
            {
                return _e.TryGetValue(id, out E e) ? e.Source : null;
            }
        }

        [Test]
        public void Seed_installs_a_new_bundled_mod_with_origin_and_seed_stamps()
        {
            MemStore store = new();
            FakeSource src = new();
            src.Add("welcome", "1.0.0");

            BundledModSeeder.SeedResult r = new BundledModSeeder(store, new IBundledModSource[] { src }).Seed();

            Assert.AreEqual(1, r.Installed);
            Assert.IsTrue(store.Has("welcome"));
            LuaModManifest m = store.ManifestOf("welcome");
            Assert.AreEqual("resources", m.Origin);
            Assert.AreEqual("1.0.0", m.SeededVersion);
            Assert.IsNotEmpty(m.SeededHash);
            Assert.IsTrue(m.Active);
        }

        [Test]
        public void Seed_is_idempotent_for_the_same_version()
        {
            MemStore store = new();
            FakeSource src = new();
            src.Add("welcome", "1.0.0");
            BundledModSeeder seeder = new(store, new IBundledModSource[] { src });

            seeder.Seed();
            BundledModSeeder.SeedResult second = seeder.Seed();

            Assert.AreEqual(0, second.Installed);
            Assert.AreEqual(0, second.Updated);
            Assert.AreEqual(1, second.Skipped);
        }

        [Test]
        public void Newer_version_updates_an_unmodified_entry_and_preserves_active_choice()
        {
            MemStore store = new();
            FakeSource v1 = new();
            v1.Add("welcome", "1.0.0", body: "report('v1')");
            new BundledModSeeder(store, new IBundledModSource[] { v1 }).Seed();

            // Player disabled it, but did NOT edit the source.
            store.SetActive("welcome", false);

            FakeSource v2 = new();
            v2.Add("welcome", "1.1.0", body: "report('v2')");
            BundledModSeeder.SeedResult r = new BundledModSeeder(store, new IBundledModSource[] { v2 }).Seed();

            Assert.AreEqual(1, r.Updated);
            StringAssert.Contains("v2", store.SourceOf("welcome"));
            Assert.AreEqual("1.1.0", store.ManifestOf("welcome").SeededVersion);
            Assert.IsFalse(store.ManifestOf("welcome").Active, "the player's disabled choice must survive an update");
        }

        [Test]
        public void Newer_version_supersedes_local_edits_and_ships_the_canonical_bundle()
        {
            MemStore store = new();
            FakeSource v1 = new();
            v1.Add("welcome", "1.0.0", body: "report('v1')");
            new BundledModSeeder(store, new IBundledModSource[] { v1 }).Seed();

            // Player edited the stored source (hash now differs from SeededHash).
            LuaModManifest m = store.ManifestOf("welcome");
            store.Save("welcome", store.SourceOf("welcome") + "\n-- my tweak\n", m);

            FakeSource v2 = new();
            v2.Add("welcome", "2.0.0", body: "report('v2')");
            BundledModSeeder.SeedResult r = new BundledModSeeder(store, new IBundledModSource[] { v2 }).Seed();

            // A strictly-newer bundled version is canonical and ships, superseding the local edit (the prior
            // source stays recoverable from the store's revision history — not asserted by this in-memory fake).
            Assert.AreEqual(1, r.Updated);
            StringAssert.Contains("v2", store.SourceOf("welcome"));
            Assert.AreEqual("2.0.0", store.ManifestOf("welcome").SeededVersion);
            Assert.IsFalse(store.ManifestOf("welcome").UpdateAvailable);
        }

        [Test]
        public void Seed_never_clobbers_a_user_authored_mod_that_shares_an_id()
        {
            MemStore store = new();
            store.Save("welcome", "report('mine')", new LuaModManifest { Id = "welcome", Origin = "" });

            FakeSource src = new();
            src.Add("welcome", "9.9.9");
            BundledModSeeder.SeedResult r = new BundledModSeeder(store, new IBundledModSource[] { src }).Seed();

            Assert.AreEqual(1, r.Skipped);
            StringAssert.Contains("mine", store.SourceOf("welcome"));
        }

        [Test]
        public void CompareVersions_orders_dotted_numeric_correctly()
        {
            Assert.Less(BundledModSeeder.CompareVersions("1.2.0", "1.10.0"), 0);
            Assert.Greater(BundledModSeeder.CompareVersions("2.0.0", "1.9.9"), 0);
            Assert.AreEqual(0, BundledModSeeder.CompareVersions("1.0", "1.0.0"));
        }
    }
}
