using CoreAI.Ai.LuaCs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Ai
{
    /// <summary>
    /// The <c>JsonSchema</c> post-check of <c>TextGenerator</c>: a conforming answer (even fenced or wrapped in prose)
    /// is returned as compact JSON, and every violation is refused with the path that broke the schema.
    /// </summary>
    [TestFixture]
    public sealed class RbxJsonSchemaCheckEditModeTests
    {
        private const string Mood = "{\"type\":\"object\",\"properties\":{\"mood\":{\"type\":\"string\",\"enum\":[\"happy\",\"sad\"]}," +
                                    "\"score\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}," +
                                    "\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":2}}," +
                                    "\"required\":[\"mood\",\"score\"],\"additionalProperties\":false}";

        [TestCase("{\"mood\":\"happy\",\"score\":3}")]
        [TestCase("```json\n{\"mood\":\"sad\",\"score\":10}\n```")]
        [TestCase("Here you go: {\"mood\":\"happy\",\"score\":1,\"tags\":[\"a\"]} hope it helps")]
        public void AConformingAnswer_PassesAsCompactJson(string answer)
        {
            Assert.IsTrue(RbxJsonSchemaCheck.TryParseSchema(Mood, out JObject schema, out _));

            Assert.IsTrue(RbxJsonSchemaCheck.TryConform(answer, schema, out string json, out string reason), reason);
            StringAssert.StartsWith("{\"mood\":", json);
            StringAssert.DoesNotContain(" ", json.Replace("\"happy\"", "").Replace("\"sad\"", ""));
        }

        [TestCase("{\"mood\":\"angry\",\"score\":3}", "one of")]
        [TestCase("{\"mood\":\"happy\"}", "required property \"score\"")]
        [TestCase("{\"mood\":\"happy\",\"score\":11}", "maximum")]
        [TestCase("{\"mood\":\"happy\",\"score\":2.5}", "must be")]
        [TestCase("{\"mood\":\"happy\",\"score\":2,\"extra\":1}", "does not allow")]
        [TestCase("{\"mood\":\"happy\",\"score\":2,\"tags\":[\"a\",\"b\",\"c\"]}", "more than")]
        [TestCase("{\"mood\":\"happy\",\"score\":2,\"tags\":[1]}", "$.tags[0]")]
        [TestCase("I feel happy today.", "not JSON")]
        [TestCase("", "not JSON")]
        public void AViolatingAnswer_IsRefusedWithTheBrokenPath(string answer, string expected)
        {
            RbxJsonSchemaCheck.TryParseSchema(Mood, out JObject schema, out _);

            Assert.IsFalse(RbxJsonSchemaCheck.TryConform(answer, schema, out _, out string reason));
            StringAssert.Contains(expected, reason);
        }

        [TestCase("not json")]
        [TestCase("[1,2]")]
        [TestCase("")]
        public void ASchemaThatIsNotAJsonObject_IsRefused(string schemaText)
        {
            Assert.IsFalse(RbxJsonSchemaCheck.TryParseSchema(schemaText, out _, out string reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void ATypeList_AcceptsAnyOfItsTypes()
        {
            RbxJsonSchemaCheck.TryParseSchema("{\"type\":[\"string\",\"null\"]}", out JObject schema, out _);

            Assert.IsTrue(RbxJsonSchemaCheck.TryConform("\"x\"", schema, out _, out _));
            Assert.IsTrue(RbxJsonSchemaCheck.TryConform("null", schema, out _, out _));
            Assert.IsFalse(RbxJsonSchemaCheck.TryConform("3", schema, out _, out _));
        }
    }
}
