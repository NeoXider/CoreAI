using System;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// Decides which instances a given client is allowed to know about at all.
    /// </summary>
    /// <remarks>
    /// WHY replication is filtered rather than broadcast: a client that receives the whole tree can
    /// read <c>ServerStorage</c>, every other player's private state and the answer to any puzzle in
    /// the world — with no exploit, just by looking at what it was sent. Filtering at the source is
    /// the only place that cannot be bypassed by a modified client.
    /// </remarks>
    public interface IReplicationFilter
    {
        /// <summary>Whether <paramref name="recipientActorId"/> may be told about this instance.</summary>
        bool IsVisibleTo(string recipientActorId, RbxInstance instance);

        /// <summary>
        /// Whether one member (see <see cref="ReplicationMembers"/>) of an instance the recipient may
        /// already see is for its eyes too. Asked only about visible instances. Defaults to yes so a
        /// filter that only thinks in instances keeps compiling and keeps meaning what it meant.
        /// </summary>
        bool IsMemberVisibleTo(string recipientActorId, RbxInstance instance, string member)
        {
            return true;
        }
    }

    /// <summary>
    /// The default filter: what Roblox itself replicates, read off the class reference.
    /// </summary>
    /// <remarks>
    /// WHY a denylist over the whole DataModel rather than the old allowlist of three roots: the
    /// reference (creator-docs classes/*.yaml, class-level <c>tags</c>) marks the containers that do
    /// NOT replicate, and everything else under <c>game</c> does. ReplicatedStorage.yaml carries no
    /// replication tag and says objects there "are fully replicated to clients"; StarterGui,
    /// StarterPack, StarterPlayer, ReplicatedFirst, Teams, Lighting, Players and MaterialService are
    /// plain <c>Service</c>s with no replication tag either. An allowlist silently dropped a Team, a
    /// StarterPlayerScripts folder and every Player object — content client scripts expect to find.
    /// </remarks>
    public sealed class DefaultReplicationFilter : IReplicationFilter
    {
        /// <summary>Shared instance; the type holds no state.</summary>
        public static readonly DefaultReplicationFilter Instance = new();

        // WHY this list: every class CoreAI's ClassCatalog or ServiceCatalog can put in a tree whose
        // yaml carries the NotReplicated tag — ServerStorage, ServerScriptService, PlayerScripts,
        // Camera, RunService, UserInputService, ScriptContext, DataStoreService, PathfindingService —
        // plus the NotReplicated services Roblox itself hangs off game that a host may attach later:
        // CoreGui, Chat, GuiService, NetworkServer, NetworkClient. The prose agrees — ServerStorage
        // "will not replicate to the client", ServerScriptService "never replicated to player
        // clients at all", PlayerScripts "not accessible to the server", Camera "each client has its
        // own Camera object which resides in that client's local Workspace"; a client runs its own
        // RunService, UserInputService and ScriptContext and never receives the server's.
        // GuardedReplicationFilterEditModeTests walks the bootstrapped tree against a transcription
        // of those tags, so a service added to DataModelBootstrap without a verdict here fails a test
        // instead of reaching every client.
        // WHY AIService is here with no yaml to cite (OURS): it is CoreAI's bridge from Lua to the
        // host's agent, and what it holds (model configuration, conversation memory, tool access)
        // is minted on the server and cannot be exercised by a client, so it is scoped like
        // DataStoreService — the closest Roblox service that fronts a server-side backend.
        private static readonly string[] NotReplicatedClasses =
        {
            "ServerStorage", "ServerScriptService", "PlayerScripts", "Camera", "RunService",
            "UserInputService", "ScriptContext", "DataStoreService", "PathfindingService", "CoreGui",
            "Chat", "GuiService", "NetworkServer", "NetworkClient", "AIService"
        };

        // WHY owner-only: PlayerGui.yaml is tagged PlayerReplicated — it exists for one player and
        // reaches that player's client alone. Backpack.yaml carries no tag but describes exactly the
        // same access ("Players.LocalPlayer.Backpack" from a client script). OURS: Backpack is scoped
        // like PlayerGui, because an inventory every client could read is one every client could copy.
        private static readonly string[] PlayerReplicatedContainers = { "PlayerGui", "Backpack" };

        /// <inheritdoc />
        public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
        {
            if (instance == null || instance.IsDestroyed)
            {
                return false;
            }

            RbxInstance top = null;
            for (RbxInstance node = instance; node != null; node = node.Parent)
            {
                if (Contains(NotReplicatedClasses, node.ClassName))
                {
                    return false;
                }

                if (Contains(PlayerReplicatedContainers, node.ClassName)
                    && node.Parent is RbxPlayer owner
                    && !string.Equals(owner.NetworkActorId, recipientActorId, StringComparison.Ordinal))
                {
                    return false;
                }

                top = node;
            }

            // WHY the tree must hang off a DataModel: every container yaml describes what replicates
            // under game. A detached subtree, or one still being assembled with Parent nil, is under
            // nothing, and Roblox sends nothing for it either.
            return top is RbxDataModel;
        }

        internal static bool Contains(string[] classNames, string className)
        {
            for (int index = 0; index < classNames.Length; index++)
            {
                if (string.Equals(classNames[index], className, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Wraps the filter a game supplies with a floor the game cannot lift: server-only containers,
    /// the client-local classes (Camera, PlayerScripts) and another player's own containers are
    /// never visible; visibility is closed under ancestry; a filter that throws is treated as "not
    /// visible" and reported through <see cref="InstanceRegistry.Diagnostics"/>, whatever that sink
    /// does in turn.
    /// </summary>
    /// <remarks>
    /// WHY a fault here hides rather than shows — the opposite of <see cref="IRbxCharacterMotor"/>,
    /// whose <c>IsAvailable</c> defaults to true so a motor that cannot answer keeps the character
    /// moving: a stalled character is seen at once, costs one player, and is fixed by the next
    /// respawn; a leaked ServerStorage subtree is seen by nobody, reaches every client that asked,
    /// and cannot be recalled once it is on their disks. The cheap failure gets the permissive
    /// default; the irreversible one gets the restrictive default.
    /// </remarks>
    public sealed class GuardedReplicationFilter : IReplicationFilter
    {
        // WHY this floor and not the whole default filter: these are the cases where a wrong "yes" is
        // a security fault rather than a gameplay bug — server-private content, the classes each
        // client owns locally, and another client's private containers. PlayerScripts sits with the
        // never-visible classes rather than the owner-only ones: PlayerScripts.yaml is tagged
        // NotReplicated outright, the container is client-local by definition, and a game filter
        // that let it through would ship a client-side artefact back to the very client that owns it.
        private static readonly string[] NeverVisibleClasses =
        {
            "ServerStorage", "ServerScriptService", "Camera", "PlayerScripts"
        };

        // WHY owner-only rather than never: PlayerGui.yaml is PlayerReplicated and Backpack is scoped
        // with it (see DefaultReplicationFilter); the owner must receive them, everyone else must not.
        private static readonly string[] PlayerOwnedContainers =
        {
            "Backpack", "PlayerGui"
        };

        private readonly IReplicationFilter _inner;
        private readonly InstanceRegistry _registry;

        /// <summary>Guards <paramref name="inner"/>; faults are reported through the registry.</summary>
        public GuardedReplicationFilter(IReplicationFilter inner, InstanceRegistry registry)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>The game's filter under the floor.</summary>
        public IReplicationFilter Inner => _inner;

        /// <summary>Guards <paramref name="filter"/> (the default when null) unless it already is.</summary>
        public static GuardedReplicationFilter Wrap(IReplicationFilter filter, InstanceRegistry registry)
        {
            return filter as GuardedReplicationFilter
                   ?? new GuardedReplicationFilter(filter ?? DefaultReplicationFilter.Instance, registry);
        }

        /// <inheritdoc />
        public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
        {
            if (instance == null || instance.IsDestroyed)
            {
                return false;
            }

            for (RbxInstance node = instance; node != null; node = node.Parent)
            {
                if (IsBelowFloor(recipientActorId, node))
                {
                    return false;
                }
            }

            // WHY every ancestor is asked rather than trusting the game filter to be consistent: a
            // replica handed a node whose parent it does not hold can only drop it or invent a parent,
            // and the planner promises it will never have to do either.
            for (RbxInstance node = instance; node != null; node = node.Parent)
            {
                if (!AskInner(recipientActorId, node))
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        public bool IsMemberVisibleTo(string recipientActorId, RbxInstance instance, string member)
        {
            if (instance == null || instance.IsDestroyed || member == null)
            {
                return false;
            }

            for (RbxInstance node = instance; node != null; node = node.Parent)
            {
                if (IsBelowFloor(recipientActorId, node))
                {
                    return false;
                }
            }

            try
            {
                return _inner.IsMemberVisibleTo(recipientActorId, instance, member);
            }
            catch (Exception exception)
            {
                Report(recipientActorId, instance, member, exception);
                return false;
            }
        }

        private static bool IsBelowFloor(string recipientActorId, RbxInstance node)
        {
            if (DefaultReplicationFilter.Contains(NeverVisibleClasses, node.ClassName))
            {
                return true;
            }

            return DefaultReplicationFilter.Contains(PlayerOwnedContainers, node.ClassName)
                   && node.Parent is RbxPlayer owner
                   && !string.Equals(owner.NetworkActorId, recipientActorId, StringComparison.Ordinal);
        }

        private bool AskInner(string recipientActorId, RbxInstance node)
        {
            try
            {
                return _inner.IsVisibleTo(recipientActorId, node);
            }
            catch (Exception exception)
            {
                Report(recipientActorId, node, null, exception);
                return false;
            }
        }

        private void Report(string recipientActorId, RbxInstance instance, string member,
            Exception exception)
        {
            _registry.ReportDiagnostic(
                "[CoreAI.RbxApi] replication filter " + _inner.GetType().Name + " threw while deciding "
                + (member == null ? "" : "member '" + member + "' of ")
                + instance.ClassName + " '" + instance.Name + "' (id " + instance.Id.Value
                + ") for '" + recipientActorId + "'; treated as not visible: " + exception);
        }
    }
}
