using System;
using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Resolves a <see cref="LlmEndpointDescriptor.SecretReference"/> as the name of an environment variable, like
    /// the chat registry's default. Returns nothing in a WebGL player, which has no environment.
    /// </summary>
    public sealed class EnvironmentDecisionSecretProvider : ILlmEndpointSecretProvider
    {
        /// <inheritdoc />
        public bool TryResolve(string secretReference, out string secret)
        {
            secret = string.IsNullOrWhiteSpace(secretReference)
                ? ""
                : Environment.GetEnvironmentVariable(secretReference.Trim()) ?? "";
            return secret.Length > 0;
        }
    }

    /// <summary>
    /// Session-only credentials keyed by secret reference, for keys a player types in at runtime. Nothing is
    /// persisted; an optional fallback provider answers references not set here.
    /// </summary>
    public sealed class InMemoryDecisionSecretProvider : ILlmEndpointSecretProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        private readonly ILlmEndpointSecretProvider _fallback;

        /// <summary>Creates a provider with an optional fallback.</summary>
        public InMemoryDecisionSecretProvider(ILlmEndpointSecretProvider fallback = null)
        {
            _fallback = fallback;
        }

        /// <summary>Sets or, with an empty secret, clears the credential for <paramref name="secretReference"/>.</summary>
        public void Set(string secretReference, string secret)
        {
            string key = secretReference?.Trim() ?? "";
            if (key.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (string.IsNullOrEmpty(secret))
                {
                    _secrets.Remove(key);
                }
                else
                {
                    _secrets[key] = secret;
                }
            }
        }

        /// <inheritdoc />
        public bool TryResolve(string secretReference, out string secret)
        {
            string key = secretReference?.Trim() ?? "";
            lock (_gate)
            {
                if (_secrets.TryGetValue(key, out secret))
                {
                    return true;
                }
            }

            if (_fallback != null)
            {
                return _fallback.TryResolve(secretReference, out secret);
            }

            secret = "";
            return false;
        }
    }
}
