using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Включение OpenAI-совместимого HTTP API вместо локального LLMUnity (выбор в CoreAILifetimeScope на сцене).
    /// Ключ не коммитьте: для билда используйте локальный asset вне репозитория или загрузку из защищённого хранилища.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/LLM/OpenAI-compatible HTTP", fileName = "OpenAiHttpLlmSettings")]
    public sealed class OpenAiHttpLlmSettings : ScriptableObject
    {
        [Tooltip("Если включено, ILlmClient ходит в HTTP chat/completions вместо LLMAgent на сцене.")]
        [SerializeField]
        private bool useOpenAiCompatibleHttp;

        [Tooltip("База без завершающего слэша, например https://api.openai.com/v1 или http://localhost:1234/v1 (LM Studio).")]
        [SerializeField]
        private string apiBaseUrl = "https://api.openai.com/v1";

        [SerializeField]
        private string apiKey = "";

        [SerializeField]
        private string model = "gpt-4o-mini";

        [SerializeField]
        [Range(0f, 2f)]
        private float temperature = 0.2f;

        [SerializeField]
        [Min(5)]
        private int requestTimeoutSeconds = 120;

        public bool UseOpenAiCompatibleHttp => useOpenAiCompatibleHttp;

        public string ApiBaseUrl => string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.openai.com/v1" : apiBaseUrl.TrimEnd('/');

        public string ApiKey => apiKey ?? "";

        public string Model => string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;

        public float Temperature => temperature;

        public int RequestTimeoutSeconds => requestTimeoutSeconds;

        /// <summary>
        /// Для PlayMode-тестов и рантайм-конфигурации без asset (не вызывать из прод-кода без явной политики).
        /// </summary>
        public void SetRuntimeConfiguration(
            bool useOpenAiCompatibleHttp,
            string apiBaseUrl,
            string apiKey,
            string model,
            float temperature = 0.2f,
            int requestTimeoutSeconds = 120)
        {
            this.useOpenAiCompatibleHttp = useOpenAiCompatibleHttp;
            this.apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.openai.com/v1" : apiBaseUrl;
            this.apiKey = apiKey ?? "";
            this.model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            this.temperature = temperature;
            this.requestTimeoutSeconds = requestTimeoutSeconds < 5 ? 5 : requestTimeoutSeconds;
        }
    }
}
