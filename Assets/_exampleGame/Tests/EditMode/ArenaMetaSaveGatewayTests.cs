using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using Neo.Save;
using NUnit.Framework;

namespace CoreAI.ExampleGame.Tests
{
    /// <summary>Regression coverage for JSON meta persistence: delimiter-safe ids and legacy back-compat.</summary>
    public sealed class ArenaMetaSaveGatewayTests
    {
        private const string Key = "CoreAI.Arena.Meta.v1";

        [SetUp]
        public void SetUp()
        {
            SaveProvider.SetProvider(new PlayerPrefsSaveProvider());
            SaveProvider.DeleteKey(Key);
        }

        [TearDown]
        public void TearDown()
        {
            SaveProvider.DeleteKey(Key);
        }

        [Test]
        public void SaveLoad_UpgradeIdWithDelimiters_RoundTripsIntact()
        {
            ArenaMetaSaveGateway gateway = new(null);
            ArenaMetaProgressionState saved = new();
            saved.SetFromSnapshot(123, 4, new[] { "fire|storm", "a,b,c", "plain" });

            gateway.Save(saved);

            ArenaMetaProgressionState loaded = new();
            gateway.LoadInto(loaded);

            Assert.AreEqual(123, loaded.MetaXp);
            Assert.AreEqual(4, loaded.MetaLevel);
            CollectionAssert.AreEquivalent(new[] { "fire|storm", "a,b,c", "plain" }, loaded.UnlockedUpgradeIds);
        }

        [Test]
        public void Load_LegacyPackedString_StillLoads()
        {
            // WHY: pre-JSON saves used "{ver}|{xp}|{level}|{id,id}".
            SaveProvider.SetString(Key, "1|250|7|alpha,beta");
            SaveProvider.Save();

            ArenaMetaSaveGateway gateway = new(null);
            ArenaMetaProgressionState loaded = new();
            gateway.LoadInto(loaded);

            Assert.AreEqual(250, loaded.MetaXp);
            Assert.AreEqual(7, loaded.MetaLevel);
            CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, loaded.UnlockedUpgradeIds);
        }
    }
}
