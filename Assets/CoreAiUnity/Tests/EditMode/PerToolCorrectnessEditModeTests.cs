using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Crafting;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class PerToolCorrectnessEditModeTests
    {
        [Test]
        public void DelegateLlmTool_ParameterizedDelegate_ExposesGeneratedSchema()
        {
            DelegateLlmTool tool = new("echo_value", "Echo a value.", new Func<string, string>(EchoValue));

            Assert.AreNotEqual("{}", tool.ParametersSchema);

            JObject schema = JObject.Parse(tool.ParametersSchema);
            Assert.IsNotNull(schema["properties"]?["value"]);
        }

        [Test]
        public void DelegateLlmTool_NoParameterDelegate_KeepsEmptySchema()
        {
            DelegateLlmTool tool = new("mark_done", "Mark done.", new Action(() => { }));

            Assert.AreEqual("{}", tool.ParametersSchema);
        }

        [Test]
        public void InventoryLlmTool_NullProvider_ThrowsAtConstruction()
        {
            Assert.Throws<ArgumentNullException>(() => new InventoryLlmTool(null));
        }

        [Test]
        public void CompatibilityLlmTool_DescriptionRequestsJsonArray()
        {
            CompatibilityLlmTool tool = new(new CompatibilityChecker());

            StringAssert.Contains("JSON array", tool.Description);
            StringAssert.DoesNotContain("comma-separated", tool.Description);
            StringAssert.Contains("JSON array", tool.ParametersSchema);
        }

        [Test]
        public void WaitLlmTool_ContractSaysOverMaxValuesAreClamped()
        {
            WaitLlmTool tool = new(2d);

            StringAssert.Contains("clamped", tool.ParametersSchema);

            AIFunction function = tool.CreateAIFunction();
            StringAssert.Contains("clamped", function.JsonSchema.ToString());
        }

        [Test]
        public async Task SceneLlmTool_FindObjects_BlankTermListsAllAndNullMethodDefaultsToName()
        {
            GameObject go = new("PerToolCorrectness_FindObjects");
            try
            {
                AIFunction function = new SceneLlmTool().CreateAIFunctions()
                    .Single(f => f.Name == "find_objects");

                object result = await function.InvokeAsync(
                    new AIFunctionArguments(new Dictionary<string, object>
                    {
                        ["searchTerm"] = "",
                        ["searchMethod"] = null,
                        ["includeInactive"] = true
                    }),
                    CancellationToken.None);

                JObject payload = JObject.Parse(result.ToString());
                Assert.IsTrue((bool)payload["success"]);
                Assert.IsTrue(payload["data"].Any(item => (string)item["name"] == go.name));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task SceneLlmTool_SetTransformWithoutFields_ReturnsValidationError()
        {
            GameObject go = new("PerToolCorrectness_SetTransform");
            try
            {
                AIFunction function = new SceneLlmTool().CreateAIFunctions()
                    .Single(f => f.Name == "set_transform");

                object result = await function.InvokeAsync(
                    new AIFunctionArguments(new Dictionary<string, object>
                    {
                        ["instanceId"] = go.GetEntityId().GetHashCode()
                    }),
                    CancellationToken.None);

                JObject payload = JObject.Parse(result.ToString());
                Assert.IsFalse((bool)payload["success"]);
                StringAssert.Contains("At least one transform field", (string)payload["error"]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static string EchoValue([System.ComponentModel.Description("Value to echo.")] string value)
        {
            return value;
        }
    }
}
