using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Camera seam for the Lua surface (workspace.CurrentCamera / camera_set_cframe /
    /// camera_follow): pose in Roblox space (studs, right-handed) plus a follow attachment and the lens
    /// behind <c>Camera.FieldOfView</c> and <c>Camera.ViewportSize</c>. Signatures are engine-free so the
    /// bindings layer never touches UnityEngine types (D2 lint); the Unity adapter converts through
    /// RbxSpace. A host rig that predates the lens members keeps compiling: it reports no lens, and the
    /// world then answers with Roblox's defaults and refuses a field-of-view write loudly.
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

        /// <summary>The rendering camera's vertical field of view, degrees; NaN when the rig has no lens.</summary>
        double GetFieldOfView()
        {
            return double.NaN;
        }

        /// <summary>Sets the vertical field of view, degrees (already clamped to [1, 120]); false when the
        /// rig has no lens to set.</summary>
        bool TrySetFieldOfView(double degrees)
        {
            return false;
        }

        /// <summary>The rendered viewport in pixels; zero when the rig does not know it.</summary>
        RbxVector2 GetViewportSize()
        {
            return RbxVector2.Zero;
        }
    }
}
