using System;
using System.IO;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEditor;
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

        [Test]
        public void UxmlTemplate_UsesStableTypeAndAttributeNames()
        {
            string path = FindShippedBubbleTemplatePath();

            // WHY: the authored values happen to equal the C# defaults, so a cloned tree alone cannot
            // tell "attribute applied" from "attribute silently ignored". Pin the authored NAMES on the
            // raw UXML text; a renamed attribute or type breaks every consumer template.
            // Path.GetFullPath resolves both "Assets/..." and virtual "Packages/..." asset paths.
            string uxml = File.ReadAllText(Path.GetFullPath(path));
            StringAssert.Contains("CoreAI.Chat.CoreAiChatMessageBubbleElement", uxml);
            StringAssert.Contains("is-user=", uxml);
            StringAssert.Contains("message-text=", uxml);

            VisualTreeAsset asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            Assert.IsNotNull(asset);

            TemplateContainer tree = asset.CloneTree();
            CoreAiChatMessageBubbleElement bubble = tree.Q<CoreAiChatMessageBubbleElement>();

            Assert.IsNotNull(bubble, "The custom element type in the shipped UXML no longer resolves.");
            Assert.IsFalse(bubble.IsUser);
            Assert.AreEqual("Message text", bubble.MessageText);
            Assert.IsNull(bubble.AvatarSprite);
        }

        /// <summary>The shipped chat-bubble template, wherever the package is mounted (Assets or Packages).</summary>
        private static string FindShippedBubbleTemplatePath()
        {
            string[] guids = AssetDatabase.FindAssets("CoreAiChatMessageBubble t:VisualTreeAsset");
            Assert.IsNotEmpty(
                guids,
                "CoreAiChatMessageBubble.uxml must ship with the package — it is the authored contract " +
                "for the custom element's UXML type and attribute names.");

            foreach (string guid in guids)
            {
                string candidate = AssetDatabase.GUIDToAssetPath(guid);
                if (candidate.EndsWith("/CoreAiChatMessageBubble.uxml", StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            Assert.Fail("CoreAiChatMessageBubble.uxml was not found among the matching VisualTreeAssets.");
            return null;
        }
    }
}
