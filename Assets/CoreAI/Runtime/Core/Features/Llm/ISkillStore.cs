using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    /// authoring coordinator publishes to the live catalog only after a successful store operation.
    /// Implementations must report write failures and write atomically so a crash mid-write cannot
    /// corrupt an existing skill. A no-op store deliberately provides session-only skills.
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

    /// <summary>
    /// Publishes a committed mutation before releasing its durable key lock. Callbacks are synchronous,
    /// must not wait or re-enter skill stores/coordinators, and should not throw. A publication exception
    /// means the storage operation completed; implementations must not report it as a storage rollback.
    /// </summary>
    public interface ICommittedSkillStore : IAtomicSkillStore
    {
        TResult MutateAndPublish<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator,
            Action<TResult> publish);
    }

    /// <summary>The store committed, but publication failed; retrying the write is not a rollback.</summary>
    public sealed class SkillStorePublicationException : InvalidOperationException
    {
        public SkillStorePublicationException(string id, Exception innerException)
            : base($"Skill '{id}' storage operation completed, but publication failed; stored data was not rolled back.", innerException)
        {
        }
    }

    internal static class SkillStoreCallbackContext
    {
        [ThreadStatic] private static bool _active;

        internal static void ThrowIfActive()
        {
            if (_active) throw new InvalidOperationException("Skill store callbacks must not re-enter skill stores or coordinators.");
        }

        internal static TResult Run<TResult>(Func<TResult> callback)
        {
            ThrowIfActive();
            _active = true;
            try { return callback(); }
            finally { _active = false; }
        }

        internal static void Publish<TResult>(string id, TResult result, Action<TResult> publish)
        {
            if (publish == null) return;
            try
            {
                Run(() => { publish(result); return true; });
            }
            catch (Exception ex)
            {
                throw new SkillStorePublicationException(id, ex);
            }
        }
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
            if (record == null) throw new ArgumentNullException(nameof(record));
            return new SkillStoreMutation<TResult>(result, record, true, false);
        }

        public static SkillStoreMutation<TResult> DeleteRecord(TResult result)
        {
            return new SkillStoreMutation<TResult>(result, null, false, true);
        }

        public static SkillStoreMutation<TResult> NoChange(TResult result)
        {
            return new SkillStoreMutation<TResult>(result, null, false, false);
        }
    }

    /// <summary>Atomic mutation fallback for skill stores without a durable store-specific primitive.</summary>
    public static class SkillStoreExtensions
    {
        /// <summary>
        /// Mutation locks keyed by skill id, held <b>per store instance</b> - the fallback path used only
        /// when a store does not implement <see cref="IAtomicSkillStore"/> itself. Two independent stores
        /// (different directories) must not serialize against each other, and the whole table must die with
        /// the store rather than pinning a model-controlled id set for the process lifetime. Entries within
        /// one store are intentionally never evicted: a caller could already hold the
        /// <see cref="SemaphoreSlim"/> fetched from the dictionary while a concurrent
        /// eviction-then-<c>GetOrAdd</c> hands a second caller a fresh instance, silently breaking the
        /// mutual exclusion this lock exists for.
        /// </summary>
        private static readonly ConditionalWeakTable<ISkillStore, ConcurrentDictionary<string, SemaphoreSlim>>
            MutationLocks = new();

        public static TResult Mutate<TResult>(
            this ISkillStore store,
            string id,
            Func<SkillRecord, SkillStoreMutation<TResult>> mutator)
        {
            return MutateAndPublish(store, id, mutator, null);
        }

        /// <summary>
        /// Commits before publication. The fallback orders coordinators sharing this store instance;
        /// stores shared through different instances implement ICommittedSkillStore for a common key lock.
        /// </summary>
        public static TResult MutateAndPublish<TResult>(this ISkillStore store, string id,
            Func<SkillRecord, SkillStoreMutation<TResult>> mutator, Action<TResult> publish)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            if (store is ICommittedSkillStore committed)
            {
                return committed.MutateAndPublish(id, mutator, publish);
            }

            string skillId = (id ?? "").Trim();
            if (skillId.Length == 0) throw new ArgumentException("Skill id must not be empty.", nameof(id));
            ConcurrentDictionary<string, SemaphoreSlim> gates = MutationLocks.GetValue(
                store, _ => new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase));
            SemaphoreSlim gate = gates.GetOrAdd(skillId, _ => new SemaphoreSlim(1, 1));
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!gate.Wait(0)) throw new InvalidOperationException("Skill store is busy; retry after the current operation.");
#else
            gate.Wait();
#endif
            try
            {
                if (store is IAtomicSkillStore atomic)
                {
                    TResult result = atomic.Mutate(skillId,
                        current => SkillStoreCallbackContext.Run(() => mutator(current)));
                    SkillStoreCallbackContext.Publish(skillId, result, publish);
                    return result;
                }
                store.TryLoad(skillId, out SkillRecord current);
                if (current != null)
                {
                    current = new SkillRecord(current.Id, current.Description, current.Instructions, current.ToolNames,
                        current.Version, current.Sections);
                }
                SkillStoreMutation<TResult> mutation = SkillStoreCallbackContext.Run(() => mutator(current));
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
                    if (!string.Equals(mutation.Record.Id?.Trim(), skillId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A skill mutation cannot write a different key.");
                    store.Save(mutation.Record);
                }

                SkillStoreCallbackContext.Publish(skillId, mutation.Result, publish);
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
        /// Ordered instruction documents, first being the complete main document. Empty means a legacy
        /// single-file record using Instructions. Tools and executable implementations are never embedded.
        /// </summary>
        public List<SkillSection> Sections { get; set; } = new();

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
            IEnumerable<string> toolNames, int version = 0, IEnumerable<SkillSection> sections = null)
        {
            Id = id ?? "";
            Description = description ?? "";
            Instructions = instructions ?? "";
            ToolNames = toolNames != null ? new List<string>(toolNames) : new List<string>();
            Version = version;
            Sections = sections != null ? new List<SkillSection>(sections) : new List<SkillSection>();
        }
    }
}
