using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using kcp2k;
using Mirror;
using UnityEngine;
using UnityEngine.Profiling;

namespace CoreAI.Net.Mirror.Harness
{
    /// <summary>
    /// A Mirror host and its remote clients as separate player processes on localhost kcp, running
    /// the CoreAI bridge for real: admission with the protocol handshake, the host's own player in
    /// host mode, readiness, the roster, a RemoteEvent chat relayed to everyone, RemoteFunction
    /// round trips both ways, an optional inbound flood, and the spike S3 measurement. Each process
    /// writes one JSON result and exits 0 when every check passed.
    /// </summary>
    /// <remarks>
    /// WHY bridge level and not a Lua world: replication does not cross the wire yet, so two
    /// processes have no shared instance ids for a script's remote; the fixed remote ids here are
    /// what the bridge carries either way. WHY compiled only with <c>COREAI_MIRROR_HARNESS</c>: this
    /// is a test rig, and a game that ships the package must not ship a command-line entry point
    /// into it; <c>CoreAiMirrorHarnessBuild</c> adds the define to the harness player build only.
    /// The arguments are listed in the package README, section "Two-process harness".
    /// </remarks>
    public sealed class CoreAiMirrorTwoProcessHarness : MonoBehaviour
    {
        /// <summary>The argument that turns a player into a harness process: <c>host</c> or <c>client</c>.</summary>
        public const string RoleArgument = "-coreaiHarness";

        /// <summary>The line every result is also logged after, for a runner that reads the log.</summary>
        public const string ResultLogPrefix = "COREAI_MIRROR_HARNESS_RESULT ";

        /// <summary>The spike S3 payload: 5 MiB.</summary>
        public const int SpikeTotalBytes = 5 * 1024 * 1024;

        private const ulong ChatRemote = 0x1001UL;
        private const ulong RelayRemote = 0x1002UL;
        private const ulong EchoRemote = 0x1003UL;
        private const ulong DoneRemote = 0x1004UL;
        private const ulong FloodRemote = 0x1005UL;
        private const int ChunkEnvelopeBytes = 20;
        private const string OversizeLogMarker = "larger than the max allowed message size";
        private static readonly int[] SpikeChunkSizes = { 16384, 65536, 262144, 0 };

        private enum Phase
        {
            Joining,
            Talking,
            Flooding,
            Spike,
            Waiting,
            Finishing,
            Done
        }

        private HarnessOptions _options;
        private NetworkManager _manager;
        private CoreAiMirrorAuthenticator _authenticator;
        private CoreAiMirrorNetworkBridgeProvider _provider;
        private MirrorNetworkBridge _bridge;
        private readonly HarnessResult _result = new();
        private readonly List<float> _frameMs = new();
        private readonly List<float> _floodFrameMs = new();
        private Phase _phase;
        private double _startedAt;
        private double _phaseStartedAt;
        private double _finishAt;
        private string _hostActorId;
        private int _relaysHeard;
        private int _callsExpected;
        private int _callsAnswered;
        private bool _echoChecked;
        private double _floodOwed;
        private SpikeRun _run;
        private int _runIndex = -1;
        private int _spikeConnectionId = int.MinValue;
        private bool _oversizeLogged;
        private bool _doneSent;
        private bool _wasConnected;
        private SpikeRun _clientRun;

        /// <summary>
        /// Creates the harness when the player was started with <see cref="RoleArgument"/>; does
        /// nothing otherwise.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            HarnessOptions options = HarnessOptions.FromCommandLine(Environment.GetCommandLineArgs());
            if (options == null)
            {
                return;
            }

            GameObject go = new("CoreAI Mirror Two-Process Harness");
            go.SetActive(false);
            CoreAiMirrorTwoProcessHarness harness = go.AddComponent<CoreAiMirrorTwoProcessHarness>();
            harness.Compose(go, options);
            go.SetActive(true);
        }

