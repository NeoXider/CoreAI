using CoreAI.Audit;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Audit
{
    public sealed class AuditHashEditModeTests
    {
        [Test]
        public void Compute_ReturnsDeterministicHash()
        {
            string hash1 = AuditHash.Compute("hello");
            string hash2 = AuditHash.Compute("hello");
            Assert.AreEqual(hash1, hash2);
        }

        [Test]
        public void Compute_Returns64CharHex()
        {
            string hash = AuditHash.Compute("test input");
            Assert.AreEqual(64, hash.Length);
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]+$"));
        }

        [Test]
        public void Compute_EmptyInput_ReturnsEmpty()
        {
            Assert.AreEqual("", AuditHash.Compute(""));
            Assert.AreEqual("", AuditHash.Compute(null));
        }

        [Test]
        public void Compute_DifferentInputs_DifferentHashes()
        {
            string hash1 = AuditHash.Compute("alpha");
            string hash2 = AuditHash.Compute("beta");
            Assert.AreNotEqual(hash1, hash2);
        }

        [Test]
        public void Chain_CombinesPrevHashAndJson()
        {
            string h = AuditHash.Chain("prev123", "{\"key\":\"value\"}");
            Assert.AreEqual(64, h.Length);
        }

        [Test]
        public void Chain_SameInputs_SameResult()
        {
            string h1 = AuditHash.Chain("p", "data");
            string h2 = AuditHash.Chain("p", "data");
            Assert.AreEqual(h1, h2);
        }

        [Test]
        public void Chain_DifferentPrevHash_DifferentResult()
        {
            string h1 = AuditHash.Chain("prev1", "data");
            string h2 = AuditHash.Chain("prev2", "data");
            Assert.AreNotEqual(h1, h2);
        }
    }
}