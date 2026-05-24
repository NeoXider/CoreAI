namespace CoreAI.Ai
{
    /// <summary>
    /// Unity-free skill authoring snapshot. Tools are still supplied by code when building a SkillSet.
    /// </summary>
    public sealed class SkillSetDefinition
    {
        public string Name { get; set; } = "NewSkill";
        public string Description { get; set; } = "";
        public string Instructions { get; set; } = "";

        public SkillSet BuildSkillSet(params ILlmTool[] tools)
        {
            return new SkillSet(Name, Description, Instructions, tools);
        }
    }
}
