using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Every tool a built-in role's system prompt tells the model to call must be a tool CoreAI
    /// actually ships, spelled the way the tool spells itself.
    /// </summary>
    /// <remarks>
    /// WHY this exists: a prompt that names a tool the model cannot reach is a silent contradiction —
    /// the model obeys the prompt, the call is stripped without execution, and the turn is spent with
    /// nothing done and nothing thrown. The live case that prompted these tests on 2026-09-05 was the
    /// `memory` tool: it is advertised by two role prompts but binds only when a host wires
    /// `IAgentMemoryStore`, and a composition without one leaves the model calling a no-op.
    /// <para>
    /// A correction worth keeping, because it is the trap this file is about. The same investigation
    /// first blamed a lost tool set for the castle test building 0 parts, reading a "1 tool requested,
    /// 0 bound" warning as evidence. That warning came from a DIFFERENT test in the same log; the
    /// castle run shows `SmartToolCallingChatClient created with 4 tools` with `execute_lua` among
    /// them. Nothing was lost — the 3B model answered in prose (`execute_lua('...')`) instead of
    /// emitting a tool call. Two silent-failure shapes look identical in a log, and only the tool
    /// count separates them: the tool was missing, or the model never really called it.
    /// </para>
    /// This file pins the half that can be checked statically — that the names in the prompts are
    /// real. Whether the composition delivers them into the request is pinned by
    /// `RoleToolsReachLlmRequestEditModeTests`.
    /// </remarks>
    public sealed class BuiltInAgentPromptToolContractEditModeTests
    {
        /// <summary>Tool names CoreAI ships, as each tool spells its own <c>Name</c>.</summary>
        private static readonly string[] ShippedToolNames =
        {
            "call_skill_tool", "camera", "camera_tool", "check_compatibility", "component_command",
            "execute_lua", "game_config", "game_state", "get_inventory", "list_autosaves",
            "manage_mods", "manage_skills", "memory", "read_skill", "save_world", "scene_tool",
            "wait", "world_command"
        };

        private static IEnumerable<(string Role, string Text)> BuiltInPrompts()
        {
            Type texts = typeof(SkillSet).Assembly
                .GetType("CoreAI.Ai.BuiltInAgentSystemPromptTexts", throwOnError: false);
            Assert.IsNotNull(texts, "BuiltInAgentSystemPromptTexts must exist — the prompts moved.");

            foreach (FieldInfo field in texts.GetFields(
                         BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(string))
                {
                    yield return (field.Name, (string)field.GetRawConstantValue());
                }
            }
        }

        [Test]
        public void EveryBuiltInPromptIsDiscoverable()
        {
            List<(string Role, string Text)> prompts = BuiltInPrompts().ToList();

            Assert.GreaterOrEqual(prompts.Count, 8,
                "the built-in roles are the product surface; finding fewer means the reflection broke " +
                "and every assertion below would pass vacuously");
            foreach ((string role, string text) in prompts)
            {
                Assert.IsNotEmpty(text, role + " has an empty system prompt");
            }
        }

        [Test]
        public void NoPromptNamesAToolCoreAiDoesNotShip()
        {
            // WHY the shape of this check: a prompt naming `execute_lua_now` or a renamed-away tool
            // teaches the model to call something that will never exist. The model has no way to find
            // out except by trying, and the failure is silent.
            List<string> problems = new();

            foreach ((string role, string text) in BuiltInPrompts())
            {
                foreach (string candidate in ToolsThePromptTellsTheModelToCall(text))
                {
                    if (!ShippedToolNames.Contains(candidate, StringComparer.Ordinal))
                    {
                        problems.Add($"{role}: prompt names '{candidate}', which is not a shipped tool");
                    }
                }
            }

            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [Test]
        public void ProgrammerPromptNamesItsCoreTools()
        {
            // The negative twin of the check above: it would pass on a prompt that named NO tools at
            // all, so at least one role must be pinned to the tools it genuinely depends on.
            string programmer = BuiltInPrompts()
                .First(p => p.Role.Equals("Programmer", StringComparison.Ordinal)).Text;

            foreach (string required in new[] { "execute_lua", "manage_mods", "read_skill" })
            {
                StringAssert.Contains(required, programmer,
                    "the Programmer cannot do its job without " + required +
                    "; if the prompt stopped naming it, the model stops calling it");
            }
        }

        [Test]
        public void PromptsThatPromiseMemoryAreTheOnesThatShould()
        {
            // WHY pinned: the memory tool is bound only when a host wires IAgentMemoryStore. A prompt
            // that promises memory in a composition without a store leaves the model calling something
            // that is stripped without execution — the exact silent no-op seen live on 2026-09-05.
            HashSet<string> promising = new(StringComparer.Ordinal);
            foreach ((string role, string text) in BuiltInPrompts())
            {
                if (text.Contains("memory tool", StringComparison.Ordinal) ||
                    text.Contains("the memory tool", StringComparison.Ordinal))
                {
                    promising.Add(role);
                }
            }

            CollectionAssert.AreEquivalent(new[] { "SmartChat", "Merchant" }, promising.ToArray(),
                "only the conversational roles should promise memory; a new role promising it must " +
                "come with a wired store, or its calls vanish silently");
        }

        /// <summary>
        /// Identifiers the prompt tells the model to CALL — not every snake_case token in the prose.
        /// </summary>
        /// <remarks>
        /// WHY so narrow: the first version matched any snake_case word and produced six findings, all
        /// wrong. They fell into four categories a prompt legitimately contains and that are not tool
        /// names — an ARGUMENT VALUE (<c>action='set_color'</c>), a SUB-ACTION
        /// (<c>manage_mods (list/get_source/load/reload/unload)</c>), a NEGATIVE EXAMPLE ("never call
        /// invented APIs such as <c>game_rules</c>", where flagging it would demand deleting the very
        /// warning that stops the model inventing it), and PAYLOAD FIELD NAMES (<c>lua_error</c>,
        /// <c>fix_this_lua</c>). A check that cries wolf on all four gets switched off, so this one
        /// looks only at the phrasings a prompt uses to point the model AT a tool.
        /// </remarks>
        private static IEnumerable<string> ToolsThePromptTellsTheModelToCall(string text)
        {
            const string Pattern =
                @"(?:\buse the\s+|\bcall\s+)`?([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`?\s*(?:tool\b|\()"
                + @"|`?([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`?\s+tool\b";

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text ?? "", Pattern))
            {
                for (int group = 1; group < match.Groups.Count; group++)
                {
                    if (match.Groups[group].Success)
                    {
                        yield return match.Groups[group].Value;
                    }
                }
            }
        }
    }
}
