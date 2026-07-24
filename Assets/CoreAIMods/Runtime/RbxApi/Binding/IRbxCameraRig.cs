using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Camera seam for the Lua surface (workspace.CurrentCamera / camera_set_cframe /
    /// camera_follow): pose in Roblox space (studs, right-handed) plus a follow attachment.
    /// Signatures are engine-free so the bindings layer never touches UnityEngine types
    /// (D2 lint); the Unity adapter converts through RbxSpace.
    /// </summary>
    public interface IRbxCameraRig
    {
        /// <summary>Current camera pose as a Roblox-space CFrame.</summary>
        RbxCFrame GetCFrame();

        /// <summary>Moves the camera to the Roblox-space pose. While following, this re-bases
        /// the follow offset instead of being overwritten on the next frame.</summary>
        void SetCFrame(in RbxCFrame cframe);

        /// <summary>Starts following the instance's backing object at the current world offset;
        /// false when the instance has no backing object in the world.</summary>
        bool Follow(InstanceId id);

        void StopFollowing();
    }
}
