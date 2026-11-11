using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Well-known consumer ids. Any string works; these are the ones the shipped policies use.</summary>
    public static class DecisionConsumers
    {
        /// <summary>NPC brains (<see cref="NpcDecisionPolicy"/>).</summary>
        public const string NpcBrain = "npc-brain";

        /// <summary>The player autopilot (<see cref="PlayerAutopilotPolicy"/>).</summary>
        public const string Autopilot = "autopilot";
    }

    /// <summary>Routes one consumer pattern to an endpoint with a budget.</summary>
    public sealed class DecisionRouteProfile
    {
        /// <summary>Exact consumer id, a prefix pattern ending with <c>*</c>, or <c>*</c>.</summary>
        public string ConsumerPattern { get; set; } = "*";

        /// <summary>Target endpoint id.</summary>
        public string EndpointId { get; set; } = "";

        /// <summary>Budget of every consumer this profile matches (each consumer has its own counters).</summary>
        public DecisionBudget Budget { get; set; } = new();

        /// <summary>Lower values are evaluated first; at equal order an exact pattern beats a prefix beats <c>*</c>.</summary>
        public int SortOrder { get; set; }
    }

    /// <summary>Decision routing data: profiles per consumer. Plain data so a game can ship it as config.</summary>
    public sealed class DecisionRouteTable
    {
        /// <summary>Profiles; order of equal entries is preserved.</summary>
        public List<DecisionRouteProfile> Profiles { get; set; } = new();

        /// <summary>A table that sends every consumer to <paramref name="endpointId"/>.</summary>
        public static DecisionRouteTable AllTo(string endpointId, DecisionBudget budget = null)
        {
            return new DecisionRouteTable
            {
                Profiles = new List<DecisionRouteProfile>
                {
                    new() { ConsumerPattern = "*", EndpointId = endpointId ?? "", Budget = budget ?? new DecisionBudget() }
                }
            };
        }
    }

    /// <summary>Outcome of routing one consumer.</summary>
    public sealed class DecisionRouteResolution
    {
        private DecisionRouteResolution(string consumerId, DecisionRouteProfile profile,
            LlmEndpointDescriptor endpoint, LlmErrorCode errorCode, string reason, string message)
        {
            ConsumerId = consumerId ?? "";
            Profile = profile;
            Endpoint = endpoint;
            ErrorCode = errorCode;
            Reason = reason ?? "";
            Message = message ?? "";
        }

        /// <summary>True when the consumer may call <see cref="Endpoint"/>.</summary>
        public bool Ok => ErrorCode == LlmErrorCode.None;

        /// <summary>Consumer that was routed.</summary>
        public string ConsumerId { get; }

        /// <summary>Matched profile; null when none matched.</summary>
        public DecisionRouteProfile Profile { get; }

        /// <summary>Routed endpoint; null when refused before an endpoint was found.</summary>
        public LlmEndpointDescriptor Endpoint { get; }

        /// <summary><see cref="LlmErrorCode.RoutingError"/> on refusal.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>Named reason from <see cref="DecisionErrorReasons"/>; empty when routed.</summary>
        public string Reason { get; }

        /// <summary>English description of a refusal.</summary>
        public string Message { get; }

        internal static DecisionRouteResolution Routed(string consumerId, DecisionRouteProfile profile,
            LlmEndpointDescriptor endpoint)
        {
            return new DecisionRouteResolution(consumerId, profile, endpoint, LlmErrorCode.None, "", "");
        }

        internal static DecisionRouteResolution Refused(string consumerId, DecisionRouteProfile profile,
            LlmEndpointDescriptor endpoint, string reason, string message)
        {
            return new DecisionRouteResolution(consumerId, profile, endpoint, LlmErrorCode.RoutingError, reason,
                message);
        }

        /// <summary>The refusal as a failed decision result.</summary>
        public DecisionResult ToFailure()
        {
            return DecisionResult.Failure(ErrorCode, Reason, Message, "routing");
        }
    }

    /// <summary>Resolves a consumer to a decision endpoint, refusing endpoints without the capability.</summary>
    public interface IDecisionRouter
    {
        /// <summary>Routes <paramref name="consumerId"/> against current data.</summary>
        DecisionRouteResolution Resolve(string consumerId);

        /// <summary>Remembers a capability report; a reachable list that refutes the protocol makes routing refuse.</summary>
        void RecordCapability(DecisionCapabilityReport report);
    }

    /// <summary>
    /// Default router: matches consumer patterns like chat role routing (<see cref="LlmRouteResolver.RoleMatches"/>),
    /// then refuses, with a named reason, an unknown, inactive or non-HTTP endpoint, an endpoint whose
    /// <see cref="LlmEndpointDescriptor.DecisionProtocol"/> is <see cref="LlmDecisionProtocol.None"/>
    /// (<see cref="DecisionErrorReasons.CapabilityMissing"/>), and one whose recorded model list refutes the declared
    /// protocol (<see cref="DecisionErrorReasons.CapabilityNotAdvertised"/>).
    /// </summary>
    public sealed class DecisionRouter : IDecisionRouter
    {
        /// <summary>Consumer id used when a caller passes none.</summary>
        public const string DefaultConsumerId = "default";

        private readonly object _gate = new();
        private readonly IDecisionEndpointSource _endpoints;
        private readonly Dictionary<string, DecisionCapabilityReport> _reports = new(StringComparer.Ordinal);
        private List<DecisionRouteProfile> _profiles;

        /// <summary>Creates a router.</summary>
        public DecisionRouter(DecisionRouteTable table, IDecisionEndpointSource endpoints)
        {
            _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
            _profiles = Order(table);
        }

        /// <summary>Replaces the route table; calls already routed keep their endpoint.</summary>
        public void ReplaceTable(DecisionRouteTable table)
        {
            List<DecisionRouteProfile> ordered = Order(table);
            lock (_gate)
            {
                _profiles = ordered;
            }
        }

        /// <inheritdoc />
        public void RecordCapability(DecisionCapabilityReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.EndpointId))
            {
                return;
            }

            lock (_gate)
            {
                _reports[report.EndpointId.Trim()] = report;
            }
        }

        /// <summary>Probes the endpoint <paramref name="endpointId"/> and records the report.</summary>
        public async Task<DecisionCapabilityReport> ProbeAsync(IDecisionCapabilityProbe probe, string endpointId,
            CancellationToken cancellationToken = default)
        {
            if (probe == null)
            {
                throw new ArgumentNullException(nameof(probe));
            }

            LlmEndpointDescriptor endpoint = FindEndpoint(endpointId);
            if (endpoint == null)
            {
                return new DecisionCapabilityReport
                {
                    EndpointId = endpointId ?? "", Error = $"Endpoint '{endpointId}' is unknown."
                };
            }

            DecisionCapabilityReport report = await probe.ProbeAsync(endpoint, cancellationToken);
            RecordCapability(report);
            return report;
        }

        /// <inheritdoc />
        public DecisionRouteResolution Resolve(string consumerId)
        {
            string consumer = string.IsNullOrWhiteSpace(consumerId) ? DefaultConsumerId : consumerId.Trim();
            List<DecisionRouteProfile> profiles;
            lock (_gate)
            {
                profiles = _profiles;
            }

            DecisionRouteProfile profile = profiles.FirstOrDefault(p => LlmRouteResolver.RoleMatches(p.ConsumerPattern, consumer));
            if (profile == null)
            {
                return DecisionRouteResolution.Refused(consumer, null, null, DecisionErrorReasons.NoRoute,
                    $"No decision route matches consumer '{consumer}'.");
            }

            string endpointId = profile.EndpointId?.Trim() ?? "";
            LlmEndpointDescriptor endpoint = FindEndpoint(endpointId);
            if (endpoint == null)
            {
                return DecisionRouteResolution.Refused(consumer, profile, null, DecisionErrorReasons.EndpointNotFound,
                    $"Decision route for '{consumer}' names endpoint '{endpointId}', which is not registered.");
            }

            if (!endpoint.Active)
            {
                return DecisionRouteResolution.Refused(consumer, profile, endpoint,
                    DecisionErrorReasons.EndpointInactive, $"Decision endpoint '{endpointId}' is inactive.");
            }

            if (endpoint.DecisionProtocol == LlmDecisionProtocol.None)
            {
                return DecisionRouteResolution.Refused(consumer, profile, endpoint,
                    DecisionErrorReasons.CapabilityMissing,
                    $"Endpoint '{endpointId}' declares no decision protocol (LlmEndpointDescriptor.DecisionProtocol is None).");
            }

            if (!IsHttpEndpoint(endpoint))
            {
                return DecisionRouteResolution.Refused(consumer, profile, endpoint,
                    DecisionErrorReasons.EndpointInvalid,
                    $"Decision endpoint '{endpointId}' must be an HTTP endpoint with an absolute http(s) base URL.");
            }

            DecisionCapabilityReport report;
            lock (_gate)
            {
                _reports.TryGetValue(endpointId, out report);
            }

            if (report != null && report.Refutes(endpoint.DecisionProtocol))
            {
                return DecisionRouteResolution.Refused(consumer, profile, endpoint,
                    DecisionErrorReasons.CapabilityNotAdvertised,
                    $"Endpoint '{endpointId}' declares {endpoint.DecisionProtocol}, but its model list does not advertise it" +
                    (report.Error.Length > 0 ? ": " + report.Error : "."));
            }

            return DecisionRouteResolution.Routed(consumer, profile, endpoint);
        }

        private static bool IsHttpEndpoint(LlmEndpointDescriptor endpoint)
        {
            return endpoint.Kind == LlmEndpointKind.HttpOpenAi &&
                   Uri.TryCreate(endpoint.BaseUrl?.Trim(), UriKind.Absolute, out Uri uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private LlmEndpointDescriptor FindEndpoint(string endpointId)
        {
            string id = endpointId?.Trim() ?? "";
            if (id.Length == 0)
            {
                return null;
            }

            foreach (LlmEndpointDescriptor endpoint in _endpoints.GetEndpoints() ?? Array.Empty<LlmEndpointDescriptor>())
            {
                if (endpoint != null && string.Equals(endpoint.EndpointId?.Trim(), id, StringComparison.Ordinal))
                {
                    return endpoint;
                }
            }

            return null;
        }

        private static List<DecisionRouteProfile> Order(DecisionRouteTable table)
        {
            List<DecisionRouteProfile> source = table?.Profiles ?? new List<DecisionRouteProfile>();
            return source
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.ConsumerPattern))
                .Select((profile, index) => (profile, index))
                .OrderBy(entry => entry.profile.SortOrder)
                .ThenBy(entry => Specificity(entry.profile.ConsumerPattern))
                .ThenBy(entry => entry.index)
                .Select(entry => entry.profile)
                .ToList();
        }

        private static int Specificity(string pattern)
        {
            string trimmed = pattern.Trim();
            if (trimmed == "*")
            {
                return 2;
            }

            return trimmed.EndsWith("*", StringComparison.Ordinal) ? 1 : 0;
        }
    }
}
