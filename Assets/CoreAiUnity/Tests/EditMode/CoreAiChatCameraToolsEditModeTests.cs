using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Vision;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for wiring the runtime agent-vision <c>camera</c> tool into chat agents
    /// (<see cref="CoreAiChatCameraTools"/>): the <c>EnableCameraTool</c> toggle attaches the tool when on
    /// and leaves the role untouched when off, mirroring the benchmark's opt-in camera construction.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatCameraToolsEditModeTests
    {
        private static bool RoleHasCameraTool(AgentMemoryPolicy policy, string roleId)
        {
            foreach (ILlmTool tool in policy.GetToolsForRole(roleId))
            {
                if (tool != null &&
                    string.Equals(tool.Name, CoreAiChatCameraTools.CameraToolName,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        [Test]
        public void TryAttach_Enabled_AttachesCameraTool()
        {
            AgentMemoryPolicy policy = new();
            AgentCameraService camera = new();

            bool attached =
                CoreAiChatCameraTools.TryAttachCameraTool(policy, camera, BuiltInAgentRoleIds.SmartChat, true);

            Assert.IsTrue(attached, "the camera must be attached when the toggle is on");
            Assert.IsTrue(RoleHasCameraTool(policy, BuiltInAgentRoleIds.SmartChat));
        }

        [Test]
        public void TryAttach_Disabled_DoesNotAttachCameraTool()
        {
            AgentMemoryPolicy policy = new();
            AgentCameraService camera = new();

            bool attached =
                CoreAiChatCameraTools.TryAttachCameraTool(policy, camera, BuiltInAgentRoleIds.SmartChat, false);

            Assert.IsFalse(attached, "the camera is not attached when the toggle is off");
            Assert.IsFalse(RoleHasCameraTool(policy, BuiltInAgentRoleIds.SmartChat));
        }

        [Test]
        public void TryAttach_NoVisionService_SilentlyDegrades()
        {
            AgentMemoryPolicy policy = new();

            bool attached =
                CoreAiChatCameraTools.TryAttachCameraTool(policy, null, BuiltInAgentRoleIds.SmartChat, true);

            Assert.IsFalse(attached, "without a vision service the tool is skipped silently");
            Assert.IsFalse(RoleHasCameraTool(policy, BuiltInAgentRoleIds.SmartChat));
        }

        [Test]
        public void TryAttach_CalledTwice_DoesNotDuplicate()
        {
            AgentMemoryPolicy policy = new();
            AgentCameraService camera = new();

            Assert.IsTrue(
                CoreAiChatCameraTools.TryAttachCameraTool(policy, camera, BuiltInAgentRoleIds.SmartChat, true));
            Assert.IsFalse(
                CoreAiChatCameraTools.TryAttachCameraTool(policy, camera, BuiltInAgentRoleIds.SmartChat, true),
                "a repeated call must not duplicate the tool");

            int cameraCount = 0;
            foreach (ILlmTool tool in policy.GetToolsForRole(BuiltInAgentRoleIds.SmartChat))
            {
                if (tool != null &&
                    string.Equals(tool.Name, CoreAiChatCameraTools.CameraToolName,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    cameraCount++;
                }
            }

            Assert.AreEqual(1, cameraCount);
        }
    }
}
