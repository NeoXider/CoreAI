using System;
using System.Collections.Generic;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// <see cref="AgentMemoryPolicy.AddToolForRole"/> replaces a tool of the same name instead of appending a
    /// duplicate (strict providers reject a request that declares one function name twice), keeps the tool's
    /// position, and <see cref="AgentMemoryPolicy.AddOrReplaceToolForRole"/> reports what changed.
    /// </summary>
    public sealed class AgentMemoryPolicyToolRegistrationEditModeTests
    {
        private const string Role = "Blacksmith";

        private static ILlmTool Tool(string name)
        {
            return new DelegateLlmTool(name, "Test tool " + name + ".", (Func<string, string>)(input => name));
        }

        private static List<ILlmTool> NamedTools(AgentMemoryPolicy policy, string name)
        {
            List<ILlmTool> matches = new();
            foreach (ILlmTool tool in policy.GetToolsForRole(Role))
            {
                if (tool != null && tool.Name == name)
                {
                    matches.Add(tool);
                }
            }

            return matches;
        }

        [Test]
        public void SameName_RegisteredTwice_KeepsOneTool_TheLatest()
        {
            AgentMemoryPolicy policy = new();
            ILlmTool first = Tool("game_state");
            ILlmTool second = Tool("game_state");

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Added,
                policy.AddOrReplaceToolForRole(Role, first));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Replaced,
                policy.AddOrReplaceToolForRole(Role, second));

            List<ILlmTool> matches = NamedTools(policy, "game_state");
            Assert.AreEqual(1, matches.Count, "One function per name reaches the request.");
            Assert.AreSame(second, matches[0]);
        }

        [Test]
        public void DifferentNames_AreBothKept()
        {
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Added,
                policy.AddOrReplaceToolForRole(Role, Tool("game_state")));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Added,
                policy.AddOrReplaceToolForRole(Role, Tool("inventory")));

            Assert.AreEqual(1, NamedTools(policy, "game_state").Count);
            Assert.AreEqual(1, NamedTools(policy, "inventory").Count);
        }

        [Test]
        public void Replacement_KeepsThePosition()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(Role, Tool("a"));
            policy.AddToolForRole(Role, Tool("b"));
            policy.AddToolForRole(Role, Tool("c"));
            ILlmTool replacement = Tool("b");

            policy.AddToolForRole(Role, replacement);

            List<string> order = new();
            foreach (ILlmTool tool in policy.GetToolsForRole(Role))
            {
                if (tool.Name == "a" || tool.Name == "b" || tool.Name == "c")
                {
                    order.Add(tool.Name);
                }
            }

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order);
            Assert.AreSame(replacement, NamedTools(policy, "b")[0]);
        }

        [Test]
        public void SameInstanceTwice_ReportsNoChange()
        {
            AgentMemoryPolicy policy = new();
            ILlmTool tool = Tool("camera");

            policy.AddToolForRole(Role, tool);

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddOrReplaceToolForRole(Role, tool));
            Assert.AreEqual(1, NamedTools(policy, "camera").Count);
        }

        [Test]
        public void NameMatch_IsOrdinal()
        {
            AgentMemoryPolicy policy = new();

            policy.AddToolForRole(Role, Tool("Camera"));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Added,
                policy.AddOrReplaceToolForRole(Role, Tool("camera")),
                "Function names are case-sensitive on the wire; only an exact name is the same tool.");
        }

        [Test]
        public void DuplicatesFromAnEarlierList_AreCollapsedByTheNextRegistration()
        {
            AgentMemoryPolicy policy = new();
            ILlmTool latest = Tool("dup");
            policy.SetToolsForRole(Role, new[] { Tool("dup"), Tool("other"), Tool("dup") });

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.Replaced,
                policy.AddOrReplaceToolForRole(Role, latest));

            List<ILlmTool> matches = NamedTools(policy, "dup");
            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(latest, matches[0]);
        }

        [Test]
        public void InvalidArguments_ChangeNothing()
        {
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddOrReplaceToolForRole(" ", Tool("x")));
            Assert.AreEqual(AgentMemoryPolicy.ToolRegistrationChange.None,
                policy.AddOrReplaceToolForRole(Role, null));
        }
    }
}
