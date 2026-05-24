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
    ///
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

        [Tooltip("Alternative: type instructions directly (used if TextAsset is null).")]
        [TextArea(3, 15)]
        [SerializeField]
        private string inlineInstructions = "";

        /// <summary>Skill name as configured in the Inspector.</summary>
        public string SkillName => skillName;

        /// <summary>Short description for the catalog.</summary>
        public string Description => description;

        /// <summary>
/// Executes build skill set.
        /// </summary>
        public string Instructions =>
            instructionsAsset != null ? instructionsAsset.text : inlineInstructions;

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
