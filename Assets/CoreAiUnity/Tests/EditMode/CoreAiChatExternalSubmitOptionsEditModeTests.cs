using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Опции <see cref="CoreAiChatExternalSubmitOptions"/> для <see cref="CoreAiChatPanel.SubmitMessageFromExternalAsync"/>.
    /// Саму панель без полного UITK + DI не тестируем здесь — только контракт полей.
    /// </summary>
    public sealed class CoreAiChatExternalSubmitOptionsEditModeTests
    {
        [Test]
        public void Defaults_AppendUserTrue_SimulatedNull()
        {
            CoreAiChatExternalSubmitOptions o = new();
            Assert.IsTrue(o.AppendUserMessageToChat);
            Assert.IsNull(o.SimulatedAssistantReply);
        }

        [Test]
        public void Overrides_Persist()
        {
            CoreAiChatExternalSubmitOptions o = new()
            {
                AppendUserMessageToChat = false,
                SimulatedAssistantReply = "NPC line"
            };
            Assert.IsFalse(o.AppendUserMessageToChat);
            Assert.AreEqual("NPC line", o.SimulatedAssistantReply);
        }
    }
}
