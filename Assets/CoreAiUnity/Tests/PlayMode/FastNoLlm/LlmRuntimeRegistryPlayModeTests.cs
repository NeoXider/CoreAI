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
        private const float AsyncTimeoutSeconds = 10f;
        private readonly List<LlmClientRegistry> _registries = new();
        private readonly List<CoreAISettingsAsset> _settingsAssets = new();

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
            public TaskCompletionSource<bool> NewEntered { get; } = new();
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
                    NewEntered.TrySetResult(true);
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

        [TearDown]
        public void TearDown()
        {
            for (int i = _registries.Count - 1; i >= 0; i--)
            {
                _registries[i]?.Dispose();
            }

            _registries.Clear();
            for (int i = _settingsAssets.Count - 1; i >= 0; i--)
            {
                if (_settingsAssets[i] != null)
                {
                    Object.DestroyImmediate(_settingsAssets[i]);
                }
            }

            _settingsAssets.Clear();
        }

        [UnityTest]
        public IEnumerator TwoAgents_RunConcurrentlyOnDifferentActiveEndpoints()
        {
            CoreAISettingsAsset settings = CreateSettings();
            ConcurrentFactory factory = new();
            LlmClientRegistry registry = CreateRegistry(settings, factory);
            RoutingLlmClient routing = new(registry);
            Task setup = SetupAsync(registry);
            yield return PlayModeTestAwait.WaitTask(setup, AsyncTimeoutSeconds, "registry setup");

            Assert.IsFalse(setup.IsFaulted, setup.Exception?.ToString());
            Task<LlmCompletionResult> api = routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "CloudAgent"
            });
            Task<LlmCompletionResult> local = routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "LocalAgent"
            });
            yield return PlayModeTestAwait.WaitTask(
                factory.BothEntered.Task, AsyncTimeoutSeconds, "both endpoint requests to start");

            Assert.IsFalse(api.IsCompleted);
            Assert.IsFalse(local.IsCompleted);
            factory.Release.SetResult(true);
            Task all = Task.WhenAll(api, local);
            yield return PlayModeTestAwait.WaitTask(all, AsyncTimeoutSeconds, "both endpoint requests");

            Assert.AreEqual("api", api.Result.Content);
            Assert.AreEqual("local", local.Result.Content);
        }

        [UnityTest]
        public IEnumerator RouteSwitch_AffectsNextRequestWithoutRecreatingRegistry()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmClientRegistry registry = CreateRegistry(settings, new NamedFactory());
            RoutingLlmClient routing = new(registry);
            Task setup = SetupAsync(registry);
            yield return PlayModeTestAwait.WaitTask(setup, AsyncTimeoutSeconds, "registry setup");

            registry.AssignRoleProfile("CloudAgent", "local");
            Task<LlmCompletionResult> switched = routing.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = "CloudAgent"
            });
            yield return PlayModeTestAwait.WaitTask(switched, AsyncTimeoutSeconds, "switched route request");

            Assert.AreEqual("local", switched.Result.Content);
            Assert.AreEqual("local", registry.GetRoleProfile("CloudAgent"));
        }

        [UnityTest]
        public IEnumerator ZeroEndpoints_AutomaticRouteUsesLegacyFallback()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmClientRegistry registry = CreateRegistry(settings, new NamedFactory());
            registry.SetLegacyFallback(new NamedClient("legacy"));

            Task<LlmCompletionResult> completion = registry.ResolveClientForRole("Chat")
                .CompleteAsync(new LlmCompletionRequest());
            yield return PlayModeTestAwait.WaitTask(
                completion, AsyncTimeoutSeconds, "legacy fallback request");

            Assert.AreEqual("legacy", completion.Result.Content);
        }

        [UnityTest]
        public IEnumerator HttpFactory_ModelsProbeRequiresSuccessAndForwardsCredential()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);
            using (LocalHttpServer server = new(200, "Bearer session-key"))
            {
                Task<LlmEndpointClientActivation> success = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "session-key", CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(
                    success, AsyncTimeoutSeconds, "successful HTTP endpoint activation");

                Assert.IsFalse(success.IsFaulted, success.Exception?.ToString());
                Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, success.Result.Mode);
            }

            foreach (int status in new[] { 401, 403, 500 })
            {
                using LocalHttpServer server = new(status);
                Task<LlmEndpointClientActivation> failed = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "", CancellationToken.None);
                yield return PlayModeTestAwait.WaitUntil(
                    () => failed.IsCompleted,
                    AsyncTimeoutSeconds,
                    $"failed HTTP {status} endpoint activation");

                Assert.IsTrue(failed.IsFaulted, $"HTTP {status} must fail readiness.");
            }
        }

        [UnityTest]
        public IEnumerator HttpFactory_FallsBackToCompletionsWhenModelsRouteIsUnavailable()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);

            foreach (int modelsStatus in new[] { 404, 405 })
            {
                using FallbackHttpServer server = new(
                    modelsStatus, 400, "Bearer session-key");
                Task<LlmEndpointClientActivation> success = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "session-key", CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(
                    success,
                    AsyncTimeoutSeconds,
                    $"HTTP {modelsStatus} completions fallback activation");

                Assert.IsFalse(success.IsFaulted, success.Exception?.ToString());
                Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, success.Result.Mode);
            }
        }

        [UnityTest]
        public IEnumerator HttpFactory_CompletionsFallbackRejectsAuthAndServerFailures()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);

            foreach (int status in new[] { 401, 403, 404, 500 })
            {
                using FallbackHttpServer server = new(404, status);
                Task<LlmEndpointClientActivation> failed = factory.ActivateAsync(
                    HttpDescriptor(server.Port), "", CancellationToken.None);
                yield return PlayModeTestAwait.WaitUntil(
                    () => failed.IsCompleted,
                    AsyncTimeoutSeconds,
                    $"failed fallback HTTP {status} endpoint activation");

                Assert.IsTrue(failed.IsFaulted, $"Fallback HTTP {status} must fail readiness.");
            }
        }

        [UnityTest]
        public IEnumerator HttpFactory_ModelsProbeRejectsConnectionFailure()
        {
            TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int closedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            CoreAISettingsAsset settings = CreateSettings();
            LlmEndpointClientFactory factory = new(
                settings, GameLoggerUnscopedFallback.Instance);

            Task<LlmEndpointClientActivation> failed = factory.ActivateAsync(
                HttpDescriptor(closedPort), "", CancellationToken.None);
            yield return PlayModeTestAwait.WaitUntil(
                () => failed.IsCompleted,
                AsyncTimeoutSeconds,
                "connection-failed HTTP endpoint activation");

            Assert.IsTrue(failed.IsFaulted);
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
                yield return PlayModeTestAwait.WaitTask(
                    task,
                    AsyncTimeoutSeconds,
                    $"HTTP {status} readiness probe");

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
            using CompletionHttpServer server = new(0, false);
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
            yield return PlayModeTestAwait.WaitUntil(
                () => task.IsCompleted,
                AsyncTimeoutSeconds,
                "canceled readiness probe");

            Assert.IsTrue(task.IsCanceled, task.Exception?.ToString());
            yield return PlayModeTestAwait.WaitTask(
                server.Disconnected.Task,
                2f,
                "readiness probe connection abort");

            Assert.IsTrue(server.Disconnected.Task.IsCompleted, "Abort must close the native HTTP connection.");
            Assert.IsTrue(server.Disconnected.Task.Result);
        }

        [UnityTest]
        public IEnumerator DisabledEndpointWithoutAssignment_DoesNotBreakLegacyFallback()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmClientRegistry registry = CreateRegistry(settings, new NamedFactory());
            registry.SetLegacyFallback(new NamedClient("legacy"));
            LlmEndpointDescriptor disabled = Descriptor("disabled");
            disabled.Active = false;
            Task<LlmEndpointSnapshot> add = registry.AddOrUpdateEndpointAsync(disabled);
            yield return PlayModeTestAwait.WaitTask(add, AsyncTimeoutSeconds, "disabled endpoint add");

            Task<LlmCompletionResult> completion = registry.ResolveClientForRole("Chat")
                .CompleteAsync(new LlmCompletionRequest());
            yield return PlayModeTestAwait.WaitTask(
                completion, AsyncTimeoutSeconds, "disabled-endpoint legacy fallback request");

            Assert.AreEqual("legacy", completion.Result.Content);
        }

        [UnityTest]
        public IEnumerator ExplicitRequestProfile_WinsRoleAssignment()
        {
            CoreAISettingsAsset settings = CreateSettings();
            LlmClientRegistry registry = CreateRegistry(settings, new NamedFactory());
            RoutingLlmClient routing = new(registry);
            Task setup = SetupAsync(registry);
            yield return PlayModeTestAwait.WaitTask(setup, AsyncTimeoutSeconds, "registry setup");

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "CloudAgent",
                RoutingProfileId = "local"
            };
            Task<LlmCompletionResult> completion = routing.CompleteAsync(request);
            yield return PlayModeTestAwait.WaitTask(
                completion, AsyncTimeoutSeconds, "explicit profile request");

            Assert.AreEqual("local", completion.Result.Content);
            Assert.AreEqual("local", request.RoutingProfileId);
        }

        [UnityTest]
        public IEnumerator HotReplacement_KeepsResolvedInFlightClientAndRoutesNewCallsToNewGeneration()
        {
            CoreAISettingsAsset settings = CreateSettings();
            HotSwapFactory factory = new();
            LlmClientRegistry registry = CreateRegistry(settings, factory);
            LlmEndpointDescriptor oldDescriptor = Descriptor("shared");
            oldDescriptor.Model = "old";
            Task<LlmEndpointSnapshot> initial = registry.AddOrUpdateEndpointAsync(oldDescriptor);
            yield return PlayModeTestAwait.WaitTask(
                initial, AsyncTimeoutSeconds, "initial endpoint activation");

            ILlmClient oldClient = registry.ResolveClientForRole("Agent", "shared");
            Task<LlmCompletionResult> oldCall = oldClient.CompleteAsync(new LlmCompletionRequest());
            LlmEndpointDescriptor replacement = Descriptor("shared");
            replacement.Model = "new";
            Task<LlmEndpointSnapshot> update = registry.AddOrUpdateEndpointAsync(replacement);
            yield return PlayModeTestAwait.WaitTask(
                factory.NewEntered.Task, AsyncTimeoutSeconds, "replacement endpoint activation to start");

            Task<LlmCompletionResult> duringWarmup = registry.ResolveClientForRole("Agent", "shared")
                .CompleteAsync(new LlmCompletionRequest());
            Assert.IsFalse(update.IsCompleted);
            Assert.IsFalse(duringWarmup.IsCompleted);
            factory.NewReady.SetResult(true);
            yield return PlayModeTestAwait.WaitTask(
                update, AsyncTimeoutSeconds, "replacement endpoint activation");

            Task<LlmCompletionResult> newCall = registry.ResolveClientForRole("Agent", "shared")
                .CompleteAsync(new LlmCompletionRequest());
            yield return PlayModeTestAwait.WaitTask(
                newCall, AsyncTimeoutSeconds, "new-generation request");

            Assert.AreEqual("new", newCall.Result.Content);
            Assert.IsFalse(oldCall.IsCompleted);
            factory.OldGate.SetResult(true);
            yield return PlayModeTestAwait.WaitTask(
                oldCall, AsyncTimeoutSeconds, "old-generation in-flight request");

            Assert.AreEqual("old", oldCall.Result.Content);
            Assert.Greater(update.Result.Generation, initial.Result.Generation);
        }

        [UnityTest]
        public IEnumerator FailedHotReplacement_LeavesOldReadyGenerationRoutable()
        {
            CoreAISettingsAsset settings = CreateSettings();
            HotSwapFactory factory = new();
            factory.OldGate.SetResult(true);
            LlmClientRegistry registry = CreateRegistry(settings, factory);
            LlmEndpointDescriptor oldDescriptor = Descriptor("shared");
            oldDescriptor.Model = "old";
            Task<LlmEndpointSnapshot> initial = registry.AddOrUpdateEndpointAsync(oldDescriptor);
            yield return PlayModeTestAwait.WaitTask(
                initial, AsyncTimeoutSeconds, "initial endpoint activation");

            LlmEndpointDescriptor failed = Descriptor("shared");
            failed.Model = "bad";
            Task<LlmEndpointSnapshot> update = registry.AddOrUpdateEndpointAsync(failed);
            yield return PlayModeTestAwait.WaitTask(
                update, AsyncTimeoutSeconds, "failed replacement endpoint activation");

            Assert.AreEqual(LlmEndpointLifecycleState.Failed, update.Result.State);
            Task<LlmCompletionResult> afterFailure = registry.ResolveClientForRole("Agent", "shared")
                .CompleteAsync(new LlmCompletionRequest());
            yield return PlayModeTestAwait.WaitTask(
                afterFailure, AsyncTimeoutSeconds, "request after failed replacement");

            Assert.AreEqual("old", afterFailure.Result.Content);
            Assert.AreEqual(initial.Result.Generation, registry.GetEndpoints()[0].Generation);
        }

        private static async Task SetupAsync(LlmClientRegistry registry)
        {
            await registry.AddOrUpdateEndpointAsync(Descriptor("api"));
            await registry.AddOrUpdateEndpointAsync(Descriptor("local"));
            registry.AssignRoleProfile("CloudAgent", "api");
            registry.AssignRoleProfile("LocalAgent", "local");
        }

        private CoreAISettingsAsset CreateSettings()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settingsAssets.Add(settings);
            return settings;
        }

        private LlmClientRegistry CreateRegistry(
            CoreAISettingsAsset settings,
            ILlmEndpointClientFactory factory)
        {
            LlmClientRegistry registry = new(
                GameLoggerUnscopedFallback.Instance, settings, null, null, factory);
            _registries.Add(registry);
            return registry;
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
