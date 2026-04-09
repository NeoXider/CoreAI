using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Версионирование системных промптов: трекинг версий, A/B тесты, откаты.
    /// </summary>
    public interface IPromptVersionRegistry
    {
        /// <summary>Зарегистрировать промпт для роли (создаёт новую версию).</summary>
        string Register(string roleId, string promptText, string label = null);

        /// <summary>Получить текущий активный промпт для роли.</summary>
        string GetActive(string roleId);

        /// <summary>Откатиться к предыдущей версии.</summary>
        bool Rollback(string roleId);

        /// <summary>Получить историю версий для роли.</summary>
        IReadOnlyList<PromptVersion> GetHistory(string roleId);

        /// <summary>Выбрать вариант A/B (по весу или имени). Null = текущий активный.</summary>
        string ResolveVariant(string roleId, string variantName = null);

        /// <summary>Добавить A/B вариант для роли (активируется через ResolveVariant).</summary>
        void AddVariant(string roleId, string variantName, string promptText);
    }

    /// <summary>Одна версия промпта.</summary>
    public sealed class PromptVersion
    {
        /// <summary>Уникальный id версии (hash).</summary>
        public string VersionId { get; set; }

        /// <summary>Текст промпта.</summary>
        public string Text { get; set; }

        /// <summary>Метка версии (например "v2.1-concise").</summary>
        public string Label { get; set; }

        /// <summary>Время создания.</summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>Является ли текущей активной версией.</summary>
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// In-memory реализация <see cref="IPromptVersionRegistry"/>.
    /// </summary>
    public sealed class InMemoryPromptVersionRegistry : IPromptVersionRegistry
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<PromptVersion>> _history = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _activeIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _variants = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string Register(string roleId, string promptText, string label = null)
        {
            if (string.IsNullOrEmpty(roleId)) throw new ArgumentException("roleId required", nameof(roleId));
            if (string.IsNullOrEmpty(promptText)) throw new ArgumentException("promptText required", nameof(promptText));

            lock (_lock)
            {
                if (!_history.TryGetValue(roleId, out List<PromptVersion> list))
                {
                    list = new List<PromptVersion>();
                    _history[roleId] = list;
                }

                // Деактивируем предыдущую
                foreach (PromptVersion pv in list)
                {
                    pv.IsActive = false;
                }

                string versionId = ComputeHash(promptText);
                PromptVersion version = new()
                {
                    VersionId = versionId,
                    Text = promptText,
                    Label = label ?? $"v{list.Count + 1}",
                    CreatedUtc = DateTime.UtcNow,
                    IsActive = true
                };

                list.Add(version);
                _activeIndex[roleId] = list.Count - 1;
                return versionId;
            }
        }

        /// <inheritdoc />
        public string GetActive(string roleId)
        {
            lock (_lock)
            {
                if (_history.TryGetValue(roleId, out List<PromptVersion> list) &&
                    _activeIndex.TryGetValue(roleId, out int idx) &&
                    idx >= 0 && idx < list.Count)
                {
                    return list[idx].Text;
                }

                return null;
            }
        }

        /// <inheritdoc />
        public bool Rollback(string roleId)
        {
            lock (_lock)
            {
                if (!_history.TryGetValue(roleId, out List<PromptVersion> list) ||
                    !_activeIndex.TryGetValue(roleId, out int idx) ||
                    idx <= 0)
                {
                    return false;
                }

                list[idx].IsActive = false;
                idx--;
                list[idx].IsActive = true;
                _activeIndex[roleId] = idx;
                return true;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<PromptVersion> GetHistory(string roleId)
        {
            lock (_lock)
            {
                if (_history.TryGetValue(roleId, out List<PromptVersion> list))
                {
                    return new List<PromptVersion>(list);
                }

                return Array.Empty<PromptVersion>();
            }
        }

        /// <inheritdoc />
        public string ResolveVariant(string roleId, string variantName = null)
        {
            if (string.IsNullOrEmpty(variantName))
            {
                return GetActive(roleId);
            }

            lock (_lock)
            {
                if (_variants.TryGetValue(roleId, out Dictionary<string, string> vars) &&
                    vars.TryGetValue(variantName, out string promptText))
                {
                    return promptText;
                }

                return GetActive(roleId);
            }
        }

        /// <inheritdoc />
        public void AddVariant(string roleId, string variantName, string promptText)
        {
            if (string.IsNullOrEmpty(roleId)) throw new ArgumentException("roleId required", nameof(roleId));
            if (string.IsNullOrEmpty(variantName))
                throw new ArgumentException("variantName required", nameof(variantName));

            lock (_lock)
            {
                if (!_variants.TryGetValue(roleId, out Dictionary<string, string> vars))
                {
                    vars = new Dictionary<string, string>(StringComparer.Ordinal);
                    _variants[roleId] = vars;
                }

                vars[variantName] = promptText;
            }
        }

        private static string ComputeHash(string input)
        {
            // Simple deterministic hash for version tracking
            unchecked
            {
                int hash = 17;
                foreach (char c in input)
                {
                    hash = hash * 31 + c;
                }

                return hash.ToString("x8");
            }
        }
    }
}
