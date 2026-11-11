using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>What a <c>require</c> call does next, as decided by <see cref="ModuleRequireGraph.Begin"/>.</summary>
    public enum RequireStepKind
    {
        /// <summary>The caller runs the module body now; it reports the outcome with Complete or Fail.</summary>
        Load,

        /// <summary>The module already returned in this environment; the step carries its value (R3.2, R3.3).</summary>
        Cached,

        /// <summary>Another thread is loading the module; the caller suspends until Complete or Fail wakes it.</summary>
        Wait,

        /// <summary>Waiting would never end; the step carries the require path (DEV-1, CYCLIC_REQUIRE).</summary>
        Cycle,

        /// <summary>The module raised while loading in this environment; the step carries the first error (R3.8).</summary>
        Failed
    }

    /// <summary>The load status of one module in one environment.</summary>
    public enum ModuleLoadStatus
    {
        Loading,
        Completed,
        Failed
    }

    /// <summary>The answer of <see cref="ModuleRequireGraph.Begin"/>.</summary>
    public readonly struct RequireStep
    {
        public readonly RequireStepKind Kind;

        /// <summary>The module's return value for <see cref="RequireStepKind.Cached"/>.</summary>
        public readonly object Value;

        /// <summary>The first load error for <see cref="RequireStepKind.Failed"/>.</summary>
        public readonly string FirstError;

        /// <summary>The module paths of the cycle, first module repeated at the end, for <see cref="RequireStepKind.Cycle"/>.</summary>
        public readonly IReadOnlyList<string> CyclePath;

        /// <summary>The thread loading the module, for <see cref="RequireStepKind.Wait"/>.</summary>
        public readonly object LoadingThread;

        private RequireStep(RequireStepKind kind, object value, string firstError, IReadOnlyList<string> cyclePath,
            object loadingThread)
        {
            Kind = kind;
            Value = value;
            FirstError = firstError;
            CyclePath = cyclePath;
            LoadingThread = loadingThread;
        }

        internal static RequireStep Load()
        {
            return new RequireStep(RequireStepKind.Load, null, null, null, null);
        }

        internal static RequireStep Cached(object value)
        {
            return new RequireStep(RequireStepKind.Cached, value, null, null, null);
        }

        internal static RequireStep Wait(object loadingThread)
        {
            return new RequireStep(RequireStepKind.Wait, null, null, null, loadingThread);
        }

        internal static RequireStep Cycle(IReadOnlyList<string> path)
        {
            return new RequireStep(RequireStepKind.Cycle, null, null, path, null);
        }

        internal static RequireStep Failed(string firstError)
        {
            return new RequireStep(RequireStepKind.Failed, null, firstError, null, null);
        }
    }

    /// <summary>
    /// The engine-free <c>require</c> cache and cycle detector of decisions SC-11 and SC-12. An environment is one
    /// requiring script's Lua state (any object, compared by reference): a module runs at most once per environment
    /// and every <c>require</c> there gets the same value (R3.2, R3.3); another environment loads it afresh.
    /// A module body runs either inline on the requiring thread, or on a thread of its own after
    /// <see cref="HandOff"/>. Either way the graph knows which thread loads each module and which module each suspended
    /// thread waits for, so a request whose wait would close a loop is a <see cref="RequireStepKind.Cycle"/> and any
    /// other request for a loading module is a <see cref="RequireStepKind.Wait"/>. Single-threaded, like the VM.
    /// </summary>
    public sealed class ModuleRequireGraph
    {
        private sealed class Entry
        {
            public ModuleLoadStatus Status;
            public object LoaderThread;
            public object Value;
            public string FirstError;
            public string Path;
        }

        private sealed class ThreadState
        {
            public readonly List<InstanceId> LoadStack = new List<InstanceId>();
            public InstanceId WaitingFor;
        }

        private sealed class EnvironmentState
        {
            public readonly Dictionary<InstanceId, Entry> Entries = new Dictionary<InstanceId, Entry>();

            public readonly Dictionary<object, ThreadState> Threads =
                new Dictionary<object, ThreadState>(ReferenceComparer.Instance);
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static readonly IReadOnlyList<object> NoThreads = Array.Empty<object>();

        private readonly Dictionary<object, EnvironmentState> _environments =
            new Dictionary<object, EnvironmentState>(ReferenceComparer.Instance);

        /// <summary>
        /// Starts a <c>require</c> of <paramref name="module"/> by <paramref name="thread"/> in
        /// <paramref name="environment"/>. <paramref name="modulePath"/> (for example <c>ReplicatedStorage.A</c>) names the
        /// module in a cycle path. On <see cref="RequireStepKind.Load"/> the module is marked loading by this thread; on
        /// <see cref="RequireStepKind.Wait"/> the thread is recorded as waiting for it.
        /// </summary>
        public RequireStep Begin(object environment, InstanceId module, string modulePath, object thread)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            if (thread == null)
            {
                throw new ArgumentNullException(nameof(thread));
            }

            if (!module.IsValid)
            {
                throw new ArgumentException("a required module has a valid instance id", nameof(module));
            }

            if (string.IsNullOrEmpty(modulePath))
            {
                throw new ArgumentException("a required module has a path for messages", nameof(modulePath));
            }

            EnvironmentState state = GetOrCreateEnvironment(environment);
            ThreadState requester = GetOrCreateThread(state, thread);

            // WHY: a thread that calls require is running, so any wait it was recorded in has ended.
            requester.WaitingFor = InstanceId.None;
            if (state.Entries.TryGetValue(module, out Entry entry))
            {
                switch (entry.Status)
                {
                    case ModuleLoadStatus.Completed:
                        return RequireStep.Cached(entry.Value);
                    case ModuleLoadStatus.Failed:
                        return RequireStep.Failed(entry.FirstError);
                    default:
                        List<string> cycle = FindCycle(state, module, thread);
                        if (cycle != null)
                        {
                            return RequireStep.Cycle(cycle);
                        }

                        requester.WaitingFor = module;
                        return RequireStep.Wait(entry.LoaderThread);
                }
            }

            state.Entries.Add(module, new Entry
            {
                Status = ModuleLoadStatus.Loading,
                LoaderThread = thread,
                Path = modulePath
            });
            requester.LoadStack.Add(module);
            return RequireStep.Load();
        }

        /// <summary>
        /// Moves a loading module to the thread that runs its body: <paramref name="bodyThread"/> now loads it, and the
        /// thread that required it waits for it.
        /// </summary>
        public void HandOff(object environment, InstanceId module, object bodyThread)
        {
            if (bodyThread == null)
            {
                throw new ArgumentNullException(nameof(bodyThread));
            }

            EnvironmentState state = RequireEnvironment(environment);
            Entry entry = RequireLoading(state, module);
            if (ReferenceEquals(entry.LoaderThread, bodyThread))
            {
                return;
            }

            ThreadState previous = GetOrCreateThread(state, entry.LoaderThread);
            previous.LoadStack.Remove(module);
            previous.WaitingFor = module;
            GetOrCreateThread(state, bodyThread).LoadStack.Add(module);
            entry.LoaderThread = bodyThread;
        }

        /// <summary>
        /// Records the module's single non-nil return value. Returns the threads that were waiting for it, which the caller
        /// resumes; their waits are cleared.
        /// </summary>
        public IReadOnlyList<object> Complete(object environment, InstanceId module, object value)
        {
            EnvironmentState state = RequireEnvironment(environment);
            Entry entry = RequireLoading(state, module);
            entry.Status = ModuleLoadStatus.Completed;
            entry.Value = value;
            return Settle(state, module, entry);
        }

        /// <summary>
        /// Records that the module raised while loading. The entry stays failed in this environment, and every later
        /// <see cref="Begin"/> answers <see cref="RequireStepKind.Failed"/> with this first error, until
        /// <see cref="Invalidate"/> or <see cref="ForgetEnvironment"/> (R3.8). Returns the waiting threads, as
        /// <see cref="Complete"/> does.
        /// </summary>
        public IReadOnlyList<object> Fail(object environment, InstanceId module, string error)
        {
            EnvironmentState state = RequireEnvironment(environment);
            Entry entry = RequireLoading(state, module);
            entry.Status = ModuleLoadStatus.Failed;
            entry.FirstError = string.IsNullOrEmpty(error) ? "the module raised an error" : error;
            return Settle(state, module, entry);
        }

        /// <summary>
        /// Forgets a thread that stopped (cancelled or killed with its script). Every module it was still loading fails
        /// with <paramref name="reason"/>; returns the threads that were waiting for those modules.
        /// </summary>
        public IReadOnlyList<object> ForgetThread(object environment, object thread, string reason)
        {
            if (environment == null || thread == null
                || !_environments.TryGetValue(environment, out EnvironmentState state)
                || !state.Threads.TryGetValue(thread, out ThreadState threadState))
            {
                return NoThreads;
            }

            List<object> woken = new List<object>();
            InstanceId[] loading = threadState.LoadStack.ToArray();
            for (int index = loading.Length - 1; index >= 0; index--)
            {
                IReadOnlyList<object> waiters = Fail(environment, loading[index],
                    string.IsNullOrEmpty(reason) ? "the thread loading the module stopped" : reason);
                woken.AddRange(waiters);
            }

            state.Threads.Remove(thread);
            woken.Remove(thread);
            return woken;
        }

        /// <summary>
        /// Drops the completed or failed entries of a module whose source changed, in every environment, so the next
        /// <see cref="Begin"/> loads it again. A load still in progress is left alone: the dependents restart anyway
        /// (decision SC-18).
        /// </summary>
        public void Invalidate(InstanceId module)
        {
            foreach (EnvironmentState state in _environments.Values)
            {
                if (state.Entries.TryGetValue(module, out Entry entry) && entry.Status != ModuleLoadStatus.Loading)
                {
                    state.Entries.Remove(module);
                }
            }
        }

        /// <summary>Drops everything an environment knows; called when its script stops or restarts.</summary>
        public void ForgetEnvironment(object environment)
        {
            if (environment != null)
            {
                _environments.Remove(environment);
            }
        }

        /// <summary>
        /// The environments that hold the module in any status, so the runtime can restart the scripts that depend on it
        /// after a reload or revert (decision SC-18). An environment includes every module required through other
        /// modules, so the dependents are transitive.
        /// </summary>
        public IReadOnlyList<object> EnvironmentsUsing(InstanceId module)
        {
            List<object> result = new List<object>();
            foreach (KeyValuePair<object, EnvironmentState> pair in _environments)
            {
                if (pair.Value.Entries.ContainsKey(module))
                {
                    result.Add(pair.Key);
                }
            }

            return result;
        }

        /// <summary>The module's status in an environment; false when the environment never required it.</summary>
        public bool TryGetStatus(object environment, InstanceId module, out ModuleLoadStatus status)
        {
            status = ModuleLoadStatus.Loading;
            if (environment == null || !_environments.TryGetValue(environment, out EnvironmentState state)
                || !state.Entries.TryGetValue(module, out Entry entry))
            {
                return false;
            }

            status = entry.Status;
            return true;
        }

        /// <summary>
        /// Follows the wait-for chain from the module's loader. A cycle exists when the chain reaches the requesting thread:
        /// every thread on it waits, directly or through others, for a module the requester is loading. The path lists
        /// each module in the order it was required, the requested module first and last.
        /// </summary>
        private static List<string> FindCycle(EnvironmentState state, InstanceId requested, object requester)
        {
            List<string> path = new List<string> { state.Entries[requested].Path };
            HashSet<object> visited = new HashSet<object>(ReferenceComparer.Instance);
            InstanceId current = requested;
            object loader = state.Entries[requested].LoaderThread;
            while (true)
            {
                if (!visited.Add(loader))
                {
                    return null;
                }

                ThreadState loaderState = GetOrCreateThread(state, loader);
                int at = loaderState.LoadStack.IndexOf(current);
                for (int index = at + 1; at >= 0 && index < loaderState.LoadStack.Count; index++)
                {
                    path.Add(state.Entries[loaderState.LoadStack[index]].Path);
                }

                if (ReferenceEquals(loader, requester))
                {
                    path.Add(state.Entries[requested].Path);
                    return path;
                }

                InstanceId next = loaderState.WaitingFor;
                if (!next.IsValid || !state.Entries.TryGetValue(next, out Entry nextEntry)
                    || nextEntry.Status != ModuleLoadStatus.Loading)
                {
                    return null;
                }

                path.Add(nextEntry.Path);
                current = next;
                loader = nextEntry.LoaderThread;
            }
        }

        private static IReadOnlyList<object> Settle(EnvironmentState state, InstanceId module, Entry entry)
        {
            if (state.Threads.TryGetValue(entry.LoaderThread, out ThreadState loader))
            {
                loader.LoadStack.Remove(module);
            }

            entry.LoaderThread = null;
            List<object> waiters = null;
            foreach (KeyValuePair<object, ThreadState> pair in state.Threads)
            {
                if (pair.Value.WaitingFor == module)
                {
                    pair.Value.WaitingFor = InstanceId.None;
                    waiters ??= new List<object>();
                    waiters.Add(pair.Key);
                }
            }

            return waiters ?? NoThreads;
        }

        private EnvironmentState GetOrCreateEnvironment(object environment)
        {
            if (!_environments.TryGetValue(environment, out EnvironmentState state))
            {
                state = new EnvironmentState();
                _environments.Add(environment, state);
            }

            return state;
        }

        private EnvironmentState RequireEnvironment(object environment)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            if (!_environments.TryGetValue(environment, out EnvironmentState state))
            {
                throw new InvalidOperationException("the environment has no module loading");
            }

            return state;
        }

        private static Entry RequireLoading(EnvironmentState state, InstanceId module)
        {
            if (!state.Entries.TryGetValue(module, out Entry entry) || entry.Status != ModuleLoadStatus.Loading)
            {
                throw new InvalidOperationException("module " + module + " is not loading in this environment");
            }

            return entry;
        }

        private static ThreadState GetOrCreateThread(EnvironmentState state, object thread)
        {
            if (!state.Threads.TryGetValue(thread, out ThreadState threadState))
            {
                threadState = new ThreadState();
                state.Threads.Add(thread, threadState);
            }

            return threadState;
        }
    }
}
