using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.AiMemory
{
    /// <summary>
    /// Что и сколько отрезал <see cref="FileAgentMemoryStore"/> у переписки одной роли, когда она упёрлась
    /// в потолок. Ключ роли не передаётся: это может быть scoped-ключ ученика, а событие уходит в лог хоста.
    /// </summary>
    public readonly struct AgentHistoryTrimmedEventArgs
    {
        internal AgentHistoryTrimmedEventArgs(int droppedChatMessages, int droppedTranscriptEntries,
            int retainedChatMessages, int retainedTranscriptEntries, int maxChatHistoryMessages,
            int maxTranscriptEntries)
        {
            DroppedChatMessages = droppedChatMessages;
            DroppedTranscriptEntries = droppedTranscriptEntries;
            RetainedChatMessages = retainedChatMessages;
            RetainedTranscriptEntries = retainedTranscriptEntries;
            MaxChatHistoryMessages = maxChatHistoryMessages;
            MaxTranscriptEntries = maxTranscriptEntries;
        }

        /// <summary>Сколько самых старых сообщений чата исчезло из истории этим усечением.</summary>
        public int DroppedChatMessages { get; }

        /// <summary>Сколько строк транскрипта (включая сообщения чата) исчезло этим усечением.</summary>
        public int DroppedTranscriptEntries { get; }

        /// <summary>Сколько сообщений чата осталось.</summary>
        public int RetainedChatMessages { get; }

        /// <summary>Сколько строк транскрипта осталось.</summary>
        public int RetainedTranscriptEntries { get; }

        /// <summary>Действующий потолок сообщений чата.</summary>
        public int MaxChatHistoryMessages { get; }

        /// <summary>Действующий потолок строк транскрипта.</summary>
        public int MaxTranscriptEntries { get; }
    }

    /// <summary>
    /// File-backed Unity implementation of agent memory, chat history, and conversation transcripts.
    /// Data is stored below <see cref="Application.persistentDataPath"/> in the CoreAI folder so it
    /// survives scene reloads and player restarts.
    /// <para>
    /// <b>Раскладка на диске (v2).</b> На роль два файла: <c>&lt;stem&gt;.json</c> — документ памяти
    /// (текст, версии, снимок системного промпта; переписывается целиком только при мутации памяти) и
    /// <c>&lt;stem&gt;.history.jsonl</c> — переписка, по одной JSON-строке на запись. Сообщение чата и его
    /// строка транскрипта — ОДНА запись с флагом <c>m</c>, а не две копии одного текста. Добавление сообщения
    /// — это дозапись одной строки в конец, а не чтение-разбор-сериализация-замена всего файла. Файл старого
    /// формата (переписка внутри документа памяти в полях <c>chatHistoryJson</c> /
    /// <c>transcriptEntriesJson</c>) мигрируется при первом обращении к переписке роли.
    /// </para>
    /// <para>
    /// <b>Потолки.</b> Переписка усекается до <c>maxChatHistoryMessages</c> сообщений чата и
    /// <c>maxTranscriptEntries</c> строк транскрипта. Усечение видимо: событие <see cref="HistoryTrimmed"/>
    /// на каждое усечение и запись в лог (первый раз для роли и далее при каждом уплотнении файла). Файл
    /// уплотняется не на каждое сообщение, а когда в его голове накопилось <see cref="CompactionSlack"/>
    /// уже отрезанных строк; при чтении лишние строки головы отбрасываются, поэтому читатели никогда не
    /// видят больше потолка.
    /// </para>
    /// <para>
    /// <b>Честность записи.</b> Стор различает «файла нет» (<see cref="AgentMemoryLoadStatus.NotFound"/>)
    /// и «файл есть, но не читается» (<see cref="AgentMemoryLoadStatus.Failed"/>): атомарная мутация на
    /// нечитаемом документе бросает <see cref="AgentMemoryLoadException"/> и НЕ перезаписывает его пустым.
    /// Запись документа памяти не зависит от чтения старого файла, поэтому битый документ лечится первым же
    /// <see cref="Save"/> или <see cref="Clear"/>; оборванная строка в файле переписки пропускается с
    /// предупреждением и стирается при следующем уплотнении. <c>persistToDisk=false</c> означает «только в
    /// этом процессе»: такая запись НИКОГДА не попадает на диск, в том числе при уплотнении.
    /// </para>
    /// <para>
    /// <b>WebGL.</b> <c>persistentDataPath</c> здесь — MEMFS в памяти вкладки; в IndexedDB его доводит
    /// асинхронный <c>FS.syncfs</c>. Синхронные методы интерфейса после записи лишь СТАВЯТ флаш в очередь
    /// (<see cref="CoreAiWebGlPersistence.Sync"/>) — они не могут ждать на единственном потоке. Async-варианты
    /// (<see cref="SaveAsync"/>, <see cref="MutateAsync{TResult}"/>, <see cref="AppendChatMessageAsync"/> и
    /// далее) возвращаются только после подтверждения браузера и бросают <see cref="IOException"/>, если
    /// подтверждения нет. После серии синхронных записей вызовите <see cref="FlushAsync"/>, чтобы дождаться
    /// подтверждения всего, что уже записано. На остальных платформах подтверждение мгновенно.
    /// </para>
    /// </summary>
    public sealed class FileAgentMemoryStore : IAgentMemoryStore, IAgentMemoryLoadDiagnostics,
        IAtomicAgentMemoryStore, IConversationTranscriptStore, IDisposable
    {
        /// <summary>Потолок сообщений чата по умолчанию.</summary>
        public const int DefaultMaxChatHistoryMessages = 500;

        /// <summary>Потолок строк транскрипта по умолчанию.</summary>
        public const int DefaultMaxTranscriptEntries = 2000;

        /// <summary>
        /// Process-wide mutation locks keyed by each role's persisted memory file path, one entry per
        /// distinct role id ever mutated. Entries are intentionally never evicted: a caller could already
        /// hold the <see cref="SemaphoreSlim"/> instance fetched from this dictionary while a concurrent
        /// eviction-then-<c>GetOrAdd</c> for the same key hands a second caller a fresh instance, which
        /// would silently break the mutual exclusion this lock exists for. In practice the key set is
        /// bounded by the number of distinct agent roles a host ever creates, which is small relative to
        /// process lifetime.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks =
            new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> HistoryRevisions =
            new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> HistoryClearRevisions =
            new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> HistoryOrders =
            new(StringComparer.Ordinal);

        /// <summary>Документ памяти роли на диске (JsonUtility; имена полей — контракт файла).</summary>
        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;

            // WHY: поля формата v1 — переписка жила внутри документа памяти. Читаются только ради миграции
            // в <stem>.history.jsonl и после неё всегда пустые; писать в них нельзя.
            public string chatHistoryJson;
            public string transcriptEntriesJson;

            // WHY: Memory versioning + stable-prefix snapshot. Persisted so the documented rollback feature
            // (ListVersions/Revert) and the system-prompt tail-update prompt-cache optimization survive a
            // reload; the orchestrator re-reads state from disk every request, so dropping these silently
            // disables both. Versions are stored as a JSON string (JsonConvert) since JsonUtility cannot
            // round-trip an array of reference-typed objects through a string field reliably.
            public string versionsJson;
            public string systemPromptMemorySnapshot;
            public int systemPromptMemoryVersion;
            public int maxMemoryVersions;
        }

        /// <summary>Одна строка файла переписки. Короткие имена — это формат файла, не стиль.</summary>
        private sealed class HistoryLine
        {
            [JsonProperty("k")] public int Kind;
            [JsonProperty("r")] public string Key;
            [JsonProperty("c")] public string Content;
            [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)] public string CallId;
            [JsonProperty("t")] public long Timestamp;
            // Additive ordering metadata: legacy rows default to zero and retain their file order.
            // Unlike Unix seconds, this distinguishes concurrent turns and session-only rows.
            [JsonProperty("o", DefaultValueHandling = DefaultValueHandling.Ignore)] public long Order;

            /// <summary>Строка одновременно является сообщением плоской истории чата.</summary>
            [JsonProperty("m")] public bool IsChatMessage;

            /// <summary>
            /// Только в этом процессе (<c>persistToDisk=false</c>): строка живёт в памяти и никогда не
            /// пишется в файл. Не сериализуется — на диске таких строк не бывает по определению.
            /// </summary>
            [JsonIgnore] public bool Persisted = true;
            [JsonIgnore] public bool PendingWrite;

            /// <summary>
            /// Строка входит в окно чата (последние <c>maxChatHistoryMessages</c> сообщений). Вычисляется при
            /// загрузке заново, поэтому не сериализуется.
            /// </summary>
            [JsonIgnore] public bool InChatView;

            /// <summary>Строка входит в окно транскрипта (последние <c>maxTranscriptEntries</c> строк).</summary>
            [JsonIgnore] public bool InTranscriptView;
        }

        /// <summary>Загруженная переписка одной роли плюс учёт того, что лежит в её файле.</summary>
        private sealed class RoleHistory
        {
            public readonly List<HistoryLine> Lines = new();

            /// <summary>Сколько сообщений чата сейчас в окне чата.</summary>
            public int ChatCount;

            /// <summary>Сколько строк сейчас в окне транскрипта.</summary>
            public int TranscriptCount;

            /// <summary>Сколько строк в <see cref="Lines"/> предназначено для диска.</summary>
            public int PersistedCount;

            /// <summary>Сколько строк физически лежит в файле, включая уже отрезанные и нечитаемые.</summary>
            public int FileLineCount;

            /// <summary>
            /// Файл надо переписать из памяти при следующей записи: в нём есть нечитаемые строки, либо
            /// дозапись не удалась и содержимое файла расходится с памятью.
            /// </summary>
            public bool NeedsRewrite;

            /// <summary>
            /// Файл существует, но прочитать его не удалось (ввод-вывод, не разбор). Его содержимое неизвестно,
            /// поэтому переписывать его из памяти нельзя — это стёрло бы то, чего мы не видели. Дозапись
            /// безопасна.
            /// </summary>
            public bool LoadFailed;

            /// <summary>Первое усечение для роли уже отмечено в логе.</summary>
            public bool TrimLogged;
            public long Revision;
        }

        private static readonly JsonSerializerSettings HistoryJson = new()
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore
        };

        private static readonly JsonSerializerSettings CompactJson = new() { Formatting = Formatting.None };

        private readonly string _dir;
        private readonly Dictionary<string, RoleHistory> _histories = new(StringComparer.Ordinal);
        private readonly ILog _log;
        private readonly int _maxChatHistoryMessages;
        private readonly int _maxTranscriptEntries;

        /// <summary>
        /// Serializes all file and in-memory cache access for this store instance so the async
        /// thread-pool offloads cannot race the synchronous (main-thread) interface methods.
        /// SemaphoreSlim is not reentrant, so the gate is acquired only in public entry points;
        /// private *Core helpers assume the gate is already held. Под этим замком нельзя ждать ничего,
        /// что по-настоящему уступает поток: на WebGL синхронные методы берут его блокирующим
        /// <c>Wait()</c>, и ожидание браузерного колбэка под замком остановило бы единственный поток.
        /// <para>
        /// WHY every <c>await</c> that acquires or releases <see cref="_gate"/> or a mutation gate (see
        /// <see cref="GetMutationGate"/>) uses <c>.ConfigureAwait(false)</c>: without it, a continuation
        /// that resumes off the calling thread (real <c>Task.Run</c> in <see cref="RunOffThread(Action)"/>
        /// off WebGL) is posted back to whatever <see cref="SynchronizationContext"/> was captured when
        /// the async method was entered. If that call came from Unity's main thread, the posted
        /// continuation — which is what releases <see cref="_gate"/> / the mutation gate — sits in the
        /// main-thread queue. A synchronous <c>Save</c>/<c>TryLoad</c>/<c>GetTranscriptEntries</c> call
        /// on that same main thread blocks on the same gate via <c>Wait()</c> and never returns to pump
        /// that queue: permanent deadlock, no exception. This is NOT the WebGL-pool hazard
        /// <c>WebGlUnsafeAsyncPrimitivesEditModeTests</c> normally bans <c>ConfigureAwait(false)</c> for
        /// (see its allowlist entry for this file): on WebGL, <see cref="RunOffThread(Action)"/> runs the
        /// action inline and returns an already-completed task, and the gate-acquire → work → gate-release
        /// chain never yields before both gates are released (nothing else runs on WebGL's single
        /// cooperative thread to contend for them mid-chain), so every await in that chain is already
        /// complete when awaited there — <c>ConfigureAwait(false)</c> never causes a continuation to be
        /// scheduled at all, let alone onto a thread pool WebGL doesn't have.
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Creates a file-backed agent memory store under CoreAI persistent data.</summary>
        /// <param name="log">Optional logger.</param>
        /// <param name="rootDirectory">
        /// Optional override for the storage directory (used by tests); defaults to the CoreAI
        /// agent-memory folder under <see cref="Application.persistentDataPath"/>.
        /// </param>
        /// <param name="maxChatHistoryMessages">Потолок сообщений чата на роль.</param>
        /// <param name="maxTranscriptEntries">Потолок строк транскрипта на роль.</param>
        public FileAgentMemoryStore(ILog log = null, string rootDirectory = null,
            int maxChatHistoryMessages = DefaultMaxChatHistoryMessages,
            int maxTranscriptEntries = DefaultMaxTranscriptEntries)
        {
            _dir = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.AgentMemory);
            _log = log;
            _maxChatHistoryMessages = Math.Max(1, maxChatHistoryMessages);
            _maxTranscriptEntries = Math.Max(1, maxTranscriptEntries);
        }

        /// <summary>
        /// Срабатывает после каждого усечения переписки (уже вне внутренних замков стора, так что из
        /// обработчика можно обращаться к стору). Дополняет запись в логе, а не заменяет её.
        /// </summary>
        public event Action<AgentHistoryTrimmedEventArgs> HistoryTrimmed;

        /// <summary>Действующий потолок сообщений чата.</summary>
        public int MaxChatHistoryMessages => _maxChatHistoryMessages;

        /// <summary>Действующий потолок строк транскрипта.</summary>
        public int MaxTranscriptEntries => _maxTranscriptEntries;

        /// <summary>
        /// Сколько уже отрезанных строк может накопиться в голове файла переписки до его уплотнения.
        /// Меньше — чаще полная перезапись, больше — больше мусора на диске; читатели мусор не видят.
        /// </summary>
        internal int CompactionSlack => Math.Max(16, _maxTranscriptEntries / 8);

        /// <summary>
        /// Runs file I/O on the thread pool so large reads/writes do not stall the Unity main thread.
        /// On WebGL (no threads) the work runs inline instead.
        /// </summary>
        private static Task RunOffThread(Action action)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            action();
            return Task.CompletedTask;
