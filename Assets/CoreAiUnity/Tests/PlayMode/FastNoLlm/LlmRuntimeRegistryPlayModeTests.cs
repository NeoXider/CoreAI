using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class LlmRuntimeRegistryPlayModeTests
    {
        private sealed class NamedClient : ILlmClient
        {
            private readonly string _name;
            private readonly Task _gate;

            public NamedClient(string name, Task gate = null)
            {
                _name = name;
                _gate = gate;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (_gate != null)
                {
                    await _gate;
                }
                else
                {
                    await Task.Yield();
                }
                return new LlmCompletionResult { Ok = true, Content = _name };
            }
        }

        private sealed class LocalHttpServer : System.IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _statusCode;
            private readonly string _expectedAuthorization;

            public LocalHttpServer(int statusCode, string expectedAuthorization = "")
            {
                _statusCode = statusCode;
                _expectedAuthorization = expectedAuthorization;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = ServeOnceAsync();
            }

            public int Port { get; }

            private async Task ServeOnceAsync()
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();
                byte[] requestBytes = new byte[8192];
                int read = await stream.ReadAsync(requestBytes, 0, requestBytes.Length);
                string request = Encoding.ASCII.GetString(requestBytes, 0, read);
                bool validPath = request.StartsWith("GET /v1/models ", System.StringComparison.Ordinal);
                bool validAuthorization = string.IsNullOrEmpty(_expectedAuthorization) ||
                    request.IndexOf("Authorization: " + _expectedAuthorization,
                        System.StringComparison.OrdinalIgnoreCase) >= 0;
                int code = validPath && validAuthorization ? _statusCode : 400;
                string reason = code == 200 ? "OK" : "Error";
                byte[] body = Encoding.UTF8.GetBytes(code == 200 ? "{\"data\":[]}" : "{}");
                byte[] response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length);
                await stream.WriteAsync(body, 0, body.Length);
            }

            public void Dispose()
            {
                _listener.Stop();
            }
        }

        private sealed class FallbackHttpServer : System.IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _modelsStatus;
            private readonly int _completionsStatus;
            private readonly string _expectedAuthorization;

            public FallbackHttpServer(
                int modelsStatus,
                int completionsStatus,
                string expectedAuthorization = "")
            {
                _modelsStatus = modelsStatus;
                _completionsStatus = completionsStatus;
                _expectedAuthorization = expectedAuthorization;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = ServeAsync();
            }

            public int Port { get; }

            private async Task ServeAsync()
            {
                try
                {
                    await ServeRequestAsync("GET /v1/models ", _modelsStatus);
                    if (_modelsStatus is 404 or 405)
                    {
                        await ServeRequestAsync("POST /v1/chat/completions ", _completionsStatus);
                    }
                }
                catch (System.ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
            }

            private async Task ServeRequestAsync(string expectedPath, int configuredStatus)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();
                byte[] requestBytes = new byte[8192];
                int read = await stream.ReadAsync(requestBytes, 0, requestBytes.Length);
                string request = Encoding.ASCII.GetString(requestBytes, 0, read);
                bool validPath = request.StartsWith(expectedPath, System.StringComparison.Ordinal);
                bool validAuthorization = string.IsNullOrEmpty(_expectedAuthorization) ||
                    request.IndexOf("Authorization: " + _expectedAuthorization,
                        System.StringComparison.OrdinalIgnoreCase) >= 0;
                int code = !validPath ? 404 : validAuthorization ? configuredStatus : 401;
                string reason = code is >= 200 and < 300 ? "OK" : "Error";
                byte[] body = Encoding.UTF8.GetBytes("{}");
                byte[] response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length);
                await stream.WriteAsync(body, 0, body.Length);
            }

            public void Dispose()
            {
                _listener.Stop();
            }
        }

        private sealed class CompletionHttpServer : System.IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _statusCode;
            private readonly bool _respond;

            public CompletionHttpServer(int statusCode, bool respond = true)
            {
                _statusCode = statusCode;
                _respond = respond;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = ServeAsync();
            }

            public int Port { get; }
            public TaskCompletionSource<bool> Disconnected { get; } = new();

            private async Task ServeAsync()
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    byte[] requestBytes = new byte[8192];
                    int read = await stream.ReadAsync(requestBytes, 0, requestBytes.Length);
                    if (!_respond)
                    {
                        try
                        {
                            byte[] closeProbe = new byte[1];
                            int closeRead = await stream.ReadAsync(closeProbe, 0, closeProbe.Length);
                            Disconnected.TrySetResult(closeRead == 0);
                        }
                        catch (IOException)
                        {
                            Disconnected.TrySetResult(true);
                        }
                        catch (SocketException)
                        {
                            Disconnected.TrySetResult(true);
                        }
                        return;
                    }

                    string request = Encoding.ASCII.GetString(requestBytes, 0, read);
                    bool validPath = request.StartsWith(
                        "POST /v1/chat/completions ",
                        System.StringComparison.Ordinal);
                    int code = validPath ? _statusCode : 404;
                    byte[] body = Encoding.UTF8.GetBytes("{}");
                    byte[] response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {code} Result\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length);
                    await stream.WriteAsync(body, 0, body.Length);
                }
                catch (System.ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
            }

            public void Dispose()
            {
                _listener.Stop();
            }
        }

        private sealed class NamedFactory : ILlmEndpointClientFactory
        {
            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = new NamedClient(string.IsNullOrWhiteSpace(descriptor.Model)
                        ? descriptor.EndpointId
                        : descriptor.Model),
                    Mode = LlmExecutionMode.Offline
                });
            }
        }

        private sealed class ConcurrentFactory : ILlmEndpointClientFactory
        {
            private int _entered;

            public TaskCompletionSource<bool> BothEntered { get; } = new();
            public TaskCompletionSource<bool> Release { get; } = new();

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = new ConcurrentClient(this, descriptor.Model),
                    Mode = LlmExecutionMode.Offline
                });
            }

            private sealed class ConcurrentClient : ILlmClient
            {
                private readonly ConcurrentFactory _owner;
                private readonly string _name;

                public ConcurrentClient(ConcurrentFactory owner, string name)
                {
                    _owner = owner;
                    _name = name;
                }

                public async Task<LlmCompletionResult> CompleteAsync(
                    LlmCompletionRequest request,
                    CancellationToken cancellationToken = default)
                {
                    if (Interlocked.Increment(ref _owner._entered) == 2)
                    {
                        _owner.BothEntered.TrySetResult(true);
                    }

                    await _owner.Release.Task;
                    return new LlmCompletionResult { Ok = true, Content = _name };
                }
            }
        }

        private sealed class HotSwapFactory : ILlmEndpointClientFactory
        {
            public TaskCompletionSource<bool> OldGate { get; } = new();
            public TaskCompletionSource<bool> NewReady { get; } = new();

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                if (descriptor.Model == "bad")
                {
                    return Task.FromException<LlmEndpointClientActivation>(
                        new System.InvalidOperationException("candidate failed"));
                }

                return BuildAsync(descriptor);
            }

            private async Task<LlmEndpointClientActivation> BuildAsync(LlmEndpointDescriptor descriptor)
            {
                if (descriptor.Model == "new")
                {
                    await NewReady.Task;
                }

                return new LlmEndpointClientActivation
                {
                    Client = new NamedClient(descriptor.Model,
                        descriptor.Model == "old" ? OldGate.Task : null),
                    Mode = LlmExecutionMode.Offline
                };
            }
        }

        [UnityTest]
        public IEnumerator TwoAgents_RunConcurrentlyOnDifferentActiveEndpoints()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            ConcurrentFactory factory = new();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, factory);
            RoutingLlmClient routing = new(registry);
            Task setup = SetupAsync(registry);
            while (!setup.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(setup.IsFaulted, setup.Exception?.ToString());
            Task<LlmCompletionResult> api = routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "CloudAgent"
            });
            Task<LlmCompletionResult> local = routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "LocalAgent"
            });
            while (!factory.BothEntered.Task.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(api.IsCompleted);
            Assert.IsFalse(local.IsCompleted);
            factory.Release.SetResult(true);
            Task all = Task.WhenAll(api, local);
            while (!all.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("api", api.Result.Content);
            Assert.AreEqual("local", local.Result.Content);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator RouteSwitch_AffectsNextRequestWithoutRecreatingRegistry()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, new NamedFactory());
            RoutingLlmClient routing = new(registry);
            Task setup = SetupAsync(registry);
            while (!setup.IsCompleted)
            {
                yield return null;
            }

            registry.AssignRoleProfile("CloudAgent", "local");
            Task<LlmCompletionResult> switched = routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "CloudAgent"
            });
            while (!switched.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("local", switched.Result.Content);
            Assert.AreEqual("local", registry.GetRoleProfile("CloudAgent"));
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator ZeroEndpoints_AutomaticRouteUsesLegacyFallback()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, new NamedFactory());
            registry.SetLegacyFallback(new NamedClient("legacy"));

            Task<LlmCompletionResult> completion = registry.ResolveClientForRole("Chat")
                .CompleteAsync(new LlmCompletionRequest());
            while (!completion.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("legacy", completion.Result.Content);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator HttpFactory_ModelsProbeRequiresSuccessAndForwardsCredential()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);
            using (LocalHttpServer server = new(200, "Bearer session-key"))
            {
                Task<LlmEndpointClientActivation> success = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "session-key", CancellationToken.None);
                while (!success.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsFalse(success.IsFaulted, success.Exception?.ToString());
                Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, success.Result.Mode);
            }

            foreach (int status in new[] { 401, 403, 500 })
            {
                using LocalHttpServer server = new(status);
                Task<LlmEndpointClientActivation> failed = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "", CancellationToken.None);
                while (!failed.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsTrue(failed.IsFaulted, $"HTTP {status} must fail readiness.");
            }

            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator HttpFactory_FallsBackToCompletionsWhenModelsRouteIsUnavailable()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);

            foreach (int modelsStatus in new[] { 404, 405 })
            {
                using FallbackHttpServer server = new(
                    modelsStatus, 400, "Bearer session-key");
                Task<LlmEndpointClientActivation> success = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "session-key", CancellationToken.None);
                while (!success.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsFalse(success.IsFaulted, success.Exception?.ToString());
                Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, success.Result.Mode);
            }

            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator HttpFactory_CompletionsFallbackRejectsAuthAndServerFailures()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);

            foreach (int status in new[] { 401, 403, 404, 500 })
            {
                using FallbackHttpServer server = new(404, status);
                Task<LlmEndpointClientActivation> failed = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "", CancellationToken.None);
                while (!failed.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsTrue(failed.IsFaulted, $"Fallback HTTP {status} must fail readiness.");
            }

            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator HttpFactory_ModelsProbeRejectsConnectionFailure()
        {
            TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int closedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);

            Task<LlmEndpointClientActivation> failed = factory.ActivateAsync(
                HttpDescriptor(closedPort), "", CancellationToken.None);
            while (!failed.IsCompleted)
            {
                yield return null;
            }

            Assert.IsTrue(failed.IsFaulted);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator UnityReadinessProbe_CompletionsOnlyUsesSharedStatusPolicy()
        {
            UnityWebRequestOpenAiReadinessProbe probe = new();
            foreach (int status in new[] { 200, 204, 400, 405, 422, 429, 302, 401, 403, 404, 500 })
            {
                using CompletionHttpServer server = new(status);
                Task<LlmEndpointReadinessResult> task = probe.ProbeAsync(
                    new LlmEndpointReadinessRequest
                    {
                        BaseUrl = $"http://127.0.0.1:{server.Port}/v1",
                        Mode = LlmEndpointReadinessMode.CompletionsOnly
                    });
                while (!task.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
                Assert.AreEqual(
                    LlmEndpointReadinessPolicy.IsHandlerReached(status),
                    task.Result.IsReady,
                    $"HTTP {status}");
            }
        }

        [UnityTest]
        public IEnumerator UnityReadinessProbe_CancellationAbortsInFlightRequest()
        {
            UnityWebRequestOpenAiReadinessProbe probe = new();
            using CompletionHttpServer server = new(0, respond: false);
            using CancellationTokenSource cancellation = new();
            Task<LlmEndpointReadinessResult> task = probe.ProbeAsync(
                new LlmEndpointReadinessRequest
                {
                    BaseUrl = $"http://127.0.0.1:{server.Port}/v1",
                    Mode = LlmEndpointReadinessMode.CompletionsOnly,
                    TimeoutSeconds = 10
                },
                cancellation.Token);
            cancellation.CancelAfter(50);
            while (!task.IsCompleted)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCanceled, task.Exception?.ToString());
            float disconnectDeadline = Time.realtimeSinceStartup + 2f;
            while (!server.Disconnected.Task.IsCompleted && Time.realtimeSinceStartup < disconnectDeadline)
            {
                yield return null;
            }

            Assert.IsTrue(server.Disconnected.Task.IsCompleted, "Abort must close the native HTTP connection.");
            Assert.IsTrue(server.Disconnected.Task.Result);
        }

        [UnityTest]
        public IEnumerator DisabledEndpointWithoutAssignment_DoesNotBreakLegacyFallback()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, new NamedFactory());
            registry.SetLegacyFallback(new NamedClient("legacy"));
            LlmEndpointDescriptor disabled = Descriptor("disabled");
            disabled.Active = false;
            Task<LlmEndpointSnapshot> add = registry.AddOrUpdateEndpointAsync(disabled);
            while (!add.IsCompleted)
            {
                yield return null;
            }

            Task<LlmCompletionResult> completion = registry.ResolveClientForRole("Chat")
                .CompleteAsync(new LlmCompletionRequest());
            while (!completion.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("legacy", completion.Result.Content);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator ExplicitRequestProfile_WinsRoleAssignment()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, new NamedFactory());
            RoutingLlmClient routing = new(registry);
            Task setup = SetupAsync(registry);
            while (!setup.IsCompleted)
            {
                yield return null;
            }

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "CloudAgent",
                RoutingProfileId = "local"
            };
            Task<LlmCompletionResult> completion = routing.CompleteAsync(request);
            while (!completion.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("local", completion.Result.Content);
            Assert.AreEqual("local", request.RoutingProfileId);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator HotReplacement_KeepsResolvedInFlightClientAndRoutesNewCallsToNewGeneration()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            HotSwapFactory factory = new();
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, factory);
            LlmEndpointDescriptor oldDescriptor = Descriptor("shared");
            oldDescriptor.Model = "old";
            Task<LlmEndpointSnapshot> initial = registry.AddOrUpdateEndpointAsync(oldDescriptor);
            while (!initial.IsCompleted)
            {
                yield return null;
            }

            ILlmClient oldClient = registry.ResolveClientForRole("Agent", "shared");
            Task<LlmCompletionResult> oldCall = oldClient.CompleteAsync(new LlmCompletionRequest());
            LlmEndpointDescriptor replacement = Descriptor("shared");
            replacement.Model = "new";
            Task<LlmEndpointSnapshot> update = registry.AddOrUpdateEndpointAsync(replacement);
            yield return null;

            Task<LlmCompletionResult> duringWarmup = registry.ResolveClientForRole("Agent", "shared")
                .CompleteAsync(new LlmCompletionRequest());
            Assert.IsFalse(update.IsCompleted);
            Assert.IsFalse(duringWarmup.IsCompleted);
            factory.NewReady.SetResult(true);
            while (!update.IsCompleted)
            {
                yield return null;
            }

            Task<LlmCompletionResult> newCall = registry.ResolveClientForRole("Agent", "shared")
                .CompleteAsync(new LlmCompletionRequest());
            while (!newCall.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("new", newCall.Result.Content);
            Assert.IsFalse(oldCall.IsCompleted);
            factory.OldGate.SetResult(true);
            while (!oldCall.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("old", oldCall.Result.Content);
            Assert.Greater(update.Result.Generation, initial.Result.Generation);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator FailedHotReplacement_LeavesOldReadyGenerationRoutable()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            HotSwapFactory factory = new();
            factory.OldGate.SetResult(true);
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, factory);
            LlmEndpointDescriptor oldDescriptor = Descriptor("shared");
            oldDescriptor.Model = "old";
            Task<LlmEndpointSnapshot> initial = registry.AddOrUpdateEndpointAsync(oldDescriptor);
            while (!initial.IsCompleted)
            {
                yield return null;
            }

            LlmEndpointDescriptor failed = Descriptor("shared");
            failed.Model = "bad";
            Task<LlmEndpointSnapshot> update = registry.AddOrUpdateEndpointAsync(failed);
            while (!update.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual(LlmEndpointLifecycleState.Failed, update.Result.State);
            Task<LlmCompletionResult> afterFailure = registry.ResolveClientForRole("Agent", "shared")
                .CompleteAsync(new LlmCompletionRequest());
            while (!afterFailure.IsCompleted)
            {
                yield return null;
            }

            Assert.AreEqual("old", afterFailure.Result.Content);
            Assert.AreEqual(initial.Result.Generation, registry.GetEndpoints()[0].Generation);
            Object.Destroy(settings);
        }

        private static async Task SetupAsync(LlmClientRegistry registry)
        {
            await registry.AddOrUpdateEndpointAsync(Descriptor("api"));
            await registry.AddOrUpdateEndpointAsync(Descriptor("local"));
            registry.AssignRoleProfile("CloudAgent", "api");
            registry.AssignRoleProfile("LocalAgent", "local");
        }

        private static LlmEndpointDescriptor Descriptor(string id)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = id,
                Kind = LlmEndpointKind.Offline,
                Model = id,
                ContextWindowTokens = 4096,
                Active = true
            };
        }

        private static LlmEndpointDescriptor HttpDescriptor(int port)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = "http-test",
                DisplayName = "HTTP test",
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = $"http://127.0.0.1:{port}/v1",
                Model = "test",
                ContextWindowTokens = 4096,
                Active = true
            };
        }
    }
}
