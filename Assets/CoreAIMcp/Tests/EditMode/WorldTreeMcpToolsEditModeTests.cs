using System.Collections.Generic;
using System.Threading;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// MCP <c>world_tree</c> and <c>instance_get</c> over an engine-free world: depth-first pages with class and name
    /// filters and a cursor, paths and <c>id:</c> lookup, one instance's properties, attributes, tags and owning mod,
    /// and plain refusals for an unknown path or a missing world.
    /// </summary>
    public sealed class WorldTreeMcpToolsEditModeTests
    {
        [Test]
        public void WorldTree_ListsDepthFirst_WithFiltersAndPages()
        {
            (RbxDataModel game, InstanceRegistry registry) = World(out RbxInstance model);
            IReadOnlyList<IMcpTool> tools = WorldTreeMcpTools.Create(() => game, () => registry);
            IMcpTool tree = Tool(tools, "world_tree");

            JObject parts = Call(tree, new JObject { ["root"] = "Workspace", ["class"] = "BasePart", ["depth"] = 5 });
            List<string> paths = Paths(parts);
            CollectionAssert.AreEqual(new[] { "Workspace.House.Wall", "Workspace.House.Roof", "Workspace.Lamp" }, paths);

            JObject named = Call(tree, new JObject { ["root"] = "Workspace", ["name"] = "roof", ["depth"] = 5 });
            CollectionAssert.AreEqual(new[] { "Workspace.House.Roof" }, Paths(named));

            JObject first = Call(tree, new JObject { ["root"] = "Workspace.House", ["depth"] = 5, ["max"] = 2 });
            Assert.AreEqual(2, (int)first["count"]);
            JObject rest = Call(tree, new JObject
            {
                ["root"] = "Workspace.House", ["depth"] = 5, ["max"] = 10, ["cursor"] = first["next_cursor"]
            });
            Assert.AreEqual(-1, (long)rest["next_cursor"]);
            Assert.AreEqual("Workspace.House.Roof", (string)rest["instances"][0]["path"]);
            Assert.AreEqual(2, (int)first["instances"][0]["children"], "the House model has two parts");
        }

        [Test]
        public void InstanceGet_ReadsPropertiesAttributesTagsAndOwner_ByPathOrId()
        {
            (RbxDataModel game, InstanceRegistry registry) = World(out RbxInstance model);
            model.SetAttribute("Floors", 2.0);
            model.AddTag("Building");
            IMcpTool get = Tool(WorldTreeMcpTools.Create(() => game, () => registry), "instance_get");

            JObject byPath = Call(get, new JObject { ["path"] = "Workspace.House" });
            Assert.AreEqual("Model", (string)byPath["class"]);
            Assert.AreEqual("mod-house", (string)byPath["owner_mod"]);
            Assert.AreEqual("2", (string)byPath["attributes"]["Floors"]);
            Assert.AreEqual("Building", (string)byPath["tags"][0]);
            Assert.AreEqual("House", (string)byPath["properties"]["Name"]);

            JObject byId = Call(get, new JObject { ["path"] = (string)byPath["id"] });
            Assert.AreEqual("Workspace.House", (string)byId["path"]);
        }

        [Test]
        public void InstanceGet_UnknownPath_FailsWithTheFormat()
        {
            (RbxDataModel game, InstanceRegistry registry) = World(out _);
            McpToolResult result = Tool(WorldTreeMcpTools.Create(() => game, () => registry), "instance_get")
                .InvokeAsync(new JObject { ["path"] = "Workspace.Nope" }, CancellationToken.None).Result;

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("GetFullName", result.Content[0].Text);
        }

        [Test]
        public void WorldTree_WithoutAWorld_SaysSo()
        {
            McpToolResult result = Tool(WorldTreeMcpTools.Create(() => null), "world_tree")
                .InvokeAsync(new JObject(), CancellationToken.None).Result;

            Assert.IsTrue(result.IsError);
        }

        private static IMcpTool Tool(IReadOnlyList<IMcpTool> tools, string name)
        {
            foreach (IMcpTool tool in tools)
            {
                if (tool.Name == name) return tool;
            }

            Assert.Fail("no tool " + name);
            return null;
        }

        private static JObject Call(IMcpTool tool, JObject arguments)
        {
            McpToolResult result = tool.InvokeAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsFalse(result.IsError, result.Content[0].Text);
            return JObject.Parse(result.Content[0].Text);
        }

        private static List<string> Paths(JObject listing)
        {
            List<string> paths = new();
            foreach (JToken row in (JArray)listing["instances"]) paths.Add((string)row["path"]);
            return paths;
        }

        private static (RbxDataModel, InstanceRegistry) World(out RbxInstance model)
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance workspace = game.FindService("Workspace");
            model = registry.Create("Model", "mod-house");
            model.Name = "House";
            model.Parent = workspace;
            RbxInstance wall = registry.Create("Part", "mod-house");
            wall.Name = "Wall";
            wall.Parent = model;
            RbxInstance roof = registry.Create("Part", "mod-house");
            roof.Name = "Roof";
            roof.Parent = model;
            RbxInstance lamp = registry.Create("Part");
            lamp.Name = "Lamp";
            lamp.Parent = workspace;
            return (game, registry);
        }
    }
}
