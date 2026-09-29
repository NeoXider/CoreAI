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
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Verifies that the shared live-test setup sends requests to the configured HTTP endpoint.
    /// </summary>
    public sealed class TestAgentSetupHttpRoutingPlayModeTests
    {
        private static readonly string[] EnvNames =
        {
            "COREAI_TEST_BASE_URL", "COREAI_TEST_MODEL", "COREAI_TEST_API_KEY",
            "COREAI_TEST_STREAMING", "COREAI_TEST_NATIVE_TOOLS", "COREAI_TEST_CONFIG",
            "COREAI_PLAYMODE_LLM_BACKEND"
        };

        [UnityTest]
        [Timeout(30000)]
        public IEnumerator Initialize_ExplicitHttpConfig_SendsRequestToConfiguredModel()
        {
            Dictionary<string, string> savedEnv = new();
            foreach (string name in EnvNames)
            {
                savedEnv[name] = Environment.GetEnvironmentVariable(name);
            }

            string configPath = Path.GetTempFileName();
            File.WriteAllText(configPath, "{}");
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<string> serverTask = Task.Run(() => ServeOneRequestAsync(listener));
            TestAgentSetup setup = null;

            try
            {
                Environment.SetEnvironmentVariable("COREAI_TEST_BASE_URL", $"http://127.0.0.1:{port}/v1");
                Environment.SetEnvironmentVariable("COREAI_TEST_MODEL", "route-probe-model");
                Environment.SetEnvironmentVariable("COREAI_TEST_API_KEY", "route-probe-key");
                Environment.SetEnvironmentVariable("COREAI_TEST_STREAMING", "false");
                Environment.SetEnvironmentVariable("COREAI_TEST_NATIVE_TOOLS", "false");
                Environment.SetEnvironmentVariable("COREAI_TEST_CONFIG", configPath);
                Environment.SetEnvironmentVariable("COREAI_PLAYMODE_LLM_BACKEND", "http");

                setup = new TestAgentSetup();
                yield return setup.Initialize();
                Assert.IsTrue(setup.IsReady, "The configured HTTP test backend must be ready.");

                Task<LlmCompletionResult> completion = setup.Client.CompleteAsync(new LlmCompletionRequest
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
            finally
            {
                setup?.Dispose();
                listener.Stop();
                File.Delete(configPath);
                foreach (string name in EnvNames)
                {
                    Environment.SetEnvironmentVariable(name, savedEnv[name]);
                }
            }
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
