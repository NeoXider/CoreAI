using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class CoreAiChatMessageBubbleElementEditModeTests
    {
        [Test]
        public void Defaults_RepresentAiBubble_WithAvatarVisible()
        {
            CoreAiChatMessageBubbleElement bubble = new();

            Assert.IsTrue(bubble.ClassListContains("coreai-message-row"));
            Assert.IsTrue(bubble.ClassListContains("coreai-ai-row"));
            Assert.IsFalse(bubble.ClassListContains("coreai-user-row"));
            Assert.AreEqual("Message text", bubble.MessageText);

            VisualElement avatar = bubble.Q<VisualElement>("coreai-message-avatar");
            Assert.IsNotNull(avatar);
            Assert.AreEqual(DisplayStyle.Flex, avatar.style.display.value);
        }

        [Test]
        public void UserMessage_HidesAvatar_AndChangesMessageStyle()
        {
            CoreAiChatMessageBubbleElement bubble = new();
            bubble.IsUser = true;

            Assert.IsTrue(bubble.ClassListContains("coreai-user-row"));
            Assert.IsFalse(bubble.ClassListContains("coreai-ai-row"));

            Label message = bubble.Q<Label>("coreai-message-text");
            Assert.IsNotNull(message);
            Assert.IsTrue(message.ClassListContains("coreai-user-message"));
            Assert.IsFalse(message.ClassListContains("coreai-ai-message"));

            VisualElement avatar = bubble.Q<VisualElement>("coreai-message-avatar");
            Assert.IsNotNull(avatar);
            Assert.AreEqual(DisplayStyle.None, avatar.style.display.value);
        }

        [Test]
        public void ToggleUserState_ReturnsToAiStyle()
        {
            CoreAiChatMessageBubbleElement bubble = new();
            bubble.IsUser = true;
            bubble.IsUser = false;

            Assert.IsTrue(bubble.ClassListContains("coreai-ai-row"));
            Assert.IsFalse(bubble.ClassListContains("coreai-user-row"));

            Label message = bubble.Q<Label>("coreai-message-text");
            Assert.IsNotNull(message);
            Assert.IsTrue(message.ClassListContains("coreai-ai-message"));
            Assert.IsFalse(message.ClassListContains("coreai-user-message"));

            VisualElement avatar = bubble.Q<VisualElement>("coreai-message-avatar");
            Assert.IsNotNull(avatar);
            Assert.AreEqual(DisplayStyle.Flex, avatar.style.display.value);
        }

        [Test]
        public void MessageText_NullValue_NormalizesToEmpty()
        {
            CoreAiChatMessageBubbleElement bubble = new();
            Label label = bubble.Q<Label>("coreai-message-text");
            bubble.MessageText = null;

            Assert.AreEqual(string.Empty, bubble.MessageText);
            Assert.AreEqual(string.Empty, label.text);
        }
    }
}