using System;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Logging;
using CoreAI.Messaging;
using MessagePipe;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Ensures <see cref="CoreServicesInstaller.RegisterCore"/> builds on all targets (incl. WebGL/IL2CPP):
    /// <see cref="IAiGameCommandSink"/> must resolve without VContainer constructor analysis on
    /// <see cref="MessagePipeAiCommandSink"/>.
    /// </summary>
    public sealed class CoreServicesInstallerEditModeTests
    {
        [TearDown]
        public void TearDown()
        {
            GlobalMessagePipe.SetProvider(null);
            Log.Instance = null;
        }

        [Test]
        public void RegisterCore_Builds_AndResolves_IAiGameCommandSink_As_MessagePipeSink()
        {
            var builder = new ContainerBuilder();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            IObjectResolver container = builder.Build();
            try
            {
                IAiGameCommandSink sink = container.Resolve<IAiGameCommandSink>();

                Assert.That(sink, Is.Not.Null);
                Assert.That(sink, Is.InstanceOf<MessagePipeAiCommandSink>());
            }
            finally
            {
                if (container is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}
