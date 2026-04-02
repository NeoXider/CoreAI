using UnityEngine;

namespace CoreAI.Presentation.AiDashboard
{
    /// <summary>
    /// Политика разрешённых ролей ИИ для UI и оркестратора (MVP).
    /// </summary>
    [CreateAssetMenu(fileName = "AiPermissions", menuName = "CoreAI/Ai Permissions", order = 0)]
    public sealed class AiPermissionsAsset : ScriptableObject
    {
        [SerializeField]
        private bool allowCreator = true;

        [SerializeField]
        private bool allowAnalyzer = true;

        [SerializeField]
        private bool allowCoreMechanic = true;

        public bool AllowCreator => allowCreator;
        public bool AllowAnalyzer => allowAnalyzer;
        public bool AllowCoreMechanic => allowCoreMechanic;
    }
}
