using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

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

    /// <summary>Optional store capability for atomic skill read/modify/write transactions.</summary>
    public interface IAtomicSkillStore
    {
        /// <summary>
        /// Runs <paramref name="mutator"/> while holding the store's durable key lock, then applies the
        /// requested save/delete before releasing it.
        /// </summary>
        TResult Mutate<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator);
    }

    /// <summary>Result of one atomic skill store mutation.</summary>
    public sealed class SkillStoreMutation<TResult>
    {
        private SkillStoreMutation(TResult result, SkillRecord record, bool save, bool delete)
        {
            Result = result;
            Record = record;
            Save = save;
            Delete = delete;
        }

        public TResult Result { get; }
        public SkillRecord Record { get; }
        public bool Save { get; }
        public bool Delete { get; }

        public static SkillStoreMutation<TResult> SaveRecord(SkillRecord record, TResult result)
        {
            return new SkillStoreMutation<TResult>(result, record, save: true, delete: false);
        }

        public static SkillStoreMutation<TResult> DeleteRecord(TResult result)
        {
            return new SkillStoreMutation<TResult>(result, null, save: false, delete: true);
        }

        public static SkillStoreMutation<TResult> NoChange(TResult result)
        {
            return new SkillStoreMutation<TResult>(result, null, save: false, delete: false);
        }
    }

    /// <summary>Atomic mutation fallback for skill stores without a durable store-specific primitive.</summary>
    public static class SkillStoreExtensions
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks = new();

        public static TResult Mutate<TResult>(
            this ISkillStore store,
            string id,
            Func<SkillRecord, SkillStoreMutation<TResult>> mutator)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            if (store is IAtomicSkillStore atomic)
            {
                return atomic.Mutate(id, mutator);
            }

            string skillId = (id ?? "").Trim();
            string key = $"{store.GetType().FullName}:{skillId}";
            SemaphoreSlim gate = MutationLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                store.TryLoad(skillId, out SkillRecord current);
                SkillStoreMutation<TResult> mutation = mutator(current);
                if (mutation == null)
                {
                    throw new InvalidOperationException("Skill store mutator returned null.");
                }

                if (mutation.Delete)
                {
                    store.Delete(skillId);
                }
                else if (mutation.Save && mutation.Record != null)
                {
                    store.Save(mutation.Record);
                }

                return mutation.Result;
            }
            finally
            {
                gate.Release();
            }
        }
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
