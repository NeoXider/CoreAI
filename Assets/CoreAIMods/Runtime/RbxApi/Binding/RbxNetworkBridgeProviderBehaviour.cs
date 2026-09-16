using CoreAI.Mods.Rbx.Instances.Networking;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Scene-side base class for a host that puts the Rbx world on a real network transport: derive
    /// from it, drop it on a GameObject, and hand it to <c>CoreAiModsLifetimeScope</c>'s network
    /// bridge provider field.
    /// </summary>
    /// <remarks>
    /// WHY a MonoBehaviour base exists next to the plain <see cref="INetworkBridge"/> interface: the
    /// architecture rules require an explicit serialized reference rather than scene reflection or a
    /// static singleton, and an interface cannot be dragged into an inspector field. A host with no
    /// scene state can still register the plain interface in the container directly; this type
    /// exists so the common case needs no composition code at all.
    /// <para>
    /// The composition reads <see cref="Bridge"/> only when the container first resolves the
    /// interface, never while the scope is being configured, so a transport that cannot exist yet in
    /// Awake is not asked to. An implementation hands back ONE bridge for its lifetime: the world
    /// keeps the reference it was given, and a second instance would carry traffic nothing listens
    /// to.
    /// </para>
    /// </remarks>
    public abstract class RbxNetworkBridgeProviderBehaviour : MonoBehaviour
    {
        /// <summary>The transport this process's Rbx world sends and receives remotes through.</summary>
        public abstract INetworkBridge Bridge { get; }
    }
}
