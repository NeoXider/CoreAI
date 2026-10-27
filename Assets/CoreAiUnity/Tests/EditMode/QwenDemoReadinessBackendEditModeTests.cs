using System;
using System.Reflection;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>HTTP readiness must not depend on a native host, including in a WebGL player.</summary>
    public sealed class QwenDemoReadinessBackendEditModeTests
    {
        private CoreAISettingsAsset _settings;
        private MethodInfo _evaluate;

        [SetUp]
        public void SetUp()
        {
            Type readiness = Type.GetType("CoreAI.Demos.QwenDemo.QwenDemoReadiness, CoreAI.Demos", false);
            if (readiness == null)
            {
                Assert.Ignore("CoreAI.Demos is not installed; Qwen demo readiness is unavailable.");
            }

            _evaluate = readiness.GetMethod("EvaluateBackendReadiness", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(_evaluate);
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
            {
                Object.DestroyImmediate(_settings);
            }
        }

        [TestCase(RuntimePlatform.WindowsEditor)]
        [TestCase(RuntimePlatform.WebGLPlayer)]
        public void ConfiguredHttpScope_DoesNotWaitForNativeHost(RuntimePlatform platform)
        {
            _settings.ConfigureHttpApi("http://127.0.0.1:1234/v1", "", "qa-model");
            string error = Evaluate(true, platform, out bool native);
            Assert.IsNull(error);
            Assert.IsFalse(native, "A configured HTTP scene must bypass native-host lookup and port polling.");
        }

        [Test]
        public void LocalBackend_OnUnsupportedPlatform_KeepsTheNativeLimitation()
        {
            _settings.ConfigureLlmUnity();
            string error = Evaluate(true, RuntimePlatform.WebGLPlayer, out bool native);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
            Assert.AreEqual(LocalModelPlatformSupport.BrowserUnavailableMessage, error);
            Assert.IsFalse(native, "An unsupported native backend must fail before polling a host.");
        }

        [Test]
        public void LocalBackend_OnDesktop_StillRequiresNativeHostAndSocket()
        {
            _settings.ConfigureLlmUnity();
            Assert.IsNull(Evaluate(true, RuntimePlatform.WindowsEditor, out bool native));
            Assert.IsTrue(native);
        }

        [Test]
        public void OfflineScope_DoesNotClaimReadiness()
        {
            _settings.ConfigureOffline();
            StringAssert.Contains("Offline", Evaluate(true, RuntimePlatform.WindowsEditor, out bool native));
            Assert.IsFalse(native);
        }

        [Test]
        public void HttpWithoutModel_DoesNotClaimReadiness()
        {
            _settings.ConfigureHttpApi("http://127.0.0.1:1234/v1", "", "");
            StringAssert.Contains("model id", Evaluate(true, RuntimePlatform.WindowsEditor, out bool native));
            Assert.IsFalse(native);
        }

        [TestCase("not-an-endpoint")]
        [TestCase("file:///tmp/provider")]
        public void InvalidHttpEndpoint_DoesNotClaimReadiness(string endpoint)
        {
            _settings.ConfigureHttpApi(endpoint, "", "qa-model");
            StringAssert.Contains("http(s)", Evaluate(true, RuntimePlatform.WindowsEditor, out bool native));
            Assert.IsFalse(native);
        }

        [Test]
        public void HttpWithoutReadyScope_DoesNotClaimReadiness()
        {
            _settings.ConfigureHttpApi("http://127.0.0.1:1234/v1", "", "qa-model");
            StringAssert.Contains("scope is not ready", Evaluate(false, RuntimePlatform.WebGLPlayer, out bool native));
            Assert.IsFalse(native);
        }

        [Test]
        public void ServerManagedHttp_AllowsTheServerToSelectTheModel()
        {
            _settings.ConfigureServerManagedApi("http://127.0.0.1:1234/v1", "", "");
            Assert.IsNull(Evaluate(true, RuntimePlatform.WebGLPlayer, out bool native));
            Assert.IsFalse(native);
        }

        private string Evaluate(bool ready, RuntimePlatform platform, out bool native)
        {
            object[] arguments = { _settings, ready, platform, false };
            string error = (string)_evaluate.Invoke(null, arguments);
            native = (bool)arguments[3];
            return error;
        }
    }
}
