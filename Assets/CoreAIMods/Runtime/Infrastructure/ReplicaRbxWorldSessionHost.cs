using System;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The session host of a client (MVP4 SC-16): every world it stages is a <see cref="RegistryAuthority.Replica"/>
    /// registry restored inside one replication-apply scope, so the staged instances keep the server's ids and
    /// revisions and none is marked diverged, while a local write after the commit is. Engine-free; it stages and
    /// commits exactly like <see cref="HeadlessRbxWorldSessionHost"/>.
    /// </summary>
    /// <remarks>
    /// Before anything is restored, <see cref="Stage"/> refuses a payload a client must never hold (SC-16, step C5;
    /// <see cref="RbxClientJoinPayload.RefuseServerContent"/>): a server or legacy source, a script bound to one, or
    /// anything under ServerScriptService or ServerStorage.
    /// </remarks>
    public sealed class ReplicaRbxWorldSessionHost : IRbxWorldSessionHost
    {
        private readonly HeadlessRbxWorldSessionHost _host;

        /// <param name="registry">The client's current world; must be a replica.</param>
        public ReplicaRbxWorldSessionHost(
            InstanceRegistry registry,
            RbxDataModel game,
            RbxWorldSettings settings = null,
            IPartPropertySink partSink = null,
            IRbxCameraRig cameraRig = null)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (registry.Authority != RegistryAuthority.Replica)
            {
                throw new ArgumentException(
                    "A client session host holds replica worlds; this registry is authoritative.", nameof(registry));
            }

            _host = new HeadlessRbxWorldSessionHost(
                registry, game, settings, partSink, cameraRig, RegistryAuthority.Replica);
        }

        public InstanceRegistry Registry => _host.Registry;

        public RbxDataModel Game => _host.Game;

        public IPartPropertySink PartSink => _host.PartSink;

        public IRbxCameraRig CameraRig => _host.CameraRig;

        public IInputSource InputSource => _host.InputSource;

        public IClickPickSource PickSource => _host.PickSource;

        public RbxWorldSettings Settings => _host.Settings;

        public IRbxPhysicsPort PhysicsPort => _host.PhysicsPort;

        /// <summary>
        /// Restores <paramref name="payload"/> into a fresh replica registry, unpublished until its commit. A payload that
        /// holds server content is refused with <see cref="RbxWorldPackageException"/> before anything is restored.
        /// </summary>
        public IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            RbxClientJoinPayload.RefuseServerContent(payload);
            return _host.Stage(payload);
        }
    }
}
