using System;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Cross-generation guard for <see cref="AiGameCommandRouter.CommandReceived"/>: disposing a router
    /// from an older generation must not clear a live router's subscription.
    /// </summary>
    public sealed class AiGameCommandRouterGenerationEditModeTests
    {
        private sealed class NoOpGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class StubSubscriber : ISubscriber<ApplyAiGameCommand>
        {
            public IDisposable Subscribe(IMessageHandler<ApplyAiGameCommand> handler,
                params MessageHandlerFilter<ApplyAiGameCommand>[] filters)
            {
                return new NoopDisposable();
            }

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        private sealed class NullWorldExecutor : CoreAI.Infrastructure.World.ICoreAiWorldCommandExecutor
        {
            public string[] LastListedAnimations { get; } = System.Array.Empty<string>();

            public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>
                LastListedObjects { get; } = new();

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                return false;
            }
        }

        [Test]
        public void Dispose_StaleGeneration_DoesNotClearLiveRouterSubscription()
        {
            AiGameCommandRouter.ResetStatics();
            AiGameCommandRouter oldRouter =
                new(new StubSubscriber(), new NoOpGameLogger(), new NullWorldExecutor());
            AiGameCommandRouter.ResetStatics();
            AiGameCommandRouter newRouter =
                new(new StubSubscriber(), new NoOpGameLogger(), new NullWorldExecutor());

            void OnCommand(ApplyAiGameCommand _)
            {
            }

            AiGameCommandRouter.CommandReceived += OnCommand;
            try
            {
                oldRouter.Dispose();

                System.Reflection.FieldInfo countField = typeof(AiGameCommandRouter).GetField(
                    "_activeRouterCount",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                System.Reflection.FieldInfo eventField = typeof(AiGameCommandRouter).GetField(
                    "CommandReceived",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(countField);
                Assert.NotNull(eventField);
                Assert.AreEqual(1, countField.GetValue(null),
                    "Disposing a stale-generation router must not consume the live generation's refcount.");
                Assert.IsNotNull(eventField.GetValue(null),
                    "Disposing a stale-generation router must not clear the live router's subscription.");
            }
            finally
            {
                AiGameCommandRouter.CommandReceived -= OnCommand;
                oldRouter.Dispose();
                newRouter.Dispose();
                AiGameCommandRouter.ResetStatics();
            }
        }
    }
}
