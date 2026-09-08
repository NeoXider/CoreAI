using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CoreAI.Ai
{
    /// <summary>
    /// Resolves a tool name to an already-registered <see cref="ILlmTool"/> instance. An authored skill
    /// references existing tools by name; the coordinator uses this to turn a persisted allowlist back
    /// into a runnable <see cref="SkillSet"/>. Returning null for an unknown name lets the coordinator
    /// reject a skill that lists a tool the role does not actually have.
    /// </summary>
    public delegate ILlmTool SkillToolResolver(string toolName);

    /// <summary>
    /// In-memory brain behind the <c>manage_skills</c> tool. It owns the role's live
    /// <see cref="MutableSkillCatalog"/>, persists authored skills through <see cref="ISkillStore"/>,
    /// records a revision per edit in <see cref="ILuaScriptVersionStore"/> (keyed by skill id, exactly
    /// like Lua mods), and resolves a skill's tool-name allowlist against the tools already registered
    /// for the role. A skill the model creates or updates is added to the same catalog the role's
    /// <c>read_skill</c> reads from, so the model can reuse what it just authored within the same session.
    /// <para>
    /// Tool resolution is strict by default: a skill referencing a tool the role does not have is rejected
    /// (the model cannot invent C# tools). The coordinator is portable (no Unity / Lua dependency);
    /// the host supplies the persistence and version stores.
    /// </para>
    /// </summary>
    public sealed class SkillAuthoringCoordinator
    {
        private const string VersionKeyPrefix = "skill:";

        private static readonly ConditionalWeakTable<MutableSkillCatalog, object> CatalogLocks = new();
        private static readonly ConditionalWeakTable<ILuaScriptVersionStore, object> RevisionLocks = new();
        private static readonly ConditionalWeakTable<MutableSkillCatalog, Dictionary<string, SkillRecord>> SessionRecords = new();
        private readonly Dictionary<string, SkillRecord> _sessionRecords;
        private readonly object _lock;
        private readonly object _revisionLock;
        private readonly MutableSkillCatalog _catalog;
        private readonly ISkillStore _store;
        private readonly ILuaScriptVersionStore _versionStore;
        private readonly SkillToolResolver _toolResolver;
        private readonly bool _requireKnownTools;

        /// <param name="catalog">Live catalog the role's read_skill / call_skill_tool read from.</param>
        /// <param name="store">Persistent skill store; write failures prevent publication. Null provides session-only skills.</param>
        /// <param name="versionStore">
        /// Version store for skill revisions, keyed by <c>skill:&lt;id&gt;</c>. Optional; when null no
        /// history is recorded but create/update still work.
        /// </param>
        /// <param name="toolResolver">
        /// Resolves an allowlisted tool name to a registered tool instance. Required so authored skills
        /// can only reference existing tools.
        /// </param>
        /// <param name="requireKnownTools">
        /// When true (default), create/update fail if any listed tool name is unknown to the role.
        /// </param>
        public SkillAuthoringCoordinator(
            MutableSkillCatalog catalog,
            ISkillStore store,
            ILuaScriptVersionStore versionStore,
            SkillToolResolver toolResolver,
            bool requireKnownTools = true)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _lock = CatalogLocks.GetValue(_catalog, _ => new object());
            _store = store ?? new NullSkillStore();
            if (_store is NullSkillStore)
                _sessionRecords = SessionRecords.GetValue(_catalog, _ => new Dictionary<string, SkillRecord>(StringComparer.OrdinalIgnoreCase));
            _versionStore = versionStore;
            _revisionLock = versionStore == null ? new object() : RevisionLocks.GetValue(versionStore, _ => new object());
            _toolResolver = toolResolver ?? (_ => null);
            _requireKnownTools = requireKnownTools;
        }

        /// <summary>
        /// Loads every persisted skill into the live catalog (and seeds version history) at startup, so
        /// skills authored in a previous session reappear in the agent's <c>read_skill</c> catalog.
        /// Invalid skill records are skipped. Revision-store failures or ambiguous legacy keys are
        /// surfaced before publishing the affected skill; existing history is never guessed or merged.
        /// </summary>
        public int RehydrateFromStore()
        {
            SkillStoreCallbackContext.ThrowIfActive();
            lock (_lock)
            {
                List<KeyValuePair<SkillRecord, SkillSet>> prepared = new();
                foreach (SkillRecord record in _store.List())
                {
                    if (record != null && !string.IsNullOrWhiteSpace(record.Id) &&
                        TryBuildSkill(record, out SkillSet skill, out _, true))
                    {
                        prepared.Add(new KeyValuePair<SkillRecord, SkillSet>(record, skill));
                    }
                }

                // WHY: Resolve tools and read skill storage before the revision gate; writers acquire
                // the skill-store gate first. One key snapshot serves the whole startup batch.
                lock (_revisionLock)
                {
                    Dictionary<string, string> keys = _versionStore == null ? null : ReadVersionKeys();
                    foreach (KeyValuePair<SkillRecord, SkillSet> pair in prepared)
                    {
                        SeedVersion(pair.Key, keys);
                        _catalog.AddOrReplace(pair.Value);
                    }
                }
                return prepared.Count;
            }
        }

        /// <summary>
        /// Returns a snapshot of the current authored skills in the catalog (id, description, version,
        /// tool names). Host-registered skills are included too, with version 0. Takes one coherent
        /// batch snapshot of the store per call; only live catalog entries are merged.
        /// </summary>
        public IReadOnlyList<SkillRecord> ListSkills()
        {
            SkillStoreCallbackContext.ThrowIfActive();
            lock (_lock)
            {
                Dictionary<string, SkillRecord> snapshot = new(StringComparer.OrdinalIgnoreCase);
                foreach (SkillRecord stored in _store.List())
                {
                    if (stored == null || string.IsNullOrWhiteSpace(stored.Id))
                    {
                        continue;
                    }

                    if (!snapshot.ContainsKey(stored.Id))
                    {
                        snapshot[stored.Id] = stored;
                    }
                }

                List<SkillRecord> result = new();
                foreach (SkillSet skill in _catalog)
                {
                    if (skill == null || string.IsNullOrWhiteSpace(skill.Name)) continue;
                    SkillRecord record = ReadRecordSnapshot(skill.Name, snapshot);
                    if (record != null) result.Add(record);
                }
                return result.AsReadOnly();
            }
        }

        /// <summary>Returns one coherent persisted/session snapshot, or the host-registered definition.</summary>
        public SkillRecord GetSkill(string id)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock) { return ReadRecordSnapshot(id.Trim()); }
        }

        private SkillRecord ReadRecordSnapshot(string id)
        {
            return ReadRecordSnapshot(id, null);
        }

        private SkillRecord ReadRecordSnapshot(string id, Dictionary<string, SkillRecord> batch)
        {
            if (batch != null)
            {
                if (batch.TryGetValue(id, out SkillRecord batched) && batched != null) return CopyRecord(batched);
            }
            else if (_store.TryLoad(id, out SkillRecord stored) && stored != null) return CopyRecord(stored);
            if (_sessionRecords != null && _sessionRecords.TryGetValue(id, out SkillRecord session))
                return CopyRecord(session);
            SkillSet skill = _catalog.Get(id);
            return skill == null ? null : new SkillRecord(skill.Name, skill.Description, skill.Instructions,
                skill.ToolNames, sections: skill.Sections);
        }

        private static SkillRecord CopyRecord(SkillRecord record) => new(record.Id, record.Description,
            record.Instructions, record.ToolNames, record.Version, record.Sections);
        /// <summary>
        /// Creates a new authored skill: validates the tool allowlist, adds it to the live catalog,
        /// persists it (revision 0), and records the original revision. Fails when the id is blank, the
        /// id already exists, or a referenced tool is unknown.
        /// </summary>
        public SkillAuthoringResult Create(string id, string description, string instructions,
            IEnumerable<string> toolNames)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("create: 'name' is required.");
            }

            string skillId = id.Trim();
            lock (_lock)
            {
                SkillSet preparedSkill = null;
                bool? revisionRecorded = null;
                string revisionWarning = "";
                SkillAuthoringResult result = _store.MutateAndPublish(
                    skillId,
                    current =>
                    {
                        if (_catalog.Get(skillId) != null || current != null)
                        {
                            return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                SkillAuthoringResult.Failure(
                                    $"create: a skill named '{skillId}' already exists. Use update to revise it."));
                        }

                        SkillRecord record = new(skillId, description ?? "", instructions ?? "",
                            toolNames ?? new List<string>());
                        if (!TryBuildSkill(record, out SkillSet skill, out string error, false))
                        {
                            return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                SkillAuthoringResult.Failure($"create: {error}"));
                        }

                        ValidateCatalogCandidate(skill);
                        preparedSkill = skill;
                        SkillAuthoringResult created =
                            SkillAuthoringResult.Ok(record, $"Skill '{skillId}' created (version 0).");
                        return SkillStoreMutation<SkillAuthoringResult>.SaveRecord(record, created);
                    }, committed =>
                    {
                        if (committed.Success && preparedSkill != null)
                        {
                            _catalog.AddOrReplace(preparedSkill);
                            if (_sessionRecords != null) _sessionRecords[preparedSkill.Name] = CopyRecord(committed.Record);
                            RecordVersionAfterCommit(committed.Record, out revisionRecorded, out revisionWarning);
                        }
                    });

                return result.Success ? result.WithRevisionOutcome(revisionRecorded, revisionWarning) : result;
            }
        }

        /// <summary>
        /// Updates an existing authored skill. Null arguments leave the corresponding field unchanged;
        /// a non-null <paramref name="toolNames"/> replaces the allowlist. Auto-increments the version
        /// and records a new revision. Instructions replaces only the main document, preserving reference
        /// documents. Fails when the skill is unknown or a referenced tool is unknown.
        /// </summary>
        public SkillAuthoringResult Update(string id, string description, string instructions,
            IEnumerable<string> toolNames)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("update: 'name' is required.");
            }

            string skillId = id.Trim();
            lock (_lock)
            {
                SkillSet preparedSkill = null;
                bool? revisionRecorded = null;
                string revisionWarning = "";
                SkillAuthoringResult result = _store.MutateAndPublish(
                    skillId,
                    current =>
                    {
                        if (current == null && _sessionRecords != null &&
                            _sessionRecords.TryGetValue(skillId, out SkillRecord session)) current = CopyRecord(session);
                        if (current == null)
                        {
                            SkillSet existingSkill = _catalog.Get(skillId);
                            if (existingSkill == null)
                            {
                                return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                    SkillAuthoringResult.Failure(
                                        $"update: skill '{skillId}' not found. Use create to author it first."));
                            }

                            // Promote a host-registered (un-persisted) skill into an authored, versioned one.
                            current = new SkillRecord(existingSkill.Name, existingSkill.Description,
                                existingSkill.Instructions, existingSkill.ToolNames, sections: existingSkill.Sections);
                        }

                        SkillRecord revised = new(
                            current.Id,
                            description ?? current.Description,
                            instructions ?? current.Instructions,
                            toolNames != null ? new List<string>(toolNames) : current.ToolNames,
                            current.Version + 1, current.Sections);
                        if (instructions != null && revised.Sections.Count > 0)
                            revised.Sections[0] = new SkillSection(revised.Sections[0].Name, instructions);

                        if (!TryBuildSkill(revised, out SkillSet skill, out string error, false))
                        {
                            return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                SkillAuthoringResult.Failure($"update: {error}"));
                        }

                        revised.Instructions = skill.Instructions;

                        ValidateCatalogCandidate(skill);
                        preparedSkill = skill;
                        SkillAuthoringResult updated = SkillAuthoringResult.Ok(revised,
                            $"Skill '{skillId}' updated (now version {revised.Version}).");
                        return SkillStoreMutation<SkillAuthoringResult>.SaveRecord(revised, updated);
                    }, committed =>
                    {
                        if (committed.Success && preparedSkill != null)
                        {
                            _catalog.AddOrReplace(preparedSkill);
                            if (_sessionRecords != null) _sessionRecords[preparedSkill.Name] = CopyRecord(committed.Record);
                            RecordVersionAfterCommit(committed.Record, out revisionRecorded, out revisionWarning);
                        }
                    });

                return result.Success ? result.WithRevisionOutcome(revisionRecorded, revisionWarning) : result;
            }
        }

        /// <summary>Removes a skill from the live catalog and persistent store.</summary>
        public SkillAuthoringResult Delete(string id)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("delete: 'name' is required.");
            }

            string skillId = id.Trim();
            lock (_lock)
            {
                return _store.MutateAndPublish(
                    skillId,
                    current =>
                    {
                        bool inCatalog = _catalog.Get(skillId) != null;
                        if (!inCatalog && current == null)
                        {
                            return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                SkillAuthoringResult.Failure($"delete: skill '{skillId}' not found."));
                        }

                        return SkillStoreMutation<SkillAuthoringResult>.DeleteRecord(
                            SkillAuthoringResult.Ok(null, $"Skill '{skillId}' deleted."));
                    }, committed =>
                    {
                        if (committed.Success)
                        {
                            _catalog.Remove(skillId);
                            _sessionRecords?.Remove(skillId);
                        }
                    });
            }
        }

        /// <summary>Lists the recorded revisions for a skill (oldest first; revision 0 is the original).</summary>
        public IReadOnlyList<LuaScriptRevision> ListRevisions(string id)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (_versionStore == null || string.IsNullOrWhiteSpace(id))
            {
                return Array.Empty<LuaScriptRevision>();
            }

            lock (_lock)
            {
                lock (_revisionLock)
                {
                    string key = ResolveVersionKey(id.Trim(), ReadVersionKeys());
                    return _versionStore.TryGetSnapshot(key, out LuaScriptVersionRecord snap) && snap != null
                        ? snap.History
                        : Array.Empty<LuaScriptRevision>();
                }
            }
        }

        private bool TryBuildSkill(SkillRecord record, out SkillSet skill, out string error, bool allowUnknownTools)
        {
            skill = null;
            error = null;

            List<ILlmTool> tools = new();
            List<string> missing = new();
            if (record.ToolNames != null)
            {
                foreach (string toolName in record.ToolNames)
                {
                    if (string.IsNullOrWhiteSpace(toolName))
                    {
                        continue;
                    }

                    ILlmTool resolved = _toolResolver(toolName.Trim());
                    if (resolved != null)
                    {
                        tools.Add(resolved);
                    }
                    else
                    {
                        missing.Add(toolName.Trim());
                    }
                }
            }

            if (missing.Count > 0 && _requireKnownTools && !allowUnknownTools)
            {
                error = $"tool(s) not registered for this agent: {string.Join(", ", missing)}. " +
                        "A skill may only reference existing tools.";
                return false;
            }

            try
            {
                if (record.Sections != null && record.Sections.Count > 0)
                {
                    List<KeyValuePair<string, string>> parts = new();
                    foreach (SkillSection section in record.Sections)
                        parts.Add(new KeyValuePair<string, string>(section.Name, section.Content));
                    skill = SkillSet.FromTextParts(record.Id, record.Description, parts, tools.ToArray());
                }
                else skill = new SkillSet(record.Id, record.Description, record.Instructions, tools);
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Checks a prospective catalog without publishing it before the storage commit.</summary>
        private void ValidateCatalogCandidate(SkillSet skill)
        {
            List<SkillSet> proposed = new();
            foreach (SkillSet existing in _catalog)
            {
                if (!string.Equals(existing.Name, skill.Name, StringComparison.OrdinalIgnoreCase)) proposed.Add(existing);
            }
            proposed.Add(skill);
            SkillSetToolResolver.ValidateCatalog(proposed);
        }

        private void SeedVersion(SkillRecord record, Dictionary<string, string> keys)
        {
            if (_versionStore == null) return;
            string key = ResolveVersionKey(record.Id, keys);
            _versionStore.SeedOriginal(key, SerializeRevision(record));
            keys[record.Id] = key;
        }

        private void RecordVersionAfterCommit(SkillRecord record, out bool? recorded, out string warning)
        {
            recorded = null;
            warning = "";
            if (_versionStore == null) return;
            try
            {
                lock (_revisionLock)
                {
                    // WHY: Case aliases share the surviving key even after deletion. Resolve and write
                    // under one per-store gate so concurrent coordinators cannot create two aliases.
                    Dictionary<string, string> keys = ReadVersionKeys();
                    string key = ResolveVersionKey(record.Id, keys);
                    if (record.Version == 0)
                    {
                        if (_versionStore.TryGetSnapshot(key, out LuaScriptVersionRecord existing) &&
                            existing != null && existing.History.Count > 0)
                            _versionStore.RecordSuccessfulExecution(key, SerializeRevision(record));
                        else SeedVersion(record, keys);
                    }
                    else _versionStore.RecordSuccessfulExecution(key, SerializeRevision(record));
                }
                recorded = true;
            }
            catch (Exception ex)
            {
                recorded = false;
                warning = "The skill change was committed, but revision history could not be recorded: " +
                    ex.Message + ". Do not repeat the edit; inspect or repair revision history separately.";
            }
        }

        private Dictionary<string, string> ReadVersionKeys()
        {
            Dictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase);
            foreach (string key in _versionStore.GetKnownKeys())
            {
                if (key == null || !key.StartsWith(VersionKeyPrefix, StringComparison.Ordinal)) continue;
                string id = key.Substring(VersionKeyPrefix.Length);
                if (keys.TryGetValue(id, out string existing) && !string.Equals(existing, key, StringComparison.Ordinal))
                    keys[id] = null;
                else if (!keys.ContainsKey(id)) keys.Add(id, key);
            }
            return keys;
        }

        private static string ResolveVersionKey(string id, Dictionary<string, string> keys)
        {
            if (!keys.TryGetValue(id, out string key)) return VersionKey(id);
            if (key == null)
                throw new InvalidOperationException($"Ambiguous revision history for skill '{id}': multiple case aliases exist. " +
                    "Repair the conflicting history keys explicitly; no history was merged or overwritten.");
            return key;
        }

        /// <summary>
        /// Serializes the revision-relevant content (instructions + allowlist) so two edits that change
        /// nothing do not record a duplicate revision (RecordSuccessfulExecution dedupes identical text).
        /// </summary>
        private static string SerializeRevision(SkillRecord record)
        {
            string tools = record.ToolNames != null ? string.Join(",", record.ToolNames) : "";
            string sections = record.Sections != null && record.Sections.Count > 0
                ? Newtonsoft.Json.JsonConvert.SerializeObject(record.Sections) : "";
            return $"# {record.Description}\n# tools: {tools}\n{record.Instructions}\n# sections: {sections}";
        }

        private static string VersionKey(string id)
        {
            return VersionKeyPrefix + id;
        }
    }

    /// <summary>Outcome of a <see cref="SkillAuthoringCoordinator"/> mutation.</summary>
    public sealed class SkillAuthoringResult
    {
        public bool Success { get; }
        public string Message { get; }
        public SkillRecord Record { get; }
        public bool Committed => Success;
        public bool? RevisionRecorded { get; }
        public string Warning { get; }

        private SkillAuthoringResult(bool success, string message, SkillRecord record,
            bool? revisionRecorded = null, string warning = "")
        {
            Success = success;
            Message = message;
            Record = record;
            RevisionRecorded = revisionRecorded;
            Warning = warning;
        }

        internal SkillAuthoringResult WithRevisionOutcome(bool? recorded, string warning) =>
            new(Success, string.IsNullOrEmpty(warning) ? Message : Message + " " + warning, Record, recorded, warning);

        public static SkillAuthoringResult Ok(SkillRecord record, string message)
        {
            return new SkillAuthoringResult(true, message, record);
        }

        public static SkillAuthoringResult Failure(string message)
        {
            return new SkillAuthoringResult(false, message, null);
        }
    }
}
