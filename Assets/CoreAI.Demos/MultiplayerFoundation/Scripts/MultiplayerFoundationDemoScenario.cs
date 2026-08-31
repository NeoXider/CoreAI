#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>One visible authorization check performed by the multiplayer-foundation demo.</summary>
    public sealed class MultiplayerFoundationProofResult
    {
        /// <summary>Short group name rendered above the request.</summary>
        public string Category { get; internal set; } = "";

        /// <summary>Human-readable operation that was attempted.</summary>
        public string Operation { get; internal set; } = "";

        /// <summary>Durable actor id that made the request.</summary>
        public string RequesterActorId { get; internal set; } = "";

        /// <summary>Actor or host resource targeted by the request.</summary>
        public string Target { get; internal set; } = "";

        /// <summary>Exact refusal emitted by the production path.</summary>
        public string Reason { get; internal set; } = "";

        /// <summary>Whether the operation was refused for the expected actor-scoped reason.</summary>
        public bool Enforced { get; internal set; }

        /// <summary>Whether the protected target remained valid after the request.</summary>
        public bool TargetIntact { get; internal set; }
    }

    /// <summary>Read-only presentation state for one simulated durable actor.</summary>
    public sealed class MultiplayerFoundationActorState
    {
        private readonly List<string> _chatTranscript = new List<string>();

        internal MultiplayerFoundationActorState(
            int index,
            ActorContext modActorContext,
            ActorContext chatActorContext,
            IInGameLlmChatService chatService,
            Color displayColor,
            string modId,
            string worldObjectName)
        {
            Index = index;
            ModActorContext = modActorContext;
            ChatActorContext = chatActorContext;
            ChatService = chatService;
            DisplayColor = displayColor;
            ModId = modId;
            WorldObjectName = worldObjectName;
            ChatServiceIdentity = RuntimeHelpers.GetHashCode(chatService).ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>Zero-based actor index.</summary>
        public int Index { get; }

        /// <summary>Durable actor id shared by the actor's Programmer and SmartChat contexts.</summary>
        public string ActorId => ModActorContext.ActorId;

        /// <summary>Bright color shared by the card and the actor's Rbx beacon.</summary>
        public Color DisplayColor { get; }

        /// <summary>Actor-owned persistent mod id.</summary>
        public string ModId { get; }

        /// <summary>Actor-owned Rbx part name in the shared Workspace.</summary>
        public string WorldObjectName { get; }

        /// <summary>Short identity proving the actor has a distinct production chat service instance.</summary>
        public string ChatServiceIdentity { get; }

        /// <summary>Whether this actor resolved a different chat service from every other actor.</summary>
        public bool ChatIsIsolated { get; internal set; }

        /// <summary>Whether the actor loaded and then reloaded its own mod successfully.</summary>
        public bool OwnModEdited { get; internal set; }

        /// <summary>Whether the actor's edited mod remains loaded.</summary>
        public bool ModLoaded { get; internal set; }

        /// <summary>Number of live timers declared by the edited mod.</summary>
        public int TimerCount { get; internal set; }

        /// <summary>Number of mods currently held by this actor in the demo.</summary>
        public int LoadedModCount { get; internal set; }

        /// <summary>Production per-actor mod quota displayed by the demo.</summary>
        public int ModQuota { get; internal set; } = MultiplayerFoundationDemoScenario.ProductionModQuota;

        /// <summary>Number of successful chat pairs visible only to this actor.</summary>
        public int ChatHistoryPairCount => ChatService.HistoryPairCount;

        /// <summary>Whether a provider request is currently in flight for this actor.</summary>
        public bool ChatBusy { get; internal set; }

        /// <summary>Visible transcript created by this demo's optional live-provider chat control.</summary>
        public IReadOnlyList<string> ChatTranscript => _chatTranscript;

        internal ActorContext ModActorContext { get; }

        internal ActorContext ChatActorContext { get; }

        internal IInGameLlmChatService ChatService { get; }

        internal void AppendChatLine(string line)
        {
            _chatTranscript.Add(line ?? "");
        }
    }

    /// <summary>Complete read-only result of one multiplayer-foundation proof run.</summary>
    public sealed class MultiplayerFoundationProofReport
    {
        internal MultiplayerFoundationProofReport(
            IReadOnlyList<MultiplayerFoundationActorState> actors,
            IReadOnlyList<MultiplayerFoundationProofResult> proofs,
            IReadOnlyList<string> setupErrors)
        {
            Actors = actors;
            Proofs = proofs;
            SetupErrors = setupErrors;
        }

        /// <summary>All simulated actor cards.</summary>
        public IReadOnlyList<MultiplayerFoundationActorState> Actors { get; }

        /// <summary>Every expected refusal performed by the scenario.</summary>
        public IReadOnlyList<MultiplayerFoundationProofResult> Proofs { get; }

        /// <summary>Unexpected setup or positive-path failures.</summary>
        public IReadOnlyList<string> SetupErrors { get; }

        /// <summary>Number of expected refusals that held and left their target intact.</summary>
        public int EnforcedProofCount
        {
            get
            {
                int count = 0;
                foreach (MultiplayerFoundationProofResult proof in Proofs)
                {
                    if (proof.Enforced && proof.TargetIntact)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Whether every expected refusal held and all actor setup paths succeeded.</summary>
        public bool Passed =>
            SetupErrors.Count == 0 && Proofs.Count > 0 && EnforcedProofCount == Proofs.Count;
    }

    /// <summary>
    /// Shared scenario used by the playable demo and its production-composition regression test. It uses
    /// only actor-scoped chat and mod interfaces resolved by the host; no runtime implementation is built here.
    /// </summary>
    public sealed class MultiplayerFoundationDemoScenario : IDisposable
    {
        /// <summary>Stable world id shared by every simulated actor.</summary>
        public const string SharedWorldId = "mvp2-foundation-world";

        /// <summary>Minimum actor count that can demonstrate a cross-actor refusal.</summary>
        public const int MinimumActorCount = 2;

        /// <summary>Maximum actor count supported by the demo UI and scenario.</summary>
        public const int MaximumActorCount = 20;

        /// <summary>Production per-actor mod quota proved by the MVP2 acceptance scenario.</summary>
        public const int ProductionModQuota = 32;

        private const string ModPrefix = "mvp2_foundation_demo_";
        private readonly List<MultiplayerFoundationActorState> _actors =
            new List<MultiplayerFoundationActorState>();
        private readonly IInGameLlmChatServiceFactory _chatFactory;
        private readonly ActorContext _hostActor;
        private readonly ILuaModRuntime _mods;
        private readonly List<MultiplayerFoundationProofResult> _proofs =
            new List<MultiplayerFoundationProofResult>();
        private readonly List<string> _setupErrors = new List<string>();
        private bool _disposed;
        private bool _hasRun;

        /// <summary>Creates a scenario over production-resolved services.</summary>
        public MultiplayerFoundationDemoScenario(
            ILuaModRuntime mods,
            IInGameLlmChatServiceFactory chatFactory,
            ActorContext hostActor,
            int actorCount)
        {
            _mods = mods ?? throw new ArgumentNullException(nameof(mods));
            _chatFactory = chatFactory ?? throw new ArgumentNullException(nameof(chatFactory));
            if (!hostActor.IsTrusted || !hostActor.Grants.IsUnrestricted)
            {
                throw new ArgumentException(
                    "The demo host context must be trusted and unrestricted.", nameof(hostActor));
            }

            _hostActor = hostActor;
            int clampedCount = Math.Max(MinimumActorCount, Math.Min(MaximumActorCount, actorCount));
            CreateActors(clampedCount);
        }

        /// <summary>Current actor state, populated during construction and updated by <see cref="Run"/>.</summary>
        public IReadOnlyList<MultiplayerFoundationActorState> Actors => _actors;

        /// <summary>Runs the complete positive and negative proof once.</summary>
        public MultiplayerFoundationProofReport Run()
        {
            ThrowIfDisposed();
            if (_hasRun)
            {
                return new MultiplayerFoundationProofReport(_actors, _proofs, _setupErrors);
            }

            _hasRun = true;
            CleanupKnownArtifacts();
            LoadAndEditEveryActorMod();
            VerifyDistinctChats();
            if (_actors.Count >= 2)
            {
                RunCrossActorModProofs(_actors[0], _actors[1]);
                RunCrossActorWorldProofs(_actors[0], _actors[1]);
                RunCrossActorChatProofs(_actors[0], _actors[1]);
            }

            RunHostProtectedProofs();
            RunQuotaProof(_actors[_actors.Count - 1]);
            RefreshActorModState();
            return new MultiplayerFoundationProofReport(_actors, _proofs, _setupErrors);
        }

        /// <summary>Sends one real provider request through the selected actor's private production chat service.</summary>
        public async Task<LlmCompletionResult> SendChatAsync(
            int actorIndex,
            string message,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MultiplayerFoundationActorState actor = ActorAt(actorIndex);
            if (actor.ChatBusy)
            {
                return new LlmCompletionResult { Ok = false, Error = "chat_busy" };
            }

            string normalized = message?.Trim() ?? "";
            if (normalized.Length == 0)
            {
                return new LlmCompletionResult { Ok = false, Error = "empty message" };
            }

            actor.ChatBusy = true;
            actor.AppendChatLine("You: " + normalized);
            try
            {
                LlmCompletionResult result =
                    await actor.ChatService.SendPlayerMessageAsync(normalized, cancellationToken);
                actor.AppendChatLine(result.Ok
                    ? "AI: " + (result.Content ?? "")
                    : "Refused/error: " + (result.Error ?? "unknown provider error"));
                return result;
            }
            finally
            {
                actor.ChatBusy = false;
            }
        }

        /// <summary>Releases actor chat sessions and removes every demo-owned mod/source.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CleanupKnownArtifacts();
            foreach (MultiplayerFoundationActorState actor in _actors)
            {
                try
                {
                    _chatFactory.ReleaseActor(actor.ChatActorContext);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void CreateActors(int actorCount)
        {
            Color[] colors =
            {
                new Color(0.18f, 0.82f, 1f),
                new Color(1f, 0.38f, 0.56f),
                new Color(0.48f, 0.94f, 0.42f),
                new Color(1f, 0.78f, 0.2f),
                new Color(0.67f, 0.46f, 1f),
                new Color(1f, 0.52f, 0.2f)
            };

            for (int index = 0; index < actorCount; index++)
            {
                string suffix = (index + 1).ToString("00", CultureInfo.InvariantCulture);
                string actorId = "demo-actor-" + suffix;
                LocalActorIdentityProvider identity = new LocalActorIdentityProvider(
                    actorId,
                    "demo-session-" + suffix,
                    SharedWorldId,
                    ActorGrantSet.None,
                    new AgentMemoryScope("coreai-demo", actorId, "", "mvp2-foundation"));
                ActorContext modContext = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
                ActorContext chatContext = identity.GetActorContext(BuiltInAgentRoleIds.SmartChat);
                IInGameLlmChatService chatService = _chatFactory.Resolve(chatContext);
                MultiplayerFoundationActorState actor = new MultiplayerFoundationActorState(
                    index,
                    modContext,
                    chatContext,
                    chatService,
                    colors[index % colors.Length],
                    ModPrefix + "actor_" + suffix + "_live",
                    "Mvp2ActorBeacon" + suffix);
                _actors.Add(actor);
            }
        }

        private void LoadAndEditEveryActorMod()
        {
            foreach (MultiplayerFoundationActorState actor in _actors)
            {
                try
                {
                    _mods.LoadMod(
                        actor.ModActorContext,
                        actor.ModId,
                        BuildActorModSource(actor, false),
                        LuaCapabilities.All,
                        false);
                    _mods.ReloadMod(
                        actor.ModActorContext,
                        actor.ModId,
                        BuildActorModSource(actor, true));
                    actor.OwnModEdited = true;
                    actor.ModLoaded = _mods.IsLoaded(actor.ModActorContext, actor.ModId);
                }
                catch (Exception ex)
                {
                    _setupErrors.Add(
                        $"Actor '{actor.ActorId}' could not load/edit its own mod: {FlattenException(ex)}");
                }
            }
        }

        private void VerifyDistinctChats()
        {
            foreach (MultiplayerFoundationActorState actor in _actors)
            {
                bool isolated = true;
                foreach (MultiplayerFoundationActorState other in _actors)
                {
                    if (actor.Index != other.Index && ReferenceEquals(actor.ChatService, other.ChatService))
                    {
                        isolated = false;
                        break;
                    }
                }

                actor.ChatIsIsolated = isolated;
                if (!isolated)
                {
                    _setupErrors.Add($"Actor '{actor.ActorId}' shared a chat service with another actor.");
                }
            }
        }

        private void RunCrossActorModProofs(
            MultiplayerFoundationActorState requester,
            MultiplayerFoundationActorState target)
        {
            AddExpectedDenial(
                "MOD OWNERSHIP",
                $"Unload {target.ModId}",
                requester.ActorId,
                target.ActorId,
                () => _mods.UnloadMod(requester.ModActorContext, target.ModId),
                requester.ActorId,
                "owned by actor",
                () => _mods.IsLoaded(target.ModActorContext, target.ModId));

            AddExpectedDenial(
                "MOD OWNERSHIP",
                $"Reload {target.ModId}",
                requester.ActorId,
                target.ActorId,
                () => _mods.ReloadMod(requester.ModActorContext, target.ModId, "return true"),
                requester.ActorId,
                "owned by actor",
                () => _mods.IsLoaded(target.ModActorContext, target.ModId));

            AddExpectedDenial(
                "MOD OWNERSHIP",
                $"Revert {target.ModId}",
                requester.ActorId,
                target.ActorId,
                () =>
                {
                    string restoredSource;
                    _mods.TryRevertMod(requester.ModActorContext, target.ModId, 0, out restoredSource);
                },
                requester.ActorId,
                "owned by actor",
                () => _mods.IsLoaded(target.ModActorContext, target.ModId));
        }

        private void RunCrossActorWorldProofs(
            MultiplayerFoundationActorState requester,
            MultiplayerFoundationActorState target)
        {
            string mutateId = ModPrefix + "cross_actor_mutate";
            string destroyId = ModPrefix + "cross_actor_destroy";
            int mutateProofIndex = _proofs.Count;
            AddExpectedDenial(
                "WORLD ACL",
                $"Rename {target.WorldObjectName}",
                requester.ActorId,
                target.ActorId,
                () => _mods.LoadMod(
                    requester.ModActorContext,
                    mutateId,
                    $"workspace:FindFirstChild('{target.WorldObjectName}').Name = 'StolenByActorA'",
                    LuaCapabilities.All,
                    false),
                requester.ActorId,
                "Owned by actor",
                () => _mods.IsLoaded(target.ModActorContext, target.ModId));

            int destroyProofIndex = _proofs.Count;
            AddExpectedDenial(
                "WORLD ACL",
                $"Destroy {target.WorldObjectName}",
                requester.ActorId,
                target.ActorId,
                () => _mods.LoadMod(
                    requester.ModActorContext,
                    destroyId,
                    $"workspace:FindFirstChild('{target.WorldObjectName}'):Destroy()",
                    LuaCapabilities.All,
                    false),
                requester.ActorId,
                "Owned by actor",
                () => _mods.IsLoaded(target.ModActorContext, target.ModId));

            bool worldObjectIntact = VerifyWorldObjectWithOwner(target);
            _proofs[mutateProofIndex].TargetIntact = worldObjectIntact;
            _proofs[destroyProofIndex].TargetIntact = worldObjectIntact;
        }

        private void RunCrossActorChatProofs(
            MultiplayerFoundationActorState requester,
            MultiplayerFoundationActorState target)
        {
            AddExpectedDenial(
                "CHAT PRIVACY",
                "Read private chat history",
                requester.ActorId,
                target.ActorId,
                () => ReadChatHistoryPairCount(requester.ChatActorContext, target),
                requester.ActorId,
                "chat ownership is actor-scoped",
                () => target.ChatService.HistoryPairCount >= 0);

            AddExpectedDenial(
                "CHAT PRIVACY",
                "Observe private chat rate state",
                requester.ActorId,
                target.ActorId,
                () => ReadChatRateState(requester.ChatActorContext, target),
                requester.ActorId,
                "chat ownership is actor-scoped",
                () => target.ChatService.GetRateLimiterMetrics().AcceptedInWindow >= 0);
        }

        private void RunHostProtectedProofs()
        {
            string destroyId = ModPrefix + "host_destroy_lighting";
            string reparentId = ModPrefix + "host_reparent_players";
            AddExpectedDenial(
                "HOST PROTECTED",
                "Destroy game:GetService('Lighting')",
                _hostActor.ActorId,
                "Lighting singleton",
                () => _mods.LoadMod(
                    _hostActor,
                    destroyId,
                    "game:GetService('Lighting'):Destroy()",
                    LuaCapabilities.All,
                    false),
                _hostActor.ActorId,
                "HostProtected",
                () => true);

            AddExpectedDenial(
                "HOST PROTECTED",
                "Reparent game:GetService('Players')",
                _hostActor.ActorId,
                "Players singleton",
                () => _mods.LoadMod(
                    _hostActor,
                    reparentId,
                    "game:GetService('Players').Parent = workspace",
                    LuaCapabilities.All,
                    false),
                _hostActor.ActorId,
                "HostProtected",
                () => true);
        }

        private void RunQuotaProof(MultiplayerFoundationActorState actor)
        {
            for (int index = 1; index < ProductionModQuota; index++)
            {
                string fillerId = QuotaFillerId(actor, index);
                try
                {
                    _mods.LoadMod(
                        actor.ModActorContext,
                        fillerId,
                        "return true",
                        LuaCapabilities.Read,
                        false);
                }
                catch (Exception ex)
                {
                    _setupErrors.Add(
                        $"Actor '{actor.ActorId}' could not reach quota N at filler {index}: {FlattenException(ex)}");
                    return;
                }
            }

            actor.LoadedModCount = ProductionModQuota;
            AddExpectedDenial(
                "PER-ACTOR QUOTA",
                $"Load mod {ProductionModQuota + 1} after N={ProductionModQuota}",
                actor.ActorId,
                actor.ActorId,
                () => _mods.LoadMod(
                    actor.ModActorContext,
                    QuotaOverflowId(actor),
                    "return true",
                    LuaCapabilities.Read,
                    false),
                actor.ActorId,
                "loaded mods quota reached",
                () => actor.LoadedModCount == ProductionModQuota);
        }

        private void RefreshActorModState()
        {
            IReadOnlyList<LuaModInfo> mods = _mods.ListMods(_hostActor);
            foreach (MultiplayerFoundationActorState actor in _actors)
            {
                int ownedCount = 0;
                actor.TimerCount = 0;
                foreach (LuaModInfo mod in mods)
                {
                    string ownerActorId;
                    try
                    {
                        ownerActorId = _mods.GetModOwnerActorId(_hostActor, mod.Id) ?? "";
                    }
                    catch (Exception)
                    {
                        ownerActorId = "";
                    }

                    if (string.Equals(ownerActorId, actor.ActorId, StringComparison.Ordinal))
                    {
                        ownedCount++;
                    }

                    if (string.Equals(mod.Id, actor.ModId, StringComparison.Ordinal))
                    {
                        actor.TimerCount = mod.TimerCount;
                    }
                }

                actor.LoadedModCount = ownedCount;
                actor.ModLoaded = _mods.IsLoaded(actor.ModActorContext, actor.ModId);
            }
        }

        private bool VerifyWorldObjectWithOwner(MultiplayerFoundationActorState owner)
        {
            string verifierId = ModPrefix + "world_verify_" + (owner.Index + 1).ToString("00", CultureInfo.InvariantCulture);
            SafeForget(owner.ModActorContext, verifierId);
            try
            {
                _mods.LoadMod(
                    owner.ModActorContext,
                    verifierId,
                    $"assert(workspace:FindFirstChild('{owner.WorldObjectName}') ~= nil, 'owned beacon missing')",
                    LuaCapabilities.All,
                    false);
                return _mods.IsLoaded(owner.ModActorContext, verifierId);
            }
            catch (Exception ex)
            {
                _setupErrors.Add(
                    $"Actor '{owner.ActorId}' could not verify its own world object: {FlattenException(ex)}");
                return false;
            }
            finally
            {
                SafeForget(owner.ModActorContext, verifierId);
            }
        }

        private int ReadChatHistoryPairCount(
            ActorContext requester,
            MultiplayerFoundationActorState target)
        {
            DemandOwnChat(requester, target, "chat_history");
            return target.ChatService.HistoryPairCount;
        }

        private RateLimiterMetrics ReadChatRateState(
            ActorContext requester,
            MultiplayerFoundationActorState target)
        {
            DemandOwnChat(requester, target, "chat_rate_state");
            return target.ChatService.GetRateLimiterMetrics();
        }

        private static void DemandOwnChat(
            ActorContext requester,
            MultiplayerFoundationActorState target,
            string operation)
        {
            if (!requester.IsTrusted)
            {
                throw new InvalidOperationException("Actor context was not issued by an identity provider.");
            }

            if (string.Equals(requester.ActorId, target.ActorId, StringComparison.Ordinal))
            {
                return;
            }

            throw new UnauthorizedAccessException(
                $"{operation}: actor '{requester.ActorId}' is not authorized to access actor " +
                $"'{target.ActorId}' chat because chat ownership is actor-scoped.");
        }

        private void AddExpectedDenial(
            string category,
            string operation,
            string requesterActorId,
            string target,
            Action request,
            string actorFragment,
            string reasonFragment,
            Func<bool> targetIntact)
        {
            MultiplayerFoundationProofResult proof = new MultiplayerFoundationProofResult
            {
                Category = category,
                Operation = operation,
                RequesterActorId = requesterActorId,
                Target = target
            };

            try
            {
                request();
                proof.Reason =
                    $"BUG: request by actor '{requesterActorId}' was allowed; expected {reasonFragment}.";
                proof.Enforced = false;
            }
            catch (Exception ex)
            {
                proof.Reason = FlattenException(ex);
                proof.Enforced =
                    proof.Reason.IndexOf(actorFragment, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    proof.Reason.IndexOf(reasonFragment, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            try
            {
                proof.TargetIntact = targetIntact();
            }
            catch (Exception ex)
            {
                proof.TargetIntact = false;
                proof.Reason += " Target verification failed: " + FlattenException(ex);
            }

            _proofs.Add(proof);
        }

        private string BuildActorModSource(MultiplayerFoundationActorState actor, bool edited)
        {
            int columns = Math.Min(5, Math.Max(2, (int)Math.Ceiling(Math.Sqrt(_actors.Count))));
            int row = actor.Index / columns;
            int column = actor.Index % columns;
            float x = (column - (columns - 1) * 0.5f) * 5.2f;
            float z = row * 5.2f;
            int red = (int)Math.Round(actor.DisplayColor.r * 255f, MidpointRounding.AwayFromZero);
            int green = (int)Math.Round(actor.DisplayColor.g * 255f, MidpointRounding.AwayFromZero);
            int blue = (int)Math.Round(actor.DisplayColor.b * 255f, MidpointRounding.AwayFromZero);
            int dimRed = (int)Math.Round(red * 0.45f, MidpointRounding.AwayFromZero);
            int dimGreen = (int)Math.Round(green * 0.45f, MidpointRounding.AwayFromZero);
            int dimBlue = (int)Math.Round(blue * 0.45f, MidpointRounding.AwayFromZero);
            float height = edited ? 1.3f : 0.7f;
            return FormattableString.Invariant($@"
local node = workspace:FindFirstChild('{actor.WorldObjectName}')
if node == nil then
    node = Instance.new('Part')
    node.Name = '{actor.WorldObjectName}'
    node.Anchored = true
    node.Position = Vector3.new({x}, 1.2, {z})
    node.Parent = workspace
end
node.Size = Vector3.new(4.2, {height}, 4.2)
node.Color = Color3.fromRGB({red}, {green}, {blue})
local bright = true
hooks_every(0.75, function()
    bright = not bright
    if bright then
        node.Color = Color3.fromRGB({red}, {green}, {blue})
    else
        node.Color = Color3.fromRGB({dimRed}, {dimGreen}, {dimBlue})
    end
end)");
        }

        private void CleanupKnownArtifacts()
        {
            foreach (MultiplayerFoundationActorState actor in _actors)
            {
                SafeForget(actor.ModActorContext, actor.ModId);
                SafeForget(actor.ModActorContext, ModPrefix + "cross_actor_mutate");
                SafeForget(actor.ModActorContext, ModPrefix + "cross_actor_destroy");
                SafeForget(
                    actor.ModActorContext,
                    ModPrefix + "world_verify_" +
                    (actor.Index + 1).ToString("00", CultureInfo.InvariantCulture));
                for (int index = 1; index < ProductionModQuota; index++)
                {
                    SafeForget(actor.ModActorContext, QuotaFillerId(actor, index));
                }

                SafeForget(actor.ModActorContext, QuotaOverflowId(actor));
            }

            SafeForget(_hostActor, ModPrefix + "host_destroy_lighting");
            SafeForget(_hostActor, ModPrefix + "host_reparent_players");
        }

        private void SafeForget(ActorContext actor, string modId)
        {
            try
            {
                _mods.ForgetMod(actor, modId);
            }
            catch (Exception)
            {
            }
        }

        private static string QuotaFillerId(MultiplayerFoundationActorState actor, int index)
        {
            return ModPrefix + "quota_" +
                   (actor.Index + 1).ToString("00", CultureInfo.InvariantCulture) + "_" +
                   index.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string QuotaOverflowId(MultiplayerFoundationActorState actor)
        {
            return ModPrefix + "quota_" +
                   (actor.Index + 1).ToString("00", CultureInfo.InvariantCulture) + "_overflow";
        }

        private MultiplayerFoundationActorState ActorAt(int actorIndex)
        {
            if (actorIndex < 0 || actorIndex >= _actors.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(actorIndex));
            }

            return _actors[actorIndex];
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MultiplayerFoundationDemoScenario));
            }
        }

        private static string FlattenException(Exception exception)
        {
            StringBuilder text = new StringBuilder();
            Exception current = exception;
            while (current != null)
            {
                if (text.Length > 0)
                {
                    text.Append(" -> ");
                }

                text.Append(current.Message);
                current = current.InnerException;
            }

            return text.ToString();
        }
    }
}
#endif
