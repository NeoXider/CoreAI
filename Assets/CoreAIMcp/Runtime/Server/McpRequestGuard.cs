using System;
using System.Security.Cryptography;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Engine-free admission checks for the loopback MCP endpoint, kept out of the HTTP adapter so the
    /// whole security decision is unit-testable without a socket.
    /// <para>
    /// WHY (threat model): binding 127.0.0.1 is NOT a security boundary on its own. A page in the user's
    /// browser can POST to <c>http://127.0.0.1:&lt;port&gt;/mcp</c> without a CORS preflight (a "simple"
    /// request), and DNS rebinding turns that into a same-origin request whose responses the page can
    /// read - which would leak <c>screenshot</c> output and allow <c>execute_lua</c> / <c>manage_mods</c>
    /// (whose effect survives a restart). The three defences here are complementary: the Origin check
    /// stops the browser CSRF path, the Host check stops DNS rebinding (a rebound page still sends the
    /// attacker's hostname), and the bearer token stops a malicious LOCAL process, which neither header
    /// check can see.
    /// </para>
    /// </summary>
    public static class McpRequestGuard
    {
        /// <summary>Entropy of a generated bearer token, in bytes.</summary>
        public const int TokenByteLength = 24;

        /// <summary>The scheme prefix of an <c>Authorization</c> header value.</summary>
        public const string BearerPrefix = "Bearer ";

        /// <summary>Creates a fresh URL-safe bearer token from a cryptographic RNG.</summary>
        public static string GenerateToken()
        {
            byte[] bytes = new byte[TokenByteLength];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// True when the request may proceed based on its <c>Origin</c>. An absent Origin is allowed (no
        /// browser is involved - that is every real MCP client); a present Origin must be loopback on the
        /// port this server listens on.
        /// </summary>
        public static bool IsOriginAllowed(string origin, int port)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return true;
            }

            // WHY: an opaque origin ("null") is what a sandboxed iframe or a data: document sends - never
            // a legitimate local client, always worth refusing.
            if (string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Uri.TryCreate(origin.Trim(), UriKind.Absolute, out Uri uri) && IsLoopbackAuthority(uri, port);
        }

        /// <summary>
        /// True when the <c>Host</c> header names this loopback endpoint. A rebound DNS name
        /// (<c>evil.example:port</c> resolving to 127.0.0.1) fails here even though the socket is local.
        /// </summary>
        public static bool IsHostAllowed(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            return Uri.TryCreate($"http://{host.Trim()}", UriKind.Absolute, out Uri uri)
                   && IsLoopbackAuthority(uri, port);
        }

        /// <summary>
        /// True when the body's media type is acceptable. An absent type is allowed; anything else must be
        /// JSON, which rejects the <c>text/plain</c> / form encodings a browser can send cross-origin
        /// without a preflight.
        /// </summary>
        public static bool IsContentTypeAllowed(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return true;
            }

            int separator = contentType.IndexOf(';');
            string media = (separator >= 0 ? contentType.Substring(0, separator) : contentType).Trim();

            return media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                   || media.Equals("application/json-rpc", StringComparison.OrdinalIgnoreCase)
                   || media.Equals("application/jsonrequest", StringComparison.OrdinalIgnoreCase)
                   || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the <c>Authorization</c> header carries <paramref name="expectedToken"/>. A null or
        /// empty expected token means auth is disabled and everything passes. The token is accepted with
        /// or without the <c>Bearer</c> prefix and compared in constant time.
        /// </summary>
        public static bool IsAuthorized(string authorizationHeader, string expectedToken)
        {
            if (string.IsNullOrEmpty(expectedToken))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(authorizationHeader))
            {
                return false;
            }

            string presented = authorizationHeader.Trim();
            if (presented.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                presented = presented.Substring(BearerPrefix.Length).Trim();
            }

            return FixedTimeEquals(presented, expectedToken);
        }

        private static bool IsLoopbackAuthority(Uri uri, int port)
        {
            if (uri == null)
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return uri.Port == port && IsLoopbackHost(uri.Host);
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            string trimmed = host.Trim('[', ']');
            return trimmed.Equals("127.0.0.1", StringComparison.Ordinal)
                   || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("::1", StringComparison.Ordinal);
        }

        // WHY: compare the whole string regardless of where it diverges, so a caller cannot time-probe the
        // token one character at a time.
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            int difference = a.Length ^ b.Length;
            int shared = Math.Min(a.Length, b.Length);
            for (int i = 0; i < shared; i++)
            {
                difference |= a[i] ^ b[i];
            }

            return difference == 0;
        }
    }
}
