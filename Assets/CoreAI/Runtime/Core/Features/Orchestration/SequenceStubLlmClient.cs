using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Очередь заранее заданных ответов — для тестов цикла Programmer / ремонта Lua.
    /// </summary>
    public sealed class SequenceStubLlmClient : ILlmClient
    {
        private readonly Queue<string> _queue = new();

        /// <summary>Добавить заранее заданный текст ответа в очередь (FIFO).</summary>
        public void EnqueueResponse(string content)
        {
            _queue.Enqueue(content);
        }

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_queue.Count == 0)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    Error = "SequenceStubLlmClient: queue empty"
                });
            }

            string c = _queue.Dequeue();
            return Task.FromResult(new LlmCompletionResult { Ok = true, Content = c });
        }
    }
}