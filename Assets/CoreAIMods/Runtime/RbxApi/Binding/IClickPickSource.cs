using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

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
    }
}
