using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// Argument reading must survive what models actually send: every optional parameter filled with an
    /// explicit JSON <c>null</c>. Reading those with <c>Value&lt;int&gt;()</c> used to throw and turn a
    /// plain <c>manage_mods list</c> into an isError result.
    /// </summary>
    public sealed class McpArgumentsEditModeTests
    {
        private static JObject WithExplicitNulls()
        {
            return JObject.Parse(
                "{\"action\":\"list\",\"mod_id\":null,\"revision\":null,\"since_sequence\":null," +
                "\"max_entries\":null,\"volume\":null,\"x\":null,\"worldPositionStays\":null}");
        }

        [Test]
        public void ExplicitJsonNull_FallsBackInsteadOfThrowing()
        {
            JObject arguments = WithExplicitNulls();

            Assert.AreEqual(-1, McpArguments.Int(arguments, "revision", -1));
            Assert.AreEqual(0L, McpArguments.Long(arguments, "since_sequence", 0));
            Assert.AreEqual(50, McpArguments.Int(arguments, "max_entries", 50));
            Assert.AreEqual(1f, McpArguments.Float(arguments, "volume", 1f));
            Assert.IsNull(McpArguments.FloatOrNull(arguments, "x"));
            Assert.IsFalse(McpArguments.Bool(arguments, "worldPositionStays"));
            Assert.IsNull(McpArguments.String(arguments, "mod_id"));
        }

        [Test]
        public void SuppliedValues_AreRead()
        {
            JObject arguments = JObject.Parse(
                "{\"action\":\"revert\",\"mod_id\":\"hud\",\"revision\":3,\"since_sequence\":120," +
                "\"volume\":0.5,\"x\":1.25,\"worldPositionStays\":true}");

            Assert.AreEqual("revert", McpArguments.String(arguments, "action"));
            Assert.AreEqual("hud", McpArguments.String(arguments, "mod_id"));
            Assert.AreEqual(3, McpArguments.Int(arguments, "revision", -1));
            Assert.AreEqual(120L, McpArguments.Long(arguments, "since_sequence", 0));
            Assert.AreEqual(0.5f, McpArguments.Float(arguments, "volume", 1f));
            Assert.AreEqual(1.25f, McpArguments.FloatOrNull(arguments, "x"));
            Assert.IsTrue(McpArguments.Bool(arguments, "worldPositionStays"));
        }

        [Test]
        public void MissingKeysAndNullObject_FallBack()
        {
            JObject empty = new();

            Assert.AreEqual(9, McpArguments.Int(empty, "revision", 9));
            Assert.AreEqual(9, McpArguments.Int(null, "revision", 9));
            Assert.AreEqual("fallback", McpArguments.String(null, "mod_id", "fallback"));
        }

        [Test]
        public void UnconvertibleValues_FallBackInsteadOfThrowing()
        {
            JObject arguments = JObject.Parse("{\"revision\":\"not-a-number\",\"volume\":{\"nested\":1}}");

            Assert.AreEqual(-1, McpArguments.Int(arguments, "revision", -1));
            Assert.AreEqual(1f, McpArguments.Float(arguments, "volume", 1f));
        }

        [Test]
        public void NumericStrings_AreStillAccepted()
        {
            // Some clients stringify every argument; that must keep working.
            JObject arguments = JObject.Parse("{\"revision\":\"4\",\"max_entries\":\"20\"}");

            Assert.AreEqual(4, McpArguments.Int(arguments, "revision", -1));
            Assert.AreEqual(20, McpArguments.Int(arguments, "max_entries", 50));
        }
    }
}
