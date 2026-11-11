using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Newtonsoft.Json;

namespace CoreAI.Decisions
{
    /// <summary>Hands each consumer the decision client it should talk to.</summary>
    public interface IDecisionClientProvider
    {
        /// <summary>
        /// The client for <paramref name="consumerId"/>: routed per call (so endpoint and route changes apply at
        /// once) and wrapped in the consumer's own budget. The same instance is returned for the same id.
        /// </summary>
        IDecisionClient ForConsumer(string consumerId);
    }

    /// <summary>
    /// Default provider: <see cref="BudgetedDecisionClient"/> over a routed client per consumer. Backend clients
    /// are built by the <see cref="IDecisionClientFactory"/> and reused while the endpoint descriptor is unchanged.
    /// </summary>
    public sealed class DecisionClientProvider : IDecisionClientProvider
    {
        private readonly object _gate = new();
        private readonly IDecisionRouter _router;
        private readonly IDecisionClientFactory _factory;
        private readonly IDecisionClock _clock;
        private readonly Dictionary<string, IDecisionClient> _consumers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, IDecisionClient>> _backends = new(StringComparer.Ordinal);

        /// <summary>Creates a provider.</summary>
        public DecisionClientProvider(IDecisionRouter router, IDecisionClientFactory factory, IDecisionClock clock)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <inheritdoc />
        public IDecisionClient ForConsumer(string consumerId)
        {
            string consumer = string.IsNullOrWhiteSpace(consumerId) ? DecisionRouter.DefaultConsumerId : consumerId.Trim();
            lock (_gate)
            {
                if (!_consumers.TryGetValue(consumer, out IDecisionClient client))
                {
                    client = new BudgetedDecisionClient(new RoutedDecisionClient(this, consumer),
                        () => _router.Resolve(consumer).Profile?.Budget, _clock);
                    _consumers[consumer] = client;
                }

                return client;
            }
        }

        private IDecisionClient BackendFor(LlmEndpointDescriptor endpoint)
        {
            string id = endpoint.EndpointId?.Trim() ?? "";
            string fingerprint = JsonConvert.SerializeObject(endpoint);
            lock (_gate)
            {
                if (_backends.TryGetValue(id, out KeyValuePair<string, IDecisionClient> cached) &&
                    string.Equals(cached.Key, fingerprint, StringComparison.Ordinal))
                {
                    return cached.Value;
                }
            }

            IDecisionClient created = _factory.Create(endpoint) ??
                                      new UnavailableDecisionClient(LlmErrorCode.NotConfigured,
                                          DecisionErrorReasons.NotConfigured,
                                          $"No decision backend could be built for endpoint '{id}'.");
            lock (_gate)
            {
                _backends[id] = new KeyValuePair<string, IDecisionClient>(fingerprint, created);
            }

            return created;
        }

        private sealed class RoutedDecisionClient : IDecisionClient
        {
            private readonly DecisionClientProvider _owner;
            private readonly string _consumer;

            public RoutedDecisionClient(DecisionClientProvider owner, string consumer)
            {
                _owner = owner;
                _consumer = consumer;
            }

            public async Task<DecisionResult> DecideAsync(DecisionRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DecisionRouteResolution route = _owner._router.Resolve(_consumer);
                if (!route.Ok)
                {
                    return route.ToFailure();
                }

                DecisionResult result = await _owner.BackendFor(route.Endpoint).DecideAsync(request, cancellationToken);
                if (result != null && result.Usage.RoleId.Length == 0)
                {
                    result.Usage.RoleId = _consumer;
                }

                return result;
            }
        }
    }
}
