using System;
using System.Collections;
using System.Threading;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The idle deadline behind <see cref="CoreAiChatService"/>'s request timeout. Streaming re-arms it on
    /// EVERY chunk, so the re-arm must cost nothing: the previous shape started a new player-loop timer per
    /// re-arm (one allocation and one player-loop registration per streamed token). The semantics stay
    /// those of a per-re-arm timer: the source is cancelled at the first moment the turn has been idle
    /// for the whole window, and never while progress keeps arriving.
    /// </summary>
    [Category("Chat")]
    public sealed class IdleTimeoutDeadlineEditModeTests
    {
        [Test]
        public void Rearm_DoesNotAllocateGcMemory()
        {
            using CancellationTokenSource cts = new();
            using CoreAiChatService.IdleTimeoutDeadline deadline = new(cts, 30f);

            // Warm-up: the first call may JIT the method; the steady state is what streaming pays per chunk.
            deadline.Rearm();

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    deadline.Rearm();
                }
            }, Is.Not.AllocatingGCMemory(), "Rearm runs once per streamed chunk and must not allocate.");
        }

        [Test]
        public void Rearm_AfterDispose_IsHarmless()
        {
            using CancellationTokenSource cts = new();
            CoreAiChatService.IdleTimeoutDeadline deadline = new(cts, 30f);
            deadline.Dispose();

            Assert.DoesNotThrow(deadline.Rearm,
                "A late tool-call event after the turn ended must not throw into its publisher.");
            Assert.DoesNotThrow(deadline.Dispose, "Dispose must be idempotent.");
            Assert.IsFalse(cts.IsCancellationRequested,
                "Disposing the deadline stops the watchdog; it must never cancel the source itself.");
        }

        [Test]
        public void Constructor_DoesNotCancelSynchronously()
        {
            using CancellationTokenSource cts = new();
            using CoreAiChatService.IdleTimeoutDeadline deadline = new(cts, 0.01f);

            Assert.IsFalse(cts.IsCancellationRequested,
                "The window starts at construction; even a tiny window cannot elapse before the constructor returns.");
        }

        [UnityTest]
        [Timeout(20000)]
        public IEnumerator IdleForWholeWindow_CancelsSource()
        {
            CancellationTokenSource cts = new();
            CoreAiChatService.IdleTimeoutDeadline deadline = new(cts, 0.25f);
            try
            {
                float startedAt = Time.realtimeSinceStartup;
                float giveUpAt = startedAt + 10f;
                while (!cts.IsCancellationRequested && Time.realtimeSinceStartup < giveUpAt)
                {
                    yield return null;
                }

                Assert.IsTrue(cts.IsCancellationRequested,
                    "No re-arm for the whole window must cancel the source.");
                Assert.GreaterOrEqual(Time.realtimeSinceStartup - startedAt, 0.2f,
                    "The source must not be cancelled before the idle window has elapsed.");
            }
            finally
            {
                deadline.Dispose();
                cts.Dispose();
            }
        }

        [UnityTest]
        [Timeout(20000)]
        public IEnumerator SteadyRearms_KeepSourceAlive_ThenStallCancels()
        {
            // WHY a generous window: EditMode frames can be hundreds of milliseconds apart when the editor
            // is unfocused, and a re-arm can only happen once per frame.
            CancellationTokenSource cts = new();
            CoreAiChatService.IdleTimeoutDeadline deadline = new(cts, 0.75f);
            try
            {
                // Progress on every frame for several full windows: a per-chunk re-arm must hold the deadline off.
                float rearmUntil = Time.realtimeSinceStartup + 2f;
                while (Time.realtimeSinceStartup < rearmUntil)
                {
                    deadline.Rearm();
                    yield return null;
                }

                Assert.IsFalse(cts.IsCancellationRequested,
                    "A turn that keeps producing chunks must not be timed out by its own accumulated duration.");

                // Then a real stall: no re-arm at all.
                float giveUpAt = Time.realtimeSinceStartup + 10f;
                while (!cts.IsCancellationRequested && Time.realtimeSinceStartup < giveUpAt)
                {
                    yield return null;
                }

                Assert.IsTrue(cts.IsCancellationRequested,
                    "Once chunks stop for the whole window the deadline must still fire.");
            }
            finally
            {
                deadline.Dispose();
                cts.Dispose();
            }
        }

        [UnityTest]
        [Timeout(20000)]
        public IEnumerator SourceDisposedBeforeWatchdogWakes_DoesNotThrow()
        {
            // The turn ends and releases its source right before the watchdog would have fired: the
            // wake-up must swallow the disposed source instead of surfacing an exception in the player loop.
            CancellationTokenSource cts = new();
            CoreAiChatService.IdleTimeoutDeadline deadline = new(cts, 0.15f);
            cts.Dispose();

            float giveUpAt = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < giveUpAt)
            {
                yield return null;
            }

            Assert.DoesNotThrow(deadline.Dispose);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
