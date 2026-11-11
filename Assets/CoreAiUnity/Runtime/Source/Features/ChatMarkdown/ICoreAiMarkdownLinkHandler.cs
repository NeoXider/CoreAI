using System;
using UnityEngine;

namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// Decides what activating a link in rendered Markdown does. Without a handler a link is inert: model text
    /// is untrusted, so nothing is opened unless the host says so.
    /// </summary>
    public interface ICoreAiMarkdownLinkHandler
    {
        /// <summary>
        /// Called on the main thread when the reader activates a link. <paramref name="target"/> is the raw link
        /// target written by the model (any scheme, relative, an anchor), never validated.
        /// </summary>
        void OnLinkActivated(string target);
    }

    /// <summary>Ready-made link handlers a host may opt into.</summary>
    public static class CoreAiMarkdownLinkHandlers
    {
        /// <summary>
        /// Opens absolute <c>http</c>/<c>https</c> targets with <see cref="Application.OpenURL"/> and ignores every
        /// other target (<c>file:</c>, <c>javascript:</c>, custom schemes, relative paths, anchors).
        /// </summary>
        public static ICoreAiMarkdownLinkHandler OpenWebLinks { get; } = new WebLinkHandler();

        /// <summary>True for an absolute <c>http</c> or <c>https</c> URI with a host.</summary>
        public static bool IsWebUrl(string target)
        {
            if (string.IsNullOrWhiteSpace(target) ||
                !Uri.TryCreate(target.Trim(), UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                   !string.IsNullOrEmpty(uri.Host);
        }

        private sealed class WebLinkHandler : ICoreAiMarkdownLinkHandler
        {
            public void OnLinkActivated(string target)
            {
                if (IsWebUrl(target))
                {
                    Application.OpenURL(target.Trim());
                }
            }
        }
    }
}
