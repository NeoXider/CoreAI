using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>Maps character offsets of a text to 1-based line and column numbers.</summary>
    internal sealed class RuntimeUiLineMap
    {
        private readonly List<int> _lineStarts = new() { 0 };

        public RuntimeUiLineMap(string text)
        {
            if (text == null)
            {
                return;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    _lineStarts.Add(i + 1);
                }
            }
        }

        public void Locate(int offset, out int line, out int column)
        {
            int low = 0;
            int high = _lineStarts.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_lineStarts[mid] <= offset)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            line = low + 1;
            column = offset - _lineStarts[low] + 1;
        }
    }
}
