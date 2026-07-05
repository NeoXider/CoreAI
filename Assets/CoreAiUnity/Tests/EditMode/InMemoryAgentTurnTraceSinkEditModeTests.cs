using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class InMemoryAgentTurnTraceSinkEditModeTests
    {
        [Test]
        public void TryGetLatestTrace_ReturnsFalse_WhenNoTraceRecorded()
        {
            InMemoryAgentTurnTraceSink sink = new();

            bool found = sink.TryGetLatestTrace("Teacher", out AgentTurnTrace trace);

            Assert.IsFalse(found);
            Assert.IsNull(trace);
        }

        [Test]
        public void TryGetLatestTrace_ReturnsMostRecentTracePerRole()
        {
            InMemoryAgentTurnTraceSink sink = new();
            sink.Record(new AgentTurnTrace { RoleId = "Teacher", UserPayload = "first" });
            sink.Record(new AgentTurnTrace { RoleId = "Coder", UserPayload = "coder turn" });
            sink.Record(new AgentTurnTrace { RoleId = "Teacher", UserPayload = "second" });

            Assert.IsTrue(sink.TryGetLatestTrace("Teacher", out AgentTurnTrace teacher));
            Assert.AreEqual("second", teacher.UserPayload);

            Assert.IsTrue(sink.TryGetLatestTrace("Coder", out AgentTurnTrace coder));
            Assert.AreEqual("coder turn", coder.UserPayload);
        }

        [Test]
        public void Record_RetainsLatestPerRole_EvenAfterRingEviction()
        {
            // Capacity 1: the ring only holds the most recent trace, but the latest-per-role
            // map must still resolve the most recent trace for the queried role.
            InMemoryAgentTurnTraceSink sink = new(1);
            sink.Record(new AgentTurnTrace { RoleId = "Teacher", UserPayload = "old" });
            sink.Record(new AgentTurnTrace { RoleId = "Teacher", UserPayload = "new" });

            Assert.IsTrue(sink.TryGetLatestTrace("Teacher", out AgentTurnTrace trace));
            Assert.AreEqual("new", trace.UserPayload);
            Assert.AreEqual(1, sink.Snapshot().Length);
        }

        [Test]
        public void Record_CapturesStatusAndToolCalls_ForLiveTurnView()
        {
            InMemoryAgentTurnTraceSink sink = new();
            AgentTurnTrace recorded = new()
            {
                RoleId = "Teacher",
                Status = AgentTurnStatus.Failed,
                Error = "boom",
                AssistantResponse = "partial"
            };
            recorded.ToolCalls.Add(new AgentTurnToolCallTrace
            {
                Name = "memory",
                Success = false,
                DurationMs = 12.5,
                Source = "native",
                Detail = "tool failed"
            });
            sink.Record(recorded);

            Assert.IsTrue(sink.TryGetLatestTrace("Teacher", out AgentTurnTrace trace));
            Assert.AreEqual(AgentTurnStatus.Failed, trace.Status);
            Assert.AreEqual("boom", trace.Error);
            Assert.AreEqual(1, trace.ToolCalls.Count);
            Assert.AreEqual("memory", trace.ToolCalls[0].Name);
            Assert.IsFalse(trace.ToolCalls[0].Success);
        }

        [Test]
        public void TryGetLatestTrace_DoesNotMutateSink()
        {
            InMemoryAgentTurnTraceSink sink = new();
            sink.Record(new AgentTurnTrace { RoleId = "Teacher", UserPayload = "only" });

            sink.TryGetLatestTrace("Teacher", out _);
            sink.TryGetLatestTrace("Teacher", out _);

            Assert.AreEqual(1, sink.Snapshot().Length);
            Assert.IsTrue(sink.TryGetLatestTrace("Teacher", out AgentTurnTrace trace));
            Assert.AreEqual("only", trace.UserPayload);
        }
    }
}