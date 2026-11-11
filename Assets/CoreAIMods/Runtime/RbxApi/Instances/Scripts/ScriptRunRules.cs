using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// The container that decides where a script may run (rulebook R2.1): the nearest service, or the
    /// <c>StarterPlayer</c> sub-folder, between the script and the DataModel. <see cref="Players"/> covers a player's
    /// <c>Backpack</c>, <c>PlayerGui</c> and <c>PlayerScripts</c>; which player is <see cref="ScriptPlayerScope"/>.
    /// </summary>
    public enum ScriptContainer
    {
        /// <summary>The ancestor chain ends at nil instead of the DataModel (rulebook R1.12).</summary>
        OutsideDataModel,
        Workspace,
        ReplicatedFirst,
        ReplicatedStorage,
        ServerScriptService,
        ServerStorage,
        StarterPlayerScripts,
        StarterCharacterScripts,
        StarterGui,
        StarterPack,
        Players,

        /// <summary>Under the DataModel but in no container that runs scripts (Lighting, SoundService, the DataModel itself).</summary>
        Other
    }

    /// <summary>
    /// Whether a script sits inside a player's character, <c>Backpack</c>, <c>PlayerGui</c> or <c>PlayerScripts</c>,
    /// and whose. Only the binding layer knows which model is a character, so it supplies this.
    /// </summary>
    public enum ScriptPlayerScope
    {
        None,
        LocalPlayer,
        OtherPlayer
    }

    /// <summary>What the R1.3/R2.1 container matrix says about one class in one place.</summary>
    public enum ScriptPlacement
    {
        Runs,
        DoesNotRun,

        /// <summary>The place runs the script, but on another player's client, never on this process.</summary>
        OtherPlayersClient
    }

    /// <summary>
    /// The run state of a script, and the reason it is not running (decision SC-9). Reported for every store entry,
    /// in the order <see cref="ScriptRunRules.Evaluate"/> checks them.
    /// </summary>
    public enum ScriptRunState
    {
        Running,
        Unloaded,
        Detached,
        Quarantined,
        Suspended,
        InvalidRunContext,
        Module,
        Disabled,
        NotOnThisSide,
        ContainerDoesNotRun,
        WaitingForLocalPlayer
    }

    /// <summary>
    /// The inputs of the run predicate. The runtime re-evaluates it whenever one of them changes: load, unload,
    /// <c>Enabled</c>, <c>RunContext</c>, ancestry, destruction, quarantine, topology, the local player appearing.
    /// WHY public fields: the struct is a parameter bag filled by one caller and read by one function; C# 9 in Unity
    /// has no <c>init</c> accessors without a shim.
    /// </summary>
    public struct ScriptRunInputs
    {
        /// <summary>The bound instance's class.</summary>
        public string ClassName;

        /// <summary>The bound instance's <c>RunContext</c> (<see cref="RunContextValues"/>); ignored for a ModuleScript.</summary>
        public int RunContext;

        /// <summary>The context the source declares; <see cref="ScriptContext.Legacy"/> when it declares none.</summary>
        public ScriptContext DeclaredContext;

        /// <summary>The store entry is loaded (the manifest's <c>Active</c>).</summary>
        public bool IsLoaded;

        /// <summary>The entry has a live bound instance; false once that instance is destroyed (R1.11).</summary>
        public bool HasBoundInstance;

        public bool IsQuarantined;

        /// <summary>Suspended after repeated budget trips; only a reload clears it, never <c>Enabled</c> (decision SC-3).</summary>
        public bool IsSuspended;

        /// <summary><c>BaseScript.Enabled</c>; ignored for a ModuleScript, which has none.</summary>
        public bool Enabled;

        public ScriptContainer Container;
        public ScriptPlayerScope PlayerScope;

        /// <summary>The script was running before this input changed; a running script survives a detach to nil (R1.12).</summary>
        public bool WasRunning;

        public RbxNetworkTopology Topology;

        /// <summary>This process's local player exists; a client script waits for it (rulebook R2.2).</summary>
        public bool HasLocalPlayer;
    }

    /// <summary>Maps an instance's ancestor class names to the <see cref="ScriptContainer"/> that governs it.</summary>
    public static class ScriptContainers
    {
        /// <summary>
        /// Walks the ancestors' class names, nearest first, and returns the first container that governs scripts. A
        /// chain that never reaches <c>DataModel</c> is <see cref="ScriptContainer.OutsideDataModel"/>; one that reaches
        /// it through no known container is <see cref="ScriptContainer.Other"/>.
        /// WHY class names are safe here: services and the <c>StarterPlayer</c> folders are not creatable, so a mod cannot
        /// make an ordinary Folder that passes for one.
        /// </summary>
        public static ScriptContainer FromAncestorClassNames(IReadOnlyList<string> ancestorClassNamesNearestFirst)
        {
            if (ancestorClassNamesNearestFirst == null)
            {
                throw new ArgumentNullException(nameof(ancestorClassNamesNearestFirst));
            }

            for (int index = 0; index < ancestorClassNamesNearestFirst.Count; index++)
            {
                string className = ancestorClassNamesNearestFirst[index];
                switch (className)
                {
                    case "Workspace": return ScriptContainer.Workspace;
                    case "ReplicatedFirst": return ScriptContainer.ReplicatedFirst;
                    case "ReplicatedStorage": return ScriptContainer.ReplicatedStorage;
                    case "ServerScriptService": return ScriptContainer.ServerScriptService;
                    case "ServerStorage": return ScriptContainer.ServerStorage;
                    case "StarterPlayerScripts": return ScriptContainer.StarterPlayerScripts;
                    case "StarterCharacterScripts": return ScriptContainer.StarterCharacterScripts;
                    case "StarterGui": return ScriptContainer.StarterGui;
                    case "StarterPack": return ScriptContainer.StarterPack;
                    case "Players": return ScriptContainer.Players;
                    case "DataModel": return ScriptContainer.Other;
                }
            }

            return ScriptContainer.OutsideDataModel;
        }

        /// <summary>
        /// True for the containers a client never receives (rulebook R2.1): a client script may not require a module
        /// stored there (decision SC-10 item 4).
        /// </summary>
        public static bool IsServerOnly(ScriptContainer container)
        {
            return container == ScriptContainer.ServerScriptService || container == ScriptContainer.ServerStorage;
        }
    }

    /// <summary>The engine-free run predicate of decision SC-9 and the default placement of decision SC-6.</summary>
    public static class ScriptRunRules
    {
        /// <summary>
        /// Decides whether a script runs on this process and, when it does not, why. Checks in order: loaded, bound,
        /// not quarantined, not suspended, a valid class and run context, not a module (R2.1), enabled (R1.3, R1.10),
        /// a side this topology has (§3.2), a container that runs the class (R1.3, R1.5-R1.7, R2.1; a running script
        /// survives a detach to nil, R1.12), and for a client script the local player (R2.2).
        /// </summary>
        public static ScriptRunState Evaluate(in ScriptRunInputs inputs)
        {
            if (!inputs.IsLoaded)
            {
                return ScriptRunState.Unloaded;
            }

            if (!inputs.HasBoundInstance)
            {
                return ScriptRunState.Detached;
            }

            if (inputs.IsQuarantined)
            {
                return ScriptRunState.Quarantined;
            }

            if (inputs.IsSuspended)
            {
                return ScriptRunState.Suspended;
            }

            if (!ScriptContexts.TryGetEffective(inputs.ClassName, inputs.RunContext, inputs.DeclaredContext,
                    out ScriptContext effective, out string _))
            {
                return ScriptRunState.InvalidRunContext;
            }

            if (effective == ScriptContext.Shared)
            {
                return ScriptRunState.Module;
            }

            if (!inputs.Enabled)
            {
                return ScriptRunState.Disabled;
            }

            if (!ScriptContexts.IsSideAvailable(effective, inputs.Topology))
            {
                return ScriptRunState.NotOnThisSide;
            }

            if (inputs.Container == ScriptContainer.OutsideDataModel)
            {
                return inputs.WasRunning ? ScriptRunState.Running : ScriptRunState.ContainerDoesNotRun;
            }

            ScriptPlacement placement = Place(inputs.Container, inputs.PlayerScope, inputs.ClassName,
                inputs.RunContext);
            if (placement == ScriptPlacement.OtherPlayersClient)
            {
                return ScriptRunState.NotOnThisSide;
            }

            if (placement == ScriptPlacement.DoesNotRun)
            {
                // TODO: rulebook R1.12 does not say whether a running script moved into a non-running container stops or
                // only loses eligibility; this stops it (design note Q12) until a Studio check settles it.
                return ScriptRunState.ContainerDoesNotRun;
            }

            if (!ScriptContexts.RunsOnServerSide(effective) && !inputs.HasLocalPlayer)
            {
                return ScriptRunState.WaitingForLocalPlayer;
            }

            return ScriptRunState.Running;
        }

        /// <summary>
        /// The container matrix of rulebook R2.1 for one class (R1.3, R1.5-R1.8). A ModuleScript never runs on its own;
        /// <see cref="ScriptContainer.OutsideDataModel"/> never starts a script. A <c>Client</c> script runs in place in
        /// the <c>StarterPlayer</c> folders as Roblox does ("the original script and the copy run",
        /// <c>scripting/locations.md</c>); the per-player copy is MVP14's.
        /// </summary>
        public static ScriptPlacement Place(ScriptContainer container, ScriptPlayerScope playerScope,
            string className, int runContext)
        {
            if (string.Equals(className, ScriptClassNames.LocalScript, StringComparison.Ordinal))
            {
                if (container == ScriptContainer.ReplicatedFirst)
                {
                    return ScriptPlacement.Runs;
                }

                if (container == ScriptContainer.Players || container == ScriptContainer.Workspace)
                {
                    return ByPlayerScope(playerScope);
                }

                return ScriptPlacement.DoesNotRun;
            }

            if (!string.Equals(className, ScriptClassNames.Script, StringComparison.Ordinal))
            {
                return ScriptPlacement.DoesNotRun;
            }

            switch (runContext)
            {
                case RunContextValues.Legacy:
                    return container == ScriptContainer.Workspace || container == ScriptContainer.ServerScriptService
                        ? ScriptPlacement.Runs
                        : ScriptPlacement.DoesNotRun;
                case RunContextValues.Server:
                    return container == ScriptContainer.Workspace
                           || container == ScriptContainer.ServerScriptService
                           || container == ScriptContainer.ReplicatedStorage
                        ? ScriptPlacement.Runs
                        : ScriptPlacement.DoesNotRun;
                case RunContextValues.Client:
                    switch (container)
                    {
                        case ScriptContainer.Workspace:
                        case ScriptContainer.ReplicatedFirst:
                        case ScriptContainer.ReplicatedStorage:
                        case ScriptContainer.StarterPlayerScripts:
                        case ScriptContainer.StarterCharacterScripts:
                            return ScriptPlacement.Runs;
                        case ScriptContainer.Players:
                            return ByPlayerScope(playerScope);
                        default:
                            return ScriptPlacement.DoesNotRun;
                    }
                default:
                    return ScriptPlacement.DoesNotRun;
            }
        }

        /// <summary>
        /// The service a new instance with no <c>parent:</c> directive goes under (decision SC-6): ServerScriptService for
        /// server and legacy scripts, ReplicatedStorage for client scripts and shared modules.
        /// </summary>
        public static string DefaultContainerClass(ScriptContext declared)
        {
            switch (declared)
            {
                case ScriptContext.Legacy:
                case ScriptContext.Server:
                    return "ServerScriptService";
                case ScriptContext.Client:
                case ScriptContext.Shared:
                    return "ReplicatedStorage";
                default:
                    throw new ArgumentOutOfRangeException(nameof(declared), declared, null);
            }
        }

        /// <summary>The stable spelling in <c>manage_mods list</c> and the Hub (<c>running</c>, <c>not-on-this-side</c>, ...).</summary>
        public static string ToWireName(ScriptRunState state)
        {
            switch (state)
            {
                case ScriptRunState.Running: return "running";
                case ScriptRunState.Unloaded: return "unloaded";
                case ScriptRunState.Detached: return "detached";
                case ScriptRunState.Quarantined: return "quarantined";
                case ScriptRunState.Suspended: return "suspended";
                case ScriptRunState.InvalidRunContext: return "invalid-run-context";
                case ScriptRunState.Module: return "module";
                case ScriptRunState.Disabled: return "disabled";
                case ScriptRunState.NotOnThisSide: return "not-on-this-side";
                case ScriptRunState.ContainerDoesNotRun: return "container-does-not-run";
                case ScriptRunState.WaitingForLocalPlayer: return "waiting-for-local-player";
                default: throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        private static ScriptPlacement ByPlayerScope(ScriptPlayerScope playerScope)
        {
            switch (playerScope)
            {
                case ScriptPlayerScope.LocalPlayer: return ScriptPlacement.Runs;
                case ScriptPlayerScope.OtherPlayer: return ScriptPlacement.OtherPlayersClient;
                default: return ScriptPlacement.DoesNotRun;
            }
        }
    }
}
