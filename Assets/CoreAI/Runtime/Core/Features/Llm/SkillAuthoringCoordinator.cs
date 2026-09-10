using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

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

        private static readonly ConditionalWeakTable<MutableSkillCatalog, SkillOperationGate> CatalogLocks = new();
        private static readonly ConditionalWeakTable<ILuaScriptVersionStore, SkillOperationGate> RevisionLocks = new();
        private static readonly ConditionalWeakTable<MutableSkillCatalog, Dictionary<string, SkillRecord>> SessionRecords = new();
        private readonly Dictionary<string, SkillRecord> _sessionRecords;
        private readonly SkillOperationGate _lock;
        private readonly SkillOperationGate _revisionLock;
        private readonly MutableSkillCatalog _catalog;
        private readonly ISkillStore _store;
        private readonly IAsyncSkillStore _asyncStore;
        private readonly IAsyncLuaScriptVersionStore _asyncVersionStore;
        private readonly ILlmAsyncMarshaler _callbackContext;
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
            bool requireKnownTools = true, ILlmAsyncMarshaler callbackContext = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _lock = CatalogLocks.GetValue(_catalog, _ => new SkillOperationGate());
            _store = store ?? new NullSkillStore();
            if (_store is NullSkillStore)
                _sessionRecords = SessionRecords.GetValue(_catalog, _ => new Dictionary<string, SkillRecord>(StringComparer.OrdinalIgnoreCase));
            _versionStore = versionStore;
            _asyncStore = _store as IAsyncSkillStore;
            _asyncVersionStore = versionStore as IAsyncLuaScriptVersionStore;
            _callbackContext = callbackContext ?? PassThroughLlmAsyncMarshaler.Instance;
            ILuaScriptVersionStore revisionIdentity = versionStore is InlineAsyncLuaScriptVersionStoreAdapter adapter
                ? adapter.OrderingIdentity : versionStore;
            _revisionLock = revisionIdentity == null ? new SkillOperationGate() : RevisionLocks.GetValue(revisionIdentity, _ => new SkillOperationGate());
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
            using (_lock.EnterSync())
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
                using (_revisionLock.EnterSync())
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
            using (_lock.EnterSync())
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
            using (_lock.EnterSync()) { return ReadRecordSnapshot(id.Trim()); }
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
            return ReadInMemoryRecordSnapshot(id);
        }

        private SkillRecord ReadInMemoryRecordSnapshot(string id)
        {
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
            using (_lock.EnterSync())
            {
                SkillSet preparedSkill = null;
                bool? revisionRecorded = null;
                string revisionWarning = "";
                SkillAuthoringResult result = _store.MutateAndPublish(
                    skillId,
                    current => PrepareCreate(skillId, description, instructions, toolNames, current, out preparedSkill), committed =>
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
            using (_lock.EnterSync())
            {
                SkillSet preparedSkill = null;
                bool? revisionRecorded = null;
                string revisionWarning = "";
                SkillAuthoringResult result = _store.MutateAndPublish(
                    skillId,
                    current => PrepareUpdate(skillId, description, instructions, toolNames, current, out preparedSkill), committed =>
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
            using (_lock.EnterSync())
            {
                return _store.MutateAndPublish(
                    skillId,
                    current => PrepareDelete(skillId, current), committed =>
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

            using (_lock.EnterSync())
            {
                using (_revisionLock.EnterSync())
                {
                    string key = ResolveVersionKey(id.Trim(), ReadVersionKeys());
                    return _versionStore.TryGetSnapshot(key, out LuaScriptVersionRecord snap) && snap != null
                        ? snap.History
                        : Array.Empty<LuaScriptRevision>();
                }
            }
        }

        private void EnsureAsyncStores(CancellationToken token)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            token.ThrowIfCancellationRequested();
            if (_asyncStore == null || (_versionStore != null && _asyncVersionStore == null))
                throw new InvalidOperationException("Async skill authoring requires async skill and version stores. " +
                    "For a known fast, nonblocking legacy store, explicitly supply an InlineAsync adapter.");
        }

        /// <summary>Loads one complete skill without synchronous persistence calls.</summary>
        public Task<SkillRecord> GetSkillAsync(string id, CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);
            return _callbackContext.InvokeAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(id)) return null;
                using (await _lock.EnterAsync(cancellationToken))
                {
                    string key = id.Trim();
                    SkillRecord stored = await _asyncStore.LoadAsync(key, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stored != null) return CopyRecord(stored);
                    return ReadInMemoryRecordSnapshot(key);
                }
            }, cancellationToken);
        }

        /// <summary>Loads one batch; never performs a point read for each catalog entry.</summary>
        public Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);
            return _callbackContext.InvokeAsync<IReadOnlyList<SkillRecord>>(async () =>
            {
                using (await _lock.EnterAsync(cancellationToken))
                {
                    Dictionary<string, SkillRecord> snapshot = new(StringComparer.OrdinalIgnoreCase);
                    foreach (SkillRecord stored in await _asyncStore.ListAsync(cancellationToken))
                        if (stored != null && !string.IsNullOrWhiteSpace(stored.Id) && !snapshot.ContainsKey(stored.Id)) snapshot.Add(stored.Id, stored);
                    cancellationToken.ThrowIfCancellationRequested();
                    List<SkillRecord> result = new();
                    foreach (SkillSet skill in _catalog)
                    {
                        if (skill == null || string.IsNullOrWhiteSpace(skill.Name)) continue;
                        SkillRecord record = ReadRecordSnapshot(skill.Name, snapshot);
                        if (record != null) result.Add(record);
                    }
                    return result.AsReadOnly();
                }
            }, cancellationToken);
        }

        /// <summary>Prepares all records and revision seeds before publishing the hydrated catalog.</summary>
        public Task<int> RehydrateFromStoreAsync(CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);
            return _callbackContext.InvokeAsync(async () =>
            {
                using (await _lock.EnterAsync(cancellationToken))
                {
                    List<KeyValuePair<SkillRecord, SkillSet>> prepared = new();
                    foreach (SkillRecord record in await _asyncStore.ListAsync(cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (record != null && !string.IsNullOrWhiteSpace(record.Id) && TryBuildSkill(record, out SkillSet skill, out _, true))
                            prepared.Add(new KeyValuePair<SkillRecord, SkillSet>(record, skill));
                    }
                    using (await _revisionLock.EnterAsync(cancellationToken))
                    {
                        Dictionary<string, string> keys = _asyncVersionStore == null ? null : await ReadVersionKeysAsync(cancellationToken);
                        foreach (KeyValuePair<SkillRecord, SkillSet> pair in prepared)
                            if (_asyncVersionStore != null)
                            {
                                string key = ResolveVersionKey(pair.Key.Id, keys);
                                await _asyncVersionStore.SeedOriginalAsync(key, SerializeRevision(pair.Key), cancellationToken: cancellationToken);
                                keys[pair.Key.Id] = key;
                            }
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (KeyValuePair<SkillRecord, SkillSet> pair in prepared) _catalog.AddOrReplace(pair.Value);
                    }
                    return prepared.Count;
                }
            }, cancellationToken);
        }

        /// <summary>Reads revision history through the async version boundary.</summary>
        public Task<IReadOnlyList<LuaScriptRevision>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);
            return _callbackContext.InvokeAsync<IReadOnlyList<LuaScriptRevision>>(async () =>
            {
                if (_asyncVersionStore == null || string.IsNullOrWhiteSpace(id)) return Array.Empty<LuaScriptRevision>();
                using (await _lock.EnterAsync(cancellationToken))
                using (await _revisionLock.EnterAsync(cancellationToken))
                {
                    string key = ResolveVersionKey(id.Trim(), await ReadVersionKeysAsync(cancellationToken));
                    LuaScriptVersionRecord snapshot = await _asyncVersionStore.GetSnapshotAsync(key, cancellationToken);
                    return snapshot?.History ?? (IReadOnlyList<LuaScriptRevision>)Array.Empty<LuaScriptRevision>();
                }
            }, cancellationToken);
        }

        private async Task<Dictionary<string, string>> ReadVersionKeysAsync(CancellationToken token)
        {
            Dictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase);
            foreach (string key in await _asyncVersionStore.GetKnownKeysAsync(token))
            {
                if (key == null || !key.StartsWith(VersionKeyPrefix, StringComparison.Ordinal)) continue;
                string id = key.Substring(VersionKeyPrefix.Length);
                if (keys.TryGetValue(id, out string existing) && !string.Equals(existing, key, StringComparison.Ordinal)) keys[id] = null;
                else if (!keys.ContainsKey(id)) keys.Add(id, key);
            }
            return keys;
        }

        private async Task<(bool? Recorded, string Warning)> RecordVersionAfterCommitAsync(SkillRecord record)
        {
            if (_asyncVersionStore == null) return (null, "");
            try
            {
                using (await _revisionLock.EnterAsync(CancellationToken.None))
                {
                    string key = ResolveVersionKey(record.Id, await ReadVersionKeysAsync(CancellationToken.None));
                    LuaScriptVersionRecord existing = record.Version == 0 ? await _asyncVersionStore.GetSnapshotAsync(key) : null;
                    if (record.Version == 0 && (existing == null || existing.History.Count == 0))
                        await _asyncVersionStore.SeedOriginalAsync(key, SerializeRevision(record));
                    else await _asyncVersionStore.RecordSuccessfulExecutionAsync(key, SerializeRevision(record));
                }
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, "The skill change was committed, but revision history could not be recorded: " +
                    ex.Message + ". Do not repeat the edit; inspect or repair revision history separately.");
            }
        }

        /// <summary>Awaitable create with durable publication and cooperative pre-commit cancellation.</summary>
        public Task<SkillAuthoringResult> CreateAsync(string id, string description, string instructions, IEnumerable<string> toolNames, CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);
            List<string> snapshot = toolNames == null ? null : new List<string>(toolNames);
            return _callbackContext.InvokeAsync(() => CreateCoreAsync(id, description, instructions, snapshot, cancellationToken), cancellationToken);
        }
        private async Task<SkillAuthoringResult> CreateCoreAsync(string id, string description, string instructions, IEnumerable<string> toolNames, CancellationToken cancellationToken)
        {
            EnsureAsyncStores(cancellationToken);
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("create: 'name' is required.");
            }

            string skillId = id.Trim();
            using (await _lock.EnterAsync(cancellationToken))
            {
                SkillSet preparedSkill = null;
                bool? revisionRecorded = null;
                string revisionWarning = "";
                SkillAuthoringResult result = await _asyncStore.MutateAndPublishAsync(
                    skillId,
                    current => PrepareCreate(skillId, description, instructions, toolNames, current, out preparedSkill), async (committed, committedToken) =>
                    {
                        if (committed.Success && preparedSkill != null)
                        {
                            _catalog.AddOrReplace(preparedSkill);
                            if (_sessionRecords != null) _sessionRecords[preparedSkill.Name] = CopyRecord(committed.Record);
                            (revisionRecorded, revisionWarning) = await RecordVersionAfterCommitAsync(committed.Record);
                        }
                    }, _callbackContext, cancellationToken);

                return result.Success ? result.WithRevisionOutcome(revisionRecorded, revisionWarning) : result;
            }
        }

        /// <summary>Awaitable update with durable publication and cooperative pre-commit cancellation.</summary>
        public Task<SkillAuthoringResult> UpdateAsync(string id, string description, string instructions, IEnumerable<string> toolNames, CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);
            List<string> snapshot = toolNames == null ? null : new List<string>(toolNames);
            return _callbackContext.InvokeAsync(() => UpdateCoreAsync(id, description, instructions, snapshot, cancellationToken), cancellationToken);
        }
        private async Task<SkillAuthoringResult> UpdateCoreAsync(string id, string description, string instructions, IEnumerable<string> toolNames, CancellationToken cancellationToken)
        {
            EnsureAsyncStores(cancellationToken);
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("update: 'name' is required.");
            }

            string skillId = id.Trim();
            using (await _lock.EnterAsync(cancellationToken))
            {
                SkillSet preparedSkill = null;
                bool? revisionRecorded = null;
                string revisionWarning = "";
                SkillAuthoringResult result = await _asyncStore.MutateAndPublishAsync(
                    skillId,
                    current => PrepareUpdate(skillId, description, instructions, toolNames, current, out preparedSkill), async (committed, committedToken) =>
                    {
                        if (committed.Success && preparedSkill != null)
                        {
                            _catalog.AddOrReplace(preparedSkill);
                            if (_sessionRecords != null) _sessionRecords[preparedSkill.Name] = CopyRecord(committed.Record);
                            (revisionRecorded, revisionWarning) = await RecordVersionAfterCommitAsync(committed.Record);
                        }
                    }, _callbackContext, cancellationToken);

                return result.Success ? result.WithRevisionOutcome(revisionRecorded, revisionWarning) : result;
            }
        }

        /// <summary>Awaitable delete with durable publication and cooperative pre-commit cancellation.</summary>
        public Task<SkillAuthoringResult> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            EnsureAsyncStores(cancellationToken);

            return _callbackContext.InvokeAsync(() => DeleteCoreAsync(id, cancellationToken), cancellationToken);
        }
        private async Task<SkillAuthoringResult> DeleteCoreAsync(string id, CancellationToken cancellationToken)
        {
            EnsureAsyncStores(cancellationToken);
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("delete: 'name' is required.");
            }

            string skillId = id.Trim();
            using (await _lock.EnterAsync(cancellationToken))
            {
                return await _asyncStore.MutateAndPublishAsync(
                    skillId,
                    current => PrepareDelete(skillId, current), (committed, committedToken) =>
                    {
                        if (committed.Success)
                        {
                            _catalog.Remove(skillId);
                            _sessionRecords?.Remove(skillId);
                        }
                        return Task.CompletedTask;
                    }, _callbackContext, cancellationToken);
            }
        }
        private SkillStoreMutation<SkillAuthoringResult> PrepareCreate(string skillId, string description, string instructions, IEnumerable<string> toolNames, SkillRecord current, out SkillSet preparedSkill)
        {
            preparedSkill = null;
            if (_catalog.Get(skillId) != null || current != null)
            {
                return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                    SkillAuthoringResult.Failure(
                        $"create: a skill named '{skillId}' already exists. Use update to revise it."));
            }

            SkillRecord record = new(skillId, description ?? "", instructions ?? "",
                toolNames ?? Array.Empty<string>());
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
        }

        private SkillStoreMutation<SkillAuthoringResult> PrepareUpdate(string skillId, string description, string instructions, IEnumerable<string> toolNames, SkillRecord current, out SkillSet preparedSkill)
        {
            preparedSkill = null;
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

                // WHY: A host-registered skill becomes persistent when it is first edited.
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
        }

        private SkillStoreMutation<SkillAuthoringResult> PrepareDelete(string skillId, SkillRecord current)
        {
            bool inCatalog = _catalog.Get(skillId) != null;
            if (!inCatalog && current == null)
            {
                return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                    SkillAuthoringResult.Failure($"delete: skill '{skillId}' not found."));
            }

            return SkillStoreMutation<SkillAuthoringResult>.DeleteRecord(
                SkillAuthoringResult.Ok(null, $"Skill '{skillId}' deleted."));
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
                using (_revisionLock.EnterSync())
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
