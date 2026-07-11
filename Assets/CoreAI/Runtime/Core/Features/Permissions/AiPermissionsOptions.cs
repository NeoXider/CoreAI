namespace CoreAI.Presentation.AiDashboard
{
    public interface IAiPermissions
    {
        bool AllowCreator { get; }
        bool AllowAnalyzer { get; }
        bool AllowCoreMechanic { get; }
    }

    public sealed class AiPermissionsOptions : IAiPermissions
    {
        public bool AllowCreator { get; set; } = true;
        public bool AllowAnalyzer { get; set; } = true;
        public bool AllowCoreMechanic { get; set; } = true;
    }
}
