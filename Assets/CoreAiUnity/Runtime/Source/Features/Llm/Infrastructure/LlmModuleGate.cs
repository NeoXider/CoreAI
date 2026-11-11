using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Decides what a configured LLM mode needs from the build: whether it requires the <c>COREAI_LLM</c> module,
    /// the text that names the fix when the module is missing, and the warning the composition logs. Every decision
    /// takes the module state as a parameter, so one test build covers the define-on and the define-off branch; the
    /// pipeline and the editor build check pass <see cref="IsLlmModuleCompiled"/> (or the target's defines).
    /// </summary>
    internal static class LlmModuleGate
    {
        /// <summary>The scripting define that compiles the HTTP and local-model clients in.</summary>
        internal const string LlmDefine = "COREAI_LLM";

        /// <summary>Whether this assembly was compiled with <see cref="LlmDefine"/>.</summary>
        internal static bool IsLlmModuleCompiled
        {
            get
            {
#if COREAI_LLM
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Whether <paramref name="settings"/> select a backend that only exists with <see cref="LlmDefine"/>: every
        /// HTTP mode and the local model, and <see cref="LlmExecutionMode.Auto"/> once a model is configured (with the
        /// module, Auto would then call that model instead of serving the offline stub). Offline never needs it.
        /// </summary>
        internal static bool RequiresLlmModule(CoreAISettingsAsset settings)
        {
            if (settings == null)
            {
                return false;
            }

            switch (settings.ExecutionMode)
            {
                case LlmExecutionMode.Offline:
                    return false;
                case LlmExecutionMode.Auto:
                    return !string.IsNullOrWhiteSpace(settings.ModelName);
                default:
                    return true;
            }
        }

        /// <summary>The failure text of a request whose backend is not compiled in; it names both fixes.</summary>
        internal static string DescribeMissingModule(LlmExecutionMode mode)
        {
            return $"CoreAI cannot run the '{mode}' LLM mode: this build has no {LlmDefine} scripting define, so " +
                   "no LLM client is compiled in. Add the define for every build target (CoreAI > Setup > Modules > " +
                   "LLM Providers > Enable Providers, or Project Settings > Player > Scripting Define Symbols), or " +
                   "set LLM Mode to Offline in CoreAISettings to use the offline stub on purpose.";
        }

        /// <summary>
        /// The composition warning for <paramref name="settings"/> when the module is missing, or <c>null</c> when
        /// nothing is wrong (the module is compiled, or Offline was chosen on purpose).
        /// </summary>
        internal static string DescribeCompositionWarning(CoreAISettingsAsset settings, bool moduleCompiled)
        {
            if (moduleCompiled || settings == null || settings.ExecutionMode == LlmExecutionMode.Offline)
            {
                return null;
            }

            if (RequiresLlmModule(settings))
            {
                return "[CoreAI.Llm] " + DescribeMissingModule(settings.ExecutionMode) +
                       " Every request fails with LlmErrorCode.NotConfigured until then.";
            }

            return $"[CoreAI.Llm] LLM Mode '{settings.ExecutionMode}' without the {LlmDefine} scripting define " +
                   "serves the offline stub: no model is configured, and no LLM client is compiled in. Add the " +
                   "define and a model to reach a real backend, or set LLM Mode to Offline to silence this warning.";
        }

        /// <summary>
        /// Logs <see cref="DescribeCompositionWarning"/> once through <paramref name="logger"/>; returns whether a
        /// warning was written.
        /// </summary>
        internal static bool WarnWhenLlmModuleMissing(
            CoreAISettingsAsset settings,
            bool moduleCompiled,
            IGameLogger logger)
        {
            string warning = DescribeCompositionWarning(settings, moduleCompiled);
            if (warning == null)
            {
                return false;
            }

            (logger ?? GameLoggerUnscopedFallback.Instance).LogWarning(GameLogFeature.Llm, warning);
            return true;
        }
    }

    /// <summary>
    /// The client a build gets for a backend whose module is not compiled in. Every request fails with
    /// <see cref="LlmErrorCode.NotConfigured"/> and a message that names the fix, instead of the old silent
    /// <see cref="StubLlmClient"/> whose canned <c>Ok = true</c> replies reached players as NPC lines.
    /// </summary>
    internal sealed class NotConfiguredLlmClient : ILlmClient
    {
        internal NotConfiguredLlmClient(LlmExecutionMode mode)
        {
            Mode = mode;
            Message = LlmModuleGate.DescribeMissingModule(mode);
        }

        /// <summary>The configured mode that cannot run.</summary>
        internal LlmExecutionMode Mode { get; }

        /// <summary>The failure text every request returns.</summary>
        internal string Message { get; }

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmCompletionResult
            {
                Ok = false,
                Error = Message,
                ErrorCode = LlmErrorCode.NotConfigured
            });
        }
    }
}
