using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class SceneLlmToolEditModeTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _createdObjects.Clear();
        }

        [Test]
        public async Task FindObjectsAsync_NameSearch_RespectsIncludeInactive()
        {
            string prefix = Guid.NewGuid().ToString("N");
            GameObject activeMatch = SpawnGameObject($"{prefix}_target_active");
            SpawnGameObject($"{prefix}_target_inactive", active: false);
            SpawnGameObject($"{prefix}_other");

            SceneLlmTool tool = new();

            JObject activeOnly = await InvokeAsync(tool, "find_objects",
                new Dictionary<string, object>
                {
                    { "searchTerm", $"{prefix}_target" },
                    { "searchMethod", "name" },
                    { "includeInactive", false }
                });
            JArray activeOnlyData = AssertSuccessObjectArray(activeOnly);
            Assert.AreEqual(1, activeOnlyData.Count);
            Assert.IsTrue(activeOnlyData.Cast<JObject>().Any(item =>
                item["name"]?.ToString() == activeMatch.name));

            JObject withInactive = await InvokeAsync(tool, "find_objects",
                new Dictionary<string, object>
                {
                    { "searchTerm", $"{prefix}_target" },
                    { "searchMethod", "name" },
                    { "includeInactive", true }
                });
            JArray withInactiveData = AssertSuccessObjectArray(withInactive);
            Assert.AreEqual(2, withInactiveData.Count);
        }

        [Test]
        public async Task GetHierarchyAsync_InvalidRoot_ReturnsError()
        {
            SceneLlmTool tool = new();

            JObject response = await InvokeAsync(tool, "get_hierarchy", new Dictionary<string, object>
            {
                { "rootInstanceId", -7 }
            });

            Assert.AreEqual(false, response.Value<bool>("success"));
            StringAssert.Contains("not found", response.Value<string>("error"));
        }

        [Test]
        public async Task GetHierarchyAsync_WithValidRoot_ReturnsImmediateChildren()
        {
            string prefix = Guid.NewGuid().ToString("N");
            GameObject root = SpawnGameObject($"{prefix}_hier_root");
            SpawnGameObject($"{prefix}_hier_child_1", parent: root.transform);
            SpawnGameObject($"{prefix}_hier_child_2", parent: root.transform);

            SceneLlmTool tool = new();
            JObject response = await InvokeAsync(tool, "get_hierarchy", new Dictionary<string, object>
            {
                { "rootInstanceId", root.GetInstanceID() }
            });

            JArray children = AssertSuccessObjectArray(response);
            Assert.AreEqual(2, children.Count);
            Assert.IsTrue(children.Cast<JObject>().Any(item => item["name"]?.ToString() == $"{prefix}_hier_child_1"));
            Assert.IsTrue(children.Cast<JObject>().Any(item => item["name"]?.ToString() == $"{prefix}_hier_child_2"));
        }

        [Test]
        public async Task SetTransformAsync_PartialValues_DoNotOverwriteOthers()
        {
            GameObject go = SpawnGameObject($"{Guid.NewGuid():N}_transform_target");
            go.transform.position = new Vector3(1f, 2f, 3f);
            go.transform.eulerAngles = new Vector3(10f, 20f, 30f);
            go.transform.localScale = new Vector3(1f, 2f, 3f);

            SceneLlmTool tool = new();
            JObject response = await InvokeAsync(tool, "set_transform",
                new Dictionary<string, object>
                {
                    { "instanceId", go.GetInstanceID() },
                    { "px", 9f },
                    { "ry", 45f }
                });

            Assert.AreEqual(true, response.Value<bool>("success"));
            Assert.AreEqual(9f, go.transform.position.x, 0.0001f);
            Assert.AreEqual(2f, go.transform.position.y, 0.0001f);
            Assert.AreEqual(3f, go.transform.position.z, 0.0001f);
            Assert.AreEqual(10f, go.transform.eulerAngles.x, 0.0001f);
            Assert.AreEqual(45f, go.transform.eulerAngles.y, 0.0001f);
            Assert.AreEqual(30f, go.transform.eulerAngles.z, 0.0001f);
            Assert.AreEqual(1f, go.transform.localScale.x, 0.0001f);
            Assert.AreEqual(2f, go.transform.localScale.y, 0.0001f);
            Assert.AreEqual(3f, go.transform.localScale.z, 0.0001f);
        }

        private static JArray AssertSuccessObjectArray(JObject response)
        {
            Assert.AreEqual(true, response.Value<bool>("success"), "Expected success payload");
            JToken data = response["data"];
            Assert.IsNotNull(data, "Expected data field");
            Assert.IsInstanceOf<JArray>(data);
            return (JArray)data;
        }

        private static async Task<JObject> InvokeAsync(SceneLlmTool tool, string functionName,
            Dictionary<string, object> args)
        {
            AIFunction function = tool.CreateAIFunctions().Single(x => x.Name == functionName);

            object raw = await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
            return JObject.Parse(raw?.ToString() ?? "{}");
        }

        private GameObject SpawnGameObject(string name, bool active = true, Transform parent = null)
        {
            GameObject go = new(name);
            go.SetActive(active);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            _createdObjects.Add(go);
            return go;
        }
    }
}
