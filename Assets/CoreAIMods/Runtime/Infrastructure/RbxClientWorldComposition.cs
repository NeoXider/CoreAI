using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>What a client composition is built from (MVP4 SC-16, step C5).</summary>
    public sealed class RbxClientWorldCompositionOptions
    {
        /// <summary>The actor of the player at this client's screen; its scripts act as that player.</summary>
        public string LocalActorId;

        /// <summary>The client's bridge to the server; its topology must be <see cref="RbxNetworkTopology.Client"/>.</summary>
        public INetworkBridge NetworkBridge;

        /// <summary>The host logger the gameplay bindings write to; required.</summary>
        public IGameLogger Logger;

        /// <summary>Optional mod log, where <c>print</c>, <c>warn</c> and errors of the client's scripts go.</summary>
        public ILuaLogService LogService;

        /// <summary>Optional world log of the Roblox surface.</summary>
        public Action<string> Log;

        /// <summary>The tiers a script of the payload may hold at most; <c>Full</c> is never granted.</summary>
        public LuaCapabilities HostGrant = LuaCapabilities.All;
    }

    /// <summary>
    /// A client process's world (MVP4 SC-16, step C5): the join payload staged into a replica registry through
    /// <see cref="ReplicaRbxWorldSessionHost"/> (which refuses server and legacy sources), the Roblox surface over it with a
    /// client topology and this screen's player as the local player, and a mod runtime over an in-memory copy of the
    /// payload's sources that persists nothing, takes no world-mutation gate and offers no <c>manage_mods</c> writes. Its
    /// client scripts start in order once the replica holds the local player (SC-9 item 6); its modules wait for a
    /// <c>require</c>. Engine-free; MVP5 carries the payload over a socket and MVP6 the deltas.
    /// </summary>
    public sealed class RbxClientWorldComposition : IDisposable
    {
        private RbxClientWorldComposition(ReplicaRbxWorldSessionHost host, LuaCsRbxApiBindings roblox,
            LuaCsModStack stack, int started, IReadOnlyList<string> notStarted)
        {
            Host = host;
            Roblox = roblox;
            Stack = stack;
            Started = started;
            NotStarted = notStarted;
        }

        /// <summary>The client's session host; its registry is the replica the payload was staged into.</summary>
        public ReplicaRbxWorldSessionHost Host { get; }

        public InstanceRegistry Registry => Host.Registry;

        public RbxDataModel Game => Host.Game;

        /// <summary>The Roblox surface the client's scripts run against.</summary>
        public LuaCsRbxApiBindings Roblox { get; }

        /// <summary>The client's mod stack.</summary>
        public LuaCsModStack Stack { get; }

        /// <summary>The scripts whose main chunk ran while the composition started.</summary>
        public int Started { get; }

        /// <summary>
        /// Every source registered without a run, with its run state: the modules, and client scripts waiting for the
        /// local player (<c>hud (waiting-for-local-player)</c>).
        /// </summary>
        public IReadOnlyList<string> NotStarted { get; }

        /// <summary>
        /// Stages <paramref name="joinPayload"/> (from <see cref="RbxClientJoinPayload.CaptureForRecipient"/>) as this
        /// client's world and starts its scripts. Throws <see cref="RbxWorldPackageException"/> for a payload that holds
        /// server content, before anything is restored.
        /// </summary>
        public static RbxClientWorldComposition Join(RbxWorldPackagePayload joinPayload,
            RbxClientWorldCompositionOptions options)
        {
            if (joinPayload == null)
            {
                throw new ArgumentNullException(nameof(joinPayload));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.LocalActorId))
            {
                throw new ArgumentException("A client composition needs the actor of its local player.",
                    nameof(options));
            }

            if (options.NetworkBridge == null || options.NetworkBridge.Topology != RbxNetworkTopology.Client)
            {
                throw new ArgumentException("A client composition runs over a bridge whose topology is Client.",
                    nameof(options));
            }

            if (options.Logger == null)
            {
                throw new ArgumentException("A client composition needs a host logger.", nameof(options));
            }

            InstanceRegistry placeholder = new(worldId: joinPayload.Settings?.WorldId,
                authority: RegistryAuthority.Replica);
            ReplicaRbxWorldSessionHost host = new(placeholder, DataModelBootstrap.CreateGame(placeholder),
                joinPayload.Settings);
            IRbxWorldSessionCandidate candidate = host.Stage(joinPayload);
            LuaCsRbxApiBindings roblox = null;
            try
            {
                candidate.Commit();
                roblox = new LuaCsRbxApiBindings(registry: host.Registry, game: host.Game, log: options.Log,
                    partSink: host.PartSink, cameraRig: host.CameraRig, networkBridge: options.NetworkBridge)
                {
                    LocalPlayerActorId = options.LocalActorId
                };
                LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = options.Logger,
                    RbxApi = roblox,
                    ModSourceStore = new PayloadSourceStore(joinPayload.Mods),
                    AutoPersistMods = false,
                    LogService = options.LogService
                });

                // WHY not legacy placement: a client payload holds only declared sources, each bound to its instance.
                int started = stack.Runtime.RehydrateExactOrThrow(options.HostGrant, false, false, false,
                    out IReadOnlyList<string> notStarted);
                return new RbxClientWorldComposition(host, roblox, stack, started, notStarted);
            }
            catch
            {
                roblox?.Dispose();
                candidate.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Roblox.Dispose();
        }

        /// <summary>The payload's sources, held in memory for the client's runtime; nothing is ever written to disk.</summary>
        private sealed class PayloadSourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

            public PayloadSourceStore(IReadOnlyList<RbxWorldModSource> mods)
            {
                if (mods == null)
                {
                    return;
                }

                for (int index = 0; index < mods.Count; index++)
                {
                    RbxWorldModSource mod = mods[index];
                    if (mod?.Manifest != null && !string.IsNullOrWhiteSpace(mod.Manifest.Id))
                    {
                        _entries[mod.Manifest.Id] = (mod.Source, mod.Manifest);
                    }
                }
            }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = (source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id ?? "", out (string Source, LuaModManifest Manifest) entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = null;
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach ((string Source, LuaModManifest Manifest) entry in _entries.Values)
                {
                    manifests.Add(entry.Manifest);
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id ?? "", out (string Source, LuaModManifest Manifest) entry))
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id ?? "");
            }
        }
    }
}
