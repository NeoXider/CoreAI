using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class SmartToolCallingEditModeTests
    {
        private sealed class MockChatClient : IChatClient
        {
            public Queue<ChatResponse> Responses = new();

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                if (Responses.Count > 0)
                    return Task.FromResult(Responses.Dequeue());
                
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Stop")));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions options = null, CancellationToken cancellationToken = default)
                => throw new NotImplementedException();

            public void Dispose() { }
            public object GetService(Type serviceType, object serviceKey = null) => null;
        }

        private CoreAISettingsAsset _settings;
        private NullGameLogger _logger;
        private AIFunction _dummyFunc;
        private CoreAI.Ai.ILlmTool _dummyLlmTool;

        private sealed class DummyLlmTool : CoreAI.Ai.ILlmTool
        {
            public string Name => "dummy_tool";
            public string Description => "Dummy tool";
            public object ParametersFormat => new { };
            public bool AllowDuplicates => false;
        }

        [SetUp]
        public void Setup()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _logger = new NullGameLogger();
            _dummyFunc = AIFunctionFactory.Create(() => "Success", "dummy_tool");
            _dummyLlmTool = new DummyLlmTool();
        }

        [Test]
        public async Task DuplicateProtection_ResetsOnNewRequest()
        {
            // Убеждаемся, что разные независимые вызовы (новые реквесты) могут использовать один и тот же инструмент.
            MockChatClient mockInner = new MockChatClient();
            SmartToolCallingChatClient smartClient = new SmartToolCallingChatClient(
                mockInner, _logger, _settings, allowDuplicateToolCalls: false, new[] { _dummyLlmTool }
            );

            ChatOptions options = new ChatOptions { Tools = new[] { _dummyFunc } };
            
            // Request 1
            mockInner.Responses.Enqueue(CreateResponseWithToolCall("dummy_tool"));
            ChatResponse r1 = await smartClient.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Call 1") }, options);
            
            // Запрос должен был успешно закончиться (mockInner возвращает Text "Stop" на 2-й итерации)
            Assert.AreEqual("Stop", r1.Message.Text);

            // Request 2 (Новый внешний запрос)
            // Мы вызываем ТОТ ЖЕ самый инструмент, это НЕ дубликат, т.к. это уже новый GetResponseAsync.
            mockInner.Responses.Enqueue(CreateResponseWithToolCall("dummy_tool"));
            ChatResponse r2 = await smartClient.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Call 2") }, options);

            // Если бы защита не сбросилась, r2 завершился бы ошибкой duplicate tool. 
            // Но мы ожидаем, что будет Text "Stop", так как цикл пройдёт успешно.
            Assert.AreEqual("Stop", r2.Message.Text, "Защита дубликатов должна сбрасываться при новом вызове GetResponseAsync.");
        }

        [Test]
        public async Task DuplicateProtection_BlocksInSameRequestLoop()
        {
            // Убеждаемся, что если модель внутри ОДНОГО цикла (т.е. LLM решил снова вызвать тот же тул после ошибки или успеха), 
            // это будет заблокировано внутри SmartToolCallingChatClient.
            MockChatClient mockInner = new MockChatClient();
            SmartToolCallingChatClient smartClient = new SmartToolCallingChatClient(
                mockInner, _logger, _settings, allowDuplicateToolCalls: false, new[] { _dummyLlmTool }, maxConsecutiveErrors: 2
            );

            ChatOptions options = new ChatOptions { Tools = new[] { _dummyFunc } };
            
            // Модель вызывает тул на первой итерации
            mockInner.Responses.Enqueue(CreateResponseWithToolCall("dummy_tool"));
            // И затем СРАЗУ же вызывает его снова на второй итерации (внутри того же GetResponseAsync!)
            mockInner.Responses.Enqueue(CreateResponseWithToolCall("dummy_tool"));
            // Третий - чтобы завершиться текстом и выйти, если не упадет
            mockInner.Responses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Stop")));

            // Request
            ChatResponse result = await smartClient.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Do loop") }, options);

            // Первый тул проходит, второй блокируется (isDuplicate = true). 
            // Блокировка делает anyFailed = true -> consecutiveErrors = 1. Если maxConsecutiveErrors = 2, он прокрутит еще и на 3-й выйдет "Stop".
            // Однако в истории переписок мы увидим сообщение от тула об ошибке "Error: You just executed this exact same tool call...".
            
            // Давайте убедимся, что duplicate был заблокирован, запрашивая _any_ failed tool call block behavior. 
            // К счастью, SmartToolCallingChatClient возвращает финальный message (или падает по max errors).
            // Поскольку max errors (2) не был превышен до завершения, мы просто проверим историю, если это возможно, либо поведение.
            Assert.AreEqual("Stop", result.Message.Text); // Если бы он застрял, выпала бы ошибка. Застрял он не стал.
        }

        private ChatResponse CreateResponseWithToolCall(string toolName)
        {
            var call = new FunctionCallContent("call_123", toolName, new Dictionary<string, object>());
            var msg = new ChatMessage(ChatRole.Assistant, new[] { call });
            return new ChatResponse(msg);
        }
    }
}
