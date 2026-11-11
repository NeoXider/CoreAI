using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Ai
{
    /// <summary>
    /// The engine-free halves of <c>TextGenerator</c>: the mirror clamps, the per-world request quota and the
    /// conversations behind <c>ContextToken</c>.
    /// </summary>
    [TestFixture]
    public sealed class RbxTextGenerationLimitsEditModeTests
    {
        [TestCase(0.7d, 0.7f)]
        [TestCase(5d, 1.0f)]
        [TestCase(0d, 0.4f)]
        [TestCase(-3d, 0.4f)]
        [TestCase(double.NaN, 0.7f)]
        public void Temperature_IsClampedTo_PointFourToOne(double written, float expected)
        {
            Assert.AreEqual(expected, RbxTextGenerator.ClampTemperature(written), 1e-6f);
        }

        [TestCase(0.9d, 0.9f)]
        [TestCase(2d, 1.0f)]
        [TestCase(0.1d, 0.5f)]
        [TestCase(double.NaN, 0.9f)]
        public void TopP_IsClampedTo_PointFiveToOne(double written, float expected)
        {
            Assert.AreEqual(expected, RbxTextGenerator.ClampTopP(written), 1e-6f);
        }

        [TestCase(3.9d, 3)]
        [TestCase(-3.9d, -3)]
        [TestCase(1e20d, int.MaxValue)]
        [TestCase(-1e20d, int.MinValue)]
        [TestCase(double.NaN, 0)]
        public void Seed_IsTruncated_AndSaturates(double written, int expected)
        {
            Assert.AreEqual(expected, RbxTextGenerator.TruncateSeed(written));
        }

        [Test]
        public void TheQuota_AllowsTheLimitPerMinute_ThenRefusesWithTheWait()
        {
            RbxTextGenerationQuota quota = new(2);

            Assert.IsTrue(quota.TryTake(0d, out _));
            Assert.IsTrue(quota.TryTake(10d, out _));
            Assert.IsFalse(quota.TryTake(20d, out double wait));
            Assert.AreEqual(40d, wait, 1e-9);
            Assert.IsTrue(quota.TryTake(60d, out _), "The first request left the window.");
        }

        [Test]
        public void TheQuota_DefaultsToTheMirrorsHundred_AndZeroIsUnlimited()
        {
            RbxTextGenerationQuota mirror = new();
            RbxTextGenerationQuota unlimited = new(0);
            int taken = 0;
            for (int i = 0; i < 150; i++)
            {
                taken += mirror.TryTake(0d, out _) ? 1 : 0;
                Assert.IsTrue(unlimited.TryTake(0d, out _));
            }

            Assert.AreEqual(100, taken);
        }

        [Test]
        public void TheQuota_GivesBackARefusedRequest()
        {
            RbxTextGenerationQuota quota = new(1);
            Assert.IsTrue(quota.TryTake(0d, out _));
            quota.Release();

            Assert.IsTrue(quota.TryTake(0d, out _));
        }

        [Test]
        public void AContextToken_RestoresTheConversationUpToIt_AndAnOlderTokenBranches()
        {
            RbxTextGenerationContexts contexts = new();
            string first = contexts.Append(null, new RbxTextGenerationTurn("one", "1"));
            Assert.IsTrue(contexts.TryGet(first, out IReadOnlyList<RbxTextGenerationTurn> afterFirst));
            string second = contexts.Append(afterFirst, new RbxTextGenerationTurn("two", "2"));
            string branch = contexts.Append(afterFirst, new RbxTextGenerationTurn("other", "x"));

            Assert.IsTrue(contexts.TryGet(second, out IReadOnlyList<RbxTextGenerationTurn> main));
            Assert.IsTrue(contexts.TryGet(branch, out IReadOnlyList<RbxTextGenerationTurn> side));
            CollectionAssert.AreEqual(new[] { "one", "two" }, Prompts(main));
            CollectionAssert.AreEqual(new[] { "one", "other" }, Prompts(side));
            Assert.AreNotEqual(second, branch);
            StringAssert.StartsWith(RbxTextGenerationContexts.TokenPrefix, second);
        }

        [Test]
        public void AnEmptyTokenIsANewConversation_AndAForgedOneIsUnknown()
        {
            RbxTextGenerationContexts contexts = new();

            Assert.IsTrue(contexts.TryGet("", out IReadOnlyList<RbxTextGenerationTurn> empty));
            Assert.AreEqual(0, empty.Count);
            Assert.IsFalse(contexts.TryGet("coreai-ctx-999-forged", out _));
        }

        [Test]
        public void TheStore_IsBounded_InTokensAndInTurns()
        {
            RbxTextGenerationContexts contexts = new(maxConversations: 2, maxTurns: 2);
            string a = contexts.Append(null, new RbxTextGenerationTurn("a", "a"));
            contexts.TryGet(a, out IReadOnlyList<RbxTextGenerationTurn> aTurns);
            string b = contexts.Append(aTurns, new RbxTextGenerationTurn("b", "b"));
            contexts.TryGet(b, out IReadOnlyList<RbxTextGenerationTurn> bTurns);
            string c = contexts.Append(bTurns, new RbxTextGenerationTurn("c", "c"));

            Assert.AreEqual(2, contexts.Count);
            Assert.IsFalse(contexts.TryGet(a, out _), "The least recently used token was dropped.");
            Assert.IsTrue(contexts.TryGet(c, out IReadOnlyList<RbxTextGenerationTurn> cTurns));
            CollectionAssert.AreEqual(new[] { "b", "c" }, Prompts(cTurns), "The oldest exchange was dropped.");
        }

        [Test]
        public void TheAiClasses_RegisterOnce_AndEnsureIsIdempotent()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            RbxAiClasses.EnsureRegistered(catalog);
            RbxAiClasses.EnsureRegistered(catalog);

            Assert.IsTrue(catalog.TryGet(RbxAiClasses.TextGenerator, out ClassDescriptor generator));
            Assert.IsTrue(generator.IsCreatable);
            Assert.IsFalse(generator.IsService);
            Assert.IsTrue(catalog.TryGet(RbxAiClasses.AiService, out ClassDescriptor service));
            Assert.IsTrue(service.IsService);
            Assert.IsFalse(service.IsCreatable);
            Assert.Throws<System.InvalidOperationException>(() => RbxAiClasses.Register(catalog));

            InstanceRegistry registry = new(catalog);
            RbxInstance created = registry.Create(RbxAiClasses.TextGenerator);
            Assert.IsInstanceOf<RbxTextGenerator>(created);
            Assert.AreEqual("TextGenerator", created.Name);
            Assert.IsTrue(created.IsA("Instance"));
        }

        [Test]
        public void ACloneKeepsTheGeneratorsProperties()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            RbxAiClasses.EnsureRegistered(catalog);
            InstanceRegistry registry = new(catalog);
            RbxTextGenerator generator = (RbxTextGenerator)registry.Create(RbxAiClasses.TextGenerator);
            generator.SystemPrompt = "beaver";
            generator.Temperature = 0.5f;
            generator.TopP = 0.6f;
            generator.Seed = 7;

            RbxTextGenerator copy = (RbxTextGenerator)generator.Clone();

            Assert.AreEqual("beaver", copy.SystemPrompt);
            Assert.AreEqual(0.5f, copy.Temperature);
            Assert.AreEqual(0.6f, copy.TopP);
            Assert.AreEqual(7, copy.Seed);
        }

        private static string[] Prompts(IReadOnlyList<RbxTextGenerationTurn> turns)
        {
            string[] prompts = new string[turns.Count];
            for (int i = 0; i < turns.Count; i++)
            {
                prompts[i] = turns[i].UserPrompt;
            }

            return prompts;
        }
    }
}
