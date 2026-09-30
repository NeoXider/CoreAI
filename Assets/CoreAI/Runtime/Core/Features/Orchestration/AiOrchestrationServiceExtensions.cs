using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Engine-free one-liners over <see cref="IAiOrchestrationService"/> for hosts that embed CoreAI without the
    /// Unity facade (a custom harness, a server, a console tool): a prompt alone, a prompt with one attachment,
    /// or a prompt with any number of images and text files — buffered or streamed, through the full
    /// orchestrator (memory, authority, tools, metrics).
    /// <code>
    /// string reply = await orchestrator.RunTaskAsync("Hello!");
    /// string seen = await orchestrator.RunTaskAsync("What is on this screenshot?", AiAttachment.FromFile("shot.png"));
    /// string review = await orchestrator.RunTaskAsync("Compare these", new[] { before, after, AiAttachment.FromFile("level.lua") });
    /// await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync("Describe it", image)) Console.Write(chunk.Text);
    /// </code>
    /// <para>
    /// Every overload builds one <see cref="AiTaskRequest"/> and nothing else: the attachment list is passed
    /// through as given (the single-attachment overload wraps it in a one-element array), image bytes are never
    /// copied. For per-call options (routing profile, forced tool mode, token budgets) build the
    /// <see cref="AiTaskRequest"/> yourself and set <see cref="AiTaskRequest.Attachments"/>.
    /// </para>
    /// <para>
    /// Lifetime: the caller's list and buffers are read again on every provider request of the turn (tool-call
    /// roundtrips, the final summary request, orchestrator retries), so keep them unchanged until the returned
    /// <see cref="Task"/> completes or the returned stream is fully enumerated or disposed. A bare <c>null</c> in
    /// the attachment position (<c>RunTaskAsync("x", null)</c>) is ambiguous between the overloads (CS0121): name
    /// <c>roleId:</c> or cast the null.
    /// </para>
    /// </summary>
    public static class AiOrchestrationServiceExtensions
    {
        /// <summary>Runs a prompt-only turn for <paramref name="roleId"/> and returns the final text.</summary>
        public static Task<string> RunTaskAsync(
            this IAiOrchestrationService orchestrator,
            string prompt,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return Require(orchestrator).RunTaskAsync(CreateRequest(prompt, null, roleId), cancellationToken);
        }

        /// <summary>Runs a turn with one attachment (an image or a text-like file).</summary>
        public static Task<string> RunTaskAsync(
            this IAiOrchestrationService orchestrator,
            string prompt,
            AiAttachment attachment,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return Require(orchestrator).RunTaskAsync(
                CreateRequest(prompt, AiAttachmentList.Of(attachment), roleId), cancellationToken);
        }

        /// <summary>Runs a turn with any number of attachments (images and text-like files, in order).</summary>
        public static Task<string> RunTaskAsync(
            this IAiOrchestrationService orchestrator,
            string prompt,
            IReadOnlyList<AiAttachment> attachments,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return Require(orchestrator).RunTaskAsync(CreateRequest(prompt, attachments, roleId), cancellationToken);
        }

        /// <summary>Streaming counterpart of <see cref="RunTaskAsync(IAiOrchestrationService, string, string, CancellationToken)"/>.</summary>
        public static IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            this IAiOrchestrationService orchestrator,
            string prompt,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return Require(orchestrator).RunStreamingAsync(CreateRequest(prompt, null, roleId), cancellationToken);
        }

        /// <summary>Streams a turn with one attachment.</summary>
        public static IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            this IAiOrchestrationService orchestrator,
            string prompt,
            AiAttachment attachment,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return Require(orchestrator).RunStreamingAsync(
                CreateRequest(prompt, AiAttachmentList.Of(attachment), roleId), cancellationToken);
        }

        /// <summary>Streams a turn with any number of attachments.</summary>
        public static IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            this IAiOrchestrationService orchestrator,
            string prompt,
            IReadOnlyList<AiAttachment> attachments,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return Require(orchestrator).RunStreamingAsync(CreateRequest(prompt, attachments, roleId),
                cancellationToken);
        }

        private static AiTaskRequest CreateRequest(string prompt, IReadOnlyList<AiAttachment> attachments, string roleId)
        {
            return new AiTaskRequest
            {
                RoleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.SmartChat : roleId,
                Hint = prompt ?? "",
                Attachments = attachments
            };
        }

        private static IAiOrchestrationService Require(IAiOrchestrationService orchestrator)
        {
            return orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        }
    }

    /// <summary>Shared helper for the single-attachment overloads of the core and Unity facades.</summary>
    internal static class AiAttachmentList
    {
        /// <summary>A one-element list (a 32-byte array), or null for a null attachment.</summary>
        internal static IReadOnlyList<AiAttachment> Of(AiAttachment attachment)
        {
            return attachment == null ? null : new[] { attachment };
        }
    }
}
