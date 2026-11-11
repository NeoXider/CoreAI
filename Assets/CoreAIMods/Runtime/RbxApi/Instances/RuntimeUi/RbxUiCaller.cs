using System;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// A claim a running mod generation holds on the screens it created or looked up. A screen lives
    /// while at least one of its claims is alive; once every claim is dead the next sweep destroys it.
    /// </summary>
    public interface IRbxUiLease
    {
        /// <summary>True while the generation that holds the claim is loaded.</summary>
        bool IsAlive { get; }
    }

    /// <summary>
    /// A lease that is an <see cref="RbxScriptConnection"/> tracked under the mod's connection
    /// generation: the connection ledger disconnects it exactly when it disconnects the generation's
    /// signal handlers (unload, quarantine, the outgoing run of a reload, a failed load's rollback), so
    /// screens follow the same lifetime rules as connections without a second teardown path.
    /// </summary>
    public sealed class RbxUiConnectionLease : IRbxUiLease
    {
        /// <summary>Wraps a tracked connection.</summary>
        public RbxUiConnectionLease(RbxScriptConnection connection)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>The connection the ledger tracks.</summary>
        public RbxScriptConnection Connection { get; }

        /// <inheritdoc />
        public bool IsAlive => Connection.Connected;
    }

    /// <summary>Who is calling the UI surface: the owning mod and the claim of its running generation.</summary>
    public readonly struct RbxUiCaller
    {
        /// <summary>Creates a caller.</summary>
        public RbxUiCaller(string modId, IRbxUiLease lease)
        {
            ModId = modId;
            Lease = lease;
        }

        /// <summary>The owning mod id; null or blank for the ownerless one-off surface.</summary>
        public string ModId { get; }

        /// <summary>The running generation's claim; null for the one-off surface.</summary>
        public IRbxUiLease Lease { get; }

        /// <summary>True when the caller is a mod.</summary>
        public bool HasMod => !string.IsNullOrWhiteSpace(ModId) && Lease != null;
    }
}
