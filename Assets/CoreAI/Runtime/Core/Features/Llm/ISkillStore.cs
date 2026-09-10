using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

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


    /// <summary>Awaitable persistence; prepare and publication run on the supplied host context.</summary>
    public interface IAsyncSkillStore
    {
        Task<SkillRecord> LoadAsync(string id, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<SkillRecord>> ListAsync(CancellationToken cancellationToken = default);
        Task<TResult> MutateAndPublishAsync<TResult>(string id,
            Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
            Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler callbackContext,
            CancellationToken cancellationToken = default);
    }

    /// <summary>A local write completed, but durability was not confirmed and this call did not publish it.</summary>
    public sealed class SkillStoreDurabilityException : IOException
    {
        public SkillStoreDurabilityException(string id, Exception innerException = null)
            : base($"Skill '{id}' was stored locally, but durability is unconfirmed and this call did not publish it. " +
                "Do not replay the edit; confirm storage before rehydrating. The write may not survive a restart.", innerException) { }
        public bool Committed => true;
        public bool Durable => false;
        public bool Published => false;
        public bool Retryable => false;
    }

    /// <summary>
    /// Explicit opt-in for fast, nonblocking legacy stores. All access must use adapters over the same
    /// store instance; direct access to the underlying store bypasses this adapter's ordering boundary.
    /// </summary>
    public sealed class InlineAsyncSkillStoreAdapter : ISkillStore, IAsyncSkillStore
    {
        private static readonly ConditionalWeakTable<ISkillStore, SkillOperationGate> Gates = new();
        private readonly ISkillStore _store;
        private readonly SkillOperationGate _gate;
        public InlineAsyncSkillStoreAdapter(ISkillStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gate = Gates.GetValue(store, _ => new SkillOperationGate());
        }
        public void Save(SkillRecord record) { SkillStoreCallbackContext.ThrowIfActive(); using (_gate.EnterSync()) _store.Save(record); }
        public void Delete(string id) { SkillStoreCallbackContext.ThrowIfActive(); using (_gate.EnterSync()) _store.Delete(id); }
        public bool TryLoad(string id, out SkillRecord record)
        { SkillStoreCallbackContext.ThrowIfActive(); using (_gate.EnterSync()) return _store.TryLoad(id, out record); }
        public IReadOnlyList<SkillRecord> List()
        { SkillStoreCallbackContext.ThrowIfActive(); using (_gate.EnterSync()) return _store.List(); }
        public async Task<SkillRecord> LoadAsync(string id, CancellationToken cancellationToken = default)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            using (await _gate.EnterAsync(cancellationToken))
                return _store.TryLoad(id, out SkillRecord record) ? Copy(record) : null;
        }
        public async Task<IReadOnlyList<SkillRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            using (await _gate.EnterAsync(cancellationToken))
            {
                List<SkillRecord> result = new();
                foreach (SkillRecord record in _store.List()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(Copy(record)); }
                return result.AsReadOnly();
            }
        }
        public async Task<TResult> MutateAndPublishAsync<TResult>(string id,
            Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
            Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler callbackContext,
            CancellationToken cancellationToken = default)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            if (callbackContext == null) throw new ArgumentNullException(nameof(callbackContext));
            string key = (id ?? "").Trim();
            if (key.Length == 0) throw new ArgumentException("Skill id must not be empty.", nameof(id));
            using (await _gate.EnterAsync(cancellationToken))
            {
                _store.TryLoad(key, out SkillRecord current);
                SkillStoreMutation<TResult> mutation = await callbackContext.InvokeAsync(
                    () => Task.FromResult(SkillStoreCallbackContext.Run(() => prepare(Copy(current)))), cancellationToken)
                    ?? throw new InvalidOperationException("Skill store mutator returned null.");
                cancellationToken.ThrowIfCancellationRequested();
                if (mutation.Save)
                {
                    if (mutation.Record == null || !string.Equals(key, mutation.Record.Id?.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A skill mutation cannot write a different key.");
                    _store.Save(Copy(mutation.Record));
                }
                else if (mutation.Delete) _store.Delete(key);
                await SkillStoreCallbackContext.PublishAsync(key, mutation.Result, publish, callbackContext);
                return mutation.Result;
            }
        }
        private static SkillRecord Copy(SkillRecord record) => record == null ? null : new SkillRecord(
            record.Id, record.Description, record.Instructions, record.ToolNames, record.Version, record.Sections);
    }

    /// <summary>Serializes legacy callers; synchronous calls fail promptly while async work is pending.</summary>
    internal sealed class SkillOperationGate
    {
        private readonly object _state = new();
        private readonly Queue<TaskCompletionSource<IDisposable>> _waiters = new();
        private bool _occupied;
        private int _asyncUsers;
        private int _syncOwnerThread;
        private int _syncDepth;
        internal IDisposable EnterSync()
        {
            lock (_state)
            {
                if (_occupied && _syncOwnerThread == Environment.CurrentManagedThreadId)
                { _syncDepth++; return new Lease(this, false); }
                while (_occupied)
                {
                    if (_asyncUsers > 0) throw new InvalidOperationException("Skill operation is busy; await the current operation.");
                    Monitor.Wait(_state);
                }
                _occupied = true;
                _syncOwnerThread = Environment.CurrentManagedThreadId;
                _syncDepth = 1;
                return new Lease(this, false);
            }
        }
        internal Task<IDisposable> EnterAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_state)
            {
                _asyncUsers++;
                if (!_occupied) { _occupied = true; return Task.FromResult<IDisposable>(new Lease(this, true)); }
                TaskCompletionSource<IDisposable> waiter = new(TaskCreationOptions.None);
                _waiters.Enqueue(waiter);
                return AwaitLeaseAsync(waiter, token);
            }
        }
        private async Task<IDisposable> AwaitLeaseAsync(TaskCompletionSource<IDisposable> waiter, CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(() => waiter.TrySetCanceled(token));
            try { return await waiter.Task; }
            catch { lock (_state) { _asyncUsers--; Monitor.PulseAll(_state); } throw; }
        }
        private void Release(bool asynchronous)
        {
            TaskCompletionSource<IDisposable> next = null;
            lock (_state)
            {
                if (!asynchronous && _syncDepth > 0)
                {
                    if (--_syncDepth > 0) return;
                    _syncOwnerThread = 0;
                }
                if (asynchronous) _asyncUsers--;
                _occupied = false;
                while (_waiters.Count > 0)
                {
                    TaskCompletionSource<IDisposable> candidate = _waiters.Dequeue();
                    if (candidate.Task.IsCompleted) continue;
                    next = candidate;
                    _occupied = true;
                    break;
                }
                Monitor.PulseAll(_state);
            }
            if (next != null && !next.TrySetResult(new Lease(this, true))) Release(false);
        }
        private sealed class Lease : IDisposable
        {
            private SkillOperationGate _owner;
            private readonly bool _asynchronous;
            internal Lease(SkillOperationGate owner, bool asynchronous) { _owner = owner; _asynchronous = asynchronous; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_asynchronous);
        }
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
        private static readonly AsyncLocal<bool> AsyncActive = new();

        internal static void ThrowIfActive()
        {
            if (_active || AsyncActive.Value) throw new InvalidOperationException("Skill store callbacks must not re-enter skill stores or coordinators.");
        }

        internal static TResult Run<TResult>(Func<TResult> callback)
        {
            ThrowIfActive();
            _active = true;
            try { return callback(); }
            finally { _active = false; }
        }

        internal static async Task PublishAsync<TResult>(string id, TResult result,
            Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler context)
        {
            if (publish == null) return;
            try
            {
                await context.InvokeAsync(async () =>
                {
                    ThrowIfActive();
                    AsyncActive.Value = true;
                    try { await publish(result, CancellationToken.None); return true; }
                    finally { AsyncActive.Value = false; }
                }, CancellationToken.None);
            }
            catch (Exception ex) { throw new SkillStorePublicationException(id, ex); }
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
