using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Protocol
{
    /// <summary>
    /// A single item of MCP tool-result content. Tools return an array of these; the dispatcher wraps
    /// them into the <c>{ "content": [...] , "isError": bool }</c> shape MCP <c>tools/call</c> expects.
    /// Engine-free: an image item carries base64 text, never a Unity texture.
    /// </summary>
    public sealed class McpContent
    {
        private McpContent()
        {
        }

        /// <summary>Content kind: <c>text</c> or <c>image</c>.</summary>
        public string Type { get; private set; }

        /// <summary>Text payload for a <c>text</c> item.</summary>
        public string Text { get; private set; }

        /// <summary>Base64 (no data-URL prefix) payload for an <c>image</c> item.</summary>
        public string Data { get; private set; }

        /// <summary>MIME type for an <c>image</c> item, e.g. <c>image/png</c>.</summary>
        public string MimeType { get; private set; }

        /// <summary>Creates a text content item.</summary>
        public static McpContent CreateText(string text)
        {
            return new McpContent { Type = "text", Text = text ?? "" };
        }

        /// <summary>Creates an image content item from raw base64 (no <c>data:</c> prefix).</summary>
        public static McpContent CreateImage(string base64Data, string mimeType = "image/png")
        {
            return new McpContent { Type = "image", Data = base64Data ?? "", MimeType = mimeType ?? "image/png" };
        }

        /// <summary>Serializes this item to its MCP JSON shape.</summary>
        public JObject ToJson()
        {
            if (Type == "image")
            {
                return new JObject
                {
                    ["type"] = "image",
                    ["data"] = Data,
                    ["mimeType"] = MimeType
                };
            }

            return new JObject
            {
                ["type"] = "text",
                ["text"] = Text
            };
        }
    }

    /// <summary>Outcome of a tool invocation: a content array plus an <c>isError</c> flag.</summary>
    public sealed class McpToolResult
    {
        /// <param name="content">Content items returned to the client.</param>
        /// <param name="isError">True when the call surfaced a tool-level error (still an HTTP 200 / RPC result).</param>
        public McpToolResult(IReadOnlyList<McpContent> content, bool isError = false)
        {
            Content = content ?? new List<McpContent>();
            IsError = isError;
        }

        /// <summary>Content items to hand back to the model.</summary>
        public IReadOnlyList<McpContent> Content { get; }

        /// <summary>True when the tool reported a failure (surfaced to the model, not a protocol error).</summary>
        public bool IsError { get; }

        /// <summary>Convenience: a single-text successful result.</summary>
        public static McpToolResult Text(string text)
        {
            return new McpToolResult(new List<McpContent> { McpContent.CreateText(text) });
        }

        /// <summary>Convenience: a single-text error result.</summary>
        public static McpToolResult Failure(string text)
        {
            return new McpToolResult(new List<McpContent> { McpContent.CreateText(text) }, true);
        }

        /// <summary>Serializes to the MCP <c>tools/call</c> result shape.</summary>
        public JObject ToJson()
        {
            JArray items = new();
            foreach (McpContent item in Content)
            {
                items.Add(item.ToJson());
            }

            return new JObject
            {
                ["content"] = items,
                ["isError"] = IsError
            };
        }
    }
}
