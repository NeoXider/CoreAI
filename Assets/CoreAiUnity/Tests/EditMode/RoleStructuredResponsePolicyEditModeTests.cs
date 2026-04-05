using NUnit.Framework;
using CoreAI.Ai;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Тесты валидации ответов LLM для всех ролей.
    /// </summary>
    [TestFixture]
    public class RoleStructuredResponsePolicyEditModeTests
    {
        private CompositeRoleStructuredResponsePolicy _composite;

        [SetUp]
        public void SetUp()
        {
            _composite = new CompositeRoleStructuredResponsePolicy();
        }

        #region ProgrammerResponsePolicy Tests

        [Test]
        public void Programmer_ValidLuaCodeBlock_ReturnsTrue()
        {
            var content = @"Here's the code:
```lua
function add(a, b)
    return a + b
end
```";
            Assert.IsTrue(_composite.ShouldValidate("Programmer"));
            Assert.IsTrue(_composite.TryValidate("Programmer", content, out _));
        }

        [Test]
        public void Programmer_JsonWithExecuteLua_ReturnsTrue()
        {
            var content = @"{""execute_lua"": ""function add(a,b) return a+b end""}";
            Assert.IsTrue(_composite.TryValidate("Programmer", content, out _));
        }

        [Test]
        public void Programmer_PlainText_ReturnsFalse()
        {
            var content = "Sure, I can help with that. Here's what you should do...";
            Assert.IsFalse(_composite.TryValidate("Programmer", content, out var reason));
            StringAssert.Contains("Expected Lua code block", reason);
        }

        [Test]
        public void Programmer_EmptyResponse_ReturnsFalse()
        {
            Assert.IsFalse(_composite.TryValidate("Programmer", "", out var reason));
            StringAssert.Contains("empty", reason);
        }

        #endregion

        #region CoreMechanicResponsePolicy Tests

        [Test]
        public void CoreMechanic_ValidJsonWithNumbers_ReturnsTrue()
        {
            var content = @"{""damage"": 42.5, ""armor"": 10, ""resist"": 0.25}";
            Assert.IsTrue(_composite.ShouldValidate("CoreMechanicAI"));
            Assert.IsTrue(_composite.TryValidate("CoreMechanicAI", content, out _));
        }

        [Test]
        public void CoreMechanic_PlainText_ReturnsFalse()
        {
            var content = "I think we should increase damage by 20%.";
            Assert.IsFalse(_composite.TryValidate("CoreMechanicAI", content, out var reason));
            StringAssert.Contains("Expected JSON", reason);
        }

        [Test]
        public void CoreMechanic_JsonWithoutNumbers_ReturnsFalse()
        {
            var content = @"{""name"": ""sword"", ""type"": ""weapon""}";
            Assert.IsFalse(_composite.TryValidate("CoreMechanicAI", content, out var reason));
            StringAssert.Contains("numeric", reason);
        }

        #endregion

        #region CreatorResponsePolicy Tests

        [Test]
        public void Creator_ValidJsonObject_ReturnsTrue()
        {
            var content = @"{""commandType"": ""ArenaWavePlan"", ""payload"": {""waveIndex"": 3}}";
            Assert.IsTrue(_composite.ShouldValidate("Creator"));
            Assert.IsTrue(_composite.TryValidate("Creator", content, out _));
        }

        [Test]
        public void Creator_MarkdownJson_ReturnsTrue()
        {
            var content = @"```json
{""commandType"": ""ArenaWavePlan"", ""payload"": {}}
```";
            Assert.IsTrue(_composite.TryValidate("Creator", content, out _));
        }

        [Test]
        public void Creator_PlainText_ReturnsFalse()
        {
            var content = "I'll create a new wave with more enemies.";
            Assert.IsFalse(_composite.TryValidate("Creator", content, out var reason));
            StringAssert.Contains("Expected JSON", reason);
        }

        #endregion

        #region AnalyzerResponsePolicy Tests

        [Test]
        public void Analyzer_ValidMetricsJson_ReturnsTrue()
        {
            var content = @"{""metric"": ""player_death_rate"", ""value"": 0.35, ""status"": ""balanced""}";
            Assert.IsTrue(_composite.ShouldValidate("Analyzer"));
            Assert.IsTrue(_composite.TryValidate("Analyzer", content, out _));
        }

        [Test]
        public void Analyzer_RecommendationsJson_ReturnsTrue()
        {
            var content = @"{""recommendation"": ""increase enemy HP by 10%"", ""analysis"": ""players die too fast""}";
            Assert.IsTrue(_composite.TryValidate("Analyzer", content, out _));
        }

        [Test]
        public void Analyzer_PlainText_ReturnsFalse()
        {
            var content = "The game seems balanced enough.";
            Assert.IsFalse(_composite.TryValidate("Analyzer", content, out var reason));
            StringAssert.Contains("Expected JSON", reason);
        }

        [Test]
        public void Analyzer_JsonWithoutMetricKeys_ReturnsFalse()
        {
            var content = @"{""name"": ""test"", ""value"": 42}";
            Assert.IsFalse(_composite.TryValidate("Analyzer", content, out var reason));
            StringAssert.Contains("metric", reason);
        }

        #endregion

        #region AINpcResponsePolicy Tests

        [Test]
        public void AINpc_ValidText_ReturnsTrue()
        {
            var content = "Greetings, traveler! What brings you to these lands?";
            Assert.IsTrue(_composite.ShouldValidate("AINpc"));
            Assert.IsTrue(_composite.TryValidate("AINpc", content, out _));
        }

        [Test]
        public void AINpc_ValidJson_ReturnsTrue()
        {
            var content = @"{""dialogue"": ""Hello!"", ""emotion"": ""happy""}";
            Assert.IsTrue(_composite.TryValidate("AINpc", content, out _));
        }

        [Test]
        public void AINpc_EmptyText_ReturnsFalse()
        {
            Assert.IsFalse(_composite.TryValidate("AINpc", "", out var reason));
            StringAssert.Contains("empty", reason);
        }

        #endregion

        #region PlayerChatResponsePolicy Tests

        [Test]
        public void PlayerChat_ShouldNeverValidate_ReturnsFalse()
        {
            Assert.IsFalse(_composite.ShouldValidate("PlayerChat"));
        }

        [Test]
        public void PlayerChat_AnyContent_ReturnsTrue()
        {
            Assert.IsTrue(_composite.TryValidate("PlayerChat", "anything goes", out _));
            Assert.IsTrue(_composite.TryValidate("PlayerChat", "", out _));
            Assert.IsTrue(_composite.TryValidate("PlayerChat", "123", out _));
        }

        #endregion

        #region CompositePolicy Tests

        [Test]
        public void Composite_UnknownRole_FallbackToNoOp()
        {
            var unknownRoleId = "CustomRole";
            Assert.IsFalse(_composite.ShouldValidate(unknownRoleId));
            Assert.IsTrue(_composite.TryValidate(unknownRoleId, "anything", out _));
        }

        [Test]
        public void Composite_RegisterCustomPolicy_UsesCustom()
        {
            var customRoleId = "CustomRole";
            _composite.RegisterPolicy(customRoleId, new AlwaysFailPolicy());

            Assert.IsTrue(_composite.ShouldValidate(customRoleId));
            Assert.IsFalse(_composite.TryValidate(customRoleId, "test", out _));
        }

        [Test]
        public void Composite_GetPolicy_ReturnsCorrectPolicy()
        {
            Assert.IsTrue(_composite.GetPolicy("Programmer") is ProgrammerResponsePolicy);
            Assert.IsTrue(_composite.GetPolicy("Creator") is CreatorResponsePolicy);
            Assert.IsTrue(_composite.GetPolicy("PlayerChat") is PlayerChatResponsePolicy);
        }

        #endregion

        #region Helper Policies

        private sealed class AlwaysFailPolicy : IRoleStructuredResponsePolicy
        {
            public bool ShouldValidate(string roleId) => true;
            public bool TryValidate(string roleId, string rawContent, out string failureReason)
            {
                failureReason = "Always fails for testing";
                return false;
            }
        }

        #endregion
    }
}
