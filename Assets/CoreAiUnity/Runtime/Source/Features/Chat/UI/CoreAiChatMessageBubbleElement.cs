using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Chat
{
    /// <summary>
    /// Authorable UI Toolkit chat bubble used by CoreAiChatPanel and editable in UI Builder.
    /// </summary>
    public sealed class CoreAiChatMessageBubbleElement : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<CoreAiChatMessageBubbleElement, UxmlTraits>
        {
        }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            private readonly UxmlBoolAttributeDescription _isUser = new()
            {
                name = "is-user",
                defaultValue = false
            };

            private readonly UxmlStringAttributeDescription _messageText = new()
            {
                name = "message-text",
                defaultValue = "Message text"
            };

            private readonly UxmlAssetAttributeDescription<Sprite> _avatarSprite = new()
            {
                name = "avatar-sprite"
            };

            public override void Init(VisualElement element, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(element, bag, cc);

                CoreAiChatMessageBubbleElement bubble = (CoreAiChatMessageBubbleElement)element;
                bubble.IsUser = _isUser.GetValueFromBag(bag, cc);
                bubble.MessageText = _messageText.GetValueFromBag(bag, cc);
                bubble.AvatarSprite = _avatarSprite.GetValueFromBag(bag, cc);
            }
        }

        private readonly VisualElement _avatar;
        private readonly VisualElement _contentSlot;
        private readonly Label _messageLabel;

        private bool _isUser;
        private Sprite _avatarSprite;
        private string _messageText = "Message text";

        public bool IsUser
        {
            get => _isUser;
            set
            {
                _isUser = value;
                ApplySide();
            }
        }

        public Sprite AvatarSprite
        {
            get => _avatarSprite;
            set
            {
                _avatarSprite = value;
                _avatar.style.backgroundImage = value != null ? Background.FromSprite(value) : default;
            }
        }

        public string MessageText
        {
            get => _messageText;
            set
            {
                _messageText = value ?? string.Empty;
                _messageLabel.text = _messageText;
            }
        }

        public VisualElement ContentSlot => _contentSlot;

        public CoreAiChatMessageBubbleElement()
        {
            name = "coreai-message-row";
            AddToClassList("coreai-message-row");
            AddToClassList("coreai-ai-row");

            _avatar = new VisualElement { name = "coreai-message-avatar" };
            _avatar.AddToClassList("coreai-avatar");
            _avatar.AddToClassList("coreai-ai-avatar");

            _contentSlot = new VisualElement { name = "coreai-message-content-slot" };
            _contentSlot.AddToClassList("coreai-message-content-slot");

            _messageLabel = new Label(_messageText)
            {
                name = "coreai-message-text"
            };
            _messageLabel.AddToClassList("coreai-chat-message");
            _messageLabel.AddToClassList("coreai-ai-message");
            _contentSlot.Add(_messageLabel);

            Add(_avatar);
            Add(_contentSlot);
        }

        private void ApplySide()
        {
            RemoveFromClassList("coreai-user-row");
            RemoveFromClassList("coreai-ai-row");
            AddToClassList(_isUser ? "coreai-user-row" : "coreai-ai-row");

            _messageLabel.RemoveFromClassList("coreai-user-message");
            _messageLabel.RemoveFromClassList("coreai-ai-message");
            _messageLabel.AddToClassList(_isUser ? "coreai-user-message" : "coreai-ai-message");

            _avatar.style.display = _isUser ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
