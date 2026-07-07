using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Runtime-agnostic implementation of <see cref="IHubModService"/>. It owns the store-driven
    /// logic (merged listing, enable/disable/delete, persist-on-save, capability masking, header
    /// parsing) and delegates only the handful of VM-specific primitives (list loaded, get live
    /// source, load/reload/unload, recent errors) to a concrete runtime adapter. This keeps the two
    /// adapters (<see cref="LuaModRuntimeHubService"/> and <see cref="LuaCsModRuntimeHubService"/>)
    /// tiny and guarantees identical behaviour across VMs.
    /// </summary>
    public abstract class HubModServiceBase : IHubModService
    {
        private readonly ILuaModSourceStore _store;
        private readonly LuaCapabilities _grant;
        private readonly bool _allowFull;

        /// <param name="store">Package store (source + manifest). Null falls back to no persistence.</param>
        /// <param name="grant">Capability ceiling applied to every mod loaded through the UI.</param>
        /// <param name="allowFull">When false, <see cref="LuaCapabilities.Full"/> is stripped from every load.</param>
        protected HubModServiceBase(ILuaModSourceStore store, LuaCapabilities grant, bool allowFull)
        {
            _store = store;
            _grant = grant;
            _allowFull = allowFull;
        }

        /// <inheritdoc />
        public event Action ModsChanged;

        /// <inheritdoc />
        public abstract bool IsSupported { get; }

        /// <inheritdoc />
        public abstract bool IsLoaded(string id);

        /// <inheritdoc />
        public abstract string RecentErrors(string id);

        /// <summary>Live status of every currently loaded mod, mapped onto the shared projection.</summary>
        protected abstract IReadOnlyList<HubLoadedInfo> GetLoaded();

        /// <summary>Returns a loaded mod's live source, or false when the mod is not loaded.</summary>
        protected abstract bool TryGetLiveSource(string id, out string source);

        /// <summary>Loads a fresh mod with the given capability tier (throws on a Lua error).</summary>
        protected abstract void RuntimeLoad(string id, string code, LuaCapabilities caps);

        /// <summary>Reloads a loaded mod's code, keeping its tier (throws on a Lua error).</summary>
        protected abstract void RuntimeReload(string id, string code);

        /// <summary>Unloads a loaded mod. Returns false when it was not loaded.</summary>
        protected abstract bool RuntimeUnload(string id);

        /// <summary>Adapters call this from their runtime load/unload events so the UI live-refreshes.</summary>
        protected void RaiseChanged()
        {
            ModsChanged?.Invoke();
        }

        /// <inheritdoc />
        public IReadOnlyList<HubModRecord> ListMods()
        {
            Dictionary<string, HubModRecord> byId = new(StringComparer.Ordinal);

            foreach (LuaModManifest manifest in SafeStoreList())
            {
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    continue;
                }

                string id = manifest.Id.Trim();
                byId[id] = new HubModRecord
                {
                    Id = id,
                    Name = manifest.Name,
                    Category = manifest.Category,
                    Tags = manifest.Tags,
                    Description = manifest.Description,
                    Author = manifest.Author,
                    Version = manifest.Version,
                    Origin = manifest.Origin,
                    Capabilities = manifest.Capabilities,
                    StoredActive = manifest.Active,
                    IsStored = true
                };
            }

            foreach (HubLoadedInfo info in GetLoaded())
            {
                string id = (info.Id ?? "").Trim();
                if (id.Length == 0)
                {
                    continue;
                }

                if (!byId.TryGetValue(id, out HubModRecord record))
                {
                    record = new HubModRecord { Id = id };
                    byId[id] = record;
                }

                record.IsLoaded = true;
                record.Handlers = info.Handlers;
                record.Timers = info.Timers;
                record.Errors = info.Errors;
                record.Capabilities = info.Capabilities.ToString();
            }

            List<HubModRecord> result = new(byId.Count);
            foreach (HubModRecord record in byId.Values)
            {
                EnrichFromHeader(record);
                result.Add(record);
            }

            result.Sort((a, b) =>
            {
                int byCategory = string.Compare(
                    CategoryOrDefault(a.Category), CategoryOrDefault(b.Category), StringComparison.OrdinalIgnoreCase);
                return byCategory != 0
                    ? byCategory
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <inheritdoc />
        public bool TryGetSource(string id, out string source)
        {
            string modId = (id ?? "").Trim();
            if (modId.Length != 0 && TryGetLiveSource(modId, out source) && source != null)
            {
                return true;
            }

            if (modId.Length != 0 && _store != null &&
                _store.TryLoad(modId, out source, out _) && source != null)
            {
                return true;
            }

            source = "";
            return false;
        }

        /// <inheritdoc />
        public void SaveOrReload(string id, string code)
        {
            string modId = (id ?? "").Trim();
            if (modId.Length == 0)
            {
                throw new ArgumentException("A mod id is required (set 'id:' in the @coreai header).", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("Mod code is required.", nameof(code));
            }

            LuaCapabilities caps = EffectiveCapabilities(code);
            if (IsLoaded(modId))
            {
                RuntimeReload(modId, code);
            }
            else
            {
                RuntimeLoad(modId, code, caps);
            }

            Persist(modId, code, caps, true);
            RaiseChanged();
        }

        /// <inheritdoc />
        public void Enable(string id)
        {
            string modId = (id ?? "").Trim();
            if (modId.Length == 0 || IsLoaded(modId))
            {
                return;
            }

            if (_store == null || !_store.TryLoad(modId, out string source, out _) ||
                string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException($"Mod '{modId}' has no stored source to enable.");
            }

            RuntimeLoad(modId, source, EffectiveCapabilities(source));
            SafeSetActive(modId, true);
            RaiseChanged();
        }

        /// <inheritdoc />
        public void Disable(string id)
        {
            string modId = (id ?? "").Trim();
            if (modId.Length == 0)
            {
                return;
            }

            RuntimeUnload(modId);
            SafeSetActive(modId, false);
            RaiseChanged();
        }

        /// <inheritdoc />
        public bool Delete(string id)
        {
            string modId = (id ?? "").Trim();
            if (modId.Length == 0)
            {
                return false;
            }

            RuntimeUnload(modId);
            try
            {
                _store?.Delete(modId);
            }
            catch
            {
                // Best-effort: a store failure must not break the UI action.
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Writes source + a header-derived manifest to the store, preserving bundling markers.</summary>
        private void Persist(string modId, string code, LuaCapabilities caps, bool active)
        {
            if (_store == null)
            {
                return;
            }

            LuaModHeader header = LuaModHeader.Parse(code, modId);
            LuaModManifest manifest = new()
            {
                Id = modId,
                Name = string.IsNullOrWhiteSpace(header.Name) ? modId : header.Name,
                Description = header.Description ?? "",
                Version = header.Version ?? "",
                Category = header.Category ?? "",
                Tags = header.Tags ?? "",
                Author = header.Author ?? "",
                Capabilities = caps.ToString(),
                Active = active
            };

            // Preserve origin/seed markers already recorded for this package so persisting a user edit
            // does not erase that the mod was seeded from resources/streamingassets/etc.
            try
            {
                if (_store.TryLoad(modId, out _, out LuaModManifest existing) && existing != null)
                {
                    manifest.Origin = existing.Origin;
                    manifest.SeededVersion = existing.SeededVersion;
                    manifest.SeededHash = existing.SeededHash;
                    manifest.Entry = string.IsNullOrEmpty(existing.Entry) ? manifest.Entry : existing.Entry;
                }
            }
            catch
            {
                // Ignore: fall back to a fresh manifest when the existing entry cannot be read.
            }

            try
            {
                _store.Save(modId, code, manifest);
            }
            catch
            {
                // Best-effort persistence; the mod is already loaded in the runtime.
            }
        }

        /// <summary>Fills empty display fields from the mod's <c>@coreai</c> header (parsed from its source).</summary>
        private void EnrichFromHeader(HubModRecord record)
        {
            if (!TryGetSource(record.Id, out string source) || string.IsNullOrEmpty(source))
            {
                if (string.IsNullOrWhiteSpace(record.Name))
                {
                    record.Name = record.Id;
                }

                return;
            }

            LuaModHeader header = LuaModHeader.Parse(source, record.Id);
            if (string.IsNullOrWhiteSpace(record.Name))
            {
                record.Name = string.IsNullOrWhiteSpace(header.Name) ? record.Id : header.Name;
            }

            if (string.IsNullOrWhiteSpace(record.Category))
            {
                record.Category = header.Category ?? "";
            }

            if (string.IsNullOrWhiteSpace(record.Tags))
            {
                record.Tags = header.Tags ?? "";
            }

            if (string.IsNullOrWhiteSpace(record.Description))
            {
                record.Description = header.Description ?? "";
            }

            if (string.IsNullOrWhiteSpace(record.Author))
            {
                record.Author = header.Author ?? "";
            }

            if (string.IsNullOrWhiteSpace(record.Version))
            {
                record.Version = header.Version ?? "";
            }

            if (string.IsNullOrWhiteSpace(record.Capabilities))
            {
                record.Capabilities = header.Capabilities ?? "";
            }
        }

        /// <summary>Header capability request masked by the host grant and (unless allowed) stripped of Full.</summary>
        private LuaCapabilities EffectiveCapabilities(string code)
        {
            LuaModHeader header = LuaModHeader.Parse(code, "");
            LuaCapabilities requested = ParseCaps(header.Capabilities);
            LuaCapabilities effective = requested & _grant;
            if (!_allowFull)
            {
                effective &= ~LuaCapabilities.Full;
            }

            return effective;
        }

        private static LuaCapabilities ParseCaps(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return LuaCapabilities.None;
            }

            LuaCapabilities caps = LuaCapabilities.None;
            string[] parts = text.Replace(',', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (Enum.TryParse(part, true, out LuaCapabilities parsed))
                {
                    caps |= parsed;
                }
            }

            return caps;
        }

        private IReadOnlyList<LuaModManifest> SafeStoreList()
        {
            if (_store == null)
            {
                return Array.Empty<LuaModManifest>();
            }

            try
            {
                return _store.List() ?? Array.Empty<LuaModManifest>();
            }
            catch
            {
                return Array.Empty<LuaModManifest>();
            }
        }

        private void SafeSetActive(string modId, bool active)
        {
            try
            {
                _store?.SetActive(modId, active);
            }
            catch
            {
                // Best-effort: dormant/active flag is a convenience, not correctness-critical.
            }
        }

        /// <summary>Category used for grouping/sorting; blank categories bucket under a stable default.</summary>
        internal static string CategoryOrDefault(string category)
        {
            return string.IsNullOrWhiteSpace(category) ? "Uncategorized" : category.Trim();
        }

        /// <summary>Formats a runtime error list (oldest first) into a compact multi-line summary.</summary>
        protected static string FormatErrors(IEnumerable<(string error, int consecutive, DateTime atUtc)> errors)
        {
            StringBuilder sb = new();
            foreach ((string error, int consecutive, DateTime atUtc) in errors)
            {
                sb.Append(atUtc.ToLocalTime().ToString("HH:mm:ss"))
                    .Append("  x").Append(consecutive).Append("  ")
                    .Append(error).Append('\n');
            }

            return sb.ToString().TrimEnd();
        }
    }
}
