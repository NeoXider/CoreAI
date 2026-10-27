using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.CodeEditing;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;

namespace CoreAI.Ai.Hub.CodeEditing
{
    /// <summary>Runs explicit bounded Lua saves through the existing Hub transaction and receipt path.</summary>
    public sealed class LuaCodePlaygroundRuntime : ICodePlaygroundRuntime
    {
        private readonly IHubModService _service;
        private readonly ModReloadMode _mode;
        private readonly object _gate = new();
        private CancellationTokenSource _activeCancellation;
        private string _activeRunId;
        private bool _disposed;
        private CodeRuntimeStatus _status;

        public LuaCodePlaygroundRuntime(IHubModService service,
            ModReloadMode mode = ModReloadMode.CleanStartupObjects)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _mode = mode;
        }

        public string LanguageId => "lua";
        public CodeRuntimeCapabilities Capabilities => _service is IBudgetedHubModService
            ? CodeRuntimeCapabilities.SaveAndRun : CodeRuntimeCapabilities.None;
        public CodeRuntimeStatus Status { get { lock (_gate) return _status; } }
        public event Action<CodeRuntimeStatus> StatusChanged;
        public event Action<CodeRuntimeMessage> MessageReceived;

        public void Initialize()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LuaCodePlaygroundRuntime));
                if (_status != CodeRuntimeStatus.Uninitialized) return;
                _status = CodeRuntimeStatus.Ready;
            }
            PublishStatus(CodeRuntimeStatus.Ready);
        }

        /// <summary>Returns the result of <see cref="Run"/> in an already completed task for interpreter compatibility.</summary>
        public Task<CodeRunResult> RunAsync(CodeRunRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Run(request, cancellationToken));

        /// <summary>
        /// Completes the bounded initial Lua execution and save on the caller thread. Unity and WebGL
        /// hosts can call this entry point directly without waiting on an asynchronous continuation.
        /// </summary>
        public CodeRunResult Run(CodeRunRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!string.Equals(request.LanguageId, LanguageId, StringComparison.Ordinal))
                return Complete(new CodeRunResult(request, CodeRunStatus.Unsupported,
                    "This adapter supports Lua only. No code was run or saved."));
            if (request.Operation != CodeRunOperation.SaveAndRun)
                return Complete(new CodeRunResult(request, CodeRunStatus.Unsupported,
                    "This mod adapter supports Save & run. A run without persistence requires a different host adapter."));
            if (!(_service is IBudgetedHubModService budgeted))
                return Complete(new CodeRunResult(request, CodeRunStatus.Unsupported,
                    "The configured service does not support bounded editor execution. No code was run or saved."));
            if (request.ExpectedSource == null)
                return Complete(new CodeRunResult(request, CodeRunStatus.Conflict,
                    "An exact source expectation is required before saving. Your draft is preserved."));
            if (cancellationToken.IsCancellationRequested)
                return Complete(new CodeRunResult(request, CodeRunStatus.Cancelled, "The run was cancelled before execution."));

            CancellationTokenSource runCancellation;
            lock (_gate)
            {
                if (_disposed)
                    return Complete(new CodeRunResult(request, CodeRunStatus.Failed, "The editor runtime is disposed."));
                if (_activeCancellation != null)
                    return Complete(new CodeRunResult(request, CodeRunStatus.Failed, "Another editor run is still active."));
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeCancellation = runCancellation;
                _activeRunId = request.RunId;
                _status = CodeRuntimeStatus.Running;
            }
            PublishStatus(CodeRuntimeStatus.Running);

            CodeRunResult result;
            try
            {
                CancellationToken token = runCancellation.Token;
                token.ThrowIfCancellationRequested();
                using IDisposable invocation = budgeted.BeginInvocation(
                    new LuaModInvocationLimits(request.TimeoutMs, request.MaxSteps), token);
                bool exists = _service.TryGetSource(request.ModId, out string currentSource);
                if (exists != request.ExpectedSource.Exists || exists &&
                    !string.Equals(currentSource, request.ExpectedSource.Source, StringComparison.Ordinal))
                {
                    result = new CodeRunResult(request, CodeRunStatus.Conflict,
                        "The source changed outside this editor. Your draft is preserved.");
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                    HubModSaveResult receipt = _service.SaveOrReload(request.ModId, request.Code, _mode);
                    result = FromReceipt(request, receipt);
                }
            }
            catch (OperationCanceledException)
            {
                result = new CodeRunResult(request, CodeRunStatus.Cancelled,
                    "The run was cancelled. Your draft is preserved; live state may already have changed.");
            }
            catch (NotSupportedException exception)
            {
                result = new CodeRunResult(request, CodeRunStatus.Unsupported, exception.Message);
            }
            catch (Exception exception)
            {
                CodeRunStatus status = runCancellation.IsCancellationRequested ? CodeRunStatus.Cancelled :
                    IsBudgetTrip(exception) ? CodeRunStatus.BudgetExceeded : CodeRunStatus.Failed;
                result = new CodeRunResult(request, status, exception.Message,
                    diagnostics: new[] { new CodeErrorLocation(null, null, exception.Message) });
            }
            finally
            {
                CodeRuntimeStatus status;
                lock (_gate)
                {
                    _activeCancellation = null;
                    _activeRunId = null;
                    _status = _disposed ? CodeRuntimeStatus.Disposed : CodeRuntimeStatus.Ready;
                    status = _status;
                }
                runCancellation.Dispose();
                PublishStatus(status);
            }
            return Complete(result);
        }

        public bool Stop(string runId)
        {
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_activeCancellation == null || !string.Equals(runId, _activeRunId, StringComparison.Ordinal)) return false;
                cancellation = _activeCancellation;
            }
            Cancel(cancellation);
            return true;
        }

        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _status = CodeRuntimeStatus.Disposed;
                cancellation = _activeCancellation;
            }
            Cancel(cancellation);
            PublishStatus(CodeRuntimeStatus.Disposed);
            StatusChanged = null;
            MessageReceived = null;
        }

        private static void Cancel(CancellationTokenSource cancellation)
        {
            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        private static CodeRunResult FromReceipt(CodeRunRequest request, HubModSaveResult receipt)
        {
            if (receipt == null)
                return new CodeRunResult(request, CodeRunStatus.Unconfirmed, "The service returned no save receipt. Your draft is preserved.");
            bool saved = receipt.PersistenceStatus == HubModPersistenceStatus.Saved;
            CodeRunStatus status = saved || receipt.PersistenceStatus == HubModPersistenceStatus.NotConfigured
                ? CodeRunStatus.Success : receipt.PersistenceStatus == HubModPersistenceStatus.Unconfirmed
                    ? CodeRunStatus.Unconfirmed : CodeRunStatus.Failed;
            return new CodeRunResult(request, status, receipt.Describe(), true, saved);
        }

        private static bool IsBudgetTrip(Exception exception)
        {
            for (Exception current = exception; current != null; current = ScriptExecutionErrors.NextCause(current))
                if (current is TimeoutException || current is LuaStepBudgetException || current is IScriptMemoryBudgetTrip ||
                    current is RbxError rbx && rbx.Code == RbxErrorCode.BudgetExceeded)
                    return true;
            return false;
        }

        private CodeRunResult Complete(CodeRunResult result)
        {
            Action<CodeRuntimeMessage> subscribers = MessageReceived;
            if (subscribers != null)
                foreach (Action<CodeRuntimeMessage> subscriber in subscribers.GetInvocationList())
                    try { subscriber(new CodeRuntimeMessage(result)); }
                    catch (Exception) { }
            return result;
        }

        private void PublishStatus(CodeRuntimeStatus status)
        {
            Action<CodeRuntimeStatus> subscribers = StatusChanged;
            if (subscribers == null) return;
            foreach (Action<CodeRuntimeStatus> subscriber in subscribers.GetInvocationList())
                try { subscriber(status); }
                catch (Exception) { }
        }
    }
}
