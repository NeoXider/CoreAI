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
        /// <summary>Одна запись манифеста: роль и текстовые ассеты промптов.</summary>
        [System.Serializable]
        public sealed class Entry
        {
            /// <summary>Идентификатор роли (встроенный или свой).</summary>
            [Tooltip("Id роли: Creator, Programmer, PlayerChat или свой (например MyGame.Economist).")]
            public string roleId;

            /// <summary>Системный промпт (текст из TextAsset).</summary>
            [Tooltip("Системный промпт для модели.")]
            public TextAsset systemPrompt;

            /// <summary>Шаблон пользовательского сообщения; плейсхолдеры: wave, mode, party, hint.</summary>
            [Tooltip("Шаблон user-сообщения для оркестратора; плейсхолдеры: {wave},{mode},{party},{hint}")]
            public TextAsset userPromptTemplate;
        }

        /// <summary>Переопределения встроенных ролей CoreAI.</summary>
        [Header("Переопределения встроенных ролей (опционально)")]
        public List<Entry> roleOverrides = new List<Entry>();

        /// <summary>Дополнительные роли игры (не заменяют встроенные по умолчанию).</summary>
        [Header("Кастомные агенты (ваша игра)")]
        public List<Entry> customAgents = new List<Entry>();

        /// <summary>Все записи: сначала переопределения ролей, затем кастомные агенты.</summary>
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
