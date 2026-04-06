using System;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Опциональные дефолты для HTTP-тестов (LM Studio и т.п.).
    /// <para>По умолчанию: <b>не использовать</b> захардкоженные URL — все настройки берутся из CoreAISettingsAsset.</para>
    /// <para>Для ручной настройки задайте env vars: <c>COREAI_OPENAI_TEST_BASE</c> / <c>COREAI_OPENAI_TEST_MODEL</c>.</para>
    /// </summary>
    internal static class PlayModeOpenAiTestConfig
    {
        /// <summary>Бэкап URL — НЕ используется по умолчанию. Только через env var.</summary>
        public const string FallbackLmStudioBaseUrl = "http://192.168.56.1:1234/v1";
        /// <summary>Бэкап модель — НЕ используется по умолчанию. Только через env var.</summary>
        public const string FallbackLmStudioModelId = "qwen3.5-35b-a3b-uncensored-hauhaucs-aggressive@iq4_xs";

        /// <summary>По умолчанию: false — все настройки из CoreAISettingsAsset, не из захардкоженных значений.</summary>
        private const bool UseProjectDefaults = false;

        private const string EnvUseProjectDefaults = "COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS";

        public static string ResolveBaseUrl()
        {
            string e = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_BASE");
            if (!string.IsNullOrWhiteSpace(e))
            {
                return e.Trim().TrimEnd('/');
            }

            if (UseProjectDefaults)
            {
                return FallbackLmStudioBaseUrl.TrimEnd('/');
            }

            return null;
        }

        public static string ResolveModelId()
        {
            string e = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_MODEL");
            if (!string.IsNullOrWhiteSpace(e))
            {
                return e.Trim();
            }

            if (UseProjectDefaults)
            {
                return FallbackLmStudioModelId;
            }

            return null;
        }
    }
}