using CoreAI.Vision;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers the <c>screenshot</c> alias on <see cref="CameraLlmTool"/>: a model that searches tools by the
    /// literal word "screenshot" must find one, since <c>camera_capture</c> alone was not discoverable that
    /// way (see Docs/CoreAI/agent-vision.md and the FullAccess demo bug report).
    /// </summary>
    [TestFixture]
    public sealed class CameraLlmToolScreenshotAliasEditModeTests
    {
        private static AIFunction FindFunction(CameraLlmTool tool, string name)
        {
            foreach (AIFunction function in tool.CreateAIFunctions())
            {
                if (function.Name == name)
                {
                    return function;
                }
            }

            return null;
        }

        [Test]
        public void CreateAIFunctions_ExposesScreenshotAlias()
        {
            CameraLlmTool tool = new(new AgentCameraService(), "SomeRole");

            AIFunction screenshot = FindFunction(tool, "screenshot");

            Assert.IsNotNull(screenshot, "a tool literally named 'screenshot' must be discoverable");
            StringAssert.Contains("screenshot", screenshot.Description.ToLowerInvariant());
            StringAssert.Contains("see", screenshot.Description.ToLowerInvariant());
        }

        [Test]
        public void CreateAIFunctions_CameraCaptureDescription_MentionsScreenshotAndSeeing()
        {
            CameraLlmTool tool = new(new AgentCameraService(), "SomeRole");

            AIFunction capture = FindFunction(tool, "camera_capture");

            Assert.IsNotNull(capture);
            StringAssert.Contains("screenshot", capture.Description.ToLowerInvariant());
            StringAssert.Contains("see", capture.Description.ToLowerInvariant());
        }
    }
}
