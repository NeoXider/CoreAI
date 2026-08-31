using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>Immutable request approved by the host policy before an HTTP transport can see it.</summary>
    public sealed class RbxHttpRequest
    {
        public RbxHttpRequest(string method, Uri uri,
            IReadOnlyDictionary<string, string> headers = null, string body = null,
            bool compress = false, int? timeoutSeconds = null)
        {
            Method = string.IsNullOrWhiteSpace(method)
                ? throw new ArgumentException("HTTP method is required.", nameof(method))
                : method.ToUpperInvariant();
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            Dictionary<string, string> copiedHeaders = new(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> pair in headers)
                {
                    copiedHeaders.Add(pair.Key, pair.Value);
                }
            }

            Headers = new ReadOnlyDictionary<string, string>(copiedHeaders);
            Body = body;
            Compress = compress;
            TimeoutSeconds = timeoutSeconds;
        }

        public string Method { get; }

        public Uri Uri { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        public string Body { get; }

        public bool Compress { get; }

        public int? TimeoutSeconds { get; }
    }

    /// <summary>Transport-neutral response exposed through HttpService:RequestAsync.</summary>
    public sealed class RbxHttpResponse
    {
        public RbxHttpResponse(int statusCode, string statusMessage, string body,
            IReadOnlyDictionary<string, string> headers = null)
        {
            StatusCode = statusCode;
            StatusMessage = statusMessage ?? string.Empty;
            Body = body ?? string.Empty;
            Dictionary<string, string> copiedHeaders = new(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> pair in headers)
                {
                    copiedHeaders.Add(pair.Key, pair.Value);
                }
            }

            Headers = new ReadOnlyDictionary<string, string>(copiedHeaders);
        }

        public bool Success => StatusCode >= 200 && StatusCode <= 299;

        public int StatusCode { get; }

        public string StatusMessage { get; }

        public string Body { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }
    }

    /// <summary>
    /// Host-owned authorization seam in front of every mod HTTP request. Implementations may narrow,
    /// normalize, or reject a request; null/failed approval is always denial.
    /// </summary>
    public interface IRbxHttpRequestPolicy
    {
        bool IsEnabled { get; }

        bool TryAuthorize(string actorId, RbxHttpRequest requested,
            out RbxHttpRequest approved, out string refusalReason);
    }

    /// <summary>Production default: no mod-originated request is permitted.</summary>
    public sealed class RbxDenyAllHttpRequestPolicy : IRbxHttpRequestPolicy
    {
        public static RbxDenyAllHttpRequestPolicy Instance { get; } = new();

        private RbxDenyAllHttpRequestPolicy()
        {
        }

        public bool IsEnabled => false;

        public bool TryAuthorize(string actorId, RbxHttpRequest requested,
            out RbxHttpRequest approved, out string refusalReason)
        {
            approved = null;
            refusalReason = "the host policy denies all outbound HTTP by default";
            return false;
        }
    }

    /// <summary>
    /// Non-optional request invariants enforced again after host authorization. A host policy may
    /// narrow these rules but cannot authorize filesystem schemes, credentials, or literal local,
    /// private, link-local, documentation, multicast, and other special network addresses.
    /// </summary>
    internal static class RbxHttpSafety
    {
        private sealed class IpPrefix
        {
            private readonly byte[] _networkBytes;
            private readonly int _prefixLength;

            public IpPrefix(string networkAddress, int prefixLength)
            {
                _networkBytes = IPAddress.Parse(networkAddress).GetAddressBytes();
                _prefixLength = prefixLength;
            }

            public bool Contains(IPAddress address)
            {
                byte[] addressBytes = address.GetAddressBytes();
                if (addressBytes.Length != _networkBytes.Length)
                {
                    return false;
                }

                int fullBytes = _prefixLength / 8;
                for (int index = 0; index < fullBytes; index++)
                {
                    if (addressBytes[index] != _networkBytes[index])
                    {
                        return false;
                    }
                }

                int remainingBits = _prefixLength % 8;
                if (remainingBits == 0)
                {
                    return true;
                }

                int mask = 0xFF << 8 - remainingBits & 0xFF;
                return (addressBytes[fullBytes] & mask) == (_networkBytes[fullBytes] & mask);
            }
        }

        private static readonly HashSet<string> AllowedMethods = new(
            StringComparer.Ordinal)
        {
            "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "TRACE", "PATCH"
        };

        private static readonly IpPrefix[] ForbiddenIpv4Prefixes =
        {
            new("0.0.0.0", 8),
            new("10.0.0.0", 8),
            new("100.64.0.0", 10),
            new("127.0.0.0", 8),
            new("169.254.0.0", 16),
            new("172.16.0.0", 12),
            new("192.0.0.0", 24),
            new("192.0.2.0", 24),
            new("192.31.196.0", 24),
            new("192.52.193.0", 24),
            new("192.88.99.0", 24),
            new("192.168.0.0", 16),
            new("192.175.48.0", 24),
            new("198.18.0.0", 15),
            new("198.51.100.0", 24),
            new("203.0.113.0", 24),
            new("224.0.0.0", 4),
            new("240.0.0.0", 4)
        };

        private static readonly IpPrefix[] ForbiddenIpv6Prefixes =
        {
            new("::", 96),
            new("::ffff:0:0", 96),
            new("::ffff:0:0:0", 96),
            new("64:ff9b::", 96),
            new("64:ff9b:1::", 48),
            new("100::", 64),
            new("2001::", 23),
            new("2001:db8::", 32),
            new("2002::", 16),
            new("2620:4f:8000::", 48),
            new("3fff::", 20),
            new("5f00::", 16),
            new("fc00::", 7),
            new("fe80::", 10),
            new("fec0::", 10),
            new("ff00::", 8)
        };

        private static readonly HashSet<string> ForbiddenHeaders = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "Host",
            "Content-Length",
            "Connection",
            "Transfer-Encoding",
            "Upgrade",
            "X-Api-Key",
            "Api-Key"
        };

        public static bool TryValidate(RbxHttpRequest request, out string refusalReason)
        {
            if (request == null)
            {
                refusalReason = "the request is missing";
                return false;
            }

            Uri uri = request.Uri;
            if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                refusalReason = "only absolute HTTPS URLs are eligible for outbound HTTP";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                refusalReason = "URL user-info credentials are forbidden";
                return false;
            }

            if (IsForbiddenHost(uri.Host))
            {
                refusalReason = "local, loopback, private, link-local, and special hosts are forbidden";
                return false;
            }

            if (!AllowedMethods.Contains(request.Method))
            {
                refusalReason = "HTTP method '" + request.Method + "' is not supported";
                return false;
            }

            if ((request.Method == "GET" || request.Method == "HEAD")
                && request.Body != null)
            {
                refusalReason = request.Method + " requests cannot contain a body";
                return false;
            }

            foreach (KeyValuePair<string, string> pair in request.Headers)
            {
                if (!IsHeaderNameToken(pair.Key)
                    || pair.Value != null
                    && (pair.Value.IndexOf('\r') >= 0 || pair.Value.IndexOf('\n') >= 0))
                {
                    refusalReason = "request header names must be untrimmed RFC tokens and values "
                                    + "cannot contain line breaks";
                    return false;
                }

                if (ForbiddenHeaders.Contains(pair.Key))
                {
                    refusalReason = "request header '" + pair.Key
                                    + "' is credential-bearing or transport-controlled";
                    return false;
                }
            }

            refusalReason = null;
            return true;
        }

        public static bool IsForbiddenHost(string host)
        {
            string normalized = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            if (normalized.Length >= 2 && normalized[0] == '['
                                       && normalized[normalized.Length - 1] == ']')
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            if (normalized.Length == 0
                || normalized == "localhost"
                || normalized.EndsWith(".localhost", StringComparison.Ordinal)
                || normalized.EndsWith(".local", StringComparison.Ordinal))
            {
                return true;
            }

            if (!IPAddress.TryParse(normalized, out IPAddress address))
            {
                return false;
            }

            return IsForbiddenAddress(address);
        }

        public static bool IsForbiddenAddress(IPAddress address)
        {
            if (address == null)
            {
                return true;
            }

            if (IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.None)
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast)
            {
                return true;
            }

            IpPrefix[] prefixes = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? ForbiddenIpv4Prefixes
                : ForbiddenIpv6Prefixes;
            for (int index = 0; index < prefixes.Length; index++)
            {
                if (prefixes[index].Contains(address))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsHeaderNameToken(string name)
        {
            if (string.IsNullOrEmpty(name) || !string.Equals(name, name.Trim(),
                    StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = 0; index < name.Length; index++)
            {
                char character = name[index];
                bool allowed = character >= 'a' && character <= 'z'
                               || character >= 'A' && character <= 'Z'
                               || character >= '0' && character <= '9'
                               || character == '!' || character == '#'
                               || character == '$' || character == '%'
                               || character == '&' || character == '\''
                               || character == '*' || character == '+'
                               || character == '-' || character == '.'
                               || character == '^' || character == '_'
                               || character == '`' || character == '|'
                               || character == '~';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// HTTPS-origin allowlist policy for hosts that intentionally opt in. It rejects local/special
    /// addresses, URL credentials, and credential-bearing or transport-controlled request headers.
    /// </summary>
    public sealed class RbxAllowlistHttpRequestPolicy : IRbxHttpRequestPolicy
    {
        private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);

        public RbxAllowlistHttpRequestPolicy(IEnumerable<string> allowedHttpsOrigins)
        {
            if (allowedHttpsOrigins == null)
            {
                throw new ArgumentNullException(nameof(allowedHttpsOrigins));
            }

            foreach (string candidate in allowedHttpsOrigins)
            {
                Uri uri = ReadAllowedOrigin(candidate);
                _allowedOrigins.Add(NormalizeOrigin(uri));
            }
        }

        public bool IsEnabled => _allowedOrigins.Count > 0;

        public bool TryAuthorize(string actorId, RbxHttpRequest requested,
            out RbxHttpRequest approved, out string refusalReason)
        {
            approved = null;
            if (!RbxHttpSafety.TryValidate(requested, out refusalReason))
            {
                return false;
            }

            Uri uri = requested.Uri;
            if (!_allowedOrigins.Contains(NormalizeOrigin(uri)))
            {
                refusalReason = "origin '" + NormalizeOrigin(uri) + "' is not on the host allowlist";
                return false;
            }

            approved = new RbxHttpRequest(requested.Method, uri, requested.Headers,
                requested.Body, requested.Compress, requested.TimeoutSeconds);
            refusalReason = null;
            return true;
        }

        private static Uri ReadAllowedOrigin(string candidate)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || RbxHttpSafety.IsForbiddenHost(uri.Host)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException(
                    "Allowed HTTP origins must be public absolute HTTPS origins without path, "
                    + "query, fragment, or credentials.", nameof(candidate));
            }

            return uri;
        }

        private static string NormalizeOrigin(Uri uri)
        {
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }
    }

    /// <summary>Host resolver seam used before a transport receives any destination.</summary>
    public interface IRbxHttpDestinationResolver
    {
        Task<IReadOnlyList<IPAddress>> ResolveAsync(string host,
            CancellationToken cancellationToken);
    }

    /// <summary>Production resolver default: fail loudly without DNS or network access.</summary>
    public sealed class RbxRefusingHttpDestinationResolver : IRbxHttpDestinationResolver
    {
        public static RbxRefusingHttpDestinationResolver Instance { get; } = new();

        private RbxRefusingHttpDestinationResolver()
        {
        }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host,
            CancellationToken cancellationToken)
        {
            return Task.FromException<IReadOnlyList<IPAddress>>(new InvalidOperationException(
                "MVP2 ships no DNS resolver for mod HTTP; the host must install one explicitly"));
        }
    }

    /// <summary>
    /// One exact network destination constructed by CoreAI only after every resolved address passed
    /// post-policy special-address validation.
    /// </summary>
    public sealed class RbxValidatedHttpDestination
    {
        internal RbxValidatedHttpDestination(RbxHttpRequest request, IPAddress address)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Address = address ?? throw new ArgumentNullException(nameof(address));
        }

        public RbxHttpRequest Request { get; }

        public IPAddress Address { get; }
    }

    /// <summary>
    /// Host single-exchange transport seam. The API supplies one already validated IP destination,
    /// so implementations do not resolve the URI host or perform redirect/retry routing.
    /// </summary>
    public interface IRbxHttpTransport
    {
        Task<RbxHttpResponse> SendAsync(RbxValidatedHttpDestination destination,
            CancellationToken cancellationToken);
    }

    /// <summary>Production transport default: fail loudly without performing network I/O.</summary>
    public sealed class RbxRefusingHttpTransport : IRbxHttpTransport
    {
        public static RbxRefusingHttpTransport Instance { get; } = new();

        private RbxRefusingHttpTransport()
        {
        }

        public Task<RbxHttpResponse> SendAsync(RbxValidatedHttpDestination destination,
            CancellationToken cancellationToken)
        {
            return Task.FromException<RbxHttpResponse>(new InvalidOperationException(
                "MVP2 ships no outbound HTTP transport; the host must install a safe transport explicitly"));
        }
    }

    /// <summary>Fixed-window per-actor request limiter using an injected monotonic clock.</summary>
    internal sealed class RbxHttpActorRateLimiter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Queue<double>> _requestsByActor =
            new(StringComparer.Ordinal);
        private readonly int _limit;
        private readonly double _windowSeconds;
        private readonly Func<double> _clock;

        public RbxHttpActorRateLimiter(int limit, double windowSeconds, Func<double> clock)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            if (windowSeconds <= 0d || double.IsNaN(windowSeconds)
                                  || double.IsInfinity(windowSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(windowSeconds));
            }

            _limit = limit;
            _windowSeconds = windowSeconds;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public bool TryAcquire(string actorId, out string refusalReason)
        {
            string actor = string.IsNullOrWhiteSpace(actorId) ? "unknown" : actorId.Trim();
            double now = _clock();
            lock (_gate)
            {
                if (!_requestsByActor.TryGetValue(actor, out Queue<double> requests))
                {
                    requests = new Queue<double>();
                    _requestsByActor.Add(actor, requests);
                }

                while (requests.Count > 0 && now - requests.Peek() >= _windowSeconds)
                {
                    requests.Dequeue();
                }

                if (requests.Count >= _limit)
                {
                    refusalReason = "per-actor limit " + _limit + " requests per "
                                    + _windowSeconds + " seconds was exhausted";
                    return false;
                }

                requests.Enqueue(now);
                refusalReason = null;
                return true;
            }
        }
    }
}
