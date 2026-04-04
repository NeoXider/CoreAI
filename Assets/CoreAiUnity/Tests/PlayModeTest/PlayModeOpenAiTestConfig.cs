using System;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Опциональные дефолты для HTTP-тестов (LM Studio и т.п.). CI не ходит в сеть без явного согласия:
    /// задайте <c>COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS=1</c> или переменные <c>COREAI_OPENAI_TEST_BASE</c> / <c>COREAI_OPENAI_TEST_MODEL</c>.
    /// </summary>
    internal static class PlayModeOpenAiTestConfig
    {
        public const string DefaultLmStudioBaseUrl = "http://192.168.56.1:1234/v1";
        public const string DefaultLmStudioModelId = "qwen3.5-4b";

        private const string EnvUseProjectDefaults = "COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS";

        public static bool UseProjectDefaults => true;

        public static string ResolveBaseUrl()
        {
            string e = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_BASE");
            if (!string.IsNullOrWhiteSpace(e))
            {
                return e.Trim().TrimEnd('/');
            }

            if (UseProjectDefaults)
            {
                return DefaultLmStudioBaseUrl.TrimEnd('/');
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
                return DefaultLmStudioModelId;
            }

            return null;
        }
    }
}