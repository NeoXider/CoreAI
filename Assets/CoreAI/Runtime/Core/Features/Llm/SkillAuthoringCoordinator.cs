using System;
using System.Collections.Generic;

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
    /// (the model cannot invent C# tools). The coordinator is portable (no Unity / MoonSharp dependency);
    /// the host supplies the persistence and version stores.
    /// </para>
    /// </summary>
    public sealed class SkillAuthoringCoordinator
    {
        private const string VersionKeyPrefix = "skill:";

        private readonly object _lock = new();
        private readonly MutableSkillCatalog _catalog;
        private readonly ISkillStore _store;
        private readonly ILuaScriptVersionStore _versionStore;
        private readonly SkillToolResolver _toolResolver;
        private readonly bool _requireKnownTools;

        /// <param name="catalog">Live catalog the role's read_skill / call_skill_tool read from.</param>
        /// <param name="store">Persistent skill store (best-effort; may be a null implementation).</param>
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
            _store = store ?? new NullSkillStore();
            _versionStore = versionStore;
            _toolResolver = toolResolver ?? (_ => null);
            _requireKnownTools = requireKnownTools;
        }

        /// <summary>
        /// Loads every persisted skill into the live catalog (and seeds version history) at startup, so
        /// skills authored in a previous session reappear in the agent's <c>read_skill</c> catalog.
        /// Best-effort: a single bad record is skipped, never aborting rehydration.
        /// </summary>
        public int RehydrateFromStore()
        {
            int loaded = 0;
            foreach (SkillRecord record in _store.List())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Id))
                {
                    continue;
                }

                if (TryBuildSkill(record, out SkillSet skill, out _, true))
                {
                    _catalog.AddOrReplace(skill);
                    SeedVersion(record);
                    loaded++;
                }
            }

            return loaded;
        }

        /// <summary>
        /// Returns a snapshot of the current authored skills in the catalog (id, description, version,
        /// tool names). Host-registered skills are included too, with version 0.
        /// </summary>
        public IReadOnlyList<SkillRecord> ListSkills()
        {
            List<SkillRecord> result = new();
            foreach (SkillSet skill in _catalog)
            {
                if (skill == null || string.IsNullOrWhiteSpace(skill.Name))
                {
                    continue;
                }

                int version = 0;
                if (_store.TryLoad(skill.Name, out SkillRecord stored) && stored != null)
                {
                    version = stored.Version;
                }

                result.Add(new SkillRecord(skill.Name, skill.Description, skill.Instructions,
                    skill.ToolNames, version));
            }

            return result;
        }

        /// <summary>Returns the persisted record for a skill, or null when absent.</summary>
        public SkillRecord GetSkill(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (_store.TryLoad(id.Trim(), out SkillRecord record) && record != null)
            {
                return record;
            }

            // Fall back to a catalog-only (host-registered) skill so `get` still describes it.
            SkillSet skill = _catalog.Get(id.Trim());
            return skill == null
                ? null
                : new SkillRecord(skill.Name, skill.Description, skill.Instructions, skill.ToolNames);
        }

        /// <summary>
        /// Creates a new authored skill: validates the tool allowlist, adds it to the live catalog,
        /// persists it (revision 0), and records the original revision. Fails when the id is blank, the
        /// id already exists, or a referenced tool is unknown.
        /// </summary>
        public SkillAuthoringResult Create(string id, string description, string instructions,
            IEnumerable<string> toolNames)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("create: 'name' is required.");
            }

            string skillId = id.Trim();
            lock (_lock)
            {
                SkillAuthoringResult result = _store.Mutate(
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

                        _catalog.AddOrReplace(skill);
                        SkillAuthoringResult created =
                            SkillAuthoringResult.Ok(record, $"Skill '{skillId}' created (version 0).");
                        return SkillStoreMutation<SkillAuthoringResult>.SaveRecord(record, created);
                    });

                if (result.Success && result.Record != null)
                {
                    SeedVersion(result.Record);
                }

                return result;
            }
        }

        /// <summary>
        /// Updates an existing authored skill. Null arguments leave the corresponding field unchanged;
        /// a non-null <paramref name="toolNames"/> replaces the allowlist. Auto-increments the version
        /// and records a new revision. Fails when the skill is unknown or a referenced tool is unknown.
        /// </summary>
        public SkillAuthoringResult Update(string id, string description, string instructions,
            IEnumerable<string> toolNames)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("update: 'name' is required.");
            }

            string skillId = id.Trim();
            lock (_lock)
            {
                SkillAuthoringResult result = _store.Mutate(
                    skillId,
                    current =>
                    {
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
                                existingSkill.Instructions, existingSkill.ToolNames);
                        }

                        SkillRecord revised = new(
                            skillId,
                            description ?? current.Description,
                            instructions ?? current.Instructions,
                            toolNames != null ? new List<string>(toolNames) : current.ToolNames,
                            current.Version + 1);

                        if (!TryBuildSkill(revised, out SkillSet skill, out string error, false))
                        {
                            return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                SkillAuthoringResult.Failure($"update: {error}"));
                        }

                        _catalog.AddOrReplace(skill);
                        SkillAuthoringResult updated = SkillAuthoringResult.Ok(revised,
                            $"Skill '{skillId}' updated (now version {revised.Version}).");
                        return SkillStoreMutation<SkillAuthoringResult>.SaveRecord(revised, updated);
                    });

                if (result.Success && result.Record != null)
                {
                    RecordRevision(result.Record);
                }

                return result;
            }
        }

        /// <summary>Removes a skill from the live catalog and persistent store.</summary>
        public SkillAuthoringResult Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return SkillAuthoringResult.Failure("delete: 'name' is required.");
            }

            string skillId = id.Trim();
            lock (_lock)
            {
                return _store.Mutate(
                    skillId,
                    current =>
                    {
                        bool inCatalog = _catalog.Remove(skillId);
                        if (!inCatalog && current == null)
                        {
                            return SkillStoreMutation<SkillAuthoringResult>.NoChange(
                                SkillAuthoringResult.Failure($"delete: skill '{skillId}' not found."));
                        }

                        return SkillStoreMutation<SkillAuthoringResult>.DeleteRecord(
                            SkillAuthoringResult.Ok(null, $"Skill '{skillId}' deleted."));
                    });
            }
        }

        /// <summary>Lists the recorded revisions for a skill (oldest first; revision 0 is the original).</summary>
        public IReadOnlyList<LuaScriptRevision> ListRevisions(string id)
        {
            if (_versionStore == null || string.IsNullOrWhiteSpace(id))
            {
                return Array.Empty<LuaScriptRevision>();
            }

            return _versionStore.TryGetSnapshot(VersionKey(id.Trim()), out LuaScriptVersionRecord snap) && snap != null
                ? snap.History
                : Array.Empty<LuaScriptRevision>();
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

            skill = new SkillSet(record.Id, record.Description, record.Instructions, tools);
            return true;
        }

        private void SeedVersion(SkillRecord record)
        {
            _versionStore?.SeedOriginal(VersionKey(record.Id), SerializeRevision(record));
        }

        private void RecordRevision(SkillRecord record)
        {
            _versionStore?.RecordSuccessfulExecution(VersionKey(record.Id), SerializeRevision(record));
        }

        /// <summary>
        /// Serializes the revision-relevant content (instructions + allowlist) so two edits that change
        /// nothing do not record a duplicate revision (RecordSuccessfulExecution dedupes identical text).
        /// </summary>
        private static string SerializeRevision(SkillRecord record)
        {
            string tools = record.ToolNames != null ? string.Join(",", record.ToolNames) : "";
            return $"# {record.Description}\n# tools: {tools}\n{record.Instructions}";
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

        private SkillAuthoringResult(bool success, string message, SkillRecord record)
        {
            Success = success;
            Message = message;
            Record = record;
        }

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