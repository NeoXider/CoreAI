using System;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    ///    HTTP- (LM Studio  ..).
    /// <para> : <b> </b>  URL      CoreAISettingsAsset.</para>
    /// <para>    env vars: <c>COREAI_OPENAI_TEST_BASE</c> / <c>COREAI_OPENAI_TEST_MODEL</c>.</para>
    /// </summary>
    internal static class PlayModeOpenAiTestConfig
    {
        /// <summary> URL     .   env var.</summary>
        public const string FallbackLmStudioBaseUrl = "http://192.168.56.1:1234/v1";

        /// <summary>      .   env var.</summary>
        public const string FallbackLmStudioModelId = "qwen3.5-35b-a3b-uncensored-hauhaucs-aggressive@iq4_xs";

        /// <summary> : false     CoreAISettingsAsset,    .</summary>
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
