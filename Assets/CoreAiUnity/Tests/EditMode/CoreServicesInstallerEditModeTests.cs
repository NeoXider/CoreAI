using System;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using NUnit.Framework;
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
