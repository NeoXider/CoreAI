namespace CoreAI.Tests.EditMode.RbxApi.CompatibilityCorpus
{
    /// <summary>
    /// The Tier-B corpus: whole gameplay idioms rather than single-API probes.
    /// </summary>
    /// <remarks>
    /// WHY a second tier: Tier-A asks "does this API exist and behave"; a game is not a list of API
    /// calls. Tier-B fixtures are the shapes a Roblox developer actually writes — a kill brick, a
    /// pickup that scores into leaderstats, a tweened door — and each one crosses three or four
    /// services at once. An API set can pass every Tier-A row and still be unable to run a kill
    /// brick, which is exactly the gap this tier exists to catch.
    /// <para>
    /// The ids are frozen before the run, like Tier-A's: a corpus whose membership can be edited
    /// after seeing the results measures nothing.
    /// </para>
    /// </remarks>
    internal static class TierBCorpusCatalog
    {
        internal static readonly TierAFixtureSpec[] Fixtures =
        {
            new TierAFixtureSpec(
                "TBC-001-kill-brick",
                "TBC-001-kill-brick.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.TouchFirstTwoParts,
                "None.",
                "The canonical kill brick: tag the brick, read it back with GetTagged, connect "
                + "Touched, zero the Humanoid's Health, observe Died.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/BasePart.yaml; "
                + "classes/Humanoid.yaml; classes/CollectionService.yaml"),
            new TierAFixtureSpec(
                "TBC-002-touch-pickup-with-leaderstats",
                "TBC-002-touch-pickup-with-leaderstats.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "A pickup that scores: leaderstats folder under the Player, IntValue.Changed, and "
                + "Destroy on collect — the shape every beginner tutorial teaches.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Players.yaml; "
                + "classes/IntValue.yaml"),
            new TierAFixtureSpec(
                "TBC-003-door-tween",
                "TBC-003-door-tween.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceHalfSecond,
                "None.",
                "A door that slides open: TweenService:Create over two properties at once, with the "
                + "Completed state checked before acting.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/TweenService.yaml"),
            new TierAFixtureSpec(
                "TBC-004-raycast-ground-check",
                "TBC-004-raycast-ground-check.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "A ground probe with RaycastParams. The headless corpus has no physics engine, so "
                + "the honest result is a miss — and the fixture asserts the miss rather than "
                + "pretending a hit, which is what the PlayMode gates measure instead.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/WorldRoot.yaml"),
            new TierAFixtureSpec(
                "TBC-005-humanoid-damage-loop",
                "TBC-005-humanoid-damage-loop.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "Repeated TakeDamage with HealthChanged observed — damage over time, the case a "
                + "class-level regeneration would have silently changed.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Humanoid.yaml"),
            new TierAFixtureSpec(
                "TBC-006-collection-service-respawner",
                "TBC-006-collection-service-respawner.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "Tag-driven spawning with Debris cleanup: GetInstanceAddedSignal fires for parts "
                + "tagged after the subscription, and each one is handed to Debris.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/"
                + "CollectionService.yaml; classes/Debris.yaml"),
            new TierAFixtureSpec(
                "TBC-007-player-leave-save",
                "TBC-007-player-leave-save.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "Save-on-leave: PlayerRemoving reads the leaving player's UserId, which the mirror "
                + "names as this event's purpose. This idiom failed silently until the destruction "
                + "tombstone was widened in 7.19.0.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Players.yaml"),
            new TierAFixtureSpec(
                "TBC-008-tween-cancel-restart",
                "TBC-008-tween-cancel-restart.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "Cancel fires Completed with Cancelled — the distinction a script needs to tell an "
                + "interrupted animation from a finished one.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Tween.yaml"),
            new TierAFixtureSpec(
                "TBC-009-attribute-driven-config",
                "TBC-009-attribute-driven-config.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "Attributes as live configuration: GetAttributeChangedSignal drives Humanoid."
                + "WalkSpeed, the standard way a designer retunes a game without touching code.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Instance.yaml"),
            new TierAFixtureSpec(
                "TBC-010-gravity-low-jump",
                "TBC-010-gravity-low-jump.lua",
                TierAFixtureClassification.Unmodified,
                TierAFixtureDriver.AdvanceImmediate,
                "None.",
                "A low-gravity mode: workspace.Gravity written from Lua and read back, with "
                + "JumpHeight rather than JumpPower selecting the jump.",
                "",
                "",
                "D:/Git/RobloxDocs/creator-docs/content/en-us/reference/engine/classes/Workspace.yaml")
        };
    }
}
