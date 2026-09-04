using System;
using System.Collections.Generic;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Unity
{
    /// <summary>
    /// ScriptableObject asset that defines LLM skills and tool bindings.
    /// </summary>
    /// <example>
    /// <code>
    /// // In Inspector: set SkillName="Crafting", Description="Forge items", Instructions=crafting.txt
    /// // In code:
    /// SkillSet skill = craftingAsset.BuildSkillSet(
    ///     new DelegateLlmTool("get_recipes", "List recipes", (string type) => ...),
    ///     new DelegateLlmTool("craft_item", "Craft an item", (string id) => ...));
    /// var agent = new AgentBuilder("GameMaster")
    ///     .WithSkill(skill)
    ///     .Build();
    /// </code>
    /// </example>
    [CreateAssetMenu(fileName = "NewSkillSet", menuName = "CoreAI/Skill Set Asset", order = 200)]
    public sealed class SkillSetAsset : ScriptableObject
    {
        [Header("Skill Identity")]
        [Tooltip("Human-readable name shown in the skill catalog (e.g. 'Crafting', 'Combat').")]
        [SerializeField]
        private string skillName = "NewSkill";

        [Tooltip("Short one-line description for the catalog. The model sees this to understand what the skill does.")]
        [TextArea(1, 3)]
        [SerializeField]
        private string description = "";

        [Header("Instructions")]
        [Tooltip("Full instructions loaded on demand via read_skill(). " +
                 "Drag a .txt or .md TextAsset here. If empty, tool descriptions are used.")]
        [SerializeField]
        private TextAsset instructionsAsset;

        [Tooltip("Extra instruction files, for a skill written across several documents. They follow " +
                 "the file above, in this order. Once more than one file is in play each is introduced " +
                 "by a '## filename' heading so the model can tell the sections apart.")]
        [SerializeField]
        private TextAsset[] additionalInstructionAssets = Array.Empty<TextAsset>();

        [Tooltip("Alternative: type instructions directly (used if no TextAsset is assigned).")]
        [TextArea(3, 15)]
        [SerializeField]
        private string inlineInstructions = "";

        /// <summary>Skill name as configured in the Inspector.</summary>
        public string SkillName => skillName;

        /// <summary>Short description for the catalog.</summary>
        public string Description => description;

        /// <summary>
        /// Full on-demand instructions supplied to <c>read_skill</c>: the assigned
        /// <see cref="TextAsset"/>s joined in Inspector order, or the inline field when none is set.
        /// </summary>
        /// <remarks>
        /// WHY: a single assigned file keeps its exact text — adding a heading to a one-file skill
        /// would silently rewrite every existing asset's instructions. Headings appear only once there
        /// is more than one document to tell apart, which is the case they exist for.
        /// </remarks>
        public string Instructions
        {
            get
            {
                List<KeyValuePair<string, string>> parts = CollectInstructionParts();
                if (parts.Count == 0)
                {
                    return inlineInstructions;
                }

                return parts.Count == 1
                    ? parts[0].Value
                    : SkillSet.JoinInstructionParts(parts);
            }
        }

        /// <summary>Assigned instruction files in Inspector order, skipping empty slots.</summary>
        private List<KeyValuePair<string, string>> CollectInstructionParts()
        {
            List<KeyValuePair<string, string>> parts = new();
            AddPart(parts, instructionsAsset);
            if (additionalInstructionAssets != null)
            {
                foreach (TextAsset asset in additionalInstructionAssets)
                {
                    AddPart(parts, asset);
                }
            }

            return parts;
        }

        private static void AddPart(List<KeyValuePair<string, string>> parts, TextAsset asset)
        {
            if (asset != null)
            {
                parts.Add(new KeyValuePair<string, string>(asset.name, asset.text));
            }
        }

        /// <summary>
        /// Builds a Unity-free skill definition snapshot without TextAsset references.
        /// </summary>
        public SkillSetDefinition ToSkillDefinition()
        {
            return new SkillSetDefinition
            {
                Name = SkillName,
                Description = Description ?? "",
                Instructions = Instructions ?? ""
            };
        }

        /// <summary>
        /// Updates this authoring asset from a portable skill definition.
        /// Use this from editor tooling or import pipelines when creating or modifying skills programmatically.
        /// </summary>
        public void ApplyDefinition(SkillSetDefinition definition)
        {
            if (definition == null)
            {
                throw new System.ArgumentNullException(nameof(definition));
            }

            skillName = string.IsNullOrWhiteSpace(definition.Name) ? "NewSkill" : definition.Name.Trim();
            description = definition.Description ?? "";
            inlineInstructions = definition.Instructions ?? "";
            // WHY: the definition carries the whole body inline, so every file reference must go —
            // leaving the extra ones behind would append stale documents to the new instructions.
            instructionsAsset = null;
            additionalInstructionAssets = Array.Empty<TextAsset>();
        }

        /// <summary>
        /// Builds a <see cref="SkillSet"/> from this asset's configuration and the provided tools.
        /// </summary>
        /// <param name="tools">Tools that belong to this skill.</param>
        /// <returns>A ready-to-use <see cref="SkillSet"/> for <see cref="AgentBuilder.WithSkill"/>.</returns>
        public SkillSet BuildSkillSet(params ILlmTool[] tools)
        {
            return ToSkillDefinition().BuildSkillSet(tools);
        }
    }
}
