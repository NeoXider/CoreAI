using UnityEngine;

namespace CoreAI.Ai.Hub.CodeEditing
{

    internal readonly struct CompletionPlacement
    {
        public readonly float Left;
        public readonly float Top;

        public readonly float? MaxHeight;

        public readonly bool Above;

        private CompletionPlacement(float left, float top, float? maxHeight, bool above)
        {
            Left = left;
            Top = top;
            MaxHeight = maxHeight;
            Above = above;
        }

        public static CompletionPlacement Compute(
            Vector2 caretLineTopLeft,
            float lineHeight,
            float popupHeight,
            float popupWidth,
            Vector2? visibleSize)
        {
            float lineTop = caretLineTopLeft.y;
            float below = lineTop + lineHeight;
            float left = caretLineTopLeft.x;
            if (!visibleSize.HasValue || popupHeight <= 0.5f)
            {
                return new CompletionPlacement(left, below, null, false);
            }

            Vector2 size = visibleSize.Value;
            if (popupWidth > 0.5f && left + popupWidth > size.x)
            {
                left = size.x - popupWidth;
            }

            left = Mathf.Max(0f, left);

            float roomBelow = Mathf.Max(0f, size.y - below);
            float roomAbove = Mathf.Max(0f, lineTop);
            if (popupHeight <= roomBelow)
            {
                return new CompletionPlacement(left, below, null, false);
            }

            if (popupHeight <= roomAbove)
            {
                return new CompletionPlacement(left, lineTop - popupHeight, null, true);
            }

            return roomAbove > roomBelow
                ? new CompletionPlacement(left, 0f, roomAbove, true)
                : new CompletionPlacement(left, below, roomBelow, false);
        }
    }
}
