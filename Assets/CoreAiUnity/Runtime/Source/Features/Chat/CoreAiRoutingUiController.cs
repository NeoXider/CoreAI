using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Chat
{
    /// <summary>Persistence and activation outcome of an endpoint save operation.</summary>
    public enum CoreAiRoutingUiSaveStatus
    {
        NotSaved = 0,
        SavedAndReady = 1,
        SavedActivationFailed = 2
    }

    /// <summary>Result returned by an endpoint-management UI operation.</summary>
    public readonly struct CoreAiRoutingUiResult
    {
        public CoreAiRoutingUiResult(
            bool ok,
            string message = "",
            LlmEndpointSnapshot endpoint = null,
            CoreAiRoutingUiSaveStatus? saveStatus = null)
        {
            Ok = ok;
            Message = message ?? "";
            Endpoint = endpoint;
            SaveStatus = saveStatus ?? (ok
                ? CoreAiRoutingUiSaveStatus.SavedAndReady
                : CoreAiRoutingUiSaveStatus.NotSaved);
        }

        public bool Ok { get; }
        public string Message { get; }
        public LlmEndpointSnapshot Endpoint { get; }
        public CoreAiRoutingUiSaveStatus SaveStatus { get; }
    }

    /// <summary>
    /// Optional adapter between the built-in Hub/Chat UI and a runtime endpoint registry.
    /// Session API keys are write-only and must never be returned by this interface.
    /// </summary>
    public interface ICoreAiRoutingUiController
    {
        event Action Changed;

        IReadOnlyList<LlmEndpointSnapshot> GetEndpoints();
        IReadOnlyList<LlmRuntimeProfile> GetProfiles();
        string GetProfileForRole(string roleId);
        CoreAiRoutingUiResult AssignProfileToRole(string roleId, string profileId);

        Task<CoreAiRoutingUiResult> SaveEndpointAsync(
            LlmEndpointDescriptor endpoint,
            string sessionApiKey,
            CancellationToken cancellationToken = default);

        Task<CoreAiRoutingUiResult> RemoveEndpointAsync(
            string endpointId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Default UI adapter over the portable runtime endpoint registry.</summary>
    public sealed class LlmEndpointRegistryUiController : ICoreAiRoutingUiController
    {
        private readonly ILlmEndpointRegistry _registry;

        public LlmEndpointRegistryUiController(ILlmEndpointRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public event Action Changed
        {
            add => _registry.Changed += value;
            remove => _registry.Changed -= value;
        }

        public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
        {
            return _registry.GetEndpoints();
        }

        public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
        {
            return _registry.GetProfiles();
        }

        public string GetProfileForRole(string roleId)
        {
            return _registry.GetRoleProfile(roleId);
        }

        public CoreAiRoutingUiResult AssignProfileToRole(string roleId, string profileId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(profileId))
                {
                    _registry.ClearRoleProfile(roleId);
                }
                else
                {
                    _registry.AssignRoleProfile(roleId, profileId);
                }

                return new CoreAiRoutingUiResult(true);
            }
            catch (Exception ex)
            {
                return new CoreAiRoutingUiResult(false, ex.Message);
            }
        }

        public async Task<CoreAiRoutingUiResult> SaveEndpointAsync(
            LlmEndpointDescriptor endpoint,
            string sessionApiKey,
            CancellationToken cancellationToken = default)
        {
            try
            {
                LlmEndpointSnapshot snapshot = await _registry.AddOrUpdateEndpointAsync(
                    endpoint, sessionApiKey, cancellationToken);
                bool ready = snapshot == null ||
                             snapshot.State == LlmEndpointLifecycleState.Ready ||
                             snapshot.State == LlmEndpointLifecycleState.Inactive;
                string message = ready
                    ? snapshot?.State == LlmEndpointLifecycleState.Inactive
                        ? "Saved; activation is inactive."
                        : "Saved and ready."
                    : string.IsNullOrWhiteSpace(snapshot.Error)
                        ? "Saved; activation failed with state " + snapshot.State + "."
                        : "Saved; activation failed: " + snapshot.Error;
                return new CoreAiRoutingUiResult(
                    true,
                    message,
                    snapshot,
                    ready
                        ? CoreAiRoutingUiSaveStatus.SavedAndReady
                        : CoreAiRoutingUiSaveStatus.SavedActivationFailed);
            }
            catch (Exception ex)
            {
                return new CoreAiRoutingUiResult(
                    false,
                    ex.Message,
                    null,
                    CoreAiRoutingUiSaveStatus.NotSaved);
            }
        }

        public async Task<CoreAiRoutingUiResult> RemoveEndpointAsync(
            string endpointId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                bool removed = await _registry.RemoveEndpointAsync(
                    endpointId,
                    LlmEndpointRemovalMode.Drain,
                    cancellationToken: cancellationToken);
                return new CoreAiRoutingUiResult(removed, removed ? "" : "Endpoint was not removed.");
            }
            catch (Exception ex)
            {
                return new CoreAiRoutingUiResult(false, ex.Message);
            }
        }
    }

    /// <summary>Process-local attachment point for the optional runtime routing UI adapter.</summary>
    public static class CoreAiRoutingUi
    {
        private static ICoreAiRoutingUiController _controller;

        public static event Action ControllerChanged;

        public static ICoreAiRoutingUiController Controller
        {
            get => _controller;
            set
            {
                if (ReferenceEquals(_controller, value))
                {
                    return;
                }

                _controller = value;
                ControllerChanged?.Invoke();
            }
        }
    }

    /// <summary>Scope-owned attachment that prevents a disposed registry from remaining in the static UI facade.</summary>
    internal sealed class CoreAiRoutingUiAttachment : IDisposable
    {
        private readonly ICoreAiRoutingUiController _controller;

        public CoreAiRoutingUiAttachment(ILlmEndpointRegistry registry)
        {
            _controller = new LlmEndpointRegistryUiController(registry);
            CoreAiRoutingUi.Controller = _controller;
        }

        public void Dispose()
        {
            if (ReferenceEquals(CoreAiRoutingUi.Controller, _controller))
            {
                CoreAiRoutingUi.Controller = null;
            }
        }
    }
}
