using System;
using CoreAI.Ai;
using CoreAI.Vision;

namespace CoreAI.Chat
{
    /// <summary>
    /// Attaches the runtime agent-vision <c>camera</c> tool (<c>camera_capture</c> / <c>camera_look</c> /
    /// <c>camera_list</c>) to a chat agent role. Mirrors the benchmark's <c>env.CameraTool(roleId)</c>
    /// construction using runtime types only. Idempotent (never double-attaches, e.g. when the Programmer
    /// already has it from <c>WorldCommandsInstaller</c>) and silently degrades when the vision service is
    /// unavailable, so text-only models and headless scenes without a world executor are unaffected.
    /// </summary>
    internal static class CoreAiChatCameraTools
    {
        /// <summary>Tool name exposed by <see cref="CameraLlmTool"/>.</summary>
        internal const string CameraToolName = "camera";

        /// <summary>
        /// Adds the camera tool to <paramref name="roleId"/> when <paramref name="enabled"/> is true and the
        /// vision service is available. Returns whether a tool was newly attached.
        /// </summary>
        internal static bool TryAttachCameraTool(
            AgentMemoryPolicy policy, IAgentCameraService cameraService, string roleId, bool enabled)
        {
            if (!enabled || policy == null || cameraService == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            string trimmed = roleId.Trim();
            foreach (ILlmTool tool in policy.GetToolsForRole(trimmed))
            {
                if (tool != null &&
                    string.Equals(tool.Name, CameraToolName, StringComparison.OrdinalIgnoreCase))
                {
                    // WHY: already present (e.g. Programmer wired by WorldCommandsInstaller) — do not duplicate.
                    return false;
                }
            }

            policy.AddToolForRole(trimmed, new CameraLlmTool(cameraService, trimmed));
            return true;
        }
    }
}