#else
            return Task.Run(action);
#endif
        }

        /// <inheritdoc cref="RunOffThread(Action)"/>
        private static Task<T> RunOffThread<T>(Func<T> func)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return Task.FromResult(func());
#else
            return Task.Run(func);
#endif
        }

        /// <summary>
        /// Дожидается подтверждения долговечности всего, что уже записано: на WebGL — завершения
        /// <c>FS.syncfs</c> в IndexedDB, на остальных платформах — мгновенно. <c>false</c> — браузер
        /// подтверждения не дал (уже в логе), последние записи могут не пережить перезагрузку.
        /// </summary>
        public Task<bool> FlushAsync(CancellationToken cancellationToken = default)
        {
            return CoreAiWebGlPersistence.SyncAsync(cancellationToken).AsTask();
        }

        /// <summary>
        /// Подтверждение долговечности для async-путей. Вызывается ПОСЛЕ освобождения замков: на WebGL
        /// ожидание колбэка под замком остановило бы синхронных вызывающих на единственном потоке.
        /// </summary>
        private static async Task ConfirmDurableAsync(CancellationToken cancellationToken)
        {
            bool durable = await CoreAiWebGlPersistence.SyncAsync(cancellationToken);
            if (!durable)
            {
                throw new IOException(
                    "[FileAgentMemoryStore] The write reached the in-memory filesystem but the browser did not " +
                    "confirm it in IndexedDB; it may not survive a reload.");
            }
        }

        #region Memory document

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return TryLoadDetailed(roleId, out state) == AgentMemoryLoadStatus.Loaded;
        }

        /// <inheritdoc />
        public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
        {
            _gate.Wait();
            try
            {
                return TryLoadDetailedCore(roleId, out state);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="TryLoad"/> that performs the file read on the thread pool.
        /// Returns the loaded state, or <c>null</c> when no memory exists for the role.
        /// </summary>
        /// <exception cref="AgentMemoryLoadException">Документ есть, но прочитать его не удалось.</exception>
        public async Task<AgentMemoryState> TryLoadAsync(string roleId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await RunOffThread(() =>
                {
                    AgentMemoryLoadStatus status = TryLoadDetailedCore(roleId, out AgentMemoryState state);
                    if (status == AgentMemoryLoadStatus.Failed)
                    {
                        throw new AgentMemoryLoadException(roleId);
                    }

                    return state;
                }).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private AgentMemoryLoadStatus TryLoadDetailedCore(string roleId, out AgentMemoryState state)
        {
            state = null;
            string path = GetMemoryPath(roleId);
            if (!File.Exists(path))
            {
                return AgentMemoryLoadStatus.NotFound;
            }

            try
            {
                Persisted p = ReadPersisted(path);
                state = new AgentMemoryState
                {
                    LastSystemPrompt = p.lastSystemPrompt ?? "",
                    Memory = p.memory ?? "",
                    SystemPromptMemorySnapshot = p.systemPromptMemorySnapshot ?? "",
                    SystemPromptMemoryVersion = p.systemPromptMemoryVersion,
                    MaxMemoryVersions = p.maxMemoryVersions > 0
                        ? p.maxMemoryVersions
                        : AgentMemoryState.DefaultMaxMemoryVersions,
                    Versions = DeserializeVersions(p.versionsJson)
                };
                return AgentMemoryLoadStatus.Loaded;
            }
            catch (Exception ex)
            {
                // WHY: файл ЕСТЬ, а что в нём — неизвестно. Это не «памяти нет»: вызывающий, который
                // примет пустое за истину и сохранит, сотрёт документ и все его версии.
                LogStorageFailure("load memory", ex);
                return AgentMemoryLoadStatus.Failed;
            }
        }

        /// <summary>Читает документ памяти; любой дефект файла — исключение, а не пустой документ.</summary>
        private static Persisted ReadPersisted(string path)
        {
            string json = File.ReadAllText(path);
            Persisted p = JsonUtility.FromJson<Persisted>(json);
            if (p == null)
            {
                throw new InvalidDataException("Memory document is empty or is not a JSON object.");
            }

            return p;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Best-effort по контракту <c>void</c>: сбой записи уходит в лог, исключение не пробрасывается —
        /// оркестратор зовёт этот метод ради кэша промпта посреди хода, и падение диска не должно ронять
        /// ход. Кому нужен результат записи — <see cref="SaveAsync"/> или <see cref="MutateAsync{TResult}"/>.
        /// </remarks>
        public void Save(string roleId, AgentMemoryState state)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    SaveCore(roleId, state, true);
                }
                catch (Exception ex)
                {
                    LogStorageFailure("save memory", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="Save"/> that performs the atomic file write on the thread pool and
        /// returns only after the write is durable (on WebGL — confirmed by the browser).
        /// </summary>
        /// <exception cref="IOException">Запись не удалась или не подтверждена.</exception>
        public async Task SaveAsync(string roleId, AgentMemoryState state, CancellationToken cancellationToken = default)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunOffThread(() => SaveCore(roleId, state, false)).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Выполняет контракт буквально: результат мутации лежит на диске (на WebGL — подтверждён
        /// браузером) ДО того, как метод вернёт результат. Любой сбой — исключение, не тихий лог.
        /// </remarks>
        /// <exception cref="AgentMemoryLoadException">
        /// Документ роли существует, но не читается: мутатор не запускается, документ не трогается.
        /// </exception>
        /// <exception cref="IOException">Запись не удалась или не подтверждена.</exception>
        public async Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            TResult result;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    result = await RunOffThread(() =>
                    {
                        AgentMemoryLoadStatus status = TryLoadDetailedCore(roleId, out AgentMemoryState state);
                        if (status == AgentMemoryLoadStatus.Failed)
                        {
                            // WHY: раньше здесь стояло `TryLoadCore(roleId) ?? new AgentMemoryState()`:
                            // временный сбой чтения превращался в мутацию ПУСТОГО документа, а затем
                            // успешная запись заменяла им настоящую память и все её версии.
                            throw new AgentMemoryLoadException(roleId);
                        }

                        state ??= new AgentMemoryState();
                        TResult mutated = mutator(state);
                        SaveCore(roleId, state, false);
                        return mutated;
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
            return result;
        }

        /// <summary>
        /// Пишет документ памяти из <paramref name="state"/>. Старый файл НЕ читается: содержимое документа
        /// целиком в состоянии, поэтому битый файл просто перезаписывается. Исключения пробрасываются —
        /// решать, глотать ли их, вызывающему по его контракту.
        /// </summary>
        private void SaveCore(string roleId, AgentMemoryState state, bool queueFlush)
        {
            state ??= new AgentMemoryState();
            // WHY: файл формата v1 может ещё нести переписку в полях документа. Вынести её в свой файл
            // ДО перезаписи документа — иначе Save стёр бы историю ученика вместе с полями.
            EnsureHistoryLoaded(roleId);
            EnsureDir();
            Persisted p = new()
            {
                lastSystemPrompt = state.LastSystemPrompt,
                memory = state.Memory,
                systemPromptMemorySnapshot = state.SystemPromptMemorySnapshot,
                systemPromptMemoryVersion = state.SystemPromptMemoryVersion,
                maxMemoryVersions = state.MaxMemoryVersions,
                versionsJson = SerializeVersions(state.Versions)
            };
            AtomicWriteAllText(GetMemoryPath(roleId), JsonUtility.ToJson(p));
            if (queueFlush)
            {
                QueueFlush();
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Удаляет документ памяти роли (текст, версии, снимок промпта); переписка живёт в своём файле и не
        /// трогается. Нечитаемый документ тоже удаляется: это единственный явный способ вернуть роли
        /// работоспособную память после порчи файла, и после него <see cref="TryLoadDetailed"/> честно
        /// отвечает <see cref="AgentMemoryLoadStatus.NotFound"/>.
        /// </remarks>
        public void Clear(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    ClearCore(roleId, true);
                }
                catch (Exception ex)
                {
                    LogStorageFailure("clear memory", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="Clear"/> that performs the file removal on the thread pool and
        /// returns after it is durable.
        /// </summary>
        /// <exception cref="IOException">Удаление не удалось или не подтверждено.</exception>
        public async Task ClearAsync(string roleId, CancellationToken cancellationToken = default)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunOffThread(() => ClearCore(roleId, false)).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
        }

        private void ClearCore(string roleId, bool queueFlush)
        {
            // WHY: сначала вынести переписку из файла v1 (если она там), и только потом удалять документ.
            EnsureHistoryLoaded(roleId);
            string path = GetMemoryPath(roleId);
            if (!File.Exists(path))
            {
                return;
            }

            if (TryLoadDetailedCore(roleId, out _) == AgentMemoryLoadStatus.Failed)
            {
                _log?.Warn("[FileAgentMemoryStore] Removing an unreadable memory document on clear.");
            }

            File.Delete(path);
            if (queueFlush)
            {
                QueueFlush();
            }
        }

        #endregion

        #region Conversation history

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    ClearChatHistoryCore(roleId, true);
                }
                catch (Exception ex)
                {
                    LogStorageFailure("clear chat history", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="ClearChatHistory"/> that performs the file removal on the thread
        /// pool and returns after it is durable.
        /// </summary>
        /// <exception cref="IOException">Удаление не удалось или не подтверждено.</exception>
        public async Task ClearChatHistoryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunOffThread(() => ClearChatHistoryCore(roleId, false)).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
        }

        private void ClearChatHistoryCore(string roleId, bool queueFlush)
        {
            // WHY: миграция v1 обязана случиться до удаления — иначе переписка осталась бы в полях документа
            // памяти и «воскресла» бы при следующем обращении.
            EnsureHistoryLoaded(roleId);
            // A previous migration may have written history but failed to remove the legacy fields.
            // Remove those fields before deleting history, or the next store would resurrect it.
            string memoryPath = GetMemoryPath(roleId);
            if (File.Exists(memoryPath))
            {
                Persisted legacy = ReadPersisted(memoryPath);
                if (!string.IsNullOrEmpty(legacy.chatHistoryJson) ||
                    !string.IsNullOrEmpty(legacy.transcriptEntriesJson))
                {
                    legacy.chatHistoryJson = null;
                    legacy.transcriptEntriesJson = null;
                    AtomicWriteAllText(memoryPath, JsonUtility.ToJson(legacy));
                }
            }

            string path = GetHistoryPath(roleId);
            if (!File.Exists(path))
            {
                _histories.Remove(roleId);
                HistoryClearRevisions[GetHistoryPath(roleId)] = AdvanceHistoryRevision(roleId);
                return;
            }

            // WHY: память сбрасывается только после удавшегося удаления файла. Наоборот (сначала забыть,
            // потом удалять) превращало сбой диска в «успешный» сброс: чтение отдавало пустоту, а файл
            // оставался, и история «воскресала» при следующей загрузке.
            File.Delete(path);
            _histories.Remove(roleId);
            HistoryClearRevisions[GetHistoryPath(roleId)] = AdvanceHistoryRevision(roleId);
            if (queueFlush)
            {
                QueueFlush();
            }
        }

        /// <summary>
        /// Appends a chat message to the role's conversation. С <paramref name="persistToDisk"/> =
        /// <c>false</c> сообщение живёт только в этом процессе и на диск не попадает никогда.
        /// </summary>
        /// <remarks>Best-effort по контракту <c>void</c>: сбой записи — в лог, файл будет переписан из
        /// памяти при следующей удачной записи. Подтверждённый вариант — <see cref="AppendChatMessageAsync"/>.</remarks>
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            // WHY: как AppendTranscriptEntry: пустой role id дал бы общий stem ".history.jsonl", где
            // смешались бы чужие вызовы, а null ронял бы вызов исключением из SanitizedFileStem.
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            AgentHistoryTrimmedEventArgs? trimmed = null;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    trimmed = AppendLineCore(roleId, ChatLine(role, content), persistToDisk, true, true);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            RaiseTrimmed(trimmed);
        }

        /// <summary>
        /// Async variant of <see cref="AppendChatMessage"/> that performs the file I/O on the thread pool
        /// and, when <paramref name="persistToDisk"/> is set, returns only after the write is durable.
        /// </summary>
        /// <exception cref="IOException">Запись не удалась или не подтверждена.</exception>
        public async Task AppendChatMessageAsync(string roleId, string role, string content,
            bool persistToDisk = true, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            // WHY: как AppendChatMessage: пустой role id не маршрутизируется ни в чей файл.
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            await AppendLineAsync(roleId, ChatLine(role, content), persistToDisk, cancellationToken);
        }

        /// <inheritdoc />
        /// <remarks>Best-effort по контракту <c>void</c>, как <see cref="AppendChatMessage"/>.</remarks>
        public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            AgentHistoryTrimmedEventArgs? trimmed = null;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    trimmed = AppendLineCore(roleId, TranscriptLine(entry), persistToDisk, true, true);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            RaiseTrimmed(trimmed);
        }

        /// <summary>
        /// Async variant of <see cref="AppendTranscriptEntry"/> that performs the file I/O on the thread
        /// pool and, when <paramref name="persistToDisk"/> is set, returns only after the write is durable.
        /// </summary>
        /// <exception cref="IOException">Запись не удалась или не подтверждена.</exception>
        public async Task AppendTranscriptEntryAsync(string roleId, ConversationEntry entry,
            bool persistToDisk = true, CancellationToken cancellationToken = default)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            await AppendLineAsync(roleId, TranscriptLine(entry), persistToDisk, cancellationToken);
        }

        private async Task AppendLineAsync(string roleId, HistoryLine line, bool persistToDisk,
            CancellationToken cancellationToken)
        {
            SynchronizationContext callbackContext = SynchronizationContext.Current;
            AgentHistoryTrimmedEventArgs? trimmed;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    trimmed = await RunOffThread(() => AppendLineCore(roleId, line, persistToDisk, false, false))
                        .ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await RaiseTrimmedAsync(trimmed, callbackContext);
            if (persistToDisk)
            {
                await ConfirmDurableAsync(cancellationToken);
            }
        }

        private static HistoryLine ChatLine(string role, string content)
        {
            // WHY: единица отметки времени — та же, что у конструктора ChatMessage и у InMemoryAgentMemoryStore
            // (секунды Unix); раньше файловый стор писал миллисекунды, и единица зависела от реализации.
            ChatMessage message = new(role ?? "", content);
            return new HistoryLine
            {
                Kind = (int)MapSpeakerKind(message.Role),
                Key = message.Role,
                Content = message.Content,
                Timestamp = message.Timestamp,
                IsChatMessage = true
            };
        }

        private static HistoryLine TranscriptLine(ConversationEntry entry)
        {
            return new HistoryLine
            {
                Kind = (int)entry.Kind,
                Key = entry.Key ?? "",
                Content = entry.Content ?? "",
                CallId = string.IsNullOrEmpty(entry.CallId) ? null : entry.CallId,
                Timestamp = entry.Timestamp,
                IsChatMessage = false
            };
        }

        /// <summary>
        /// Добавляет строку в память роли и, если она предназначена для диска, в файл. Возвращает описание
        /// усечения (если оно случилось), чтобы вызывающий поднял событие уже вне замков.
        /// </summary>
        /// <param name="swallowWriteFailure">
        /// Синхронный контракт: сбой записи — в лог и <see cref="RoleHistory.NeedsRewrite"/>. Async-контракт
        /// пробрасывает исключение.
        /// </param>
        private AgentHistoryTrimmedEventArgs? AppendLineCore(string roleId, HistoryLine line, bool persistToDisk,
            bool queueFlush, bool swallowWriteFailure)
        {
            RoleHistory previous = EnsureHistoryLoaded(roleId);
            // Trimming mutates line flags as well as counts. Work on a private candidate so a failed
            // confirmed append leaves both the previous cache and the durable view unchanged.
            RoleHistory history = CopyHistory(previous);
            long lastOrder = 0;
            foreach (HistoryLine retained in history.Lines)
            {
                lastOrder = Math.Max(lastOrder, retained.Order);
            }
            line.Order = HistoryOrders.AddOrUpdate(GetHistoryPath(roleId), lastOrder + 1,
                (_, current) => Math.Max(current, lastOrder) + 1);
            line.Persisted = persistToDisk;
            Track(history, line);

            AgentHistoryTrimmedEventArgs? trimmed = TrimRetained(history);
            // WHY: первое усечение роли — в лог сразу, не дожидаясь уплотнения файла: иначе первые
            // потерянные ходы (в том числе у роли, чья история живёт только в процессе) уходят молча.
            if (!persistToDisk)
            {
                if (trimmed.HasValue && !history.TrimLogged)
                {
                    LogTrim(history, trimmed.Value);
                }
                _histories[roleId] = history;
                return trimmed;
            }

            try
            {
                bool compactNow = !history.LoadFailed &&
                                  (history.NeedsRewrite ||
                                   history.FileLineCount - history.PersistedCount >= CompactionSlack);
                if (compactNow)
                {
                    RewriteHistoryFile(roleId, history);
                }
                else
                {
                    AppendHistoryLine(roleId, line);
                    history.FileLineCount++;
                }

                if (trimmed.HasValue && (compactNow || !history.TrimLogged))
                {
                    LogTrim(history, trimmed.Value);
                }

                foreach (HistoryLine retained in history.Lines)
                {
                    retained.PendingWrite = false;
                }
                history.Revision = AdvanceHistoryRevision(roleId);
                _histories[roleId] = history;

                if (queueFlush)
                {
                    QueueFlush();
                }
            }
            catch (Exception ex) when (swallowWriteFailure)
            {
                // WHY: строка уже в памяти и помечена как предназначенная для диска; следующая удачная
                // запись перепишет файл из памяти целиком и вернёт её на диск.
                history.NeedsRewrite = true;
                line.PendingWrite = true;
                _histories[roleId] = history;
                if (trimmed.HasValue && !history.TrimLogged)
                {
                    LogTrim(history, trimmed.Value);
                }
                LogStorageFailure("append conversation history", ex);
            }

            return trimmed;
        }

        private static RoleHistory CopyHistory(RoleHistory source)
        {
            RoleHistory copy = new()
            {
                ChatCount = source.ChatCount,
                TranscriptCount = source.TranscriptCount,
                PersistedCount = source.PersistedCount,
                FileLineCount = source.FileLineCount,
                NeedsRewrite = source.NeedsRewrite,
                LoadFailed = source.LoadFailed,
                TrimLogged = source.TrimLogged,
                Revision = source.Revision
            };
            foreach (HistoryLine line in source.Lines)
            {
                copy.Lines.Add(new HistoryLine
                {
                    Kind = line.Kind, Key = line.Key, Content = line.Content, CallId = line.CallId,
                    Timestamp = line.Timestamp, Order = line.Order, IsChatMessage = line.IsChatMessage,
                    Persisted = line.Persisted, PendingWrite = line.PendingWrite,
                    InChatView = line.InChatView, InTranscriptView = line.InTranscriptView
                });
            }
            return copy;
        }

        /// <summary>Ставит новую (или только что загруженную) строку в оба окна и обновляет счётчики.</summary>
        private static void Track(RoleHistory history, HistoryLine line)
        {
            line.InChatView = line.IsChatMessage;
            line.InTranscriptView = true;
            history.Lines.Add(line);
            history.TranscriptCount++;
            if (line.IsChatMessage)
            {
                history.ChatCount++;
            }

            if (line.Persisted)
            {
                history.PersistedCount++;
            }
        }

        /// <summary>
        /// Удерживает два окна над одним списком: последние <see cref="MaxChatHistoryMessages"/> сообщений
        /// чата и последние <see cref="MaxTranscriptEntries"/> строк транскрипта. Выпадение из окна — это то,
        /// что видит читатель, и именно оно считается усечением и попадает в событие; строка физически
        /// удаляется, только когда не нужна уже ни одному окну. Сообщение чата старше окна транскрипта живёт,
        /// пока не выпало из окна чата, — ровно как раньше жили две независимые копии.
        /// </summary>
        private AgentHistoryTrimmedEventArgs? TrimRetained(RoleHistory history)
        {
            List<HistoryLine> lines = history.Lines;
            int droppedChat = 0;
            for (int i = 0; i < lines.Count && history.ChatCount > _maxChatHistoryMessages; i++)
            {
                if (!lines[i].InChatView)
                {
                    continue;
                }

                lines[i].InChatView = false;
                history.ChatCount--;
                droppedChat++;
            }

            int droppedTranscript = 0;
            for (int i = 0; i < lines.Count && history.TranscriptCount > _maxTranscriptEntries; i++)
            {
                if (!lines[i].InTranscriptView)
                {
                    continue;
                }

                lines[i].InTranscriptView = false;
                history.TranscriptCount--;
                droppedTranscript++;
            }

            if (droppedChat == 0 && droppedTranscript == 0)
            {
                return null;
            }

            int write = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                HistoryLine line = lines[i];
                if (line.InChatView || line.InTranscriptView)
                {
                    lines[write++] = line;
                    continue;
                }

                if (line.Persisted)
                {
                    history.PersistedCount--;
                }
            }

            lines.RemoveRange(write, lines.Count - write);
            return new AgentHistoryTrimmedEventArgs(
                droppedChat,
                droppedTranscript,
                history.ChatCount,
                history.TranscriptCount,
                _maxChatHistoryMessages,
                _maxTranscriptEntries);
        }

        private void LogTrim(RoleHistory history, AgentHistoryTrimmedEventArgs trimmed)
        {
            history.TrimLogged = true;
            _log?.Warn(
                "[FileAgentMemoryStore] Conversation history of one role reached its cap: " +
                $"{trimmed.DroppedChatMessages} chat message(s) / {trimmed.DroppedTranscriptEntries} transcript " +
                $"row(s) dropped (caps: {_maxChatHistoryMessages} chat messages, {_maxTranscriptEntries} transcript " +
                "rows). Older turns are gone from this role's history; raise the caps on CoreAILifetimeScope " +
                "(SetConversationHistoryCaps) if the host must keep them.");
        }

        private void RaiseTrimmed(AgentHistoryTrimmedEventArgs? trimmed)
        {
            if (trimmed.HasValue)
            {
                HistoryTrimmed?.Invoke(trimmed.Value);
            }
        }

        private Task RaiseTrimmedAsync(AgentHistoryTrimmedEventArgs? trimmed, SynchronizationContext callbackContext)
        {
            // WHY: Disk IO and lock release must not wait for Unity's main thread: a synchronous
            // read there could be waiting for these locks. Only host callbacks return to that context,
            // after the locks are free. WebGL executes IO inline and retains its original context.
            if (!trimmed.HasValue || callbackContext == null ||
                ReferenceEquals(callbackContext, SynchronizationContext.Current))
            {
                RaiseTrimmed(trimmed);
                return Task.CompletedTask;
            }

            // WHY: no RunContinuationsAsynchronously — it forces the continuation onto the thread pool,
            // which does not exist in WebGL and hangs forever. Inlining is safe here: TrySetResult runs
            // inside the Post callback above, after both gates are already released (see the WHY above).
            TaskCompletionSource<bool> completion = new();
            callbackContext.Post(_ =>
            {
                try
                {
                    RaiseTrimmed(trimmed);
                    completion.TrySetResult(true);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }, null);
            return completion.Task;
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            _gate.Wait();
            try
            {
                RoleHistory history = EnsureHistoryLoaded(roleId);
                List<HistoryLine> lines = history.Lines;
                // WHY: строки вне окна транскрипта всегда старше строк внутри него (окно покидают с головы,
                // порядок не меняется), поэтому окно — это хвост списка длиной TranscriptCount.
                int take = maxEntries > 0 ? Math.Min(maxEntries, history.TranscriptCount) : history.TranscriptCount;
                if (take == 0)
                {
                    return Array.Empty<ConversationEntry>();
                }

                List<ConversationEntry> result = new(take);
                for (int i = lines.Count - take; i < lines.Count; i++)
                {
                    HistoryLine line = lines[i];
                    result.Add(new ConversationEntry
                    {
                        Kind = (ConversationEntryKind)line.Kind,
                        Key = line.Key ?? "",
                        Content = line.Content ?? "",
                        CallId = line.CallId ?? "",
                        Timestamp = line.Timestamp
                    });
                }

                return result;
            }
            finally
            {
                _gate.Release();
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Returns the latest chat messages for a role, optionally capped to the requested count.
        /// </summary>
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            _gate.Wait();
            try
            {
                RoleHistory history = EnsureHistoryLoaded(roleId);
                int take = maxMessages > 0 ? Math.Min(maxMessages, history.ChatCount) : history.ChatCount;
                if (take == 0)
                {
                    return Array.Empty<ChatMessage>();
                }

                ChatMessage[] result = new ChatMessage[take];
                int write = take - 1;
                for (int i = history.Lines.Count - 1; i >= 0 && write >= 0; i--)
                {
                    HistoryLine line = history.Lines[i];
                    if (!line.InChatView)
                    {
                        continue;
                    }

                    result[write--] = new ChatMessage
                    {
                        Role = line.Key,
                        Content = line.Content,
                        Timestamp = line.Timestamp
                    };
                }

                return result;
            }
            finally
            {
                _gate.Release();
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Загружает переписку роли при первом обращении: из <c>.history.jsonl</c>, а если его нет —
        /// мигрирует из полей документа памяти формата v1.
        /// </summary>
        private RoleHistory EnsureHistoryLoaded(string roleId)
        {
            long revision = HistoryRevisions.GetOrAdd(GetHistoryPath(roleId), 0);
            if (_histories.TryGetValue(roleId, out RoleHistory cached) && cached.Revision == revision)
            {
                return cached;
            }

            RoleHistory history = new() { Revision = revision, TrimLogged = cached?.TrimLogged ?? false };
            string historyPath = GetHistoryPath(roleId);
            if (File.Exists(historyPath))
            {
                LoadHistoryFile(history, historyPath);
            }
            else
            {
                MigrateLegacyConversation(roleId, history);
            }

            if (history.LoadFailed && cached != null)
            {
                // A peer changed the file but it is temporarily unreadable. Retain our recoverable
                // cache and retry on the next access; never overwrite an unknown disk snapshot.
                throw new IOException("The current conversation history could not be reloaded.");
            }

            long clearedAt = HistoryClearRevisions.GetOrAdd(GetHistoryPath(roleId), 0);
            if (cached != null && cached.Revision >= clearedAt)
            {
                foreach (HistoryLine local in cached.Lines)
                {
                    if (!local.Persisted || local.PendingWrite)
                    {
                        Track(history, local);
                        // Insert the local row at its original position relative to peer commits.
                        // Moving only this row preserves the order of legacy rows with order zero.
                        int index = history.Lines.Count - 1;
                        while (index > 0 && history.Lines[index - 1].Order > local.Order)
                        {
                            history.Lines[index] = history.Lines[index - 1];
                            index--;
                        }
                        history.Lines[index] = local;
                        history.NeedsRewrite |= local.PendingWrite;
                    }
                }
            }

            TrimRetained(history);
            _histories[roleId] = history;
            return history;
        }

        private void LoadHistoryFile(RoleHistory history, string path)
        {
            string[] rawLines;
            try
            {
                rawLines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                // WHY: содержимое файла неизвестно — переписывать его из памяти нельзя (см. LoadFailed).
                history.LoadFailed = true;
                LogStorageFailure("read conversation history", ex);
                return;
            }

            int unreadable = 0;
            foreach (string raw in rawLines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                history.FileLineCount++;
                HistoryLine line = null;
                try
                {
                    line = JsonConvert.DeserializeObject<HistoryLine>(raw, HistoryJson);
                }
                catch (JsonException)
                {
                    line = null;
                }

                if (line == null)
                {
                    unreadable++;
                    continue;
                }

                line.Persisted = true;
                Track(history, line);
            }

            if (unreadable > 0)
            {
                // WHY: оборванная строка — след прерванного syncfs или частичной записи. Она пропускается,
                // остальное читается, а файл переписывается начисто при следующей записи.
                history.NeedsRewrite = true;
                _log?.Warn(
                    $"[FileAgentMemoryStore] Skipped {unreadable} unreadable line(s) in one role's conversation " +
                    "history; the file will be rewritten on the next write.");
            }
        }

        /// <summary>
        /// Формат v1 хранил переписку в документе памяти дважды: плоский чат в <c>chatHistoryJson</c>
        /// (JsonUtility) и транскрипт в <c>transcriptEntriesJson</c> (Newtonsoft). Сливает их в один список
        /// строк: запись транскрипта, совпадающая со следующим сообщением чата по роли, тексту и времени, —
        /// это то же сообщение (одна строка с флагом чата); остальное — строки только транскрипта. Чат без
        /// транскрипта (самый старый формат) становится строками чата.
        /// </summary>
        private void MigrateLegacyConversation(string roleId, RoleHistory history)
        {
            string memoryPath = GetMemoryPath(roleId);
            if (!File.Exists(memoryPath))
            {
                return;
            }

            Persisted p;
            try
            {
                p = ReadPersisted(memoryPath);
            }
            catch (Exception ex)
            {
                // WHY: документ v1 не читается — переписка внутри него недоступна так же, как и память.
                // Документ не трогаем: его состояние (Failed) сообщает TryLoadDetailed.
                LogStorageFailure("migrate legacy conversation", ex);
                return;
            }

            if (string.IsNullOrEmpty(p.chatHistoryJson) && string.IsNullOrEmpty(p.transcriptEntriesJson))
            {
                return;
            }

            List<ChatMessage> chat = new();
            if (!string.IsNullOrEmpty(p.chatHistoryJson))
            {
                try
                {
                    ChatMessageArrayWrapper wrapper = JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson);
                    if (wrapper.Items != null)
                    {
                        chat.AddRange(wrapper.Items);
                    }
                }
                catch (Exception ex)
                {
                    LogStorageFailure("parse legacy chat history", ex);
                }
            }

            List<ConversationEntry> transcript = new();
            if (!string.IsNullOrEmpty(p.transcriptEntriesJson))
            {
                try
                {
                    List<ConversationEntry> loaded =
                        JsonConvert.DeserializeObject<List<ConversationEntry>>(p.transcriptEntriesJson);
                    if (loaded != null)
                    {
                        transcript.AddRange(loaded);
                    }
                }
                catch (Exception ex)
                {
                    LogStorageFailure("parse legacy transcript", ex);
                }
            }

            int chatCursor = 0;
            foreach (ConversationEntry entry in transcript)
            {
                if (entry == null)
                {
                    continue;
                }

                bool isChat = chatCursor < chat.Count && SameMessage(chat[chatCursor], entry);
                if (isChat)
                {
                    chatCursor++;
                }

                Track(history, new HistoryLine
                {
                    Kind = (int)entry.Kind,
                    Key = entry.Key ?? "",
                    Content = entry.Content ?? "",
                    CallId = string.IsNullOrEmpty(entry.CallId) ? null : entry.CallId,
                    Timestamp = NormalizeToUnixSeconds(entry.Timestamp),
                    IsChatMessage = isChat
                });
            }

            for (; chatCursor < chat.Count; chatCursor++)
            {
                ChatMessage message = chat[chatCursor];
                Track(history, new HistoryLine
                {
                    Kind = (int)MapSpeakerKind(message.Role),
                    Key = message.Role ?? "",
                    Content = message.Content ?? "",
                    Timestamp = NormalizeToUnixSeconds(message.Timestamp),
                    IsChatMessage = true
                });
            }

            TrimRetained(history);
            RewriteHistoryFile(roleId, history);

            // WHY: переписка теперь в своём файле; документ памяти переписывается без полей v1, чтобы
            // миграция не повторялась и чтобы Save не нёс их дальше.
            p.chatHistoryJson = null;
            p.transcriptEntriesJson = null;
            AtomicWriteAllText(memoryPath, JsonUtility.ToJson(p));
            QueueFlush();
            _log?.Info("[FileAgentMemoryStore] Migrated one role's conversation from the v1 memory document " +
                       "into its own history file.");
        }

        private static bool SameMessage(ChatMessage message, ConversationEntry entry)
        {
            return string.Equals(message.Role ?? "", entry.Key ?? "", StringComparison.Ordinal) &&
                   string.Equals(message.Content ?? "", entry.Content ?? "", StringComparison.Ordinal) &&
                   NormalizeToUnixSeconds(message.Timestamp) == NormalizeToUnixSeconds(entry.Timestamp);
        }

        /// <summary>
        /// Формат v1 писал миллисекунды в отметки чата. Секунды Unix не превысят 10^11 ещё три тысячи лет,
        /// миллисекунды перевалили за него в 1973-м — граница однозначна.
        /// </summary>
        private static long NormalizeToUnixSeconds(long timestamp)
        {
            return timestamp > 100_000_000_000L ? timestamp / 1000L : timestamp;
        }

        private static ConversationEntryKind MapSpeakerKind(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return ConversationEntryKind.Assistant;
            }

            return ConversationEntryKind.User;
        }

        [Serializable]
        private struct ChatMessageArrayWrapper
        {
            public ChatMessage[] Items;
        }

        private void AppendHistoryLine(string roleId, HistoryLine line)
        {
            EnsureDir();
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(line, HistoryJson) + "\n");
            using FileStream file = new(GetHistoryPath(roleId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            long originalLength = file.Length;
            try
            {
                file.Position = originalLength;
                file.Write(bytes, 0, bytes.Length);
                file.Flush();
            }
            catch
            {
                // A failed write may have appended a partial JSON record. Restore the last durable
                // boundary before allowing a retry, otherwise a later append could duplicate it.
                file.SetLength(originalLength);
                throw;
            }
        }

        /// <summary>Переписывает файл переписки из памяти: только строки, предназначенные для диска.</summary>
        private void RewriteHistoryFile(string roleId, RoleHistory history)
        {
            EnsureDir();
            StringBuilder sb = new(history.PersistedCount * 128);
            foreach (HistoryLine line in history.Lines)
            {
                if (!line.Persisted)
                {
                    continue;
                }

                sb.Append(JsonConvert.SerializeObject(line, HistoryJson)).Append('\n');
            }

            AtomicWriteAllText(GetHistoryPath(roleId), sb.ToString());
            history.FileLineCount = history.PersistedCount;
            history.NeedsRewrite = false;
        }

        #endregion

        #region Paths, files, logging

        private string GetMemoryPath(string roleId)
        {
            return EnsureWithinRoot(Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.json"));
        }

        private string GetHistoryPath(string roleId)
        {
            return EnsureWithinRoot(Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.history.jsonl"));
        }

        private SemaphoreSlim GetMutationGate(string roleId)
        {
            return MutationLocks.GetOrAdd(GetMemoryPath(roleId), _ => new SemaphoreSlim(1, 1));
        }

        private long AdvanceHistoryRevision(string roleId)
        {
            return HistoryRevisions.AddOrUpdate(GetHistoryPath(roleId), 1, (_, current) => current + 1);
        }

        /// <summary>
        /// Guards against path traversal: <see cref="Path.GetInvalidFileNameChars"/> does NOT strip
        /// <c>..</c>, so a role id like <c>..</c> or <c>../x</c> could resolve outside <see cref="_dir"/>
        /// and read/write/delete arbitrary files. Reject any combined path that escapes the store root.
        /// </summary>
        private string EnsureWithinRoot(string combinedPath)
        {
            string rootFull = Path.GetFullPath(_dir);
            string rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(combinedPath);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "[FileAgentMemoryStore] Role id resolves outside the store root; rejected to prevent path traversal.");
            }

            return fullPath;
        }

        /// <summary>
        /// Releases the internal file-access semaphore. Отложенных записей у стора нет: всё, что
        /// предназначалось диску, уже на нём (на WebGL — как минимум в очереди на флаш).
        /// </summary>
        public void Dispose()
        {
            _gate.Dispose();
        }

        /// <summary>
        /// Maps a raw role id to a unique file stem. Invalid filename characters are replaced, and
        /// when the replacement changed anything a short hash of the raw id is appended so distinct
        /// ids like "A/B" and "A_B" cannot collide on the same file.
        /// </summary>
        private static string SanitizedFileStem(string roleId)
        {
            string safe = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, roleId, StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char c in roleId)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return $"{safe}_{hash:x8}";
        }

        private void EnsureDir()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }
        }

        /// <summary>
        /// Serializes the memory version audit trail to a JSON string for storage in the
        /// JsonUtility-backed <see cref="Persisted"/> DTO. Returns "" when there are no versions so the
        /// persisted file stays compact.
        /// </summary>
        private static string SerializeVersions(AgentMemoryVersionSnapshot[] versions)
        {
            if (versions == null || versions.Length == 0)
            {
                return "";
            }

            return JsonConvert.SerializeObject(versions, CompactJson);
        }

        /// <summary>
        /// Restores the memory version audit trail from its persisted JSON string. Нечитаемый след версий
        /// не ломает чтение документа (текст памяти важнее), но и не молчит: он попадает в лог, потому что
        /// следующая запись документа его уже не сохранит.
        /// </summary>
        private AgentMemoryVersionSnapshot[] DeserializeVersions(string versionsJson)
        {
            if (string.IsNullOrWhiteSpace(versionsJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<AgentMemoryVersionSnapshot[]>(versionsJson);
            }
            catch (Exception ex)
            {
                LogStorageFailure("parse memory versions", ex, true);
                return null;
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically by writing to a temp
        /// file first and then swapping it into place, so a crash mid-write cannot corrupt the existing file.
        /// </summary>
        private static void AtomicWriteAllText(string path, string contents)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, contents, Encoding.UTF8);

            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            catch
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    {
                        /* best-effort cleanup */
                    }
                }

                throw;
            }
        }

        /// <summary>Ставит WebGL-флаш в очередь; провал постановки — в лог, ждать здесь нельзя.</summary>
        private void QueueFlush()
        {
            if (!CoreAiWebGlPersistence.Sync())
            {
                _log?.Warn("[FileAgentMemoryStore] Durability flush could not be queued; the last write may " +
                           "not survive a reload.");
            }
        }

        private void LogStorageFailure(string operation, Exception ex, bool warning = false)
        {
            // WHY: roleId may be a scoped persistence key and exception messages commonly repeat the
            // filesystem path. Report only the operation and exception kind so logs disclose neither.
            string message = $"[FileAgentMemoryStore] Failed to {operation} ({ex.GetType().Name}).";
            if (warning)
            {
                _log?.Warn(message);
            }
            else
            {
                _log?.Error(message);
            }
        }

        #endregion
    }
}
