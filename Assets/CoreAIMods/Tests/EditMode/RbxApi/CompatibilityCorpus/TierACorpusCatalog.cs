namespace CoreAI.Tests.EditMode.RbxApi.CompatibilityCorpus
{
    internal enum TierAFixtureClassification
    {
        Unmodified,
        Modified,
        Failing
    }

    internal enum TierAFixtureDriver
    {
        None,
        AdvanceImmediate,
        AdvanceQuarterSecond,
        AdvanceHalfSecond,
        PumpThreeFrames,
        PumpSixSeconds,
        TouchFirstTwoParts,
        PressE
    }

    internal enum TierAFixtureExpectedOutcome
    {
        Completion,
        DiagnosticFailure,
        IndefiniteYield
    }

    internal sealed class TierAFixtureSpec
    {
        public TierAFixtureSpec(string id, string fileName,
            TierAFixtureClassification classification, TierAFixtureDriver driver,
            string accommodation, string why, string apiGap,
            string expectedFailureText, string reference,
            TierAFixtureExpectedOutcome? expectedOutcome = null)
        {
            Id = id;
            FileName = fileName;
            Classification = classification;
            Driver = driver;
            Accommodation = accommodation;
            Why = why;
            ApiGap = apiGap;
            ExpectedFailureText = expectedFailureText;
            Reference = reference;
            ExpectedOutcome = expectedOutcome ??
                (classification == TierAFixtureClassification.Failing
                    ? TierAFixtureExpectedOutcome.DiagnosticFailure
                    : TierAFixtureExpectedOutcome.Completion);
        }

        public string Id { get; }

        public string FileName { get; }

        public TierAFixtureClassification Classification { get; }

        public TierAFixtureDriver Driver { get; }

        public string Accommodation { get; }

        public string Why { get; }

        public string ApiGap { get; }

        public string ExpectedFailureText { get; }

        public string Reference { get; }

        public TierAFixtureExpectedOutcome ExpectedOutcome { get; }

        public override string ToString()
        {
            return Id;
        }
    }

    /// <summary>
    /// Fixed, checked-in Tier-A fixture list for G12. Changing this array changes the corpus and
    /// requires a new pre-measurement freeze; execution code enumerates this array verbatim.
    /// </summary>
    internal static class TierACorpusCatalog
    {
        public static readonly TierAFixtureSpec[] Fixtures =
        {
            new TierAFixtureSpec(
                "TAC-001-instance-parent-last",
                "TAC-001-instance-parent-last.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "The fixture is valid Roblox Luau without CoreAI-specific names or setup.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Instance.yaml:556; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R6.1"),
            new TierAFixtureSpec(
                "TAC-002-part-properties",
                "TAC-002-part-properties.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "The fixture uses Roblox BasePart property assignment and read-back unchanged.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/BasePart.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S3.1"),
            new TierAFixtureSpec(
                "TAC-003-attributes-change-signal",
                "TAC-003-attributes-change-signal.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "SetAttribute, GetAttribute, and GetAttributeChangedSignal are used exactly as on Roblox.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Instance.yaml:1271; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R6.7"),
            new TierAFixtureSpec(
                "TAC-004-signal-connect-disconnect",
                "TAC-004-signal-connect-disconnect.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "The RBXScriptSignal connection lifecycle is standard Roblox code.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/datatypes/RBXScriptSignal.yaml; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R5.1"),
            new TierAFixtureSpec(
                "TAC-005-signal-once",
                "TAC-005-signal-once.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "RBXScriptSignal:Once is used without a compatibility wrapper.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/datatypes/RBXScriptSignal.yaml; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R5.2"),
            new TierAFixtureSpec(
                "TAC-006-signal-wait",
                "TAC-006-signal-wait.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "RBXScriptSignal:Wait runs inside a normal task.spawn thread as it does on Roblox.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/datatypes/RBXScriptSignal.yaml; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R5.3"),
            new TierAFixtureSpec(
                "TAC-007-task-scheduling",
                "TAC-007-task-scheduling.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceQuarterSecond,
                "None.",
                "task.spawn, task.defer, task.delay, and task.wait are unchanged Roblox calls.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/libraries/task.yaml; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R4.8"),
            new TierAFixtureSpec(
                "TAC-008-runservice-heartbeat-loop",
                "TAC-008-runservice-heartbeat-loop.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.PumpThreeFrames,
                "None.",
                "The Heartbeat loop and connection teardown are canonical Roblox scheduling code.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/RunService.yaml; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R4.5"),
            new TierAFixtureSpec(
                "TAC-009-userinput-began",
                "TAC-009-userinput-began.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.PressE,
                "None.",
                "The script gets UserInputService, respects gameProcessedEvent, and reads KeyCode unchanged.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/UserInputService.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S5.6"),
            new TierAFixtureSpec(
                "TAC-010-vector3-math",
                "TAC-010-vector3-math.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "Typed Luau plus Vector3 arithmetic, Magnitude, Unit, Dot, and Cross are accepted unchanged.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/datatypes/Vector3.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S3.3"),
            new TierAFixtureSpec(
                "TAC-011-cframe-math",
                "TAC-011-cframe-math.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "CFrame construction, multiplication, ToWorldSpace, and LookVector are unchanged.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/datatypes/CFrame.yaml:757; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S3.4"),
            new TierAFixtureSpec(
                "TAC-012-color3-math",
                "TAC-012-color3-math.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "Color3.fromRGB, Lerp, component reads, and ToHex are unchanged.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/datatypes/Color3.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S3.5"),
            new TierAFixtureSpec(
                "TAC-013-getservice-identity",
                "TAC-013-getservice-identity.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "game:GetService and service identity checks are standard Roblox code.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/ServiceProvider.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S5.3"),
            new TierAFixtureSpec(
                "TAC-014-destroy-pcall-cleanup",
                "TAC-014-destroy-pcall-cleanup.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "Destroy cleanup and pcall around an illegal reparent use Roblox lifecycle semantics.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Instance.yaml:838; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R6.2, R7.1"),
            new TierAFixtureSpec(
                "TAC-015-script-parent-property-signal",
                "TAC-015-script-parent-property-signal.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "The global is bound to this mod's registry-backed executing Script instance.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/globals/RobloxGlobals.yaml:67; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R1.1"),
            new TierAFixtureSpec(
                "TAC-016-generic-for-descendants",
                "TAC-016-generic-for-descendants.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.None,
                "None.",
                "The production Luau downleveler preserves iterator triples and adapts direct tables and __iter.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/luau/control-structures.md:165; D:/Git/RobloxDocs/creator-docs/content/en-us/luau/metatables.md:121"),
            new TierAFixtureSpec(
                "TAC-017-waitforchild-yield",
                "TAC-017-waitforchild-yield.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.PumpThreeFrames,
                "None.",
                "Runs unmodified: WaitForChild yields until the delayed child is parented.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Instance.yaml; Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md R6.9"),
            new TierAFixtureSpec(
                "TAC-018-contextaction-bind",
                "TAC-018-contextaction-bind.lua",
                TierAFixtureClassification.Failing,
                TierAFixtureDriver.None,
                "None; the canonical ContextActionService idiom is intentionally retained.",
                "ContextActionService is a loud planned-service stub with no BindAction implementation.",
                "ContextActionService:BindAction and ContextActionResult",
                "ContextActionService",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/ContextActionService.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S5.8"),
            new TierAFixtureSpec(
                "TAC-019-tween-create",
                "TAC-019-tween-create.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceHalfSecond,
                "None.",
                "The canonical TweenService:Create idiom runs unchanged: TweenInfo.new, multi-property goals, Completed(Completed), attribute marker.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/TweenService.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S4.1-S4.6"),
            new TierAFixtureSpec(
                "TAC-020-players-localplayer",
                "TAC-020-players-localplayer.lua",
                TierAFixtureClassification.Failing,
                TierAFixtureDriver.None,
                "None; the documented LocalScript idiom is intentionally retained.",
                "The corpus executes a server Script, where Roblox defines Players.LocalPlayer as nil; the fixture requires a client LocalScript context before PlayerGui can be reached.",
                "Client LocalScript execution and Player.PlayerGui (MVP8)",
                "attempt to index a nil value (local 'player')",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Players.yaml; Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md S2.2, S6.8",
                TierAFixtureExpectedOutcome.DiagnosticFailure)
        };
    }
}
