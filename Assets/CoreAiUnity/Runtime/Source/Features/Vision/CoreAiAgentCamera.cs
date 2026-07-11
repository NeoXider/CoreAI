using UnityEngine;

namespace CoreAI.Vision
{
    /// <summary>
    /// Opt-in marker that makes a <see cref="Camera"/> agent-controllable. Cameras WITHOUT this marker are
    /// capture-only: an LLM agent may render (screenshot) any camera, but may only <b>move/rotate</b> a
    /// camera that carries this marker with <see cref="allowMove"/> enabled. This is what protects the
    /// player's camera — a camera the designer never marked can never be commandeered.
    /// <para>
    /// See <c>Docs/CoreAI/agent-vision.md</c>. Movement rules are enforced by
    /// <see cref="AgentCameraService"/>; this component only declares intent.
    /// </para>
    /// </summary>
    [AddComponentMenu("CoreAI/Agent Camera")]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class CoreAiAgentCamera : MonoBehaviour
    {
        [Tooltip("Restrict control to a single agent role (e.g. 'Programmer'). Leave empty so any agent " +
                 "may control this camera. Capture is always allowed regardless of this value.")]
        [SerializeField]
        private string agentRoleId = string.Empty;

        [Tooltip("When true, agents may move/rotate this camera via the 'camera_look' tool. When false the " +
                 "camera is capture-only but still counts as the agent's assigned camera for capture defaulting.")]
        [SerializeField]
        private bool allowMove = true;

        /// <summary>
        /// Optional agent role this camera is reserved for (empty = any agent). Matched case-insensitively
        /// against the calling agent's role id.
        /// </summary>
        public string AgentRoleId => agentRoleId;

        /// <summary>True when agents are permitted to move/rotate this camera.</summary>
        public bool AllowMove => allowMove;

        private Camera _camera;

        /// <summary>The <see cref="UnityEngine.Camera"/> this marker is attached to (cached).</summary>
        public Camera TargetCamera
        {
            get
            {
                if (_camera == null)
                {
                    _camera = GetComponent<Camera>();
                }

                return _camera;
            }
        }

        /// <summary>
        /// True when this marker applies to <paramref name="roleId"/>: either the marker is unrestricted
        /// (empty <see cref="AgentRoleId"/>) or its role matches <paramref name="roleId"/> (case-insensitive).
        /// </summary>
        public bool AppliesToRole(string roleId)
        {
            if (string.IsNullOrWhiteSpace(agentRoleId))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(roleId) &&
                   string.Equals(agentRoleId.Trim(), roleId.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when <paramref name="roleId"/> may move this camera (marked, movement enabled, role match).</summary>
        public bool IsMovableBy(string roleId)
        {
            return allowMove && AppliesToRole(roleId);
        }

        /// <summary>
        /// Test hook (InternalsVisibleTo CoreAI.Tests): sets the inspector-serialized fields directly so
        /// EditMode tests can exercise the gating rules without a serialized prefab.
        /// </summary>
        internal void SetConfigurationForTests(string role, bool move)
        {
            agentRoleId = role;
            allowMove = move;
        }
    }
}