using CoreAI.Composition;
using CoreAI.Messaging;
using MessagePipe;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="GlobalMessagePipeMinimalBootstrap"/> must wire the same broker types that
    /// <c>ToolExecutionPolicy</c> publishes when <see cref="GlobalMessagePipe"/> had no provider yet
    /// (package PlayMode <c>TestAgentSetup</c> and other minimal fixtures).
    /// </summary>
    public sealed class GlobalMessagePipeMinimalBootstrapEditModeTests
    {
        [Test]
        public void EnsureInitializedForLlmDiagnostics_IsIdempotent_And_DeliversLlmToolCallCompleted()
        {
            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();
            Assert.That(GlobalMessagePipe.IsInitialized, Is.True, "First Ensure should set GlobalMessagePipe.");

            GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();
            Assert.That(GlobalMessagePipe.IsInitialized, Is.True, "Second Ensure must not throw and keep provider.");

            int received = 0;
            LlmToolCallCompleted last = default;
            using (GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>().Subscribe(evt =>
            {
                received++;
                last = evt;
            }))
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallCompleted>().Publish(
                    new LlmToolCallCompleted(
                        traceId: "editmode-bootstrap",
                        roleId: "Creator",
                        toolName: "memory",
                        argumentsJson: "{\"action\":\"write\"}",
                        resultJson: "{\"ok\":true}",
                        durationMs: 1d));

                Assert.That(received, Is.EqualTo(1));
                Assert.That(last.ToolName, Is.EqualTo("memory"));
                Assert.That(last.RoleId, Is.EqualTo("Creator"));
            }
        }
    }
}
