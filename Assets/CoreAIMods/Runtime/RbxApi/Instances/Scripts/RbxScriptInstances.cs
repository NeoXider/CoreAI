using CoreAI.Mods.Rbx.Instances.Replication;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// Mirror <c>LuaSourceContainer</c> (abstract in Roblox; the concrete instance of <c>ModuleScript</c> here): a
    /// script instance that names the store entry holding its source by <see cref="SourceId"/> (MVP4 design note,
    /// SC-1). The instance never holds source text; <c>Source</c> is a view over the store (SC-2, step C2).
    /// </summary>
    public class RbxLuaSourceContainer : RbxInstance
    {
        internal RbxLuaSourceContainer(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>
        /// The mod id of the store entry this instance is bound to; empty while unbound. Not visible to Lua (SC-1)
        /// and never copied by <c>Clone</c>: a clone is an unbound script (SC-17).
        /// </summary>
        public string SourceId { get; internal set; } = string.Empty;

        /// <summary>True while the instance names a store entry.</summary>
        public bool IsBound => SourceId.Length > 0;
    }

    /// <summary>
    /// Mirror <c>BaseScript</c> (the concrete instance of <c>Script</c> and <c>LocalScript</c> here): a source
    /// container that runs on its own, with <see cref="Enabled"/> (default true; <c>Disabled</c> is its deprecated
    /// inverse) and <see cref="RunContext"/> (default <c>Enum.RunContext.Legacy</c>). Both are per-instance state
    /// (SC-3, SC-4); nothing reads them to start or stop a run until step C1.
    /// </summary>
    public class RbxBaseScript : RbxLuaSourceContainer
    {
        private bool _enabled = true;
        private int _runContext = RunContextValues.Legacy;

        internal RbxBaseScript(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>BaseScript.Enabled</c>: whether the script may run.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                ThrowIfDestroyed(nameof(Enabled));
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                Registry?.AdvanceRevision(Id, ReplicationMembers.Enabled);
                NotifyPropertyChanged(ReplicationMembers.Enabled);
                NotifyPropertyChanged(DisabledMember);
            }
        }

        /// <summary>Mirror <c>BaseScript.Disabled</c>: the deprecated inverse of <see cref="Enabled"/>.</summary>
        public bool Disabled
        {
            get => !_enabled;
            set => Enabled = !value;
        }

        /// <summary>
        /// Mirror <c>BaseScript.RunContext</c>, as the <c>Enum.RunContext</c> value. A value the SC-4 table refuses
        /// for this class (an undefined value, <c>Plugin</c>, or anything but <c>Legacy</c> on a
        /// <c>LocalScript</c>) raises BAD_ARGUMENT and changes nothing.
        /// </summary>
        public int RunContext
        {
            get => _runContext;
            set
            {
                ThrowIfDestroyed(nameof(RunContext));
                if (!ScriptContexts.TryGetEffective(ClassName, value, ScriptContext.Legacy, out ScriptContext _,
                        out string error))
                {
                    throw RbxError.BadArgument(ClassName + ".RunContext: " + error,
                        "use Enum.RunContext.Server or Enum.RunContext.Client on a Script, and Legacy on a LocalScript");
                }

                if (_runContext == value)
                {
                    return;
                }

                _runContext = value;
                Registry?.AdvanceRevision(Id, ReplicationMembers.RunContext);
                NotifyPropertyChanged(ReplicationMembers.RunContext);
            }
        }

        /// <summary>The deprecated property name that mirrors <see cref="Enabled"/> inverted.</summary>
        public const string DisabledMember = "Disabled";

        /// <summary>
        /// Puts saved state back without counting it as a change (no revision, no signal), for a package reader
        /// restoring a tree. Refuses, with the SC-4 reason, a <paramref name="runContext"/> the table refuses for
        /// this class, and then changes nothing.
        /// </summary>
        internal bool TryRestoreState(bool enabled, int runContext, out string error)
        {
            if (!ScriptContexts.TryGetEffective(ClassName, runContext, ScriptContext.Legacy, out ScriptContext _,
                    out error))
            {
                return false;
            }

            _enabled = enabled;
            _runContext = runContext;
            return true;
        }

        /// <summary>Copies <see cref="Enabled"/> and <see cref="RunContext"/>; never <see cref="RbxLuaSourceContainer.SourceId"/>.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            if (copy is RbxBaseScript script)
            {
                script._enabled = _enabled;
                script._runContext = _runContext;
            }
        }
    }
}
