using System;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The host's opportunity to drive a character with its own controller instead of CoreAI's.
    /// Register one implementation in the container and every Humanoid that gets a body asks it
    /// first; CoreAI's own motor is what answers when nothing is registered or the host declines.
    /// </summary>
    /// <remarks>
    /// WHY a provider and not a subclass or a scene component: the decision is per character and has
    /// to be made where the body first exists, which is inside composition — the same place the
    /// binder and the physics port are known. A provider keeps that one decision in the host's hands
    /// without giving it the rest of the pipeline.
    /// <para>
    /// WHY declining is a null return rather than a separate "can handle" call: a host that wants
    /// its controller only for player characters must be able to decide with the body in hand and
    /// fall back for everything else. A decline is FINAL for that character: CoreAI's own motor is
    /// installed immediately in its place, and nothing calls <see cref="TryCreate"/> again for the
    /// same body just because time passed — there is no "ask again once you're ready" polling. A
    /// host that is not ready to decide yet (its own rig has not finished loading) has to solve that
    /// itself: either don't register a provider until it can already decide correctly for whatever
    /// characters exist by then, or accept the body now behind a placeholder
    /// <see cref="IRbxCharacterMotor"/> and report <see cref="IRbxCharacterMotor.IsAvailable"/> as
    /// false once the real rig is ready — that is the one thing that does make the pipeline call
    /// <see cref="TryCreate"/> again for the same character, on the next fixed-step motor refresh.
    /// </para>
    /// <para>
    /// A controller reached this way OWNS the body's movement for as long as it lives, so it must
    /// stop reading input on its own: <c>Humanoid</c> is the only thing allowed to say where the
    /// character goes, or the character is driven twice. See
    /// <c>Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md</c>.
    /// </para>
    /// </remarks>
    public interface IRbxCharacterMotorProvider
    {
        /// <summary>
        /// Builds a motor for <paramref name="humanoid"/>, or returns null to use CoreAI's own.
        /// </summary>
        /// <param name="humanoid">The Humanoid whose Lua-visible contract the motor must honour.</param>
        /// <param name="body">
        /// The backing GameObject of the character's <c>HumanoidRootPart</c>, already materialized
        /// and in the world.
        /// </param>
        /// <param name="worldGravityMetresPerSecondSquared">
        /// Reads the acceleration the world actually applies to <paramref name="body"/> right now.
        /// A character body has <c>Rigidbody.useGravity</c> off — the world's own acceleration is
        /// applied per fixed step from <c>Workspace.Gravity</c> — so a controller that solves a jump
        /// height must ask this rather than <c>Physics.gravity</c>, and must ask it each time
        /// because loading a world replaces the source.
        /// </param>
        IRbxCharacterMotor TryCreate(
            RbxHumanoid humanoid,
            GameObject body,
            Func<float> worldGravityMetresPerSecondSquared);
    }
}
