using UnityEngine;

namespace CoreAI.Presentation.AiDashboard
{
    /// <summary>
    /// ScriptableObject that stores AI permission settings for dashboard workflows.
    /// </summary>
    [CreateAssetMenu(fileName = "AiPermissions", menuName = "CoreAI/Ai Permissions", order = 0)]
    public sealed class AiPermissionsAsset : ScriptableObject, IAiPermissions
    {
        [SerializeField] private bool allowCreator = true;

        [SerializeField] private bool allowAnalyzer = true;

        [SerializeField] private bool allowCoreMechanic = true;

        /// <summary>Allow creator.</summary>
        public bool AllowCreator => allowCreator;

        /// <summary>Allow analyzer.</summary>
        public bool AllowAnalyzer => allowAnalyzer;

        /// <summary>Allow core mechanic.</summary>
        public bool AllowCoreMechanic => allowCoreMechanic;

        /// <summary>
        /// Builds a Unity-free permissions snapshot for runtime consumers and tests.
        /// </summary>
        public AiPermissionsOptions ToOptions()
        {
            return new AiPermissionsOptions
            {
                AllowCreator = AllowCreator,
                AllowAnalyzer = AllowAnalyzer,
                AllowCoreMechanic = AllowCoreMechanic
            };
        }

        /// <summary>
        /// Copies portable permissions into this Unity authoring asset.
        /// </summary>
        public void ApplyOptions(AiPermissionsOptions options)
        {
            if (options == null)
            {
                return;
            }

            allowCreator = options.AllowCreator;
            allowAnalyzer = options.AllowAnalyzer;
            allowCoreMechanic = options.AllowCoreMechanic;
        }
    }
}