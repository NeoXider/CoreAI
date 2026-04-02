using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Переопределения промптов и кастомные агенты. Создание: Assets → Create → CoreAI → Agent Prompts Manifest.
    /// </summary>
    [CreateAssetMenu(fileName = "AgentPromptsManifest", menuName = "CoreAI/Agent Prompts Manifest")]
    public sealed class AgentPromptsManifest : ScriptableObject
    {
        [System.Serializable]
        public sealed class Entry
        {
            [Tooltip("Id роли: Creator, Programmer, PlayerChat или свой (например MyGame.Economist).")]
            public string roleId;

            [Tooltip("Системный промпт для модели.")]
            public TextAsset systemPrompt;

            [Tooltip("Шаблон user-сообщения для оркестратора; плейсхолдеры: {wave},{mode},{party},{hint}")]
            public TextAsset userPromptTemplate;
        }

        [Header("Переопределения встроенных ролей (опционально)")]
        public List<Entry> roleOverrides = new List<Entry>();

        [Header("Кастомные агенты (ваша игра)")]
        public List<Entry> customAgents = new List<Entry>();

        public IEnumerable<Entry> EnumerateEntries()
        {
            if (roleOverrides != null)
            {
                foreach (var e in roleOverrides)
                    yield return e;
            }

            if (customAgents != null)
            {
                foreach (var e in customAgents)
                    yield return e;
            }
        }
    }
}
