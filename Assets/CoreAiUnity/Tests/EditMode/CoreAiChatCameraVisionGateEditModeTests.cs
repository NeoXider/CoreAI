using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The chat panel's camera attach (<see cref="CoreAiChatService.TryEnsureCameraToolForRole"/>) consults the same
    /// vision verdict as <c>AskWithCameraAsync</c> and <c>RegisterCameraVisionTool</c>
    /// (<see cref="CoreAISettingsAsset.IsVisionEnabled"/>), so a text-only model is never offered a tool whose
    /// images it cannot read. The Auto default itself is unchanged.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatCameraVisionGateEditModeTests
    {
        private const string Role = BuiltInAgentRoleIds.SmartChat;

        private static bool HasCamera(AgentMemoryPolicy policy)
        {
            foreach (ILlmTool tool in policy.GetToolsForRole(Role))
            {
                if (tool != null && tool.Name == CoreAiChatCameraTools.CameraToolName)
                {
                    return true;
                }
            }

            return false;
        }

        [Test]
        public void TextOnlyVerdict_AttachesNothing()
        {
            AgentMemoryPolicy policy = new();

            Assert.IsFalse(CoreAiChatCameraTools.TryAttachCameraTool(policy, new AgentCameraService(), Role, true,
                false));
            Assert.IsFalse(HasCamera(policy));
        }

        [Test]
        public void VisionVerdict_AttachesTheCamera()
        {
            AgentMemoryPolicy policy = new();

            Assert.IsTrue(CoreAiChatCameraTools.TryAttachCameraTool(policy, new AgentCameraService(), Role, true,
                true));
            Assert.IsTrue(HasCamera(policy));
        }

        [TestCase(VisionSupportMode.Off, false)]
        [TestCase(VisionSupportMode.On, true)]
        public void ChatService_FollowsTheSettingsVerdict(VisionSupportMode mode, bool expectAttached)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.SetVisionSupport(mode);
                AgentMemoryPolicy policy = new();
                CoreAiChatService service = new(new NoopOrchestrator(), policy, settings,
                    cameraService: new AgentCameraService());

                Assert.AreEqual(expectAttached, service.TryEnsureCameraToolForRole(Role, true));
                Assert.AreEqual(expectAttached, HasCamera(policy));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void AutoVerdict_IsUnchanged_AnOrdinaryChatModelStillGetsTheCamera()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                settings.ConfigureClientOwnedApi("http://localhost:1234/v1", "", "qwen3.5-4b");
                Assert.AreEqual(VisionSupportMode.Auto, settings.VisionSupport);
                AgentMemoryPolicy policy = new();
                CoreAiChatService service = new(new NoopOrchestrator(), policy, settings,
                    cameraService: new AgentCameraService());

                Assert.IsTrue(service.TryEnsureCameraToolForRole(Role, true),
                    "Auto keeps its owner-defined default (vision on unless a utility model).");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private sealed class NoopOrchestrator : IAiOrchestrationService
        {
            public System.Threading.Tasks.Task<string> RunTaskAsync(AiTaskRequest task,
                System.Threading.CancellationToken cancellationToken = default)
            {
                return System.Threading.Tasks.Task.FromResult("");
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
