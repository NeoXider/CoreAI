using System;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// The stable <c>UI_*</c> codes the <c>UxmlService</c> Lua surface raises, carried inside the
    /// structured §5.2.7 error: <c>BAD_ARGUMENT: UI_CAPABILITY_REQUIRED: message | fix: suggestion</c>.
    /// The outer code is the closest <see cref="RbxErrorCode"/>; the inner one names the UI rule, so a
    /// script's <c>pcall</c> and the AI's self-repair both see it. UXML and USS reports are never raised:
    /// they come back as diagnostics.
    /// </summary>
    public static class RbxUiErrors
    {
        /// <summary>The script lacks the <c>Ui</c> capability.</summary>
        public const string CapabilityRequired = "UI_CAPABILITY_REQUIRED";

        /// <summary>This game registered no runtime UI host (headless or dedicated server).</summary>
        public const string Unavailable = "UI_UNAVAILABLE";

        /// <summary>The host game switched runtime UI off.</summary>
        public const string Disabled = "UI_DISABLED";

        /// <summary>The caller is the ownerless one-off surface; screens belong to mods.</summary>
        public const string ModRequired = "UI_MOD_REQUIRED";

        /// <summary>The screen belongs to another mod.</summary>
        public const string NotOwner = "UI_NOT_OWNER";

        /// <summary>The host already holds a screen under this id (another world of the same host).</summary>
        public const string ScreenExists = "UI_SCREEN_EXISTS";

        /// <summary>No live screen with this id.</summary>
        public const string ScreenNotFound = "UI_SCREEN_NOT_FOUND";

        /// <summary>The screen was destroyed.</summary>
        public const string ScreenDestroyed = "UI_SCREEN_DESTROYED";

        /// <summary>The element was removed, or its name vanished in a UXML replace.</summary>
        public const string ElementDetached = "UI_ELEMENT_DETACHED";

        /// <summary>The selector is outside the supported grammar.</summary>
        public const string InvalidSelector = "UI_INVALID_SELECTOR";

        /// <summary>The element kind has no such member (a value on a Label, items on a Button).</summary>
        public const string UnsupportedOperation = "UI_UNSUPPORTED_OPERATION";

        /// <summary>A malformed id, name, kind or option.</summary>
        public const string InvalidArgument = "UI_INVALID_ARGUMENT";

        /// <summary>One call passed more text than <see cref="RbxUiBudgets.MaxTextLength"/>.</summary>
        public const string TextTooLarge = "UI_TEXT_TOO_LARGE";

        /// <summary>The mod already owns <see cref="RbxUiBudgets.MaxScreensPerMod"/> screens.</summary>
        public const string BudgetScreens = "UI_BUDGET_SCREENS";

        /// <summary>The mod's screens would hold more than <see cref="RbxUiBudgets.MaxElementsPerMod"/> elements.</summary>
        public const string BudgetElements = "UI_BUDGET_ELEMENTS";

        /// <summary>The mod made more than <see cref="RbxUiBudgets.MaxWritesPerFrame"/> element writes this frame.</summary>
        public const string BudgetWrites = "UI_BUDGET_WRITES";

        /// <summary>The runtime UI host holds its most screens (the host ceiling above the per-mod budget).</summary>
        public const string HostScreenLimit = "UI_SCREEN_LIMIT";

        /// <summary>One screen would exceed the host's element ceiling.</summary>
        public const string HostElementLimit = "UI_ELEMENT_LIMIT";

        /// <summary>Builds the structured error for a <c>UI_*</c> code.</summary>
        public static RbxError Create(string uiCode, string message, string fix)
        {
            return new RbxError(OuterCode(uiCode), uiCode + ": " + message, fix);
        }

        /// <summary><c>UI_UNAVAILABLE</c>: this game registered no runtime UI host.</summary>
        public static RbxError NotAvailable(string member)
        {
            return Create(Unavailable,
                member + ": runtime UI is not available in this game (no runtime UI host is registered, as on "
                + "a headless or dedicated server)",
                "show the information in 3D space instead, or run where the host registers runtime UI "
                + "(RegisterRuntimeUi)");
        }

        /// <summary><c>UI_DISABLED</c>: the host game switched runtime UI off.</summary>
        public static RbxError SwitchedOff(string member)
        {
            return Create(Disabled, member + ": the host game switched runtime UI off",
                "show the information in 3D space instead; the host decides whether mods may draw UI");
        }

        /// <summary><c>UI_CAPABILITY_REQUIRED</c>: the script was not granted the <c>Ui</c> capability.</summary>
        public static RbxError MissingCapability(string member)
        {
            return Create(CapabilityRequired,
                member + " requires the Ui capability, which was not granted to this script",
                "declare Ui in the mod's capabilities (or use All); a host that withholds Ui keeps mods off "
                + "the screen");
        }

        /// <summary>The <c>UI_*</c> code inside an error this class built, or null for any other error.</summary>
        public static string GetUiCode(RbxError error)
        {
            string raw = error?.RawMessage;
            if (raw == null || !raw.StartsWith("UI_", StringComparison.Ordinal))
            {
                return null;
            }

            int end = raw.IndexOf(": ", StringComparison.Ordinal);
            return end > 0 ? raw.Substring(0, end) : null;
        }

        /// <summary>The <see cref="RbxErrorCode"/> a <c>UI_*</c> code travels under.</summary>
        public static RbxErrorCode OuterCode(string uiCode)
        {
            switch (uiCode)
            {
                case ModRequired:
                    return RbxErrorCode.ContextViolation;
                case NotOwner:
                    return RbxErrorCode.NotAuthority;
                case ScreenDestroyed:
                case ElementDetached:
                    return RbxErrorCode.InstanceDestroyed;
                case TextTooLarge:
                    return RbxErrorCode.PayloadTooLarge;
                case BudgetScreens:
                case BudgetElements:
                case BudgetWrites:
                case HostScreenLimit:
                case HostElementLimit:
                    return RbxErrorCode.BudgetExceeded;
                default:
                    return RbxErrorCode.BadArgument;
            }
        }
    }
}
