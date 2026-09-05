using System;
using System.Collections.Generic;
using CoreAI.Authority;
using Mirror;
using UnityEngine;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// Admits connections to a CoreAI world through the host's own <see cref="IActorAdmissionProvider"/>.
    /// </summary>
    /// <remarks>
    /// WHY admission is a Mirror authenticator and not a first-packet check inside the bridge: an
    /// authenticator runs BEFORE the connection is authenticated, which is the only point at which
    /// nothing exists yet — no Player, no chat session, no mod ownership, no world access. Deciding
    /// later means deciding after something has already been created for a stranger.
    /// <para>
    /// There is no anonymous path. A composition with no provider refuses every connection rather
    /// than admitting them all, because the failure mode of the other choice is an open server that
    /// looks like it is working.
    /// </para>
    /// </remarks>
    [AddComponentMenu("CoreAI/CoreAI Mirror Authenticator")]
    public sealed class CoreAiMirrorAuthenticator : NetworkAuthenticator
    {
        /// <summary>What a rejected client is told. Deliberately uninformative.</summary>
        private const string ClientFacingRejection = "not admitted";

        private readonly Dictionary<int, ActorAdmissionResult> _admitted = new();
        private IActorAdmissionProvider _provider;
        private string _worldId = "";
        private Func<byte[]> _clientCredential = Array.Empty<byte>;
        private Action<string> _log;

        /// <summary>Admissions granted so far, for the gate that counts them.</summary>
        public int AdmittedCount { get; private set; }

        /// <summary>Admissions refused so far.</summary>
        public int RejectedCount { get; private set; }

        /// <summary>
        /// Wires the host's provider. Call before <c>StartServer</c>.
        /// </summary>
        public void Configure(IActorAdmissionProvider provider, string worldId,
            Action<string> log = null)
        {
            _provider = provider;
            _worldId = worldId ?? "";
            _log = log;
        }

        /// <summary>Supplies the credential this process sends when it joins as a client.</summary>
        public void ConfigureClientCredential(Func<byte[]> credential)
        {
            _clientCredential = credential ?? (() => Array.Empty<byte>());
        }

        /// <summary>The admission result for a connection, or null when it was never admitted.</summary>
        public ActorAdmissionResult ResultFor(int connectionId)
        {
            return _admitted.TryGetValue(connectionId, out ActorAdmissionResult result)
                ? result
                : null;
        }

        /// <summary>Forgets a connection's admission when it disconnects.</summary>
        public void Forget(int connectionId)
        {
            _admitted.Remove(connectionId);
        }

        /// <inheritdoc />
        public override void OnStartServer()
        {
            NetworkServer.RegisterHandler<CoreAiAdmissionRequestMessage>(
                OnAdmissionRequest, requireAuthentication: false);
        }

        /// <inheritdoc />
        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<CoreAiAdmissionRequestMessage>();
            _admitted.Clear();
        }

        /// <inheritdoc />
        public override void OnStartClient()
        {
            NetworkClient.RegisterHandler<CoreAiAdmissionResponseMessage>(
                OnAdmissionResponse, requireAuthentication: false);
        }

        /// <inheritdoc />
        public override void OnStopClient()
        {
            NetworkClient.UnregisterHandler<CoreAiAdmissionResponseMessage>();
        }

        /// <inheritdoc />
        public override void OnClientAuthenticate()
        {
            NetworkClient.Send(new CoreAiAdmissionRequestMessage
            {
                Credential = _clientCredential() ?? Array.Empty<byte>()
            });
        }

        /// <inheritdoc />
        /// <remarks>
        /// Nothing happens here: the server waits for the credential message rather than admitting on
        /// connection. A connection that never sends one is dropped by Mirror's own authentication
        /// timeout, having reached nothing.
        /// </remarks>
        public override void OnServerAuthenticate(NetworkConnectionToClient conn)
        {
        }

        /// <summary>
        /// Decides one connection's admission, without touching the transport.
        /// </summary>
        /// <remarks>
        /// WHY the decision is separable from the message handler: this is the security boundary of
        /// the whole online rung, and a rule that can only be exercised through a live socket is a
        /// rule that gets tested rarely, late, and never in the failure cases that matter.
        /// </remarks>
        public ActorAdmissionResult Decide(int connectionId, string address, byte[] credential)
        {
            if (_provider == null)
            {
                // WHY refuse rather than allow: a composition that forgot to configure admission is
                // a misconfigured server, and the safe reading of "I do not know who you are" is no.
                return Refused(connectionId,
                    "no IActorAdmissionProvider is configured on this host");
            }

            ActorAdmissionResult result;
            try
            {
                result = _provider.TryAdmit(new ActorCredential(credential, address), _worldId);
            }
            catch (Exception exception)
            {
                // A provider that throws has not admitted anyone. Treating an exception as a pass
                // would turn every bug in a host's authentication into an open door.
                return Refused(connectionId, "the admission provider threw: " + exception.Message);
            }

            if (result == null || !result.Admitted)
            {
                return Refused(connectionId,
                    result == null ? "the provider returned no decision" : result.Reason);
            }

            _admitted[connectionId] = result;
            AdmittedCount++;
            return result;
        }

        private void OnAdmissionRequest(NetworkConnectionToClient conn,
            CoreAiAdmissionRequestMessage message)
        {
            if (conn == null)
            {
                return;
            }

            ActorAdmissionResult result =
                Decide(conn.connectionId, conn.address, message.Credential);
            conn.Send(new CoreAiAdmissionResponseMessage
            {
                Admitted = result.Admitted,
                Reason = result.Admitted ? "" : ClientFacingRejection
            });

            if (result.Admitted)
            {
                ServerAccept(conn);
                return;
            }

            ServerReject(conn);
        }

        private ActorAdmissionResult Refused(int connectionId, string reason)
        {
            RejectedCount++;
            // The detailed reason goes to the host's log only. Telling the client which half of a
            // forged credential to fix is the one thing a rejection must never do.
            _log?.Invoke("[CoreAI.Mirror] admission refused for connection " + connectionId
                         + ": " + reason);
            return ActorAdmissionResult.Reject(string.IsNullOrWhiteSpace(reason)
                ? "refused without a stated reason"
                : reason);
        }

        private void OnAdmissionResponse(CoreAiAdmissionResponseMessage message)
        {
            if (message.Admitted)
            {
                ClientAccept();
                return;
            }

            ClientReject();
        }
    }
}
