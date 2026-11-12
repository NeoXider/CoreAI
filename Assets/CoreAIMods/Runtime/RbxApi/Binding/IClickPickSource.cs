using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Building;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Click-pick seam for the Lua surface (ClickDetector.MouseClick): given a mouse screen
    /// position in Roblox convention (pixels, top-left origin), resolve the world instance under
    /// the cursor via a camera ray. Signatures are engine-free so the bindings layer never touches
    /// UnityEngine types (D2 lint) — mirrors <see cref="IRbxCameraRig"/>; the Unity adapter
    /// converts the screen point, raycasts the physics world, and maps the hit collider back to an
    /// <see cref="InstanceId"/>.
    /// </summary>
    public interface IClickPickSource
    {
        /// <summary>
        /// Casts a ray from the rendering camera through the screen point and reports the nearest
        /// hit instance. Returns false (and <see cref="InstanceId.None"/>) when there is no camera,
        /// no physics hit, or the hit object maps to no world instance.
        /// <paramref name="distanceStuds"/> is the camera-to-hit distance in Roblox studs, so the
        /// caller can gate on a ClickDetector's MaxActivationDistance.
        /// </summary>
        bool TryPick(RbxVector2 screenPositionTopLeft, out InstanceId hitId, out double distanceStuds);

        /// <summary>
        /// The richer pick behind the in-play tools and hover signals: the nearest part or Terrain hit through the
        /// screen point within <paramref name="maxDistanceStuds"/>, skipping every instance
        /// <paramref name="isEligible"/> refuses (the local character, a tool's own preview), with the hit point and
        /// face normal. A source that predates it answers <see cref="TryPick"/>'s instance without a surface.
        /// </summary>
        bool TryPickWorld(RbxVector2 screenPositionTopLeft, Func<InstanceId, bool> isEligible,
            double maxDistanceStuds, out RbxWorldPick pick)
        {
            if (TryPick(screenPositionTopLeft, out InstanceId hitId, out double distanceStuds)
                && distanceStuds <= maxDistanceStuds && (isEligible == null || isEligible(hitId)))
            {
                pick = new RbxWorldPick(hitId, default, default, distanceStuds, false, false);
                return true;
            }

            pick = RbxWorldPick.None;
            return false;
        }

        /// <summary>
        /// The camera ray through the screen point in Roblox space (origin in studs, unit direction); false where the
        /// source has no camera. Tools use it to drag along an axis.
        /// </summary>
        bool TryGetRay(RbxVector2 screenPositionTopLeft, out RbxVector3 origin, out RbxVector3 direction)
        {
            origin = default;
            direction = default;
            return false;
        }
    }
}
