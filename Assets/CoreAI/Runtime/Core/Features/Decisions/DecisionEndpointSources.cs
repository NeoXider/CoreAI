using System;
using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Supplies the endpoint descriptors decision routes may name.</summary>
    public interface IDecisionEndpointSource
    {
        /// <summary>Current endpoints; an earlier entry wins when two share an id.</summary>
        IReadOnlyList<LlmEndpointDescriptor> GetEndpoints();
    }

    /// <summary>
    /// A fixed, host-owned endpoint list. Use it for decision-only servers such as a <c>llama-server</c> sidecar:
    /// registered in the chat registry they would be activated and probed as chat endpoints.
    /// </summary>
    public sealed class StaticDecisionEndpointSource : IDecisionEndpointSource
    {
        private readonly object _gate = new();
        private readonly List<LlmEndpointDescriptor> _endpoints;

        /// <summary>Creates a source over <paramref name="endpoints"/> (copied).</summary>
        public StaticDecisionEndpointSource(IEnumerable<LlmEndpointDescriptor> endpoints = null)
        {
            _endpoints = new List<LlmEndpointDescriptor>();
            foreach (LlmEndpointDescriptor endpoint in endpoints ?? Array.Empty<LlmEndpointDescriptor>())
            {
                if (endpoint != null)
                {
                    _endpoints.Add(endpoint);
                }
            }
        }

        /// <summary>Adds an endpoint or replaces the one with the same id.</summary>
        public void AddOrReplace(LlmEndpointDescriptor endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            lock (_gate)
            {
                int index = _endpoints.FindIndex(e =>
                    string.Equals(e.EndpointId?.Trim(), endpoint.EndpointId?.Trim(), StringComparison.Ordinal));
                if (index >= 0)
                {
                    _endpoints[index] = endpoint;
                }
                else
                {
                    _endpoints.Add(endpoint);
                }
            }
        }

        /// <summary>Removes the endpoint with <paramref name="endpointId"/>; false when unknown.</summary>
        public bool Remove(string endpointId)
        {
            lock (_gate)
            {
                return _endpoints.RemoveAll(e =>
                    string.Equals(e.EndpointId?.Trim(), endpointId?.Trim(), StringComparison.Ordinal)) > 0;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<LlmEndpointDescriptor> GetEndpoints()
        {
            lock (_gate)
            {
                return _endpoints.ToArray();
            }
        }
    }

    /// <summary>
    /// Endpoints of the runtime <see cref="ILlmEndpointRegistry"/> (the Hub's endpoint list), after an optional
    /// host list that wins on id clashes. An endpoint used for chat and decisions (an OpenRouter key, a chat model
    /// asked through logprobs) only needs its <see cref="LlmEndpointDescriptor.DecisionProtocol"/> set.
    /// </summary>
    public sealed class RegistryDecisionEndpointSource : IDecisionEndpointSource
    {
        private readonly ILlmEndpointRegistry _registry;
        private readonly IDecisionEndpointSource _first;

        /// <summary>Creates a source over <paramref name="registry"/>, preceded by <paramref name="first"/>.</summary>
        public RegistryDecisionEndpointSource(ILlmEndpointRegistry registry, IDecisionEndpointSource first = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _first = first;
        }

        /// <inheritdoc />
        public IReadOnlyList<LlmEndpointDescriptor> GetEndpoints()
        {
            List<LlmEndpointDescriptor> endpoints = new();
            if (_first != null)
            {
                endpoints.AddRange(_first.GetEndpoints());
            }

            foreach (LlmEndpointSnapshot snapshot in _registry.GetEndpoints() ?? Array.Empty<LlmEndpointSnapshot>())
            {
                if (snapshot?.Descriptor != null && snapshot.State != LlmEndpointLifecycleState.Removed)
                {
                    endpoints.Add(snapshot.Descriptor);
                }
            }

            return endpoints;
        }
    }
}
