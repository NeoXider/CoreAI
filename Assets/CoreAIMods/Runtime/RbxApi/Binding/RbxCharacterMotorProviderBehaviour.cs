using System;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Scene-side base class for a host that drives Rbx characters with its own controller: derive
    /// from it, drop it on a GameObject, and hand it to <c>CoreAiModsLifetimeScope</c>'s character
    /// motor provider field.
    /// </summary>
    /// <remarks>
    /// WHY a MonoBehaviour base exists next to the plain <see cref="IRbxCharacterMotorProvider"/>
    /// interface: the architecture rules require an explicit serialized reference rather than scene
    /// reflection or a static singleton, and an interface cannot be dragged into an inspector field.
    /// A host with no scene state can still register the plain interface in the container directly;
    /// this type exists so the common case needs no composition code at all.
    /// </remarks>
    public abstract class RbxCharacterMotorProviderBehaviour : MonoBehaviour, IRbxCharacterMotorProvider
    {
        /// <inheritdoc />
        public abstract IRbxCharacterMotor TryCreate(
            RbxHumanoid humanoid,
            GameObject body,
            Func<float> worldGravityMetresPerSecondSquared);
    }
}
