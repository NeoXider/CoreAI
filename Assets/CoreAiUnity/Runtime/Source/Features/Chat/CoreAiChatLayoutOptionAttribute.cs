using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// Marks fields on <see cref="CoreAiChatConfig"/> that control panel layout in the Inspector.
    /// Visible as a normal serialized field; use for documentation and optional custom PropertyDrawers.
    /// </summary>
    public sealed class CoreAiChatLayoutOptionAttribute : PropertyAttribute
    {
    }
}
