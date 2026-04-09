using UnityEngine;

namespace CoreAI.ExampleGame.SymbiosisMode.Settings
{
    public enum UpgradeType
    {
        MaxHealth,
        Damage,
        Speed,
        Regen,
        Vampirism
    }

    [CreateAssetMenu(fileName = "NewSymbiosisUpgrade", menuName = "Symbiosis/Upgrade Data")]
    public class SymbiosisUpgradeData : ScriptableObject
    {
        public string UpgradeName;
        [TextArea(2, 4)] public string Description;
        public UpgradeType Type;
        public float Amount;
    }
}
