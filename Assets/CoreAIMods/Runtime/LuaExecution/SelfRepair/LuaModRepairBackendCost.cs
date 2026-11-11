using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>What a repair turn costs on the backend the repairing role uses.</summary>
    public enum LuaModRepairBackendCost
    {
        /// <summary>Nothing says which backend answers; treated as paid.</summary>
        Unknown = 0,

        /// <summary>A model on this machine or the local network: no per-request charge.</summary>
        Local = 1,

        /// <summary>A hosted provider or a game backend in front of one: each request spends tokens someone pays for.</summary>
        Paid = 2,

        /// <summary>No model answers (the Offline backend).</summary>
        None = 3
    }

    /// <summary>
    /// The rule that tells a local backend from a paid one, and the repair mode it gives
    /// <see cref="LuaModRuntimeRepairMode.ByBackend"/>. Local means a model the player runs: LLMUnity (the built-in
    /// local client, <see cref="LlmExecutionMode.LocalModel"/>), or an OpenAI-compatible server whose base URL names this
    /// machine or the local network (LM Studio, a llama.cpp server): a loopback host (<c>localhost</c>,
    /// <c>127.0.0.0/8</c>, <c>::1</c>), a private IPv4 address (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>) or a
    /// <c>.local</c> name. Everything else is paid: any other host, a game backend that holds the provider key
    /// (<see cref="LlmExecutionMode.ServerManagedApi"/>), and an Auto mode whose HTTP fallback is not local. When the
    /// rule cannot tell, the answer is <see cref="LuaModRepairBackendCost.Unknown"/>, which asks first like paid.
    /// </summary>
    public static class LuaModRepairBackendCostRules
    {
        /// <summary>The cost of the global backend settings: the execution mode and the HTTP base URL.</summary>
        public static LuaModRepairBackendCost Classify(LlmExecutionMode mode, string httpBaseUrl)
        {
            switch (mode)
            {
                case LlmExecutionMode.Offline:
                    return LuaModRepairBackendCost.None;
                case LlmExecutionMode.LocalModel:
                    return LuaModRepairBackendCost.Local;
                case LlmExecutionMode.ServerManagedApi:
                    return LuaModRepairBackendCost.Paid;
                case LlmExecutionMode.ClientOwnedApi:
                case LlmExecutionMode.ClientLimited:
                case LlmExecutionMode.Auto:
                    // WHY Auto by its URL: Auto tries LLMUnity and falls back to HTTP, so it is free only when the
                    // fallback is local too.
                    return IsLocalUrl(httpBaseUrl) ? LuaModRepairBackendCost.Local : LuaModRepairBackendCost.Paid;
                default:
                    return LuaModRepairBackendCost.Unknown;
            }
        }

        /// <summary>The cost of one endpoint of the runtime endpoint registry (the Hub's endpoint list).</summary>
        public static LuaModRepairBackendCost Classify(LlmEndpointDescriptor endpoint)
        {
            if (endpoint == null)
            {
                return LuaModRepairBackendCost.Unknown;
            }

            switch (endpoint.Kind)
            {
                case LlmEndpointKind.Offline:
                    return LuaModRepairBackendCost.None;
                case LlmEndpointKind.LlmUnity:
                    return !endpoint.Remote || IsLocalUrl(endpoint.BaseUrl)
                        ? LuaModRepairBackendCost.Local
                        : LuaModRepairBackendCost.Paid;
                case LlmEndpointKind.HttpOpenAi:
                    return IsLocalUrl(endpoint.BaseUrl) ? LuaModRepairBackendCost.Local : LuaModRepairBackendCost.Paid;
                default:
                    return LuaModRepairBackendCost.Unknown;
            }
        }

        /// <summary>
        /// The cost of the endpoints the registry routes <paramref name="roleId"/> to: its profile's endpoint and the
        /// endpoints of its fallback profiles. Local only when every one of them is local; Unknown when the registry
        /// assigns the role no endpoint it knows.
        /// </summary>
        public static LuaModRepairBackendCost ClassifyRole(ILlmEndpointRegistry registry, string roleId)
        {
            if (registry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return LuaModRepairBackendCost.Unknown;
            }

            string profileId = registry.GetRoleProfile(roleId) ?? "";
            if (profileId.Length == 0)
            {
                return LuaModRepairBackendCost.Unknown;
            }

            Dictionary<string, LlmRuntimeProfile> profiles = new(StringComparer.Ordinal);
            foreach (LlmRuntimeProfile profile in registry.GetProfiles() ?? Array.Empty<LlmRuntimeProfile>())
            {
                if (profile != null && !string.IsNullOrEmpty(profile.ProfileId))
                {
                    profiles[profile.ProfileId] = profile;
                }
            }

            Dictionary<string, LlmEndpointDescriptor> endpoints = new(StringComparer.Ordinal);
            foreach (LlmEndpointSnapshot snapshot in registry.GetEndpoints() ?? Array.Empty<LlmEndpointSnapshot>())
            {
                if (snapshot?.Descriptor != null && !string.IsNullOrEmpty(snapshot.Descriptor.EndpointId))
                {
                    endpoints[snapshot.Descriptor.EndpointId] = snapshot.Descriptor;
                }
            }

            List<LuaModRepairBackendCost> costs = new();
            HashSet<string> visited = new(StringComparer.Ordinal);
            Queue<string> pending = new();
            pending.Enqueue(profileId);
            while (pending.Count > 0 && visited.Count < 16)
            {
                string id = pending.Dequeue();
                if (!visited.Add(id) || !profiles.TryGetValue(id, out LlmRuntimeProfile profile))
                {
                    continue;
                }

                if (endpoints.TryGetValue(profile.EndpointId ?? "", out LlmEndpointDescriptor endpoint))
                {
                    costs.Add(Classify(endpoint));
                }

                foreach (string fallback in profile.FallbackProfileIds ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrEmpty(fallback))
                    {
                        pending.Enqueue(fallback);
                    }
                }
            }

            return Combine(costs);
        }

        /// <summary>
        /// The cost of several backends a turn may reach: Paid when any is paid, Unknown when any is unknown, Local
        /// when every one answering is local, None when none answers.
        /// </summary>
        public static LuaModRepairBackendCost Combine(IReadOnlyList<LuaModRepairBackendCost> costs)
        {
            if (costs == null || costs.Count == 0)
            {
                return LuaModRepairBackendCost.Unknown;
            }

            bool anyLocal = false;
            bool anyUnknown = false;
            foreach (LuaModRepairBackendCost cost in costs)
            {
                switch (cost)
                {
                    case LuaModRepairBackendCost.Paid:
                        return LuaModRepairBackendCost.Paid;
                    case LuaModRepairBackendCost.Unknown:
                        anyUnknown = true;
                        break;
                    case LuaModRepairBackendCost.Local:
                        anyLocal = true;
                        break;
                }
            }

            if (anyUnknown)
            {
                return LuaModRepairBackendCost.Unknown;
            }

            return anyLocal ? LuaModRepairBackendCost.Local : LuaModRepairBackendCost.None;
        }

        /// <summary>
        /// The mode the loop acts in: <paramref name="mode"/> itself, except that
        /// <see cref="LuaModRuntimeRepairMode.ByBackend"/> becomes Automatic for a local backend and AskFirst for any
        /// other.
        /// </summary>
        public static LuaModRuntimeRepairMode EffectiveMode(LuaModRuntimeRepairMode mode, LuaModRepairBackendCost cost)
        {
            if (mode != LuaModRuntimeRepairMode.ByBackend)
            {
                return mode;
            }

            return cost == LuaModRepairBackendCost.Local
                ? LuaModRuntimeRepairMode.Automatic
                : LuaModRuntimeRepairMode.AskFirst;
        }

        /// <summary>
        /// True when <paramref name="url"/> names this machine or the local network: a loopback host, a private IPv4
        /// address or a <c>.local</c> name. False for an empty or unreadable URL and for every other host.
        /// </summary>
        public static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            string host = (uri.IdnHost ?? uri.Host ?? "").Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (host.Length == 0)
            {
                return false;
            }

            if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) ||
                host.EndsWith(".local", StringComparison.Ordinal))
            {
                return true;
            }

            if (!IPAddress.TryParse(host, out IPAddress address))
            {
                return false;
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }
    }
}
