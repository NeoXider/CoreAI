#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Verifies that the shared live-test setup sends requests to the configured HTTP endpoint.
    /// <para>
    /// <see cref="TestAgentSetup.Initialize"/> picks its backend from <see cref="CoreAISettingsAsset.BackendType"/>
    /// only (it never reads <c>COREAI_PLAYMODE_LLM_BACKEND</c>), so the test installs its own HTTP settings
    /// instance for the duration of the run instead of depending on the committed project asset.
    /// </para>
    /// </summary>
    public sealed class TestAgentSetupHttpRoutingPlayModeTests
    {
        private static readonly string[] EnvNames =
        {
            "COREAI_TEST_BASE_URL", "COREAI_TEST_MODEL", "COREAI_TEST_API_KEY",
            "COREAI_TEST_STREAMING", "COREAI_TEST_NATIVE_TOOLS", "COREAI_TEST_CONFIG"
        };

        // WHY: every piece of process-wide state the test changes is saved in fields and restored in
        // [UnityTearDown]: on a framework timeout abort the test body's finally never runs, and a leaked
        // settings instance or COREAI_TEST_* variable would reroute every later live test to this loopback.
        private Dictionary<string, string> _savedEnv;
        private bool _settingsInstanceReplaced;
        private CoreAISettingsAsset _previousSettings;
        private CoreAISettingsAsset _routingSettings;
        private TestAgentSetup _setup;
        private TcpListener _listener;
        private string _configPath;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _setup?.Dispose();
            _setup = null;

            if (_settingsInstanceReplaced)
            {
                CoreAISettingsAsset.SetInstance(_previousSettings);
                _settingsInstanceReplaced = false;
            }

            _previousSettings = null;
            if (_routingSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_routingSettings);
                _routingSettings = null;
            }

            _listener?.Stop();
            _listener = null;

            if (_configPath != null)
            {
                File.Delete(_configPath);
                _configPath = null;
            }

            if (_savedEnv != null)
            {
                foreach (KeyValuePair<string, string> entry in _savedEnv)
                {
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                }

                _savedEnv = null;
            }

            yield break;
        }

        [UnityTest]
        [Timeout(30000)]
        public IEnumerator Initialize_ExplicitHttpConfig_SendsRequestToConfiguredModel()
        {
            _savedEnv = new Dictionary<string, string>();
            foreach (string name in EnvNames)
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            }

            _configPath = Path.GetTempFileName();
            File.WriteAllText(_configPath, "{}");
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            TcpListener listener = _listener;
            Task<string> serverTask = Task.Run(() => ServeOneRequestAsync(listener));
            _previousSettings = CoreAISettingsAsset.Instance;
            _routingSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // WHY: with the committed asset on Offline the setup would never reach HTTP, and on LlmUnity
            // it would load a GGUF inside this 30 s budget; pin the HTTP backend for this test only.
            _routingSettings.ConfigureHttpApi($"http://127.0.0.1:{port}/v1", "route-probe-key",
                "route-probe-model", timeoutSeconds: 20);
            CoreAISettingsAsset.SetInstance(_routingSettings);
            _settingsInstanceReplaced = true;

            Environment.SetEnvironmentVariable("COREAI_TEST_BASE_URL", $"http://127.0.0.1:{port}/v1");
            Environment.SetEnvironmentVariable("COREAI_TEST_MODEL", "route-probe-model");
            Environment.SetEnvironmentVariable("COREAI_TEST_API_KEY", "route-probe-key");
            Environment.SetEnvironmentVariable("COREAI_TEST_STREAMING", "false");
            Environment.SetEnvironmentVariable("COREAI_TEST_NATIVE_TOOLS", "false");
            Environment.SetEnvironmentVariable("COREAI_TEST_CONFIG", _configPath);

            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            Assert.IsTrue(_setup.IsReady, "The configured HTTP test backend must be ready.");

            Task<LlmCompletionResult> completion = _setup.Client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "RoutingProbe",
                SystemPrompt = "Answer briefly.",
                UserPayload = "Say route-ok."
            });
            yield return PlayModeTestAwait.WaitTask(completion, 20f, "configured HTTP routing");

            Assert.IsTrue(completion.Result.Ok, completion.Result.Error?.ToString());
            Assert.That(completion.Result.Content, Does.Contain("route-ok"),
                "The response must come from the configured loopback endpoint.");
            string requestBody = serverTask.GetAwaiter().GetResult();
            JObject requestJson = JObject.Parse(requestBody);
            Assert.AreEqual("route-probe-model", (string)requestJson["model"],
                "The selected test model must be sent to the HTTP provider.");
        }

        private static async Task<string> ServeOneRequestAsync(TcpListener listener)
        {
            using TcpClient socket = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = socket.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8, false, 1024, true);

            string requestLine = await reader.ReadLineAsync();
            Assert.That(requestLine, Does.Contain("/v1/chat/completions"),
                "The HTTP client must use the configured API base URL.");

            int contentLength = 0;
            string header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(header.Substring("Content-Length:".Length).Trim());
                }
            }

            char[] bodyChars = new char[contentLength];
            int received = 0;
            while (received < contentLength)
            {
                int count = await reader.ReadAsync(bodyChars, received, contentLength - received);
                if (count == 0)
                {
                    throw new IOException("The HTTP request body ended early.");
                }

                received += count;
            }

            const string responseBody =
                "{\"id\":\"route-probe\",\"object\":\"chat.completion\",\"created\":1," +
                "\"model\":\"route-probe-model\",\"choices\":[{\"index\":0," +
                "\"message\":{\"role\":\"assistant\",\"content\":\"route-ok\"}," +
                "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1," +
                "\"completion_tokens\":1,\"total_tokens\":2}}";
            byte[] payload = Encoding.UTF8.GetBytes(responseBody);
            byte[] headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, 0, headers.Length);
            await stream.WriteAsync(payload, 0, payload.Length);
            await stream.FlushAsync();
            return new string(bodyChars);
        }
    }
}
#endif