        private void Compose(GameObject go, HarnessOptions options)
        {
            _options = options;
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            KcpTransport transport = go.AddComponent<KcpTransport>();
            transport.Port = options.Port;
            _authenticator = go.AddComponent<CoreAiMirrorAuthenticator>();
            _authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes(options.Secret + ":" + options.Name));
            if (options.IsHost)
            {
                _authenticator.Configure(new HarnessAdmissionProvider(options.Secret), "coreai-harness", Log);
            }

            _manager = go.AddComponent<NetworkManager>();
            _manager.transport = transport;
            _manager.authenticator = _authenticator;
            _manager.autoCreatePlayer = false;
            _manager.headlessStartMode = HeadlessStartOptions.DoNothing;
            _manager.networkAddress = options.Address;
            _manager.maxConnections = options.Clients + 4;
            _manager.dontDestroyOnLoad = false;
            _manager.runInBackground = true;
            _provider = go.AddComponent<CoreAiMirrorNetworkBridgeProvider>();
            _provider.Role = options.IsHost ? CoreAiMirrorRole.Server : CoreAiMirrorRole.Client;
            _provider.Authenticator = _authenticator;
            _result.role = options.IsHost ? "host" : "client";
            _result.name = options.Name;
            _result.protocolVersion = CoreAiMirrorProtocol.Version;
        }

        private void Start()
        {
            _startedAt = Now();
            _phaseStartedAt = _startedAt;
            Application.logMessageReceived += OnLog;
            _bridge = (MirrorNetworkBridge)_provider.Bridge;
            _bridge.EventReceived += _options.IsHost ? OnHostEvent : OnClientEvent;
            _bridge.RequestReceived += _options.IsHost ? OnHostRequest : OnClientRequest;
            if (_options.IsHost)
            {
                _hostActorId = HarnessAdmissionProvider.ActorIdOf(_options.Name);
                _provider.AttachWorld(_ => true, _ => true);
                _manager.StartHost();
                NetworkServer.ReplaceHandler<CoreAiHarnessReceiptMessage>(OnReceipt);
            }
            else
            {
                _manager.StartClient();
                NetworkClient.ReplaceHandler<CoreAiHarnessChunkMessage>(OnChunk, requireAuthentication: false);
            }

            Log("[CoreAI.Mirror.Harness] " + _result.role + " '" + _options.Name + "' started on " + _options.Address
                + ":" + _options.Port.ToString(CultureInfo.InvariantCulture) + ", protocol " + CoreAiMirrorProtocol.Version);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        private void Update()
        {
            if (_phase == Phase.Done)
            {
                return;
            }

            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (_phase != Phase.Joining)
            {
                _frameMs.Add(frameMs);
            }

            SampleMemory();
            if (_phase != Phase.Finishing && Now() - _startedAt > _options.TimeoutSeconds)
            {
                Fail("the run did not finish within " + _options.TimeoutSeconds.ToString("0", CultureInfo.InvariantCulture)
                     + " s, in phase " + _phase);
                BeginFinishing();
            }

            if (_options.IsHost)
            {
                UpdateHost(frameMs);
            }
            else
            {
                UpdateClient();
            }
        }

        private void UpdateHost(float frameMs)
        {
            switch (_phase)
            {
                case Phase.Joining:
                    CoreAiMirrorSessionHost sessions = _provider.SessionHost;
                    if (sessions.LiveSessionCount >= _options.Clients + 1 && _bridge.ReadyAcknowledgements >= _options.Clients)
                    {
                        _result.secondsToReady = Now() - _startedAt;
                        Check(sessions.HostPlayerSessions == 1, "the host's own client is the host's Player (host mode)");
                        Check(sessions.Roster.Members.Count == _options.Clients + 1, "the host's roster lists every player");
                        _result.rosterMembers = sessions.Roster.Members.Count;
                        RecordTransportLimits();
                        _bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(ChatRemote), RbxNetworkDirection.ClientToServer,
                            RbxNetworkReliability.ReliableOrdered, _hostActorId, null, Encoding.UTF8.GetBytes("hello from host")));
                        CallEveryone();
                        Enter(Phase.Talking);
                    }

                    return;
                case Phase.Talking:
                    if (_relaysHeard >= _options.Clients + 1 && _callsAnswered >= _callsExpected)
                    {
                        Check(true, "every chat line reached the host's player, and every call was answered");
                        Advance(_options.FloodWatchSeconds > 0d ? Phase.Flooding : _options.Spike ? Phase.Spike : Phase.Finishing);
                    }

                    return;
                case Phase.Flooding:
                    _floodFrameMs.Add(frameMs);
                    if (Now() - _phaseStartedAt >= _options.FloodWatchSeconds)
                    {
                        _result.inboundMessagesDropped = _bridge.InboundMessagesDropped;
                        _result.inboundBytesDropped = _bridge.InboundBytesDropped;
                        _result.p99FrameMsDuringFlood = Percentile(_floodFrameMs, 0.99d);
                        Check(_result.p99FrameMsDuringFlood < 33d, "the host's p99 frame stayed under 33 ms during the flood window");
                        Check(_result.inboundMessagesDropped > 0, "the flood's excess was dropped and counted");
                        Advance(_options.Spike ? Phase.Spike : Phase.Finishing);
                    }

                    return;
                case Phase.Spike:
                    UpdateSpike();
                    return;
                case Phase.Finishing:
                    UpdateFinishing();
                    return;
            }
        }

        private void UpdateClient()
        {
            if (NetworkClient.isConnected)
            {
                _wasConnected = true;
            }

            if (_phase != Phase.Finishing && _authenticator.ClientRefusal.HasValue)
            {
                Fail("the server refused this client: " + _authenticator.ClientRefusal.Value.Reason);
                BeginFinishing();
            }
            else if (_phase != Phase.Finishing && _wasConnected && !NetworkClient.active)
            {
                Fail("the connection closed before the host said it was done"
                     + (_bridge.LastDisconnectNotice.HasValue ? ": " + _bridge.LastDisconnectNotice.Value.Message : ""));
                BeginFinishing();
            }

            switch (_phase)
            {
                case Phase.Joining:
                    if (_bridge.AdmittedActorId != null && _bridge.IsServerClockSynchronized
                                                        && _bridge.Roster.Members.Count >= _options.Clients + 1)
                    {
                        _result.secondsToReady = Now() - _startedAt;
                        CheckRoster();
                        RecordTransportLimits();
                        string self = _bridge.AdmittedActorId;
                        _bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(ChatRemote), RbxNetworkDirection.ClientToServer,
                            RbxNetworkReliability.ReliableOrdered, self, null, Encoding.UTF8.GetBytes("hello from " + _options.Name)));
                        _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(EchoRemote), RbxNetworkDirection.ClientToServer,
                            self, null, Encoding.UTF8.GetBytes(_options.Name)), response =>
                        {
                            _echoChecked = true;
                            Check(response.Succeeded && Encoding.UTF8.GetString(response.Payload ?? Array.Empty<byte>()) == Reverse(_options.Name),
                                "InvokeServer is answered with the server's reply");
                        });
                        Enter(Phase.Talking);
                    }

                    return;
                case Phase.Talking:
                    if (_relaysHeard >= _options.Clients + 1 && _echoChecked)
                    {
                        Check(true, "every chat line, the host's included, reached this client");
                        Advance(_options.FloodPerSecond > 0 ? Phase.Flooding : Phase.Waiting);
                    }

                    return;
                case Phase.Flooding:
                    Flood();
                    return;
                case Phase.Finishing:
                    UpdateFinishing();
                    return;
            }
        }

        /// <summary>
        /// Sends unreliable remotes straight to Mirror at the flood rate, around this client's own
        /// bridge and its rate limiter: the custom client the server's inbound limit exists for.
        /// </summary>
        private void Flood()
        {
            if (Now() - _phaseStartedAt >= _options.FloodSeconds)
            {
                Enter(Phase.Waiting);
                return;
            }

            _floodOwed += _options.FloodPerSecond * Time.unscaledDeltaTime;
            int count = (int)Math.Floor(_floodOwed);
            _floodOwed -= count;
            for (int index = 0; index < count && NetworkClient.isConnected; index++)
            {
                NetworkClient.Send(new CoreAiRemoteEventMessage
                {
                    RemoteId = FloodRemote,
                    Direction = (byte)RbxNetworkDirection.ClientToServer,
                    Reliability = (byte)RbxNetworkReliability.UnreliableUnordered,
                    Payload = new byte[8]
                }, Channels.Unreliable);
                _result.floodSent++;
            }
        }

        private void CallEveryone()
        {
            IReadOnlyList<string> actors = _bridge.ActorIds;
            for (int index = 0; index < actors.Count; index++)
            {
                string actor = actors[index];
                _callsExpected++;
                _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(EchoRemote), RbxNetworkDirection.ServerToClient,
                    null, actor, Encoding.UTF8.GetBytes("who are you")), response =>
                {
                    _callsAnswered++;
                    Check(response.Succeeded && Encoding.UTF8.GetString(response.Payload ?? Array.Empty<byte>()) == actor,
                        "InvokeClient on " + actor + " is answered by that player");
                });
            }

            _callsExpected++;
            _bridge.SendRequest(new RbxNetworkRequestMessage(new InstanceId(EchoRemote), RbxNetworkDirection.ClientToServer,
                _hostActorId, null, Encoding.UTF8.GetBytes(_options.Name)), response =>
            {
                _callsAnswered++;
                Check(response.Succeeded && Encoding.UTF8.GetString(response.Payload ?? Array.Empty<byte>()) == Reverse(_options.Name),
                    "the host player's InvokeServer is answered in process");
            });
        }

        private void OnHostEvent(RbxNetworkEventMessage message)
        {
            if (message.Direction == RbxNetworkDirection.ClientToServer && message.RemoteId.Value == ChatRemote)
            {
                string line = message.SenderActorId + "|" + Encoding.UTF8.GetString(message.Payload ?? Array.Empty<byte>());
                _bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(RelayRemote), RbxNetworkDirection.ServerToAllClients,
                    RbxNetworkReliability.ReliableOrdered, null, null, Encoding.UTF8.GetBytes(line)));
                return;
            }

            if (message.Direction == RbxNetworkDirection.ClientToServer && message.RemoteId.Value == FloodRemote)
            {
                _result.floodAccepted++;
                return;
            }

            if (message.RemoteId.Value == RelayRemote && message.RecipientActorId == _hostActorId)
            {
                _relaysHeard++;
            }
        }

        private void OnHostRequest(RbxNetworkRequestMessage message, RbxNetworkRequestResponder responder)
        {
            if (message.RemoteId.Value != EchoRemote)
            {
                responder.Fail("unknown remote");
                return;
            }

            responder.Complete(message.Direction == RbxNetworkDirection.ClientToServer
                ? Encoding.UTF8.GetBytes(Reverse(Encoding.UTF8.GetString(message.Payload ?? Array.Empty<byte>())))
                : Encoding.UTF8.GetBytes(_hostActorId));
        }

        private void OnClientEvent(RbxNetworkEventMessage message)
        {
            if (message.RemoteId.Value == RelayRemote)
            {
                _relaysHeard++;
                _result.chatLines.Add(Encoding.UTF8.GetString(message.Payload ?? Array.Empty<byte>()));
                return;
            }

            if (message.RemoteId.Value == DoneRemote && _phase != Phase.Finishing)
            {
                BeginFinishing();
            }
        }

        private void OnClientRequest(RbxNetworkRequestMessage message, RbxNetworkRequestResponder responder)
        {
            responder.Complete(Encoding.UTF8.GetBytes(_bridge.AdmittedActorId ?? ""));
        }

        private void CheckRoster()
        {
            IReadOnlyList<CoreAiRosterMember> members = _bridge.Roster.Members;
            int hosts = 0;
            for (int index = 0; index < members.Count; index++)
            {
                hosts += members[index].IsHost ? 1 : 0;
                _result.roster.Add(members[index].Name + (members[index].IsHost ? " (host)" : ""));
            }

            _result.rosterMembers = members.Count;
            Check(members.Count == _options.Clients + 1, "the roster lists the host and every client");
            Check(hosts == 1, "exactly one roster member is the host");
            Check(_bridge.Roster.TryGetSelf(out CoreAiRosterMember self) && !self.IsHost, "the roster names this client, not as the host");
        }

        private void UpdateSpike()
        {
            if (_run == null)
            {
                _runIndex++;
                if (_runIndex >= SpikeChunkSizes.Length)
                {
                    ProbeOversize();
                    BeginFinishing();
                    return;
                }

                StartRun(_runIndex);
                return;
            }

            if (Now() - _run.StartedAt > 60d)
            {
                Fail("spike run " + _run.Index + " (" + _run.ChunkBytes + "-byte chunks) got no receipt within 60 s");
                _run = null;
                _runIndex = SpikeChunkSizes.Length;
            }
        }

        private void StartRun(int index)
        {
            if (!TryFindSpikeConnection(out NetworkConnectionToClient conn))
            {
                Fail("no remote client to run the spike against");
                _runIndex = SpikeChunkSizes.Length;
                return;
            }

            int largest = NetworkMessages.MaxContentSize(Channels.Reliable) - ChunkEnvelopeBytes;
            int chunkBytes = SpikeChunkSizes[index] == 0 ? largest : Math.Min(SpikeChunkSizes[index], largest);
            int count = (SpikeTotalBytes + chunkBytes - 1) / chunkBytes;
            _run = new SpikeRun { Index = index, ChunkBytes = chunkBytes, Count = count, StartedAt = Now() };
            _run.Result.chunkBytes = chunkBytes;
            _run.Result.chunks = count;
            _run.Result.totalBytes = SpikeTotalBytes;
            byte[] full = new byte[chunkBytes];
            for (int position = 0; position < full.Length; position++)
            {
                full[position] = (byte)position;
            }

            int remaining = SpikeTotalBytes;
            for (int sequence = 0; sequence < count; sequence++)
            {
                int size = Math.Min(chunkBytes, remaining);
                byte[] bytes = size == chunkBytes ? full : new byte[size];
                conn.Send(new CoreAiHarnessChunkMessage { Run = index, Sequence = sequence, Count = count, Bytes = bytes },
                    Channels.Reliable);
                remaining -= size;
            }

            _run.Result.hostSendSeconds = Now() - _run.StartedAt;
        }

        private void OnReceipt(NetworkConnectionToClient conn, CoreAiHarnessReceiptMessage receipt)
        {
            if (_run == null || receipt.Run != _run.Index)
            {
                return;
            }

            _run.Result.hostSeconds = Now() - _run.StartedAt;
            _run.Result.clientSeconds = receipt.Seconds;
            _run.Result.clientReceivedBytes = receipt.Bytes;
            _run.Result.clientPeakManagedBytes = receipt.PeakManagedBytes;
            _run.Result.clientPeakUnityAllocatedBytes = receipt.PeakUnityAllocatedBytes;
            _run.Result.hostPeakManagedBytes = _result.peakManagedBytes;
            _run.Result.hostPeakUnityAllocatedBytes = _result.peakUnityAllocatedBytes;
            Check(receipt.Received == _run.Count && receipt.Bytes == SpikeTotalBytes,
                "spike run " + _run.Index + " (" + _run.ChunkBytes + "-byte chunks) arrived whole");
            _result.spike.Add(_run.Result);
            _run = null;
        }

        private void OnChunk(CoreAiHarnessChunkMessage chunk)
        {
            if (_clientRun == null || _clientRun.Index != chunk.Run)
            {
                _clientRun = new SpikeRun { Index = chunk.Run, Count = chunk.Count, StartedAt = Now() };
                _clientRun.PeakManagedBytes = 0L;
            }

            _clientRun.Received++;
            _clientRun.ReceivedBytes += chunk.Bytes?.Length ?? 0;
            _clientRun.PeakManagedBytes = Math.Max(_clientRun.PeakManagedBytes, GC.GetTotalMemory(false));
            _clientRun.PeakUnityAllocatedBytes = Math.Max(_clientRun.PeakUnityAllocatedBytes, Profiler.GetTotalAllocatedMemoryLong());
            if (_clientRun.Received < _clientRun.Count)
            {
                return;
            }

            NetworkClient.Send(new CoreAiHarnessReceiptMessage
            {
                Run = _clientRun.Index,
                Received = _clientRun.Received,
                Bytes = _clientRun.ReceivedBytes,
                Seconds = Now() - _clientRun.StartedAt,
                PeakManagedBytes = _clientRun.PeakManagedBytes,
                PeakUnityAllocatedBytes = _clientRun.PeakUnityAllocatedBytes
            });
            _result.spikeRunsReceived++;
            _clientRun = null;
        }

        /// <summary>
        /// Hands Mirror one slice a byte larger than the largest the reliable channel carries, and
        /// records whether Mirror refused it with its size error.
        /// </summary>
        private void ProbeOversize()
        {
            if (!TryFindSpikeConnection(out NetworkConnectionToClient conn))
            {
                return;
            }

            int content = NetworkMessages.MaxContentSize(Channels.Reliable);
            _oversizeLogged = false;
            conn.Send(new CoreAiHarnessChunkMessage { Run = -1, Sequence = 0, Count = 1, Bytes = new byte[content + 1] },
                Channels.Reliable);
            _result.oversizeRefusedByMirror = _oversizeLogged;
            Check(_oversizeLogged, "Mirror refuses a message larger than kcp's reliable message, with its size error");
        }

        private bool TryFindSpikeConnection(out NetworkConnectionToClient found)
        {
            if (NetworkServer.connections.TryGetValue(_spikeConnectionId, out found))
            {
                return true;
            }

            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                if (!(conn is LocalConnectionToClient) && conn.isAuthenticated)
                {
                    _spikeConnectionId = conn.connectionId;
                    found = conn;
                    return true;
                }
            }

            found = null;
            return false;
        }

        private void RecordTransportLimits()
        {
            _result.reliableMaxPacketSize = Transport.active.GetMaxPacketSize(Channels.Reliable);
            _result.unreliableMaxPacketSize = Transport.active.GetMaxPacketSize(Channels.Unreliable);
            _result.reliableMaxContentSize = NetworkMessages.MaxContentSize(Channels.Reliable);
            _result.bridgeReliablePayloadCeiling = _bridge.MaxPayloadBytes;
            _result.bridgeUnreliablePayloadCeiling = _bridge.MaxPayloadBytesFor(RbxNetworkReliability.UnreliableUnordered);
        }

        private void BeginFinishing()
        {
            if (_phase == Phase.Finishing || _phase == Phase.Done)
            {
                return;
            }

            Enter(Phase.Finishing);
            // WHY the host lingers longer: its done line is queued behind whatever kcp still carries,
            // and a client that saw the host leave first would report a lost connection.
            _finishAt = Now() + (_options.IsHost ? 3d : 1d);
            if (_options.IsHost && !_doneSent && NetworkServer.active)
            {
                _doneSent = true;
                _bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(DoneRemote), RbxNetworkDirection.ServerToAllClients,
                    RbxNetworkReliability.ReliableOrdered, null, null, Array.Empty<byte>()));
            }
        }

        /// <summary>Writes the result once the finishing delay has passed, stops Mirror and quits.</summary>
        private void UpdateFinishing()
        {
            if (Now() < _finishAt)
            {
                return;
            }

            _phase = Phase.Done;
            _result.p99FrameMs = Percentile(_frameMs, 0.99d);
            _result.frames = _frameMs.Count;
            _result.seconds = Now() - _startedAt;
            _result.peakWorkingSetBytes = PeakWorkingSet();
            if (_options.IsHost)
            {
                _result.inboundMessagesDropped = _bridge.InboundMessagesDropped;
                _result.inboundBytesDropped = _bridge.InboundBytesDropped;
                _result.readyAcknowledgements = _bridge.ReadyAcknowledgements;
            }

            _result.relaysHeard = _relaysHeard;
            _result.passed = _result.failures.Count == 0;
            string json = JsonUtility.ToJson(_result, true);
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(_options.Output));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_options.Output, json);
            }
            catch (Exception exception)
            {
                Debug.LogError("[CoreAI.Mirror.Harness] the result could not be written to " + _options.Output + ": " + exception.Message);
            }

            Debug.Log(ResultLogPrefix + JsonUtility.ToJson(_result, false));
            if (_options.IsHost)
            {
                _manager.StopHost();
            }
            else
            {
                _manager.StopClient();
            }

            Application.Quit(_result.passed ? 0 : 1);
        }

        private void Enter(Phase phase)
        {
            _phase = phase;
            _phaseStartedAt = Now();
        }

        /// <summary>Moves to the next phase; finishing always goes through <see cref="BeginFinishing"/>.</summary>
        private void Advance(Phase next)
        {
            if (next == Phase.Finishing)
            {
                BeginFinishing();
                return;
            }

            Enter(next);
        }

        private void Check(bool passed, string what)
        {
            if (passed)
            {
                _result.checks.Add("PASS " + what);
                return;
            }

            Fail(what);
        }

        private void Fail(string what)
        {
            _result.checks.Add("FAIL " + what);
            _result.failures.Add(what);
            Debug.LogError("[CoreAI.Mirror.Harness] FAIL " + what);
        }

        private void SampleMemory()
        {
            _result.peakManagedBytes = Math.Max(_result.peakManagedBytes, GC.GetTotalMemory(false));
            _result.peakUnityAllocatedBytes = Math.Max(_result.peakUnityAllocatedBytes, Profiler.GetTotalAllocatedMemoryLong());
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (condition != null && condition.Contains(OversizeLogMarker))
            {
                _oversizeLogged = true;
            }
        }

        private static long PeakWorkingSet()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
            }
            catch (Exception)
            {
                // WHY swallowed: a scripting backend without process counters still has the Unity
                // and managed peaks the result carries.
                return -1L;
            }
        }

        private static double Percentile(List<float> samples, double fraction)
        {
            if (samples.Count == 0)
            {
                return 0d;
            }

            List<float> sorted = new(samples);
            sorted.Sort();
            int index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }

        private static string Reverse(string text)
        {
            char[] chars = (text ?? "").ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        private static double Now()
        {
            return Time.realtimeSinceStartupAsDouble;
        }

        private static void Log(string line)
        {
            Debug.Log(line);
        }

        /// <summary>One run of the spike in flight, on either side.</summary>
        private sealed class SpikeRun
        {
            public int Index;
            public int ChunkBytes;
            public int Count;
            public int Received;
            public long ReceivedBytes;
            public double StartedAt;
            public long PeakManagedBytes;
            public long PeakUnityAllocatedBytes;
            public readonly HarnessSpikeResult Result = new();
        }

        /// <summary>Admits any credential of the form <c>secret:name</c> as the actor <c>harness-name</c>.</summary>
        private sealed class HarnessAdmissionProvider : IActorAdmissionProvider
        {
            private readonly string _prefix;
            private long _nextUserId = 1001L;

            public HarnessAdmissionProvider(string secret)
            {
                _prefix = secret + ":";
            }

            public static string ActorIdOf(string name) => "harness-" + name;

            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                string offered = Encoding.UTF8.GetString(credential.Opaque ?? Array.Empty<byte>());
                if (!offered.StartsWith(_prefix, StringComparison.Ordinal) || offered.Length == _prefix.Length)
                {
                    return ActorAdmissionResult.Reject("the harness credential is wrong");
                }

                string name = offered.Substring(_prefix.Length);
                ActorContext context = new LocalActorIdentityProvider(
                        ActorIdOf(name), "session-" + name, worldId, ActorGrantSet.Create(new[] { "read" }),
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.SmartChat);
                return ActorAdmissionResult.Admit(context, _nextUserId++, name, name);
            }
        }
    }

    /// <summary>What one harness process was started to do, read from its command line.</summary>
    internal sealed class HarnessOptions
    {
        public bool IsHost;
        public string Name = "host";
        public string Address = "127.0.0.1";
        public ushort Port = 7777;
        public int Clients = 2;
        public string Secret = "coreai-harness";
        public int FloodPerSecond;
        public double FloodSeconds = 5d;
        public double FloodWatchSeconds;
        public bool Spike;
        public double TimeoutSeconds = 180d;
        public string Output;

        /// <summary>The options, or null when the player was not started as a harness process.</summary>
        public static HarnessOptions FromCommandLine(string[] args)
        {
            string role = Value(args, CoreAiMirrorTwoProcessHarness.RoleArgument);
            if (role == null)
            {
                return null;
            }

            HarnessOptions options = new() { IsHost = string.Equals(role, "host", StringComparison.OrdinalIgnoreCase) };
            options.Name = Value(args, "-coreaiHarnessName") ?? (options.IsHost ? "host" : "client");
            options.Address = Value(args, "-coreaiHarnessAddress") ?? options.Address;
            options.Port = (ushort)Number(args, "-coreaiHarnessPort", options.Port);
            options.Clients = (int)Number(args, "-coreaiHarnessClients", options.Clients);
            options.Secret = Value(args, "-coreaiHarnessSecret") ?? options.Secret;
            options.FloodPerSecond = (int)Number(args, "-coreaiHarnessFlood", 0d);
            options.FloodSeconds = Number(args, "-coreaiHarnessFloodSeconds", options.FloodSeconds);
            options.FloodWatchSeconds = Number(args, "-coreaiHarnessFloodWatch", 0d);
            options.Spike = Array.IndexOf(args, "-coreaiHarnessS3") >= 0;
            options.TimeoutSeconds = Number(args, "-coreaiHarnessTimeout", options.TimeoutSeconds);
            options.Output = Value(args, "-coreaiHarnessOut")
                             ?? "coreai-mirror-harness-" + (options.IsHost ? "host" : options.Name) + ".json";
            return options;
        }

        private static string Value(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static double Number(string[] args, string name, double fallback)
        {
            string text = Value(args, name);
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : fallback;
        }
    }

    /// <summary>One harness process's result, written as JSON.</summary>
    [Serializable]
    internal sealed class HarnessResult
    {
        public string role;
        public string name;
        public int protocolVersion;
        public bool passed;
        public List<string> checks = new();
        public List<string> failures = new();
        public double seconds;
        public double secondsToReady;
        public int rosterMembers;
        public List<string> roster = new();
        public int relaysHeard;
        public List<string> chatLines = new();
        public int readyAcknowledgements;
        public int frames;
        public double p99FrameMs;
        public double p99FrameMsDuringFlood;
        public int floodSent;
        public int floodAccepted;
        public int inboundMessagesDropped;
        public long inboundBytesDropped;
        public int reliableMaxPacketSize;
        public int unreliableMaxPacketSize;
        public int reliableMaxContentSize;
        public int bridgeReliablePayloadCeiling;
        public int bridgeUnreliablePayloadCeiling;
        public bool oversizeRefusedByMirror;
        public int spikeRunsReceived;
        public List<HarnessSpikeResult> spike = new();
        public long peakManagedBytes;
        public long peakUnityAllocatedBytes;
        public long peakWorkingSetBytes;
    }

    /// <summary>One run of the spike S3: 5 MiB in slices of one size.</summary>
    [Serializable]
    internal sealed class HarnessSpikeResult
    {
        public int chunkBytes;
        public int chunks;
        public long totalBytes;
        public double hostSendSeconds;
        public double hostSeconds;
        public double clientSeconds;
        public long clientReceivedBytes;
        public long hostPeakManagedBytes;
        public long hostPeakUnityAllocatedBytes;
        public long clientPeakManagedBytes;
        public long clientPeakUnityAllocatedBytes;
    }
}
