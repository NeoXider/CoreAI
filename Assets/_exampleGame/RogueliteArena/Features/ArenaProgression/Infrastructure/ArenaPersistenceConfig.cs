using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    [CreateAssetMenu(fileName = "ArenaPersistence", menuName = "CoreAI Example/Arena/Persistence Config", order = 12)]
    public sealed class ArenaPersistenceConfig : ScriptableObject
    {
        [SerializeField] private string metaSaveKey = "CoreAI.Arena.Meta.v1";
        [SerializeField] private int saveSchemaVersion = 1;

        public string MetaSaveKey => metaSaveKey;
        public int SaveSchemaVersion => saveSchemaVersion;
    }
}
