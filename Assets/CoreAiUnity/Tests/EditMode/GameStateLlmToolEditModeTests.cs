using CoreAI.Ai;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Verifies the on-demand <c>game_state</c> tool serializes the live telemetry snapshot so an agent can
    /// pull current state instead of relying on stale values baked into history.
    /// </summary>
    public sealed class GameStateLlmToolEditModeTests
    {
        [Test]
        public void BuildStateJson_SerializesCurrentTelemetry()
        {
            SessionTelemetryCollector col = new();
            col.SetTelemetry("wave", 3);
            col.SetTelemetry("mode", "arena");

            string json = GameStateLlmTool.BuildStateJson(col.BuildSnapshot());

            StringAssert.StartsWith("{\"telemetry\":{", json);
            StringAssert.Contains("\"wave\":\"3\"", json);
            StringAssert.Contains("\"mode\":\"arena\"", json);
        }

        [Test]
        public void BuildStateJson_EmptyTelemetry_ReturnsEmptyObject()
        {
            SessionTelemetryCollector col = new();
            string json = GameStateLlmTool.BuildStateJson(col.BuildSnapshot());
            Assert.AreEqual("{\"telemetry\":{}}", json);
        }

        [Test]
        public void Tool_ExposesGameStateName_AndAllowsDuplicates()
        {
            GameStateLlmTool tool = new(new SessionTelemetryCollector());
            Assert.AreEqual("game_state", tool.Name);
            Assert.IsTrue(tool.AllowDuplicates, "Re-reading live state must be allowed on every call.");
        }
    }
}
