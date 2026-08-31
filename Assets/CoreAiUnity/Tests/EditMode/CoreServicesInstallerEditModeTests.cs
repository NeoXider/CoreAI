using System;
using System.Collections;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Features.Audit;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Ensures <see cref="CoreServicesInstaller.RegisterCore"/> builds on all targets (incl. WebGL/IL2CPP):
    /// <see cref="IAiGameCommandSink"/> must resolve without VContainer constructor analysis on
    /// <see cref="MessagePipeAiCommandSink"/>.
    /// </summary>
    /// <remarks>
    /// No <c>[TearDown]</c>: <c>GlobalMessagePipe.SetProvider(null)</c> is invalid (MessagePipe always resolves
    /// <c>EventFactory</c> from the argument). The next <c>RegisterCore</c> build replaces the static provider;
    /// <see cref="CoreAI.Logging.Log.Instance"/> is refreshed in the same callback.
    /// </remarks>
    public sealed class CoreServicesInstallerEditModeTests
    {
        [Test]
        public void RegisterCore_Builds_AndResolves_IAiGameCommandSink_As_MessagePipeSink()
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            IObjectResolver container = builder.Build();
            try
            {
                IAiGameCommandSink sink = container.Resolve<IAiGameCommandSink>();
                ActorContext actor = container.Resolve<IActorIdentityProvider>()
                    .GetActorContext(BuiltInAgentRoleIds.Creator);

                Assert.That(sink, Is.Not.Null);
                Assert.That(sink, Is.InstanceOf<MessagePipeAiCommandSink>());
                Assert.IsTrue(actor.Grants.IsUnrestricted);
            }
            finally
            {
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        [Test]
        public void RegisterCore_PassesFormattingSettingsToFinalUnitySink()
        {
            ILog savedLog = Log.Instance;
            string token = "coreai-di-prefix-probe-" + Guid.NewGuid().ToString("N");
            string captured = null;
            GameLogSettingsOptions settings = new()
            {
                EnabledFeatures = GameLogFeature.All,
                MinimumLevel = GameLogLevel.Debug,
                IncludeCoreAiPrefix = false,
                IncludeFeaturePrefix = false
            };
            ContainerBuilder builder = new();
            builder.RegisterInstance<IGameLogSettings>(settings);
            builder.RegisterCore();

            void Handler(string condition, string stackTrace, LogType type)
            {
                if (condition != null && condition.Contains(token))
                {
                    captured = condition;
                }
            }

            IObjectResolver container = builder.Build();
            Application.logMessageReceived += Handler;
            try
            {
                container.Resolve<IGameLogger>().LogInfo(GameLogFeature.Core, token);

                Assert.AreEqual(token, captured);
            }
            finally
            {
                Application.logMessageReceived -= Handler;
                Log.Instance = savedLog;
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        [Test]
        public void RegisterCore_InitializesAuditInterceptor_AndDisposesWriterWithScope()
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            IObjectResolver container = builder.Build();
            LlmAuditInterceptor interceptor = container.Resolve<LlmAuditInterceptor>();
            AuditLogWriter writer = container.Resolve<AuditLogWriter>();
            FieldInfo subscriptionsField = typeof(LlmAuditInterceptor).GetField(
                "_subscriptions", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo cancellationField = typeof(AuditLogWriter).GetField(
                "_cts", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.AreEqual(3, ((ICollection)subscriptionsField.GetValue(interceptor)).Count);
            Assert.IsNotNull(cancellationField.GetValue(writer));

            ((IDisposable)container).Dispose();

            Assert.AreEqual(0, ((ICollection)subscriptionsField.GetValue(interceptor)).Count);
            Assert.IsNull(cancellationField.GetValue(writer));
        }
    }
}
