using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persistent package store for <em>agent-authored</em> skills: a skill's name, description,
    /// procedural instructions, and the allowlist of <b>existing</b> registered tool names it may use.
    /// <para>
    /// This is the skills analogue of <see cref="ILuaModSourceStore"/>: that one persists Lua mod
    /// source, this one persists the metadata that defines a reusable skill the model wrote for itself.
    /// A skill never persists tool <em>implementations</em> — only the names of tools already registered
    /// for the role — so an authored skill can reference, but never invent, C# capabilities.
    /// </para>
    /// <para>
    /// A host wires an implementation (file system, player prefs, cloud, etc.); the
    /// authoring coordinator calls it best-effort and never lets a store failure abort the in-memory
    /// catalog update. Implementations should write atomically so a crash mid-write cannot corrupt an
    /// existing skill.
    /// </para>
    /// </summary>
    public interface ISkillStore
    {
        /// <summary>
        /// Saves (creates or overwrites) the skill under <see cref="SkillRecord.Id"/>.
        /// </summary>
        void Save(SkillRecord record);

        /// <summary>
        /// Loads a stored skill by id. Returns false (and a null out-param) when no skill with this id
        /// exists.
        /// </summary>
        bool TryLoad(string id, out SkillRecord record);

        /// <summary>Returns every stored skill record.</summary>
        IReadOnlyList<SkillRecord> List();

        /// <summary>Permanently removes the stored skill. No-op when absent.</summary>
        void Delete(string id);
    }

    /// <summary>
    /// Serializable definition of an agent-authored skill: the data persisted by <see cref="ISkillStore"/>
    /// and rehydrated into the agent's <c>read_skill</c> catalog on load.
    /// </summary>
    public sealed class SkillRecord
    {
        /// <summary>Stable id (also the catalog name) of the skill.</summary>
        public string Id { get; set; } = "";

        /// <summary>Short one-line description shown in the skill catalog.</summary>
        public string Description { get; set; } = "";

        /// <summary>Full procedural instructions returned by <c>read_skill</c>.</summary>
        public string Instructions { get; set; } = "";

        /// <summary>
        /// Names of <b>already-registered</b> tools this skill exposes through <c>call_skill_tool</c>.
        /// Tools are referenced by name, never embedded.
        /// </summary>
        public List<string> ToolNames { get; set; } = new();

        /// <summary>
        /// Current revision number, auto-incremented on every <c>update</c>. The original create is
        /// revision 0; the version history is auditable through <see cref="ILuaScriptVersionStore"/>.
        /// </summary>
        public int Version { get; set; }

        /// <summary>Creates an empty record (required for deserialization).</summary>
        public SkillRecord()
        {
        }

        /// <summary>Creates a populated record.</summary>
        public SkillRecord(string id, string description, string instructions,
            IEnumerable<string> toolNames, int version = 0)
        {
            Id = id ?? "";
            Description = description ?? "";
            Instructions = instructions ?? "";
            ToolNames = toolNames != null ? new List<string>(toolNames) : new List<string>();
            Version = version;
        }
    }
}
