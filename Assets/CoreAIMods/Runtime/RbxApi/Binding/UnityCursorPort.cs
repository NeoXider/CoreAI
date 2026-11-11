using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Unity backend of <see cref="IRbxCursorPort"/> over <see cref="Cursor"/>. A lock request hides and
    /// locks the cursor; <c>Default</c> frees it, but only when this port took the lock, so a host
    /// game's own controller that locks the cursor itself is never overridden by a script's default.
    /// </summary>
    /// <remarks>
    /// WHY both lock items map to <see cref="CursorLockMode.Locked"/>: Unity has no "lock where it is";
    /// <c>LockCurrentPosition</c> therefore recentres the pointer (a DEV candidate). On WebGL the
    /// browser grants pointer lock only after a user gesture: Unity applies the request on the next
    /// click into the canvas, and Escape drops it, which <see cref="IsPointerLocked"/> then reports.
    /// </remarks>
    public sealed class UnityCursorPort : IRbxCursorPort
    {
        private bool _ownsLock;

        /// <inheritdoc />
        public bool IsPointerLocked => Cursor.lockState == CursorLockMode.Locked;

        /// <inheritdoc />
        public void ApplyMouseBehavior(RbxMouseLock mode)
        {
            if (mode == RbxMouseLock.Default)
            {
                if (!_ownsLock)
                {
                    return;
                }

                _ownsLock = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            _ownsLock = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
