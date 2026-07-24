using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Logging;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Installs the mods shipped with a game build (from one or more <see cref="IBundledModSource"/>)
    /// into the writable <see cref="ILuaModSourceStore"/> so a game can be shipped with a ready-made set
    /// of mods "out of the box". Runs on startup <em>before</em> rehydration, so seeded mods load like
    /// any persisted mod. Idempotent and player-respectful:
    /// <list type="bullet">
    ///   <item>a bundled mod not yet in the store is <b>installed</b> (source + manifest, stamped
    ///   <see cref="LuaModManifest.Origin"/>/<see cref="LuaModManifest.SeededVersion"/>/
    ///   <see cref="LuaModManifest.SeededHash"/>);</item>
    ///   <item>a strictly-newer bundled version <b>updates</b> a previously seeded entry — including one
    ///   the player edited (bundled samples are canonical, so "bump the header version and it ships"),
    ///   while the player's enabled/disabled choice is preserved and the prior source is retained in the
    ///   store's revision history so a local edit is recoverable, not lost;</item>
    ///   <item>a same-or-older bundled version, or a user-authored mod that happens to share an id, is
    ///   <b>skipped</b>.</item>
    /// </list>
    /// All work is best-effort: a store failure on one mod never aborts the rest or startup.
    /// </summary>
    public sealed class BundledModSeeder
    {
        private readonly ILuaModSourceStore _store;
        private readonly IReadOnlyList<IBundledModSource> _sources;
        private readonly ILog _log;

        /// <param name="store">Writable source store the bundled mods are seeded into.</param>
        /// <param name="sources">Bundled mod providers (e.g. <see cref="ResourcesBundledModSource"/>).</param>
        /// <param name="log">Optional logger; seeding is best-effort and logs at info/warn.</param>
        public BundledModSeeder(ILuaModSourceStore store, IReadOnlyList<IBundledModSource> sources, ILog log = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sources = sources ?? Array.Empty<IBundledModSource>();
            _log = log;
        }

        /// <summary>Result tallies for one <see cref="Seed"/> pass.</summary>
        public readonly struct SeedResult
        {
            /// <summary>Fresh installs.</summary>
            public readonly int Installed;

            /// <summary>In-place updates of unmodified seeded entries.</summary>
            public readonly int Updated;

            /// <summary>Newer-version entries left untouched because the player edited them.</summary>
            public readonly int FlaggedForUpdate;

            /// <summary>Up-to-date, older, or user-owned entries left as-is.</summary>
            public readonly int Skipped;

            internal SeedResult(int installed, int updated, int flagged, int skipped)
            {
                Installed = installed;
                Updated = updated;
                FlaggedForUpdate = flagged;
                Skipped = skipped;
            }

            /// <inheritdoc />
            public override string ToString()
            {
                return
                    $"installed={Installed}, updated={Updated}, updateAvailable={FlaggedForUpdate}, skipped={Skipped}";
            }
        }

        /// <summary>Seeds/updates every bundled mod into the store. Never throws.</summary>
        public SeedResult Seed()
        {
            int installed = 0, updated = 0, flagged = 0, skipped = 0;

            foreach (IBundledModSource source in _sources)
            {
                if (source == null)
                {
                    continue;
                }

                IReadOnlyList<BundledMod> mods;
                try
                {
                    mods = source.Load() ?? Array.Empty<BundledMod>();
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[BundledModSeeder] Source '{source.GetType().Name}' failed to load: {ex.Message}");
                    continue;
                }

                string origin = string.IsNullOrWhiteSpace(source.Origin) ? "bundled" : source.Origin.Trim();
                foreach (BundledMod mod in mods)
                {
                    try
                    {
                        switch (SeedOne(mod, origin))
                        {
                            case Outcome.Installed: installed++; break;
                            case Outcome.Updated: updated++; break;
                            case Outcome.Flagged: flagged++; break;
                            default: skipped++; break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[BundledModSeeder] Failed to seed '{mod.Id}': {ex.Message}");
                        skipped++;
                    }
                }
            }

            SeedResult result = new(installed, updated, flagged, skipped);
            if (installed + updated + flagged > 0)
            {
                _log?.Info($"[BundledModSeeder] Seeded bundled mods: {result}");
            }

            return result;
        }

        private enum Outcome
        {
            Installed,
            Updated,
            Flagged,
            Skipped
        }

        private Outcome SeedOne(BundledMod mod, string origin)
        {
            string id = (mod.Id ?? "").Trim();
            if (id.Length == 0 || string.IsNullOrWhiteSpace(mod.Source))
            {
                return Outcome.Skipped;
            }

            string newHash = Fnv1a(mod.Source);

            if (!_store.TryLoad(id, out string existingSource, out LuaModManifest existing) || existing == null)
            {
                _store.Save(id, mod.Source, BuildManifest(mod, origin, newHash, HeaderActive(mod.Source)));
                return Outcome.Installed;
            }

            // WHY: Never clobber a user-authored mod that happens to share this id (empty Origin = not ours).
            if (string.IsNullOrEmpty(existing.Origin))
            {
                return Outcome.Skipped;
            }

            // WHY: Only act when the bundled version is strictly newer than what we last seeded.
            if (CompareVersions(mod.Version, existing.SeededVersion) <= 0)
            {
                return Outcome.Skipped;
            }

            // WHY: A strictly-newer BUNDLED version always wins so "bump the header version and it ships"
            // is reliable — the old symptom was a sample silently sticking on its previous version because
            // it had once been opened/edited (which changes its hash and used to downgrade the update to a
            // no-op "UpdateAvailable" flag). Bundled samples are canonical, and the store keeps the prior
            // source as a revision (recoverable from the mod's history), so this preserves any local edit
            // rather than losing it. A same-or-older version is still skipped (the guard above), and a
            // non-bundled user mod sharing the id is still never touched (the empty-Origin guard above).
            LuaModManifest updated = BuildManifest(mod, origin, newHash, existing.Active);
            _store.Save(id, mod.Source, updated);
            return Outcome.Updated;
        }

        private static bool HeaderActive(string source)
        {
            return LuaModHeader.Parse(source ?? "", "").Active;
        }

        private static LuaModManifest BuildManifest(BundledMod mod, string origin, string hash, bool active)
        {
            LuaModHeader header = LuaModHeader.Parse(mod.Source ?? "", mod.Id);
            return new LuaModManifest
            {
                Id = mod.Id,
                Name = string.IsNullOrWhiteSpace(header.Name) ? mod.Id : header.Name,
                Description = header.Description ?? "",
                Version = string.IsNullOrWhiteSpace(header.Version) ? mod.Version : header.Version,
                Category = header.Category ?? "",
                Tags = header.Tags ?? "",
                Author = header.Author ?? "",
                Capabilities = header.Capabilities ?? "",
                Origin = origin,
                SeededVersion = string.IsNullOrWhiteSpace(mod.Version) ? header.Version : mod.Version,
                SeededHash = hash,
                UpdateAvailable = false,
                Active = active,
                Entry = "main.lua"
            };
        }

        /// <summary>
        /// Dotted-numeric semantic version compare (e.g. <c>1.2.0</c> vs <c>1.10.0</c>). Missing
        /// components count as 0; non-numeric components fall back to an ordinal string compare of the
        /// whole value so a malformed version never throws.
        /// </summary>
        internal static int CompareVersions(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length == 0 && b.Length == 0)
            {
                return 0;
            }

            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int count = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < count; i++)
            {
                // WHY: Missing components count as 0 (so "1.0" == "1.0.0"); a present non-numeric component
                // falls back to an ordinal compare of the whole value so a malformed version never throws.
                string sa = i < pa.Length ? pa[i].Trim() : "0";
                string sb = i < pb.Length ? pb[i].Trim() : "0";
                if (!int.TryParse(sa, out int va) || !int.TryParse(sb, out int vb))
                {
                    return string.CompareOrdinal(a, b);
                }

                if (va != vb)
                {
                    return va < vb ? -1 : 1;
                }
            }

            return 0;
        }

        /// <summary>FNV-1a 32-bit hex digest of the UTF-8 bytes вЂ” a cheap edit-detection fingerprint.</summary>
        internal static string Fnv1a(string text)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= prime;
            }

            return hash.ToString("x8");
        }
    }
}
