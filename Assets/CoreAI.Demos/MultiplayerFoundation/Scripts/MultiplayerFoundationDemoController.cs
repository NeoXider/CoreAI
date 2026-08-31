using UnityEngine;
#if COREAI_LUA
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// Scene driver for the MVP2 multiplayer-foundation proof. It resolves the production core/mod scopes,
    /// runs the shared scenario, and registers its UI Toolkit page in the existing CoreAI Hub.
    /// </summary>
    [RequireComponent(typeof(CoreAI.Hub.UI.CoreAiHubWindow))]
    public sealed class MultiplayerFoundationDemoController : MonoBehaviour
    {
#if COREAI_LUA
        [SerializeField]
        [Range(MultiplayerFoundationDemoScenario.MinimumActorCount,
            MultiplayerFoundationDemoScenario.MaximumActorCount)]
        private int actorCount = 4;

        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [SerializeField]
        private CoreAiModsLifetimeScope modsScope;

        private IInGameLlmChatServiceFactory _chatFactory;
        private ActorContext _hostActor;
        private ILuaModRuntime _mods;
        private MultiplayerFoundationDemoScenario _scenario;
        private MultiplayerFoundationProofReport _report;
        private int _presentationRevision;
        private string _status = "Waiting for the production scopes...";

        /// <summary>Requested actor count, clamped to the supported range.</summary>
        public int ActorCount => actorCount;

        /// <summary>Monotonic version used by the polling Hub page to avoid needless tree rebuilds.</summary>
        public int PresentationRevision => _presentationRevision;

        /// <summary>Current scenario result, or null until initialization finishes.</summary>
        public MultiplayerFoundationProofReport Report => _report;

        /// <summary>Short setup/runtime status shown at the top of the page.</summary>
        public string Status => _status;

        private void Start()
        {
            try
            {
                RegisterPage();
                ResolveProductionServices();
                RunProof();
            }
            catch (Exception ex)
            {
                _status = "Demo setup failed: " + ex.Message;
                _presentationRevision++;
                Debug.LogError($"[MultiplayerFoundationDemo] {ex}");
            }
        }

        private void OnDestroy()
        {
            _scenario?.Dispose();
            _scenario = null;
        }

        /// <summary>Changes the simulated actor count and re-runs the complete proof.</summary>
        public void SetActorCountAndRerun(int requestedCount)
        {
            actorCount = Mathf.Clamp(
                requestedCount,
                MultiplayerFoundationDemoScenario.MinimumActorCount,
                MultiplayerFoundationDemoScenario.MaximumActorCount);
            RunProof();
        }

        /// <summary>Re-runs the complete proof using the current actor count.</summary>
        public void RerunProof()
        {
            RunProof();
        }

        /// <summary>Sends one optional real-provider message through a selected actor's private chat.</summary>
        public async Task<LlmCompletionResult> SendChatAsync(
            int actorIndex,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (_scenario == null)
            {
                return new LlmCompletionResult { Ok = false, Error = "Demo scenario is not ready." };
            }

            LlmCompletionResult result =
                await _scenario.SendChatAsync(actorIndex, message, cancellationToken);
            _presentationRevision++;
            return result;
        }

        private void ResolveProductionServices()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            }

            if (modsScope == null)
            {
                modsScope = FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
            }

            if (coreAiScope?.Container == null)
            {
                throw new InvalidOperationException(
                    "CoreAILifetimeScope is missing or its production container is not built.");
            }

            if (modsScope?.Container == null)
            {
                throw new InvalidOperationException(
                    "CoreAiModsLifetimeScope is missing or its production child container is not built.");
            }

            IObjectResolver coreContainer = coreAiScope.Container;
            IObjectResolver modsContainer = modsScope.Container;
            _mods = modsContainer.Resolve<ILuaModRuntime>();
            _chatFactory = coreContainer.Resolve<IInGameLlmChatServiceFactory>();
            IActorIdentityProvider identityProvider = coreContainer.Resolve<IActorIdentityProvider>();
            _hostActor = identityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private void RunProof()
        {
            if (_mods == null || _chatFactory == null)
            {
                _status = "Production services are not ready.";
                _presentationRevision++;
                return;
            }

            _scenario?.Dispose();
            _scenario = null;
            _report = null;
            _status = $"Running {actorCount}-actor production-path proof...";
            _presentationRevision++;

            try
            {
                _scenario = new MultiplayerFoundationDemoScenario(
                    _mods,
                    _chatFactory,
                    _hostActor,
                    actorCount);
                _report = _scenario.Run();
                _status = _report.Passed
                    ? $"PASS: {_report.EnforcedProofCount}/{_report.Proofs.Count} isolation checks refused exactly as designed."
                    : $"ATTENTION: {_report.EnforcedProofCount}/{_report.Proofs.Count} checks held; inspect the cards below.";
            }
            catch (Exception ex)
            {
                _status = "Demo setup failed: " + ex.Message;
                Debug.LogError($"[MultiplayerFoundationDemo] {ex}");
            }

            _presentationRevision++;
        }

        private void RegisterPage()
        {
            CoreAiHubWindow window = GetComponent<CoreAiHubWindow>();
            HubPageRegistry registry = window != null ? window.Registry : null;
            if (registry == null)
            {
                throw new InvalidOperationException(
                    "CoreAiHubWindow has no registry. Keep CoreAiHubDemo on the Hub prefab.");
            }

            registry.Register(
                MultiplayerFoundationHubPage.DefaultPageId,
                () => new MultiplayerFoundationHubPage(() => this),
                -100);
            window.ActivatePage(MultiplayerFoundationHubPage.DefaultPageId);
        }
#else
        private void Start()
        {
            Debug.LogWarning(
                "[MultiplayerFoundationDemo] COREAI_LUA is not set; the MVP2 proof is inactive.");
            enabled = false;
        }
#endif
    }
}
