using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.SubAgents;
using CoreAI.Messaging;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Orchestrator mode, integration change I6: a sub-agent's reply is a report for the lead, so Lua found in
    /// it is never executed by the envelope processor (no script run, no repair turn outside the plan's scope).
    /// The twin proves the same command from an ordinary turn still runs, so the guard keys on the source tag.
    /// </summary>
    public sealed class SubAgentEnvelopeLuaEditModeTests
    {
        private const string LuaReply = "```lua\nreturn 40 + 2\n```";

        [TestCase("SubAgent")]
        [TestCase("SubAgent:lua_repair")]
        public void EnvelopeFromASubAgentTurn_IsNotExecuted(string sourceTag)
        {
            Sink sink = new();
            LuaCsAiEnvelopeProcessor processor = CreateProcessor(sink);

            processor.Process(Envelope(sourceTag));

            Assert.IsEmpty(sink.Commands, "A sub-agent's reply must not run as a script.");
        }

        [Test]
        public void SameEnvelopeFromAnOrdinaryTurn_StillRuns()
        {
            Sink sink = new();
            LuaCsAiEnvelopeProcessor processor = CreateProcessor(sink);

            processor.Process(Envelope("Chat"));

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionSucceeded, sink.Commands[0].CommandTypeId);
            StringAssert.Contains("42", sink.Commands[0].JsonPayload);
        }

        [Test]
        public void SubAgentRequests_CarryTheTagTheGuardRecognises()
        {
            AiTaskRequest request = SubAgentRequestFactory.Create(
                new SubAgentDefinition("builder", BuiltInAgentRoleIds.Programmer, "Writes scripts.", null),
                "Write a door script", "", new SubAgentParentContext(BuiltInAgentRoleIds.Creator, "lead-1"),
                "plan-1", "t1", 1);

            Assert.IsTrue(SubAgentRequestFactory.IsSubAgentSourceTag(request.SourceTag));
        }

        private static LuaCsAiEnvelopeProcessor CreateProcessor(Sink sink)
        {
            return new LuaCsAiEnvelopeProcessor(new LuaCsSecureEnvironment(),
                new CoreDefaultLuaCsRuntimeBindings(), sink, () => null,
                new NullLuaExecutionObserver(), new MemoryLuaScriptVersionStore());
        }

        private static ApplyAiGameCommand Envelope(string sourceTag)
        {
            return new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.Envelope,
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTag = sourceTag,
                JsonPayload = LuaReply
            };
        }

        private sealed class Sink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command) => Commands.Add(command);
        }
    }
}
