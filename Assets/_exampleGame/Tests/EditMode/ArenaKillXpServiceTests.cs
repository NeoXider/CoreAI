using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using CoreAI.ExampleGame.ArenaProgression.UseCases;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.ExampleGame.Tests
{
    /// <summary>Regression coverage for the injected kill-XP boundary that replaced the static hub.</summary>
    public sealed class ArenaKillXpServiceTests
    {
        private static ArenaRunBalanceConfig NewBalance()
        {
            // CreateInstance yields serialized defaults: baseXpPerKill = 10, divideXpByAliveTeamMembers = true.
            return ScriptableObject.CreateInstance<ArenaRunBalanceConfig>();
        }

        [Test]
        public void AwardKill_SplitsBaseXpAcrossAliveMembers()
        {
            ArenaRunBalanceConfig balance = NewBalance();
            ArenaTeamProgressionState team = new();
            ArenaKillXpService service = new(new AddSessionKillXpUseCase(team, balance), balance.BaseXpPerKill, 2);

            service.AwardKill();

            Assert.AreEqual(balance.BaseXpPerKill / 2, team.SessionTotalXp);
        }

        [Test]
        public void Instances_AreIndependent_NoSharedGlobalState()
        {
            ArenaRunBalanceConfig balance = NewBalance();

            ArenaTeamProgressionState soloTeam = new();
            ArenaKillXpService solo = new(new AddSessionKillXpUseCase(soloTeam, balance), balance.BaseXpPerKill, 1);

            ArenaTeamProgressionState duoTeam = new();
            ArenaKillXpService duo = new(new AddSessionKillXpUseCase(duoTeam, balance), balance.BaseXpPerKill, 2);

            solo.AwardKill();
            duo.AwardKill();

            Assert.AreEqual(balance.BaseXpPerKill, soloTeam.SessionTotalXp);
            Assert.AreEqual(balance.BaseXpPerKill / 2, duoTeam.SessionTotalXp);
        }

        [Test]
        public void AwardXp_NonPositive_IsIgnored()
        {
            ArenaRunBalanceConfig balance = NewBalance();
            ArenaTeamProgressionState team = new();
            ArenaKillXpService service = new(new AddSessionKillXpUseCase(team, balance), balance.BaseXpPerKill, 1);

            service.AwardXp(0);
            service.AwardXp(-5);

            Assert.AreEqual(0, team.SessionTotalXp);
        }
    }
}
