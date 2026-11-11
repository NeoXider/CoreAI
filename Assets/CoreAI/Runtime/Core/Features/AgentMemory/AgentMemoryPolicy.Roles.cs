using System;
using System.Collections.Generic;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Role definitions, tool profiles, enforced agent modes and the unknown-role diagnostic.
    /// </summary>
    public sealed partial class AgentMemoryPolicy
    {
        private const string InternalRolePrefix = "__CoreAI_";

        private readonly Dictionary<string, RoleDefinition> _roleDefinitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _roleToolProfiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolProfileState> _toolProfiles = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Dictionary<string, MaterializedProfileTool>> _profileToolInstances =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, List<SkillSet>> _roleInlineSkills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SkillSet>> _derivedSkills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AgentMode> _enforcedModes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedUnknownRoles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedProfileToolFailures = new(StringComparer.Ordinal);

        /// <summary>
        /// Log that receives this policy's diagnostics (unknown roles, failing tool-profile factories).
        /// Null uses <see cref="Log.Instance"/>.
        /// </summary>
        public ILog DiagnosticsLog { get; set; }

        private sealed class ProfileToolEntry
        {
            public ProfileToolEntry(string name, Func<string, ILlmTool> factory, ILlmTool sharedInstance)
            {
                Name = name;
                Factory = factory;
                SharedInstance = sharedInstance;
            }

            public string Name { get; }
            public Func<string, ILlmTool> Factory { get; }
            public ILlmTool SharedInstance { get; }
        }

        private sealed class ToolProfileState
        {
            public readonly List<ProfileToolEntry> Tools = new();
            public readonly List<SkillSet> Skills = new();
        }

        private sealed class MaterializedProfileTool
        {
            public MaterializedProfileTool(ProfileToolEntry entry, ILlmTool tool)
            {
                Entry = entry;
                Tool = tool;
            }

            public ProfileToolEntry Entry { get; }
            public ILlmTool Tool { get; }
        }

        private readonly struct PendingProfileTool
        {
            public PendingProfileTool(string key, ProfileToolEntry entry)
            {
                Key = key;
                Entry = entry;
            }

            public string Key { get; }
            public ProfileToolEntry Entry { get; }
        }

        /// <summary>
        /// Applies a role definition: the role's memory, history and LLM settings, prompt reference and overlay,
        /// universal-prefix opt-out, streaming preference, tool profiles, skills and routing profile all become
        /// what the definition says. Applying a definition for a built-in role overrides the shipped one. Tools a
        /// host added directly with <see cref="AddToolForRole"/> and skills added with <see cref="AddSkillForRole"/>
        /// are kept; tools and skills that came from the previous definition's profiles are replaced.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
        /// <exception cref="ArgumentException">The definition has no id.</exception>
        public void ApplyRoleDefinition(RoleDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            string roleId = (definition.Id ?? "").Trim();
            if (roleId.Length == 0)
            {
                throw new ArgumentException("The role definition has no id.", nameof(definition));
            }

            RoleDefinition snapshot = definition.Clone();
            snapshot.Id = roleId;
            lock (_lock)
            {
                _roleDefinitions[roleId] = snapshot;
                _roleConfigs[roleId] = snapshot.ToRoleMemoryConfig();

                if (snapshot.OverrideUniversalPrefix)
                {
                    _overrideUniversalPrefix.Add(roleId);
                }
                else
                {
                    _overrideUniversalPrefix.Remove(roleId);
                }

                if (snapshot.EnableStreaming.HasValue)
                {
                    _streamingOverrides[roleId] = snapshot.EnableStreaming.Value;
                }
                else
                {
                    _streamingOverrides.Remove(roleId);
                }

                if (string.IsNullOrWhiteSpace(snapshot.AdditionalSystemPrompt))
                {
                    _additionalSystemPrompts.Remove(roleId);
                }
                else
                {
                    _additionalSystemPrompts[roleId] = snapshot.AdditionalSystemPrompt.Trim();
                }

                _enforcedModes.Remove(roleId);
                SetRoleToolProfilesLocked(roleId, snapshot.NormalizedToolProfiles());
                List<SkillSet> inline = NonNullSkills(snapshot.Skills);
                if (inline.Count > 0)
                {
                    _roleInlineSkills[roleId] = inline;
                }
                else
                {
                    _roleInlineSkills.Remove(roleId);
                }
            }

            SyncDerivedSkills(roleId);
        }

        /// <summary>Applies each definition in order; a later definition for the same role wins.</summary>
        public void ApplyRoleDefinitions(IEnumerable<RoleDefinition> definitions)
        {
            if (definitions == null)
            {
                return;
            }

            foreach (RoleDefinition definition in definitions)
            {
                if (definition != null)
                {
                    ApplyRoleDefinition(definition);
                }
            }
        }

        /// <summary>
        /// Returns a copy of the definition last applied to <paramref name="roleId"/> (the shipped one for a
        /// built-in role nobody replaced). Later setter or <see cref="AgentBuilder"/> calls change the live role
        /// without editing this record.
        /// </summary>
        public bool TryGetRoleDefinition(string roleId, out RoleDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                if (_roleDefinitions.TryGetValue(roleId.Trim(), out RoleDefinition stored))
                {
                    definition = stored.Clone();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The role id whose system-prompt chain supplies <paramref name="roleId"/>'s base prompt: the definition's
        /// <see cref="RoleDefinition.PromptRoleId"/> when set, otherwise the role itself.
        /// </summary>
        public string ResolvePromptRoleId(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return roleId;
            }

            string trimmed = roleId.Trim();
            lock (_lock)
            {
                if (_roleDefinitions.TryGetValue(trimmed, out RoleDefinition definition) &&
                    !string.IsNullOrWhiteSpace(definition.PromptRoleId))
                {
                    return definition.PromptRoleId.Trim();
                }
            }

            return trimmed;
        }

        /// <summary>Returns the inline base system prompt of the role's definition, when it has one.</summary>
        public bool TryGetInlineSystemPrompt(string roleId, out string prompt)
        {
            prompt = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                if (_roleDefinitions.TryGetValue(roleId.Trim(), out RoleDefinition definition) &&
                    !string.IsNullOrWhiteSpace(definition.SystemPrompt))
                {
                    prompt = definition.SystemPrompt.Trim();
                    return true;
                }
            }

            return false;
        }

        /// <summary>Returns the LLM routing profile the role's definition prefers, when it names one.</summary>
        public bool TryGetRoutingProfile(string roleId, out string routingProfileId)
        {
            routingProfileId = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                if (_roleDefinitions.TryGetValue(roleId.Trim(), out RoleDefinition definition) &&
                    !string.IsNullOrWhiteSpace(definition.RoutingProfileId))
                {
                    routingProfileId = definition.RoutingProfileId.Trim();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Registers <paramref name="tool"/> into a tool profile, replacing a tool of the same name (ordinal).
        /// Every role whose profiles include <paramref name="profileId"/> receives this same instance; use the
        /// factory overload for a tool that must know the role it serves.
        /// </summary>
        public ToolRegistrationChange AddToolToProfile(string profileId, ILlmTool tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
            {
                return ToolRegistrationChange.None;
            }

            ILlmTool shared = tool;
            return AddProfileEntry(profileId, tool.Name, _ => shared, shared);
        }

        /// <summary>
        /// Registers a tool factory into a tool profile under <paramref name="toolName"/>, replacing an entry of the
        /// same name (ordinal). The factory receives the role id and runs once per role that uses the profile, the
        /// first time that role's tools are read, so a tool bound to an actor identity serves the right role.
        /// </summary>
        public ToolRegistrationChange AddToolToProfile(string profileId, string toolName,
            Func<string, ILlmTool> factory)
        {
            if (factory == null || string.IsNullOrWhiteSpace(toolName))
            {
                return ToolRegistrationChange.None;
            }

            return AddProfileEntry(profileId, toolName.Trim(), factory, null);
        }

        private ToolRegistrationChange AddProfileEntry(string profileId, string toolName,
            Func<string, ILlmTool> factory, ILlmTool sharedInstance)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return ToolRegistrationChange.None;
            }

            profileId = profileId.Trim();
            lock (_lock)
            {
                ToolProfileState profile = GetOrCreateProfileLocked(profileId);
                for (int i = 0; i < profile.Tools.Count; i++)
                {
                    ProfileToolEntry existing = profile.Tools[i];
                    if (!string.Equals(existing.Name, toolName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (sharedInstance != null && ReferenceEquals(existing.SharedInstance, sharedInstance))
                    {
                        return ToolRegistrationChange.None;
                    }

                    profile.Tools[i] = new ProfileToolEntry(toolName, factory, sharedInstance);
                    return ToolRegistrationChange.Replaced;
                }

                profile.Tools.Add(new ProfileToolEntry(toolName, factory, sharedInstance));
                return ToolRegistrationChange.Added;
            }
        }

        /// <summary>Removes the tool named <paramref name="toolName"/> from a profile. Returns true when one was removed.</summary>
        public bool RemoveToolFromProfile(string profileId, string toolName)
        {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            lock (_lock)
            {
                if (!_toolProfiles.TryGetValue(profileId.Trim(), out ToolProfileState profile))
                {
                    return false;
                }

                string name = toolName.Trim();
                return profile.Tools.RemoveAll(entry => string.Equals(entry.Name, name, StringComparison.Ordinal)) > 0;
            }
        }

        /// <summary>
        /// Adds a skill to a tool profile (replacing a skill of the same name). Every role whose profiles include
        /// <paramref name="profileId"/> gets it in its <c>read_skill</c> catalog, and the first skill a role
        /// receives registers its <c>read_skill</c> / <c>call_skill_tool</c> meta-tools, as
        /// <see cref="AddSkillForRole"/> does.
        /// </summary>
        public void AddSkillToProfile(string profileId, SkillSet skill)
        {
            if (string.IsNullOrWhiteSpace(profileId) || skill == null || string.IsNullOrWhiteSpace(skill.Name))
            {
                return;
            }

            profileId = profileId.Trim();
            List<string> affectedRoles = new();
            lock (_lock)
            {
                ToolProfileState profile = GetOrCreateProfileLocked(profileId);
                int index = profile.Skills.FindIndex(existing =>
                    string.Equals(existing.Name, skill.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    profile.Skills[index] = skill;
                }
                else
                {
                    profile.Skills.Add(skill);
                }

                foreach (KeyValuePair<string, List<string>> role in _roleToolProfiles)
                {
                    if (role.Value.Contains(profileId))
                    {
                        affectedRoles.Add(role.Key);
                    }
                }
            }

            foreach (string roleId in affectedRoles)
            {
                SyncDerivedSkills(roleId);
            }
        }

        /// <summary>Names of every profile that has been registered into or referenced, sorted (ordinal).</summary>
        public IReadOnlyList<string> GetToolProfileIds()
        {
            lock (_lock)
            {
                List<string> ids = new(_toolProfiles.Keys);
                ids.Sort(StringComparer.Ordinal);
                return ids;
            }
        }

        /// <summary>Snapshot of one profile, or null when nothing was ever registered into it.</summary>
        public ToolProfile GetToolProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return null;
            }

            lock (_lock)
            {
                if (!_toolProfiles.TryGetValue(profileId.Trim(), out ToolProfileState profile))
                {
                    return null;
                }

                List<string> names = new(profile.Tools.Count);
                foreach (ProfileToolEntry entry in profile.Tools)
                {
                    names.Add(entry.Name);
                }

                return new ToolProfile(profileId.Trim(), names.AsReadOnly(),
                    new List<SkillSet>(profile.Skills).AsReadOnly());
            }
        }

        /// <summary>The tool profiles <paramref name="roleId"/> currently draws tools from, in order.</summary>
        public IReadOnlyList<string> GetToolProfilesForRole(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return Array.Empty<string>();
            }

            lock (_lock)
            {
                return _roleToolProfiles.TryGetValue(roleId.Trim(), out List<string> profiles)
                    ? new List<string>(profiles).AsReadOnly()
                    : (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        /// <summary>
        /// Replaces the tool profiles of one role without touching the rest of its configuration. An empty list
        /// detaches the role from every profile; skills that came from the old profiles leave its catalog.
        /// </summary>
        public void SetToolProfilesForRole(string roleId, IReadOnlyList<string> profileIds)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                SetRoleToolProfilesLocked(roleId, RoleDefinition.NormalizeProfileIds(profileIds));
            }

            SyncDerivedSkills(roleId);
        }

        /// <summary>
        /// Returns the agent mode enforced for a role (set by an <see cref="AgentBuilder"/> built with
        /// <see cref="AgentBuilder.WithModeEnforcement"/>). Roles without enforcement report false.
        /// </summary>
        public bool TryGetEnforcedMode(string roleId, out AgentMode mode)
        {
            mode = AgentMode.ToolsAndChat;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                return _enforcedModes.TryGetValue(roleId.Trim(), out mode);
            }
        }

        /// <summary>Skills of the named profiles, in profile order, de-duplicated by name (first wins).</summary>
        internal List<SkillSet> SnapshotProfileSkills(IReadOnlyList<string> profileIds)
        {
            List<SkillSet> skills = new();
            if (profileIds == null || profileIds.Count == 0)
            {
                return skills;
            }

            lock (_lock)
            {
                AppendProfileSkillsLocked(RoleDefinition.NormalizeProfileIds(profileIds), skills);
            }

            return skills;
        }

        private ToolProfileState GetOrCreateProfileLocked(string profileId)
        {
            if (!_toolProfiles.TryGetValue(profileId, out ToolProfileState profile))
            {
                profile = new ToolProfileState();
                _toolProfiles[profileId] = profile;
            }

            return profile;
        }

        private void SetRoleToolProfilesLocked(string roleId, List<string> profileIds)
        {
            _profileToolInstances.Remove(roleId);
            if (profileIds == null || profileIds.Count == 0)
            {
                _roleToolProfiles.Remove(roleId);
                return;
            }

            _roleToolProfiles[roleId] = profileIds;
            foreach (string profileId in profileIds)
            {
                GetOrCreateProfileLocked(profileId);
            }
        }

        private void AppendProfileSkillsLocked(List<string> profileIds, List<SkillSet> into)
        {
            foreach (string profileId in profileIds)
            {
                if (!_toolProfiles.TryGetValue(profileId, out ToolProfileState profile))
                {
                    continue;
                }

                foreach (SkillSet skill in profile.Skills)
                {
                    AddSkillUnique(into, skill);
                }
            }
        }

        private static void AddSkillUnique(List<SkillSet> skills, SkillSet skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Name))
            {
                return;
            }

            foreach (SkillSet existing in skills)
            {
                if (string.Equals(existing.Name, skill.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            skills.Add(skill);
        }

        private static List<SkillSet> NonNullSkills(IReadOnlyList<SkillSet> skills)
        {
            List<SkillSet> result = new();
            if (skills == null)
            {
                return result;
            }

            foreach (SkillSet skill in skills)
            {
                AddSkillUnique(result, skill);
            }

            return result;
        }

        /// <summary>
        /// Brings the role's skill catalog in line with the skills its definition and profiles supply: stale
        /// derived skills leave, new ones are added, and the meta-tools appear with the first skill.
        /// </summary>
        private void SyncDerivedSkills(string roleId)
        {
            MutableSkillCatalog catalog;
            List<SkillSet> desired = new();
            List<SkillSet> previous;
            bool createdCatalog = false;
            lock (_lock)
            {
                if (_roleInlineSkills.TryGetValue(roleId, out List<SkillSet> inline))
                {
                    foreach (SkillSet skill in inline)
                    {
                        AddSkillUnique(desired, skill);
                    }
                }

                if (_roleToolProfiles.TryGetValue(roleId, out List<string> profiles))
                {
                    AppendProfileSkillsLocked(profiles, desired);
                }

                _derivedSkills.TryGetValue(roleId, out previous);
                _roleSkillCatalogs.TryGetValue(roleId, out catalog);
                if (desired.Count == 0 && (previous == null || previous.Count == 0))
                {
                    return;
                }

                if (catalog == null && desired.Count > 0)
                {
                    catalog = new MutableSkillCatalog();
                    _roleSkillCatalogs[roleId] = catalog;
                    createdCatalog = true;
                }

                if (desired.Count > 0)
                {
                    _derivedSkills[roleId] = desired;
                }
                else
                {
                    _derivedSkills.Remove(roleId);
                }
            }

            if (catalog == null)
            {
                return;
            }

            if (previous != null)
            {
                foreach (SkillSet stale in previous)
                {
                    if (!desired.Contains(stale) && ReferenceEquals(catalog.Get(stale.Name), stale))
                    {
                        catalog.Remove(stale.Name);
                    }
                }
            }

            foreach (SkillSet skill in desired)
            {
                if (!ReferenceEquals(catalog.Get(skill.Name), skill))
                {
                    catalog.AddOrReplace(skill);
                }
            }

            if (createdCatalog)
            {
                AddToolForRole(roleId, ReadSkillLlmTool.Create(catalog));
                AddToolForRole(roleId, CallSkillToolLlmTool.Create(catalog, DirectToolsProviderFor(roleId)));
                return;
            }

            if (catalog.Count == 0)
            {
                RemoveEmptyCatalog(roleId, catalog);
            }
        }

        /// <summary>
        /// Drops a catalog that lost its last skill together with its meta-tools, so a role whose profiles no
        /// longer carry skills is not left offering a <c>read_skill</c> with nothing to read.
        /// </summary>
        private void RemoveEmptyCatalog(string roleId, MutableSkillCatalog catalog)
        {
            lock (_lock)
            {
                if (!_roleSkillCatalogs.TryGetValue(roleId, out MutableSkillCatalog current) ||
                    !ReferenceEquals(current, catalog) || catalog.Count > 0)
                {
                    return;
                }

                _roleSkillCatalogs.Remove(roleId);
                if (_customTools.TryGetValue(roleId, out List<ILlmTool> tools))
                {
                    RemoveSkillMetaTools(tools);
                    if (tools.Count == 0)
                    {
                        _customTools.Remove(roleId);
                    }
                }
            }
        }

        /// <summary>Profile tool entries this role uses that have no instance for it yet.</summary>
        private List<PendingProfileTool> CollectMissingProfileTools(string roleId)
        {
            if (roleId == null)
            {
                return null;
            }

            List<PendingProfileTool> missing = null;
            lock (_lock)
            {
                if (!_roleToolProfiles.TryGetValue(roleId, out List<string> profiles))
                {
                    return null;
                }

                _profileToolInstances.TryGetValue(roleId, out Dictionary<string, MaterializedProfileTool> instances);
                foreach (string profileId in profiles)
                {
                    if (!_toolProfiles.TryGetValue(profileId, out ToolProfileState profile))
                    {
                        continue;
                    }

                    foreach (ProfileToolEntry entry in profile.Tools)
                    {
                        string key = ProfileToolKey(profileId, entry.Name);
                        if (instances != null && instances.TryGetValue(key, out MaterializedProfileTool cached) &&
                            ReferenceEquals(cached.Entry, entry))
                        {
                            continue;
                        }

                        missing ??= new List<PendingProfileTool>();
                        missing.Add(new PendingProfileTool(key, entry));
                    }
                }
            }

            return missing;
        }

        /// <summary>
        /// Runs the missing factories outside the policy lock (they are host code) and publishes the results;
        /// a concurrent reader that published first wins, so a role never sees two instances of one entry.
        /// </summary>
        private void MaterializeProfileTools(string roleId, List<PendingProfileTool> missing)
        {
            List<MaterializedProfileTool> created = new(missing.Count);
            foreach (PendingProfileTool pending in missing)
            {
                ILlmTool tool = null;
                try
                {
                    tool = pending.Entry.Factory(roleId);
                }
                catch (Exception ex)
                {
                    WarnProfileToolFailure(roleId, pending.Entry.Name, ex);
                }

                created.Add(new MaterializedProfileTool(pending.Entry, tool));
            }

            lock (_lock)
            {
                if (!_profileToolInstances.TryGetValue(roleId, out Dictionary<string, MaterializedProfileTool> instances))
                {
                    instances = new Dictionary<string, MaterializedProfileTool>(StringComparer.Ordinal);
                    _profileToolInstances[roleId] = instances;
                }

                for (int i = 0; i < missing.Count; i++)
                {
                    string key = missing[i].Key;
                    if (instances.TryGetValue(key, out MaterializedProfileTool existing) &&
                        ReferenceEquals(existing.Entry, missing[i].Entry))
                    {
                        continue;
                    }

                    instances[key] = created[i];
                }
            }
        }

        /// <summary>
        /// The role's profile tools, in profile and registration order, without a name already supplied by an
        /// earlier profile or by the role's direct tools (direct tools win). Called under <see cref="_lock"/>.
        /// </summary>
        private List<ILlmTool> ResolveProfileToolsLocked(string roleId, List<ILlmTool> directTools)
        {
            List<ILlmTool> tools = new();
            if (!_roleToolProfiles.TryGetValue(roleId, out List<string> profiles) ||
                !_profileToolInstances.TryGetValue(roleId, out Dictionary<string, MaterializedProfileTool> instances))
            {
                return tools;
            }

            HashSet<string> taken = new(StringComparer.Ordinal);
            if (directTools != null)
            {
                foreach (ILlmTool direct in directTools)
                {
                    if (direct != null && !string.IsNullOrEmpty(direct.Name))
                    {
                        taken.Add(direct.Name);
                    }
                }
            }

            foreach (string profileId in profiles)
            {
                if (!_toolProfiles.TryGetValue(profileId, out ToolProfileState profile))
                {
                    continue;
                }

                foreach (ProfileToolEntry entry in profile.Tools)
                {
                    if (!instances.TryGetValue(ProfileToolKey(profileId, entry.Name), out MaterializedProfileTool made) ||
                        !ReferenceEquals(made.Entry, entry) || made.Tool == null)
                    {
                        continue;
                    }

                    string name = string.IsNullOrEmpty(made.Tool.Name) ? entry.Name : made.Tool.Name;
                    if (taken.Add(name))
                    {
                        tools.Add(made.Tool);
                    }
                }
            }

            return tools;
        }

        private static string ProfileToolKey(string profileId, string toolName)
        {
            return profileId + "\n" + toolName;
        }

        private bool IsChatOnlyEnforcedLocked(string roleId)
        {
            return _enforcedModes.TryGetValue(roleId, out AgentMode mode) && mode == AgentMode.ChatOnly;
        }

        /// <summary>
        /// True the first time a non-empty role id with no configuration of any kind is read. Called under
        /// <see cref="_lock"/>.
        /// </summary>
        private bool ShouldWarnUnknownRoleLocked(string roleId)
        {
            if (string.IsNullOrEmpty(roleId) || roleId.StartsWith(InternalRolePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (_roleConfigs.ContainsKey(roleId) ||
                _customTools.ContainsKey(roleId) ||
                _roleSkillCatalogs.ContainsKey(roleId) ||
                _roleToolProfiles.ContainsKey(roleId) ||
                _roleDefinitions.ContainsKey(roleId) ||
                _additionalSystemPrompts.ContainsKey(roleId) ||
                _overrideUniversalPrefix.Contains(roleId) ||
                _streamingOverrides.ContainsKey(roleId) ||
                _runtimeContextProviders.ContainsKey(roleId))
            {
                return false;
            }

            return _warnedUnknownRoles.Add(roleId);
        }

        private string FindCaseInsensitiveRoleLocked(string roleId)
        {
            foreach (string known in _roleConfigs.Keys)
            {
                if (string.Equals(known, roleId, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return null;
        }

        private void WarnUnknownRole(string roleId)
        {
            string similar;
            lock (_lock)
            {
                similar = FindCaseInsensitiveRoleLocked(roleId);
            }

            string hint = similar != null
                ? $" Role ids are case-sensitive: did you mean '{similar}'?"
                : AgentRoleRegistration.IsPending(roleId)
                    ? " An AgentBuilder.Build() for this role ran before CoreAI started and is queued; it is " +
                      "applied by AgentBuilder.ApplyPendingBuilds when CoreAI initialises."
                    : " Register it with AgentBuilder, AgentMemoryPolicy.ApplyRoleDefinition or a role " +
                      "definition asset; a role that really needs no configuration can be registered with an " +
                      "empty RoleDefinition to silence this warning.";
            (DiagnosticsLog ?? Log.Instance ?? NullLog.Instance).Warn(
                $"[AgentMemoryPolicy] Role '{roleId}' has no configuration, so it runs with the defaults " +
                $"(memory tool only, chat history on, no role prompt overlay).{hint}",
                LogTag.Core);
        }

        private void WarnProfileToolFailure(string roleId, string toolName, Exception ex)
        {
            bool first;
            lock (_lock)
            {
                first = _warnedProfileToolFailures.Add(roleId + "\n" + toolName);
            }

            if (!first)
            {
                return;
            }

            (DiagnosticsLog ?? Log.Instance ?? NullLog.Instance).Error(
                $"[AgentMemoryPolicy] The tool-profile factory for '{toolName}' failed for role '{roleId}', so the " +
                $"role is offered no '{toolName}' tool: {ex.GetType().Name}: {ex.Message}",
                LogTag.Core);
        }
    }
}
