using System;
using UnityEngine;

namespace CoreAI.ChatMarkdown
{
    /// <summary>The clipboard <see cref="CoreAiMarkdownView"/> copies to; a test replaces the writer.</summary>
    internal static class CoreAiMarkdownClipboard
    {
        private static readonly Action<string> SystemWriter = text => GUIUtility.systemCopyBuffer = text;

        /// <summary>Where copied text goes; null restores the system clipboard.</summary>
        internal static Action<string> Writer { get; set; }

        internal static void Write(string text)
        {
            (Writer ?? SystemWriter)(text ?? "");
        }
    }
}
