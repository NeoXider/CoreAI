using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>Verifies inventory tool continuations remain on the host synchronization context.</summary>
    public sealed class InventoryToolEditModeTests
    {
        [Test]
        public async Task ExecuteAsync_PendingProvider_ResumesOnCapturedHostContext()
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            QueuedContext context = new();
            PendingProvider provider = new();
            InventoryTool tool = new(provider);
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                Task<string> execution = tool.ExecuteAsync();
                Assert.IsFalse(execution.IsCompleted);

                SynchronizationContext.SetSynchronizationContext(null);
                provider.Completion.SetResult(new List<InventoryTool.InventoryItem>
                {
                    new() { Name = "Potion", Quantity = 2 }
                });

                Assert.AreEqual(1, context.Pending.Count,
                    "The inventory continuation must post to the captured host context.");
                Assert.IsFalse(execution.IsCompleted, "Serialization must wait for the host continuation.");
                SynchronizationContext.SetSynchronizationContext(context);
                (SendOrPostCallback callback, object state) = context.Pending.Dequeue();
                callback(state);

                Assert.IsTrue(execution.IsCompletedSuccessfully);
                InventoryTool.InventoryResult result = JsonConvert.DeserializeObject<InventoryTool.InventoryResult>(
                    await execution);
                Assert.IsTrue(result.Success);
                Assert.AreEqual("Potion", result.Items[0].Name);
                Assert.AreEqual(2, result.Items[0].Quantity);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private sealed class PendingProvider : InventoryTool.IInventoryProvider
        {
            public TaskCompletionSource<List<InventoryTool.InventoryItem>> Completion { get; } = new();

            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken)
            {
                return Completion.Task;
            }
        }

        private sealed class QueuedContext : SynchronizationContext
        {
            public Queue<(SendOrPostCallback Callback, object State)> Pending { get; } = new();

            public override void Post(SendOrPostCallback callback, object state)
            {
                Pending.Enqueue((callback, state));
            }
        }
    }
}
