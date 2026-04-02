using System;

namespace CoreAI.Ai
{
    public static class AgentMemoryDirectiveParser
    {
        public sealed class MemoryDirective
        {
            public bool Clear { get; set; }
            public bool Append { get; set; }
            public string MemoryText { get; set; }
        }

        /// <summary>
        /// Извлекает инструкции памяти из ответа LLM, чтобы агент сам решал, что сохранять.
        ///
        /// Поддерживаемый формат:
        /// - ```memory\n...text...\n```
        /// - ```memory_append\n...text...\n```
        /// - ```memory_clear\n```
        ///
        /// Директивный блок вырезается из текста (out cleanedContent).
        /// </summary>
        public static bool TryExtract(string content, out string cleanedContent, out MemoryDirective directive)
        {
            cleanedContent = content;
            directive = null;
            if (string.IsNullOrEmpty(content))
                return false;

            var start = content.IndexOf("```memory", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return false;

            var fenceEnd = content.IndexOf('\n', start);
            if (fenceEnd < 0)
                return false;

            var header = content.Substring(start, fenceEnd - start).Trim();
            var kind = header.Substring(3).Trim(); // remove leading ```

            var end = content.IndexOf("```", fenceEnd + 1, StringComparison.Ordinal);
            if (end < 0)
                return false;

            var inner = content.Substring(fenceEnd + 1, end - (fenceEnd + 1));
            var dir = new MemoryDirective();
            if (kind.Equals("memory_clear", StringComparison.OrdinalIgnoreCase))
            {
                dir.Clear = true;
            }
            else if (kind.Equals("memory_append", StringComparison.OrdinalIgnoreCase))
            {
                dir.Append = true;
                dir.MemoryText = inner.Trim();
            }
            else // memory
            {
                dir.MemoryText = inner.Trim();
            }

            // Remove directive block from content (including closing fence).
            var afterEnd = end + 3;
            cleanedContent = content.Substring(0, start) + content.Substring(afterEnd);
            cleanedContent = cleanedContent.Trim();

            directive = dir;
            return true;
        }
    }
}

