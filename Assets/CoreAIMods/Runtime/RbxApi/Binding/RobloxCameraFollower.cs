using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Keeps the camera at a fixed world offset from its follow target every LateUpdate.
    /// Position-only (rotation stays script-controlled) so mini-games get a stable chase
    /// camera. The camera is never parented under the target, so destroying the followed part
    /// can never take the camera with it — a lost target simply stops the follow.
    /// </summary>
    public sealed class RobloxCameraFollower : MonoBehaviour
    {
        public Transform Target { get; set; }

        public Vector3 Offset { get; set; }

        private void LateUpdate() => Apply();

        /// <summary>One follow step; public so EditMode tests can tick without a player loop.</summary>
        public void Apply()
        {
            if (Target == null)
            {
                return;
            }

            transform.position = Target.position + Offset;
        }
    }
}
