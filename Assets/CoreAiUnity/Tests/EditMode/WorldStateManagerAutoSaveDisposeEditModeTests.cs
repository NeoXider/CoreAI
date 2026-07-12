using System;
using System.Reflection;
using System.Threading;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for the auto-save CancellationTokenSource leak: <c>StartAutoSave</c> and
    /// <c>Dispose</c> must cancel AND dispose the previous source, a re-start must cancel the prior
    /// loop, and none of these paths may surface an <see cref="ObjectDisposedException"/>.
    /// </summary>
    public sealed class WorldStateManagerAutoSaveDisposeEditModeTests
    {
        private static CancellationTokenSource ReadCts(WorldStateManager manager)
        {
            FieldInfo field = typeof(WorldStateManager).GetField(
                "_autoSaveCts", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "_autoSaveCts field not found.");
            return (CancellationTokenSource)field.GetValue(manager);
        }

        private static WorldStateManager NewManager()
        {
            return new WorldStateManager(GameLoggerUnscopedFallback.Instance);
        }

        [Test]
        public void SecondStartAutoSave_CancelsFirstLoop()
        {
            using WorldStateManager manager = NewManager();

            manager.StartAutoSave(60f);
            CancellationTokenSource first = ReadCts(manager);
            Assert.IsNotNull(first, "First StartAutoSave should create a source.");
            CancellationToken firstToken = first.Token;
            Assert.IsFalse(firstToken.IsCancellationRequested);

            manager.StartAutoSave(60f);
            CancellationTokenSource second = ReadCts(manager);

            Assert.IsFalse(ReferenceEquals(first, second),
                "A re-start must swap in a fresh source, not reuse the disposed one.");
            Assert.IsTrue(firstToken.IsCancellationRequested,
                "A second StartAutoSave must cancel the first loop's token.");
        }

        [Test]
        public void RepeatedStartAutoSave_DoesNotThrowObjectDisposed()
        {
            using WorldStateManager manager = NewManager();

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    manager.StartAutoSave(60f);
                }
            }, "Repeated StartAutoSave must not surface ObjectDisposedException.");
        }

        [Test]
        public void StartAutoSave_NonPositiveInterval_StopsAndClearsSource()
        {
            using WorldStateManager manager = NewManager();

            manager.StartAutoSave(60f);
            Assert.IsNotNull(ReadCts(manager));

            manager.StartAutoSave(0f);
            Assert.IsNull(ReadCts(manager),
                "A non-positive interval must stop the loop and clear the source.");
        }

        [Test]
        public void Dispose_AfterStartAutoSave_ClearsSourceWithoutThrowing()
        {
            WorldStateManager manager = NewManager();
            manager.StartAutoSave(60f);

            Assert.DoesNotThrow(manager.Dispose,
                "Dispose must cancel and dispose the auto-save source without throwing.");
            Assert.IsNull(ReadCts(manager),
                "Dispose must clear the auto-save source reference.");
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            WorldStateManager manager = NewManager();
            manager.StartAutoSave(60f);

            Assert.DoesNotThrow(() =>
            {
                manager.Dispose();
                manager.Dispose();
            }, "A second Dispose must be a safe no-op.");
        }

        [Test]
        public void StartAutoSave_AfterDispose_DoesNotRestart()
        {
            WorldStateManager manager = NewManager();
            manager.Dispose();

            Assert.DoesNotThrow(() => manager.StartAutoSave(60f));
            Assert.IsNull(ReadCts(manager),
                "StartAutoSave must not create a source once disposed.");
        }
    }
}
