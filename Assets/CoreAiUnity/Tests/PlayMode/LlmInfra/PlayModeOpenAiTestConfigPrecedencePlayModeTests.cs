using System;
using System.IO;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// The live suite must follow the model and endpoint selected in the project's CoreAISettings asset:
    /// env vars still win (CI), the asset beats the gitignored local file for base URL and model, and the
    /// file keeps supplying what the asset never holds (the key, provider body fields).
    /// </summary>
    public sealed class PlayModeOpenAiTestConfigPrecedencePlayModeTests
    {
        private static readonly string[] EnvNames =
        {
            PlayModeOpenAiTestConfig.EnvBaseUrl, PlayModeOpenAiTestConfig.EnvApiKey,
            PlayModeOpenAiTestConfig.EnvModel, PlayModeOpenAiTestConfig.EnvLocalConfigPath,
            "COREAI_OPENAI_TEST_BASE", "COREAI_OPENAI_TEST_MODEL", "COREAI_OPENAI_TEST_API_KEY",
            "COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS"
        };

        private readonly System.Collections.Generic.Dictionary<string, string> _savedEnv = new();
        private Func<CoreAISettingsAsset> _savedProvider;
        private CoreAISettingsAsset _asset;
        private string _filePath;

        [SetUp]
        public void SetUp()
        {
            foreach (string name in EnvNames)
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }

            _savedProvider = PlayModeOpenAiTestConfig.ProjectSettingsProvider;
            _filePath = Path.Combine(Application.temporaryCachePath, "coreai-live-tests.precedence.json");
            File.WriteAllText(_filePath,
                "{\"baseUrl\":\"http://file.example/v1\",\"apiKey\":\"file-key\",\"model\":\"file-model\"}");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvLocalConfigPath, _filePath);
        }

        [TearDown]
        public void TearDown()
        {
            PlayModeOpenAiTestConfig.ProjectSettingsProvider = _savedProvider;
            foreach (string name in EnvNames)
            {
                Environment.SetEnvironmentVariable(name, _savedEnv[name]);
            }

            if (_asset != null)
            {
                UnityEngine.Object.DestroyImmediate(_asset);
                _asset = null;
            }

            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }

        [Test]
        public void Resolve_AssetDrivesHttpModel_AssetBeatsTheLocalFileForBaseUrlAndModel_FileStillSuppliesTheKey()
        {
            UseAsset(a => a.ConfigureHttpApi("http://asset.example/v1/", "", "asset-model"));

            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();

            Assert.AreEqual("asset-model", config.Model, "the model selected in the CoreAISettings asset must drive the live suite");
            Assert.AreEqual("http://asset.example/v1", config.BaseUrl);
            Assert.AreEqual("file-key", config.ApiKey, "the asset never holds a key; the local file still supplies it");
            Assert.IsTrue(config.IsComplete);
        }

        [Test]
        public void Resolve_EnvVarsStillWinOverTheAsset()
        {
            UseAsset(a => a.ConfigureHttpApi("http://asset.example/v1", "", "asset-model"));
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvModel, "env-model");
            Environment.SetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBaseUrl, "http://env.example/v1");

            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();

            Assert.AreEqual("env-model", config.Model);
            Assert.AreEqual("http://env.example/v1", config.BaseUrl);
        }

        [Test]
        public void Resolve_PerTestModelOverride_WinsOverTheAsset()
        {
            UseAsset(a => a.ConfigureHttpApi("http://asset.example/v1", "", "asset-model"));

            Assert.AreEqual("vision-model", PlayModeOpenAiTestConfig.Resolve("vision-model").Model);
        }

        [Test]
        public void Resolve_AssetWithoutHttpModel_FallsBackToTheLocalFile()
        {
            UseAsset(a => a.ConfigureOffline());

            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();

            Assert.AreEqual("file-model", config.Model);
            Assert.AreEqual("http://file.example/v1", config.BaseUrl);
        }

        [Test]
        public void Resolve_NoAssetAtAll_FallsBackToTheLocalFile()
        {
            PlayModeOpenAiTestConfig.ProjectSettingsProvider = () => null;

            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();

            Assert.AreEqual("file-model", config.Model);
        }

        private void UseAsset(Action<CoreAISettingsAsset> configure)
        {
            _asset = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            configure(_asset);
            CoreAISettingsAsset captured = _asset;
            PlayModeOpenAiTestConfig.ProjectSettingsProvider = () => captured;
        }
    }
}
