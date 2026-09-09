using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class FileConversationSummaryStoreEditModeTests
    {
        private static string NewTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAITestSummary_" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteRoot(string root)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        private static async Task<Exception> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private sealed class RecordingSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                SynchronizationContext previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                SynchronizationContext previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }
        }

        [Test]
        public void FileConversationSummaryStore_PersistsPerRole()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null);
                const string role = "SmartChat";
                store.SaveSummary(role, "line a");
                Assert.AreEqual("line a", store.LoadSummary(role));
                store.SaveSummary(role, "line b");
                Assert.AreEqual("line b", store.LoadSummary(role));
                store.ClearSummary(role);
                Assert.AreEqual("", store.LoadSummary(role));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task SaveAsync_Then_LoadAsync_RoundTrips()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null);
                await store.SaveSummaryAsync("RoleA", "async summary");
                Assert.AreEqual("async summary", await store.LoadSummaryAsync("RoleA"));
                Assert.AreEqual("async summary", store.LoadSummary("RoleA"));

                await store.ClearSummaryAsync("RoleA");
                Assert.AreEqual("", store.LoadSummary("RoleA"));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task SaveAsync_ConcurrentWrites_FinalFileIsValidJsonOfOneWrite()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore storeA = new(root, null);
                FileConversationSummaryStore storeB = new(Path.Combine(root, "."), null);
                const string role = "ConcurrentRole";
                string[] values = Enumerable.Range(0, 24).Select(i => "summary_" + i).ToArray();

                List<Task> tasks = new();
                for (int i = 0; i < values.Length; i++)
                {
                    FileConversationSummaryStore target = i % 2 == 0 ? storeA : storeB;
                    tasks.Add(target.SaveSummaryAsync(role, values[i]));
                }

                await Task.WhenAll(tasks);

                string path = Path.Combine(root, role + ".json");
                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length, "No leftover tmp files");

                string json = File.ReadAllText(path);
                Assert.DoesNotThrow(() => JsonConvert.DeserializeObject<object>(json));
                FileConversationSummaryStore reader = new(root, null);
                Assert.That(values, Does.Contain(reader.LoadSummary(role)));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task RootIsFile_SaveFailsHonestlyInsteadOfSilentSuccess()
        {
            string file = Path.GetTempFileName();
            try
            {
                FileConversationSummaryStore store = new(file, null);

                Exception syncFailure = null;
                try
                {
                    store.SaveSummary("Role", "v");
                }
                catch (Exception ex)
                {
                    syncFailure = ex;
                }

                Assert.IsNotNull(syncFailure, "Sync save against a file root must throw.");

                Exception asyncFailure = await CaptureAsync(() => store.SaveSummaryAsync("Role", "v"));
                Assert.IsNotNull(asyncFailure, "Async save against a file root must throw.");

                Assert.AreEqual("", store.LoadSummary("Role"));
                Exception loadFailure = await CaptureAsync(() => store.LoadSummaryAsync("Role"));
                Assert.That(loadFailure, Is.InstanceOf<IOException>(), "An invalid parent is not an absent summary.");
                Exception clearFailure = await CaptureAsync(() => store.ClearSummaryAsync("Role"));
                Assert.That(clearFailure, Is.InstanceOf<IOException>());
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        [Test]
        public async Task LockedFile_FailedPrecommitWrite_PreservesOldSummary()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null);
                await store.SaveSummaryAsync("Role", "old");

                string path = Path.Combine(root, "Role.json");
                using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Exception asyncFailure = await CaptureAsync(() => store.SaveSummaryAsync("Role", "new"));

                    Exception syncFailure = null;
                    try
                    {
                        store.SaveSummary("Role", "new");
                    }
                    catch (Exception ex)
                    {
                        syncFailure = ex;
                    }

                    Assert.IsNotNull(asyncFailure, "Locked write must surface failure.");
                    Assert.IsNotNull(syncFailure, "Locked sync write must surface failure.");
                }

                Assert.AreEqual("old", await store.LoadSummaryAsync("Role"));
                Assert.AreEqual("old", store.LoadSummary("Role"));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task DurabilityFalse_ThrowsHonestIOExceptionAfterVfsCommit()
        {
            string root = NewTempRoot();
            try
            {
                bool confirmed = false;
                FileConversationSummaryStore store = new(root, null, null, _ => Task.FromResult(confirmed));
                Exception failure = await CaptureAsync(() => store.SaveSummaryAsync("Role", "v"));
                Assert.That(failure, Is.InstanceOf<IOException>());

                string path = Path.Combine(root, "Role.json");
                Assert.IsTrue(File.Exists(path), "VFS write happened; only durability is unconfirmed.");
                FileConversationSummaryStore reopened = new(root, null);
                Assert.That(await CaptureAsync(() => reopened.LoadSummaryAsync("Role")), Is.InstanceOf<IOException>(),
                    "A new reader cannot treat the VFS fold marker as durable after a failed flush.");
                confirmed = true;
                Assert.AreEqual("v", await reopened.LoadSummaryAsync("Role"));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task LegacySyncHookFalse_ThrowsOnBothPaths()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null, () => false);

                Exception syncFailure = null;
                try
                {
                    store.SaveSummary("Role", "v");
                }
                catch (Exception ex)
                {
                    syncFailure = ex;
                }

                Assert.That(syncFailure, Is.InstanceOf<IOException>());

                Exception asyncFailure = await CaptureAsync(() => store.SaveSummaryAsync("Role", "v"));
                Assert.That(asyncFailure, Is.InstanceOf<IOException>());
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task FailedClear_NewReaderMustConfirmDeletionBeforeReturningEmpty()
        {
            string root = NewTempRoot();
            try
            {
                bool confirmed = true;
                FileConversationSummaryStore store = new(root, null, null, _ => Task.FromResult(confirmed));
                await store.SaveSummaryAsync("Role", "old");
                confirmed = false;
                Assert.That(await CaptureAsync(() => store.ClearSummaryAsync("Role")), Is.InstanceOf<IOException>());
                Assert.IsFalse(File.Exists(Path.Combine(root, "Role.json")));
                FileConversationSummaryStore reopened = new(root);
                Assert.That(await CaptureAsync(() => reopened.LoadSummaryAsync("Role")), Is.InstanceOf<IOException>());
                confirmed = true;
                Assert.AreEqual("", await reopened.LoadSummaryAsync("Role"));
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public async Task OlderSuccessfulFlush_CannotAcknowledgeANewerFailedWrite()
        {
            string root = NewTempRoot();
            TaskCompletionSource<bool> firstEntered = new();
            TaskCompletionSource<bool> secondEntered = new();
            TaskCompletionSource<bool> firstGate = new();
            TaskCompletionSource<bool> secondGate = new();
            bool recovered = false;
            Task first = null;
            Task second = null;
            try
            {
                FileConversationSummaryStore firstStore = new(root, null, null, _ =>
                {
                    firstEntered.TrySetResult(true);
                    return firstGate.Task;
                });
                FileConversationSummaryStore secondStore = new(Path.Combine(root, "."), null, null, _ =>
                {
                    secondEntered.TrySetResult(true);
                    return recovered ? Task.FromResult(true) : secondGate.Task;
                });
                first = firstStore.SaveSummaryAsync("Role", "older");
                Assert.AreSame(firstEntered.Task, await Task.WhenAny(firstEntered.Task, Task.Delay(3000)));
                second = secondStore.SaveSummaryAsync("Role", "newer");
                Assert.AreSame(secondEntered.Task, await Task.WhenAny(secondEntered.Task, Task.Delay(3000)));
                firstGate.SetResult(true);
                await first;
                FileConversationSummaryStore reopened = new(root);
                Task<string> read = reopened.LoadSummaryAsync("Role");
                Assert.IsFalse(read.IsCompleted, "The newer generation still needs its own host confirmation.");
                secondGate.SetResult(false);
                Assert.That(await CaptureAsync(() => second), Is.InstanceOf<IOException>());
                Assert.That(await CaptureAsync(() => read), Is.InstanceOf<IOException>());
                recovered = true;
                Assert.AreEqual("newer", await reopened.LoadSummaryAsync("Role"));
            }
            finally
            {
                firstGate.TrySetResult(true);
                secondGate.TrySetResult(true);
                if (first != null) await CaptureAsync(() => first);
                if (second != null) await CaptureAsync(() => second);
                DeleteRoot(root);
            }
        }

        // WHY this replaced its own opposite: an earlier test asserted that a SUCCESSFUL synchronous
        // hook must leave the durability mark parked whenever an async hook exists too, and it enshrined
        // the resulting throw as correct. That rested on the two hooks being different mechanisms - the
        // async one carrying a manual FS.syncfs handshake. That channel is gone; both hooks now return
        // the same immediate engine answer, and CoreAILifetimeScope registers exactly that pair
        // (CoreAiWebGlPersistence.Sync + SyncAsync). Nothing could clear the mark any more, so the first
        // synchronous save poisoned the file and the next synchronous read threw - on chat-history reset
        // and in the session inspector.
        [Test]
        public async Task SyncSave_WithBothHooksConfigured_LeavesTheSyncApiUsable()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null, () => true, _ => Task.FromResult(true));

                store.SaveSummary("Role", "queued");
                Assert.AreEqual("queued", store.LoadSummary("Role"),
                    "A confirmed synchronous save must not leave the file unreadable through the sync API.");
                Assert.AreEqual("queued", await store.LoadSummaryAsync("Role"));

                store.ClearSummary("Role");
                Assert.AreEqual("", store.LoadSummary("Role"),
                    "A confirmed synchronous clear must not leave the file unreadable through the sync API.");
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public async Task SyncSave_WithBothHooksConfigured_AndRefusedSyncAnswer_StillBlocksTheSyncApi()
        {
            string root = NewTempRoot();
            try
            {
                bool durable = false;
                FileConversationSummaryStore store = new(root, null, () => durable, _ => Task.FromResult(true));

                Assert.Throws<IOException>(() => store.SaveSummary("Role", "queued"),
                    "A page with no durable storage must fail the write visibly.");
                Assert.Throws<InvalidOperationException>(() => store.LoadSummary("Role"),
                    "An unconfirmed write must not be readable as a durable fold marker.");

                durable = true;
                Assert.AreEqual("queued", await store.LoadSummaryAsync("Role"),
                    "Once durability is confirmed the committed write is readable again.");
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public async Task DurabilityCallback_ReentrantSameFileReadFailsWithoutRecursion()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore reader = new(root);
                FileConversationSummaryStore writer = new(root, null, null, async _ =>
                {
                    await reader.LoadSummaryAsync("Role");
                    return true;
                });
                Exception failure = await CaptureAsync(() => writer.SaveSummaryAsync("Role", "v"));
                Assert.That(failure, Is.InstanceOf<IOException>());
                Assert.That(failure.InnerException, Is.InstanceOf<InvalidOperationException>());
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public async Task DurabilityThrow_SurfacesAndSuccessfulRetryWorks()
        {
            string root = NewTempRoot();
            try
            {
                InvalidOperationException boom = new("boom");
                FileConversationSummaryStore failing = new(root, null, null, _ => Task.FromException<bool>(boom));
                Exception failure = await CaptureAsync(() => failing.SaveSummaryAsync("Role", "v"));
                Assert.That(failure, Is.InstanceOf<IOException>());
                Assert.AreSame(boom, failure.InnerException);

                FileConversationSummaryStore retry = new(root, null, null, _ => Task.FromResult(true));
                await retry.SaveSummaryAsync("Role", "v");
                Assert.AreEqual("v", await retry.LoadSummaryAsync("Role"));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task CancelledBeforeCommit_PreventsMutation()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null);
                using (CancellationTokenSource cts = new())
                {
                    cts.Cancel();
                    Exception failure = await CaptureAsync(() => store.SaveSummaryAsync("Role", "v", cts.Token));
                    Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
                }

                Assert.AreEqual(0, Directory.GetFiles(root).Length, "Cancelled save must not mutate storage.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task SaveAsync_HostCallbackRunsOnCallerContext()
        {
            string root = NewTempRoot();
            RecordingSynchronizationContext entry = new();
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(entry);
            try
            {
                SynchronizationContext observed = null;
                FileConversationSummaryStore store = new(root, null, null, async ct =>
                {
                    observed = SynchronizationContext.Current;
                    await Task.Yield();
                    return true;
                });

                await store.SaveSummaryAsync("Role", "v");
                Assert.AreSame(entry, observed);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task SyncSave_FailFastWhileAsyncBusy()
        {
            string root = NewTempRoot();
            try
            {
                string big = new('x', 3 * 1024 * 1024);
                FileConversationSummaryStore store = new(root, null);
                Task bigSave = store.SaveSummaryAsync("Role", big);

                int failFastCount = 0;
                for (int attempt = 0; attempt < 32 && !bigSave.IsCompleted; attempt++)
                {
                    try
                    {
                        store.SaveSummary("Role", "tiny");
                    }
                    catch (InvalidOperationException)
                    {
                        failFastCount++;
                    }
                    await Task.Yield();
                }

                await bigSave;
                Assert.Greater(failFastCount, 0, "Sync path must fail fast instead of blocking on async I/O.");

                string path = Path.Combine(root, "Role.json");
                Assert.DoesNotThrow(() => JsonConvert.DeserializeObject<object>(File.ReadAllText(path)));
                Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length, "No leftover tmp files");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task DisposeDuringPending_AcceptedFinishes_NewRejected()
        {
            string root = NewTempRoot();
            try
            {
                TaskCompletionSource<bool> durabilityGate =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                FileConversationSummaryStore store = new(root, null, null, async _ =>
                {
                    callbackEntered.TrySetResult(true);
                    return await durabilityGate.Task.ConfigureAwait(false);
                });

                Task pending = store.SaveSummaryAsync("Role", "v");
                Assert.AreSame(callbackEntered.Task,
                    await Task.WhenAny(callbackEntered.Task, Task.Delay(TimeSpan.FromSeconds(10))),
                    "Durability callback not reached.");

                store.Dispose();

                Exception rejected = await CaptureAsync(() => store.SaveSummaryAsync("Role", "v2"));
                Assert.That(rejected, Is.InstanceOf<ObjectDisposedException>());

                Exception syncRejected = null;
                try
                {
                    store.SaveSummary("Role", "v2");
                }
                catch (Exception ex)
                {
                    syncRejected = ex;
                }

                Assert.That(syncRejected, Is.InstanceOf<ObjectDisposedException>());

                durabilityGate.SetResult(true);
                await pending;

                Assert.AreEqual("v", await new FileConversationSummaryStore(root, null).LoadSummaryAsync("Role"));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [Test]
        public async Task StrictRead_CorruptThrowsAndPreservesFile_MissingStaysEmpty()
        {
            string root = NewTempRoot();
            try
            {
                FileConversationSummaryStore store = new(root, null);
                Assert.AreEqual("", await store.LoadSummaryAsync("Missing"));
                Assert.AreEqual("", store.LoadSummary("Missing"));

                string path = Path.Combine(root, "Corrupt.json");
                File.WriteAllText(path, "{not valid json");

                Exception failure = await CaptureAsync(() => store.LoadSummaryAsync("Corrupt"));
                Assert.IsNotNull(failure, "Strict async read must propagate corrupt storage.");

                Assert.AreEqual("{not valid json", File.ReadAllText(path), "Corrupt data must not be overwritten.");
                Assert.AreEqual("", store.LoadSummary("Corrupt"), "Sync read keeps legacy empty fallback.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }
}
