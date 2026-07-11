using CoreAI.Ai;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Verifies the telemetry gate in <see cref="AiPromptComposer.BuildUserPayload"/>: the JSON envelope
    /// (<c>{"telemetry":..,"hint":..,"ai_task_source":..}</c>) is emitted only when the game has published
    /// telemetry; a turn with no telemetry (plain chat) sends the raw hint text so the model is not handed a
    /// confusing JSON wrapper.
    /// </summary>
    public sealed class AiPromptComposerTelemetryEnvelopeEditModeTests
    {
        private static AiPromptComposer NewComposer()
        {
            return new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
        }

        [Test]
        public void EmptyTelemetry_SendsPlainHint_NoEnvelope()
        {
            AiPromptComposer composer = NewComposer();
            GameSessionSnapshot snap = new(); // game published no telemetry

            string u = composer.BuildUserPayload(snap, new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.PlainChat,
                Hint = "привет",
                SourceTag = "Chat"
            });

            Assert.AreEqual("привет", u, "Plain chat must send the raw hint, not a JSON envelope.");
            StringAssert.DoesNotContain("telemetry", u);
            StringAssert.DoesNotContain("ai_task_source", u);
        }

        [Test]
        public void WithTelemetry_WrapsEnvelope_ForAutonomousAgents()
        {
            AiPromptComposer composer = NewComposer();
            GameSessionSnapshot snap = new();
            snap.Telemetry["wave"] = "3";

            string u = composer.BuildUserPayload(snap, new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "plan the next wave",
                SourceTag = "Arena"
            });

            StringAssert.Contains("\"telemetry\":", u);
            StringAssert.Contains("\"wave\":\"3\"", u);
            StringAssert.Contains("\"hint\":\"plan the next wave\"", u);
            StringAssert.Contains("\"ai_task_source\":\"Arena\"", u);
        }
    }
}
