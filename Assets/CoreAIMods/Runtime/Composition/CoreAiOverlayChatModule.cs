using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Composition
{
    /// <summary>
    /// The play world's chat slot (P2): the in-world chat overlay of a CoreAI scope the bootstrap has not built yet
    /// is switched on and answered by the building role, so the first line a player types can change the world.
    /// The overlay itself, its focus gate and its commands (<c>/undo</c>, <c>/redo</c>, <c>/fly</c> from the mods
    /// scope) come from the scopes; this module only chooses the preset.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoreAiOverlayChatModule : MonoBehaviour, ICoreAiPlayWorldModule
    {
        [Tooltip("Agent role that answers the overlay; empty keeps the scope's own choice.")]
        [SerializeField]
        private string roleId = BuiltInAgentRoleIds.Programmer;

        /// <summary>Agent role that answers the overlay; empty keeps the scope's own choice.</summary>
        public string RoleId
        {
            get => roleId ?? "";
            set => roleId = value;
        }

        /// <inheritdoc />
        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
            CoreAILifetimeScope core = composition?.CoreScope;
            if (core == null || core.Container != null)
            {
                return;
            }

            core.ChatOverlay.Enabled = true;
            if (!string.IsNullOrWhiteSpace(roleId) && string.IsNullOrWhiteSpace(core.ChatOverlay.RoleId))
            {
                core.ChatOverlay.RoleId = roleId.Trim();
            }
        }

        /// <inheritdoc />
        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
        }
    }
}
