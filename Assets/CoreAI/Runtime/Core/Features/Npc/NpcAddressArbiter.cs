using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>What the arbiter knows about one NPC when it decides who answers a line.</summary>
    public readonly struct NpcAddressCandidate
    {
        /// <summary>Creates a candidate.</summary>
        public NpcAddressCandidate(string npcId, IReadOnlyList<string> names, NpcAddressingRules rules,
            NpcPosition? position, string conversationPartnerId, double conversationAtSeconds)
        {
            NpcId = npcId ?? "";
            Names = names ?? Array.Empty<string>();
            Rules = rules ?? new NpcAddressingRules();
            Position = position;
            ConversationPartnerId = conversationPartnerId ?? "";
            ConversationAtSeconds = conversationAtSeconds;
        }

        /// <summary>The NPC.</summary>
        public string NpcId { get; }

        /// <summary>Names it answers to.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>Its addressing rules.</summary>
        public NpcAddressingRules Rules { get; }

        /// <summary>Where it stands; null when unknown.</summary>
        public NpcPosition? Position { get; }

        /// <summary>The speaker it last answered; empty when none.</summary>
        public string ConversationPartnerId { get; }

        /// <summary>When it last answered that speaker, on the director's clock.</summary>
        public double ConversationAtSeconds { get; }
    }

    /// <summary>The arbiter's verdict: exactly one NPC, or none with the reason.</summary>
    public readonly struct NpcAddressDecision
    {
        /// <summary>Creates a verdict.</summary>
        public NpcAddressDecision(string npcId, NpcAddressReason reason, NpcIgnoreReason ignoreReason)
        {
            NpcId = npcId ?? "";
            Reason = reason;
            IgnoreReason = ignoreReason;
        }

        /// <summary>The NPC that answers; when nobody answers, the NPC that was named but out of reach, if any.</summary>
        public string NpcId { get; }

        /// <summary>Why it answers; <see cref="NpcAddressReason.None"/> when nobody does.</summary>
        public NpcAddressReason Reason { get; }

        /// <summary>Why nobody answers; <see cref="NpcIgnoreReason.None"/> when somebody does.</summary>
        public NpcIgnoreReason IgnoreReason { get; }

        /// <summary>True when an NPC answers.</summary>
        public bool IsAddressed => Reason != NpcAddressReason.None;

        /// <summary>Nobody answers.</summary>
        public static NpcAddressDecision Ignored(NpcIgnoreReason reason, string npcId = "")
        {
            return new NpcAddressDecision(npcId, NpcAddressReason.None, reason);
        }

        /// <summary><paramref name="npcId"/> answers.</summary>
        public static NpcAddressDecision Addressed(string npcId, NpcAddressReason reason)
        {
            return new NpcAddressDecision(npcId, reason, NpcIgnoreReason.None);
        }
    }

    /// <summary>
    /// Decides which single NPC, if any, answers a heard line, so two NPCs never answer the same line.
    /// Replace it to change who answers without touching the director.
    /// </summary>
    public interface INpcAddressArbiter
    {
        /// <summary>The verdict for <paramref name="line"/> among <paramref name="candidates"/> at <paramref name="nowSeconds"/>.</summary>
        NpcAddressDecision Decide(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> candidates,
            double nowSeconds, INpcLineOfSight lineOfSight);
    }

    /// <summary>
    /// The default arbiter. An interaction goes to its target when the speaker is within its interaction radius.
    /// A chat line goes, in this order, to the NPC named earliest in the line (the nearer one on a tie), to the
    /// NPC the speaker was talking with when the follow-up window is still open and the speaker is still within its
    /// hearing radius (when both positions are known), or to the nearest NPC that
    /// answers unaddressed lines within its radius. An NPC that is out of hearing range or out of sight is
    /// skipped; a line nobody takes is ignored with the reason.
    /// </summary>
    public sealed class NpcAddressArbiter : INpcAddressArbiter
    {
        /// <inheritdoc />
        public NpcAddressDecision Decide(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> candidates,
            double nowSeconds, INpcLineOfSight lineOfSight)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.Text))
            {
                return NpcAddressDecision.Ignored(NpcIgnoreReason.EmptyLine);
            }

            IReadOnlyList<NpcAddressCandidate> all = candidates ?? Array.Empty<NpcAddressCandidate>();
            INpcLineOfSight sight = lineOfSight ?? OpenNpcLineOfSight.Instance;
            return line.Channel == NpcHearingChannel.Interaction
                ? DecideInteraction(line, all)
                : DecideChat(line, all, nowSeconds, sight);
        }

        private static NpcAddressDecision DecideInteraction(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> all)
        {
            string target = (line.TargetNpcId ?? "").Trim();
            foreach (NpcAddressCandidate candidate in all)
            {
                if (!string.Equals(candidate.NpcId, target, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!WithinRadius(candidate.Position, line.SpeakerPosition, candidate.Rules.InteractionRadius))
                {
                    return NpcAddressDecision.Ignored(NpcIgnoreReason.OutOfRange, candidate.NpcId);
                }

                return NpcAddressDecision.Addressed(candidate.NpcId, NpcAddressReason.Interaction);
            }

            return NpcAddressDecision.Ignored(NpcIgnoreReason.UnknownNpc, target);
        }

        private static NpcAddressDecision DecideChat(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> all,
            double now, INpcLineOfSight sight)
        {
            int bestIndex = -1;
            NpcAddressCandidate? best = null;
            NpcIgnoreReason firstRefusal = NpcIgnoreReason.None;
            string refusedNpc = "";
            foreach (NpcAddressCandidate candidate in all)
            {
                if (!candidate.Rules.AnswerToName)
                {
                    continue;
                }

                int index = NpcText.IndexOfAnyName(line.Text, candidate.Names);
                if (index < 0)
                {
                    continue;
                }

                NpcIgnoreReason reach = Reach(candidate, line, candidate.Rules.HearingRadius, sight);
                if (reach != NpcIgnoreReason.None)
                {
                    if (firstRefusal == NpcIgnoreReason.None)
                    {
                        firstRefusal = reach;
                        refusedNpc = candidate.NpcId;
                    }

                    continue;
                }

                if (best == null || index < bestIndex ||
                    (index == bestIndex && IsBetterTie(candidate, best.Value, line.SpeakerPosition)))
                {
                    best = candidate;
                    bestIndex = index;
                }
            }

            if (best != null)
            {
                return NpcAddressDecision.Addressed(best.Value.NpcId, NpcAddressReason.Name);
            }

            if (firstRefusal != NpcIgnoreReason.None)
            {
                return NpcAddressDecision.Ignored(firstRefusal, refusedNpc);
            }

            NpcAddressCandidate? partner = FindFollowUp(line, all, now, sight);
            if (partner != null)
            {
                return NpcAddressDecision.Addressed(partner.Value.NpcId, NpcAddressReason.FollowUp);
            }

            NpcAddressCandidate? nearest = FindNearestListener(line, all, sight);
            return nearest != null
                ? NpcAddressDecision.Addressed(nearest.Value.NpcId, NpcAddressReason.Proximity)
                : NpcAddressDecision.Ignored(NpcIgnoreReason.NotAddressed);
        }

        private static NpcAddressCandidate? FindFollowUp(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> all,
            double now, INpcLineOfSight sight)
        {
            string speaker = (line.SpeakerId ?? "").Trim();
            if (speaker.Length == 0)
            {
                return null;
            }

            NpcAddressCandidate? found = null;
            foreach (NpcAddressCandidate candidate in all)
            {
                double window = candidate.Rules.FollowUpSeconds;
                if (window <= 0d ||
                    !string.Equals(candidate.ConversationPartnerId, speaker, StringComparison.Ordinal) ||
                    now - candidate.ConversationAtSeconds > window ||
                    Reach(candidate, line, candidate.Rules.HearingRadius, sight) != NpcIgnoreReason.None)
                {
                    continue;
                }

                if (found == null || candidate.ConversationAtSeconds > found.Value.ConversationAtSeconds)
                {
                    found = candidate;
                }
            }

            return found;
        }

        private static NpcAddressCandidate? FindNearestListener(NpcHeardLine line,
            IReadOnlyList<NpcAddressCandidate> all, INpcLineOfSight sight)
        {
            if (!line.SpeakerPosition.HasValue)
            {
                return null;
            }

            NpcAddressCandidate? found = null;
            double bestDistance = double.MaxValue;
            foreach (NpcAddressCandidate candidate in all)
            {
                double radius = candidate.Rules.AnswerUnaddressedWithin;
                if (radius <= 0d || !candidate.Position.HasValue)
                {
                    continue;
                }

                double distance = candidate.Position.Value.DistanceTo(line.SpeakerPosition.Value);
                if (distance > radius ||
                    (candidate.Rules.RequireLineOfSight &&
                     !sight.CanSee(line.SpeakerPosition.Value, candidate.Position.Value)))
                {
                    continue;
                }

                if (found == null || distance < bestDistance ||
                    (distance.Equals(bestDistance) &&
                     string.CompareOrdinal(candidate.NpcId, found.Value.NpcId) < 0))
                {
                    found = candidate;
                    bestDistance = distance;
                }
            }

            return found;
        }

        private static NpcIgnoreReason Reach(NpcAddressCandidate candidate, NpcHeardLine line, double radius,
            INpcLineOfSight sight)
        {
            if (!WithinRadius(candidate.Position, line.SpeakerPosition, radius))
            {
                return NpcIgnoreReason.OutOfRange;
            }

            if (candidate.Rules.RequireLineOfSight && candidate.Position.HasValue && line.SpeakerPosition.HasValue &&
                !sight.CanSee(line.SpeakerPosition.Value, candidate.Position.Value))
            {
                return NpcIgnoreReason.NoLineOfSight;
            }

            return NpcIgnoreReason.None;
        }

        private static bool WithinRadius(NpcPosition? npc, NpcPosition? speaker, double radius)
        {
            if (radius <= 0d || !npc.HasValue || !speaker.HasValue)
            {
                return true;
            }

            return npc.Value.DistanceTo(speaker.Value) <= radius;
        }

        private static bool IsBetterTie(NpcAddressCandidate candidate, NpcAddressCandidate current, NpcPosition? speaker)
        {
            if (speaker.HasValue && candidate.Position.HasValue && current.Position.HasValue)
            {
                double a = candidate.Position.Value.DistanceTo(speaker.Value);
                double b = current.Position.Value.DistanceTo(speaker.Value);
                if (!a.Equals(b))
                {
                    return a < b;
                }
            }

            return string.CompareOrdinal(candidate.NpcId, current.NpcId) < 0;
        }
    }
}
