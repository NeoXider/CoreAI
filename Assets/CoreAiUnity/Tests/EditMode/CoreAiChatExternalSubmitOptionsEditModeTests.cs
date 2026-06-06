using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Field-contract coverage for <see cref="CoreAiChatExternalSubmitOptions"/> used by
    /// <see cref="CoreAiChatPanel.SubmitMessageFromExternalAsync"/>.
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