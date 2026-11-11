using System.Collections.Generic;
using CoreAI.Ai.Npc;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>
    /// The default "who answers" arbiter: exactly one NPC or none for every heard line, by name, interaction,
    /// follow-up or proximity, with range and line-of-sight refusals as negative twins.
    /// </summary>
    [TestFixture]
    public sealed class NpcAddressArbiterEditModeTests
    {
        private static readonly NpcPosition Origin = new(0d, 0d, 0d);

        private readonly NpcAddressArbiter _arbiter = new();

        [Test]
        public void ALineNamingOneNpc_IsAnsweredByThatNpcOnly()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Hi Bob, any bread?", Origin),
                Candidate("bob", "Bob", Origin), Candidate("alice", "Alice", Origin));

            Assert.AreEqual(NpcAddressReason.Name, decision.Reason);
            Assert.AreEqual("bob", decision.NpcId);
        }

        [Test]
        public void ALineNamingNobody_IsIgnored()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Nice weather today.", Origin),
                Candidate("bob", "Bob", Origin), Candidate("alice", "Alice", Origin));

            Assert.IsFalse(decision.IsAddressed);
            Assert.AreEqual(NpcIgnoreReason.NotAddressed, decision.IgnoreReason);
        }

        [Test]
        public void ALineNamingTwoNpcs_GoesToTheOneNamedFirst()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Alice, tell Bob I said hi", Origin),
                Candidate("bob", "Bob", Origin), Candidate("alice", "Alice", Origin));

            Assert.AreEqual("alice", decision.NpcId);
        }

        [Test]
        public void TwoNpcsWithTheSameName_TheNearerOneAnswers()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin),
                Candidate("bob-far", "Bob", new NpcPosition(10d, 0d, 0d)),
                Candidate("bob-near", "Bob", new NpcPosition(2d, 0d, 0d)));

            Assert.AreEqual("bob-near", decision.NpcId);
        }

        [Test]
        public void ANameInsideAnotherWord_DoesNotAddressTheNpc()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Bobby went home", Origin),
                Candidate("bob", "Bob", Origin));

            Assert.IsFalse(decision.IsAddressed);
        }

        [Test]
        public void AnAliasAddressesTheNpc_CaseInsensitively()
        {
            NpcAddressCandidate farmer = new("bob", new[] { "Bob", "farmer" }, new NpcAddressingRules(), Origin, "", 0d);

            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Hey FARMER!", Origin), farmer);

            Assert.AreEqual("bob", decision.NpcId);
        }

        [Test]
        public void ANamedNpcOutOfHearingRange_DoesNotAnswer()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Bob!", Origin),
                Candidate("bob", "Bob", new NpcPosition(100d, 0d, 0d)));

            Assert.IsFalse(decision.IsAddressed);
            Assert.AreEqual(NpcIgnoreReason.OutOfRange, decision.IgnoreReason);
            Assert.AreEqual("bob", decision.NpcId, "The refusal names the NPC that was out of reach.");
        }

        [Test]
        public void ANamedNpcWithoutKnownPositions_AnswersWhateverTheDistanceRule()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "Steve", "Bob!"),
                Candidate("bob", "Bob", new NpcPosition(100d, 0d, 0d)));

            Assert.AreEqual("bob", decision.NpcId);
        }

        [Test]
        public void LineOfSight_BlocksANamedNpc_WhenTheRuleRequiresIt()
        {
            NpcAddressingRules rules = new() { RequireLineOfSight = true };
            NpcAddressCandidate bob = new("bob", new[] { "Bob" }, rules, new NpcPosition(3d, 0d, 0d), "", 0d);

            NpcAddressDecision blocked = _arbiter.Decide(NpcHeardLine.Chat("p1", "S", "Bob?", Origin),
                new[] { bob }, 0d, new WallLineOfSight());
            NpcAddressDecision open = _arbiter.Decide(NpcHeardLine.Chat("p1", "S", "Bob?", Origin),
                new[] { bob }, 0d, OpenNpcLineOfSight.Instance);

            Assert.AreEqual(NpcIgnoreReason.NoLineOfSight, blocked.IgnoreReason);
            Assert.AreEqual("bob", open.NpcId);
        }

        [Test]
        public void AFollowUpWithoutAName_GoesToTheConversationPartner_WithinTheWindow()
        {
            NpcAddressCandidate bob = new("bob", new[] { "Bob" }, new NpcAddressingRules { FollowUpSeconds = 20d },
                Origin, "p1", 100d);

            NpcAddressDecision inWindow = _arbiter.Decide(NpcHeardLine.Chat("p1", "S", "and the price?", Origin),
                new[] { bob }, 110d, null);
            NpcAddressDecision expired = _arbiter.Decide(NpcHeardLine.Chat("p1", "S", "and the price?", Origin),
                new[] { bob }, 130d, null);
            NpcAddressDecision otherSpeaker = _arbiter.Decide(NpcHeardLine.Chat("p2", "A", "and the price?", Origin),
                new[] { bob }, 110d, null);

            Assert.AreEqual(NpcAddressReason.FollowUp, inWindow.Reason);
            Assert.IsFalse(expired.IsAddressed, "The follow-up window closed.");
            Assert.IsFalse(otherSpeaker.IsAddressed, "Somebody else's conversation is not a follow-up.");
        }

        [Test]
        public void NamingAnotherNpc_WinsOverAFollowUp()
        {
            NpcAddressCandidate bob = new("bob", new[] { "Bob" }, new NpcAddressingRules(), Origin, "p1", 100d);
            NpcAddressCandidate alice = Candidate("alice", "Alice", Origin);

            NpcAddressDecision decision = _arbiter.Decide(NpcHeardLine.Chat("p1", "S", "Alice, hi", Origin),
                new[] { bob, alice }, 101d, null);

            Assert.AreEqual("alice", decision.NpcId);
        }

        [Test]
        public void AnInteraction_GoesToItsTarget_InsideTheInteractionRadius()
        {
            NpcAddressDecision near = Decide(NpcHeardLine.Interaction("bob", "p1", "S", "hello", Origin),
                Candidate("bob", "Bob", new NpcPosition(2d, 0d, 0d)));
            NpcAddressDecision far = Decide(NpcHeardLine.Interaction("bob", "p1", "S", "hello", Origin),
                Candidate("bob", "Bob", new NpcPosition(50d, 0d, 0d)));
            NpcAddressDecision unknown = Decide(NpcHeardLine.Interaction("ghost", "p1", "S", "hello", Origin),
                Candidate("bob", "Bob", Origin));

            Assert.AreEqual(NpcAddressReason.Interaction, near.Reason);
            Assert.AreEqual(NpcIgnoreReason.OutOfRange, far.IgnoreReason);
            Assert.AreEqual(NpcIgnoreReason.UnknownNpc, unknown.IgnoreReason);
        }

        [Test]
        public void AnUnaddressedLine_GoesToTheNearestListener_OnlyWhenTheRuleIsOn()
        {
            NpcAddressingRules listening = new() { AnswerUnaddressedWithin = 5d };
            NpcAddressCandidate near = new("near", new[] { "Near" }, listening, new NpcPosition(2d, 0d, 0d), "", 0d);
            NpcAddressCandidate nearer = new("nearer", new[] { "Nearer" }, listening, new NpcPosition(1d, 0d, 0d), "", 0d);
            NpcAddressCandidate deaf = Candidate("deaf", "Deaf", new NpcPosition(0.5d, 0d, 0d));

            NpcAddressDecision decision = _arbiter.Decide(NpcHeardLine.Chat("p1", "S", "anyone here?", Origin),
                new[] { near, nearer, deaf }, 0d, null);
            NpcAddressDecision tooFar = _arbiter.Decide(
                NpcHeardLine.Chat("p1", "S", "anyone here?", new NpcPosition(40d, 0d, 0d)),
                new[] { near, nearer, deaf }, 0d, null);

            Assert.AreEqual(NpcAddressReason.Proximity, decision.Reason);
            Assert.AreEqual("nearer", decision.NpcId);
            Assert.IsFalse(tooFar.IsAddressed);
        }

        [Test]
        public void AnEmptyLine_IsIgnored()
        {
            NpcAddressDecision decision = Decide(NpcHeardLine.Chat("p1", "S", "   "), Candidate("bob", "Bob", Origin));

            Assert.AreEqual(NpcIgnoreReason.EmptyLine, decision.IgnoreReason);
        }

        private NpcAddressDecision Decide(NpcHeardLine line, params NpcAddressCandidate[] candidates)
        {
            return _arbiter.Decide(line, new List<NpcAddressCandidate>(candidates), 0d, null);
        }

        private static NpcAddressCandidate Candidate(string id, string name, NpcPosition? position)
        {
            return new NpcAddressCandidate(id, new[] { name }, new NpcAddressingRules(), position, "", 0d);
        }

        private sealed class WallLineOfSight : INpcLineOfSight
        {
            public bool CanSee(NpcPosition from, NpcPosition to)
            {
                return false;
            }
        }
    }
}
