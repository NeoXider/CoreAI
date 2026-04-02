using CoreAI.ExampleGame.Arena;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Схема Creator для арены + <see cref="CoreAI.Ai.LlmResponseSanitizer"/>.</summary>
    public sealed class ArenaWavePlanParserEditModeTests
    {
        private const string ValidEnvelope =
            "{\"commandType\":\"ArenaWavePlan\",\"payload\":{\"waveIndex1Based\":3,\"enemyCount\":5,\"enemyHpMult\":1.2,\"enemyDamageMult\":1,\"enemyMoveSpeedMult\":1,\"spawnIntervalSeconds\":0.5,\"spawnRadius\":16}}";

        [Test]
        public void TryParse_PlainJson_ParsesPayload()
        {
            Assert.IsTrue(ArenaWavePlanParser.TryParse(ValidEnvelope, out var plan));
            Assert.IsNotNull(plan);
            Assert.AreEqual(3, plan.waveIndex1Based);
            Assert.AreEqual(5, plan.enemyCount);
            Assert.AreEqual(1.2f, plan.enemyHpMult, 0.01f);
        }

        [Test]
        public void TryParse_MarkdownJsonFence_ParsesPayload()
        {
            var raw = "```json\n" + ValidEnvelope + "\n```";
            Assert.IsTrue(ArenaWavePlanParser.TryParse(raw, out var plan));
            Assert.IsNotNull(plan);
            Assert.AreEqual(3, plan.waveIndex1Based);
        }

        [Test]
        public void TryParse_PreambleAndFence_ParsesPayload()
        {
            var raw = "Sure!\n```json\n" + ValidEnvelope + "\n```\nDone.";
            Assert.IsTrue(ArenaWavePlanParser.TryParse(raw, out var plan));
            Assert.IsNotNull(plan);
            Assert.AreEqual(5, plan.enemyCount);
        }

        [Test]
        public void TryParse_WrongCommandType_ReturnsFalse()
        {
            const string raw = "{\"commandType\":\"Other\",\"payload\":{\"waveIndex1Based\":1,\"enemyCount\":1}}";
            Assert.IsFalse(ArenaWavePlanParser.TryParse(raw, out _));
        }

        [Test]
        public void TryParse_Empty_ReturnsFalse()
        {
            Assert.IsFalse(ArenaWavePlanParser.TryParse("", out _));
            Assert.IsFalse(ArenaWavePlanParser.TryParse("   ", out _));
        }
    }
}
