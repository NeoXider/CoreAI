using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Instances.TextChat;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>The durable properties of one <c>TextChatCommand</c> (the package's <c>text_chat_command</c> block).</summary>
    public readonly struct RbxWorldPackageTextChatCommand : IEquatable<RbxWorldPackageTextChatCommand>
    {
        public RbxWorldPackageTextChatCommand(string primaryAlias, string secondaryAlias, bool enabled, bool autocompleteVisible)
        {
            PrimaryAlias = primaryAlias ?? string.Empty;
            SecondaryAlias = secondaryAlias ?? string.Empty;
            Enabled = enabled;
            AutocompleteVisible = autocompleteVisible;
        }

        public string PrimaryAlias { get; }

        public string SecondaryAlias { get; }

        public bool Enabled { get; }

        public bool AutocompleteVisible { get; }

        /// <summary>The state of <paramref name="command"/> as it is now.</summary>
        public static RbxWorldPackageTextChatCommand Of(RbxTextChatCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            return new RbxWorldPackageTextChatCommand(
                command.PrimaryAlias, command.SecondaryAlias, command.Enabled, command.AutocompleteVisible);
        }

        /// <summary>Puts this state on <paramref name="command"/> without advancing its revision.</summary>
        public void ApplyTo(RbxTextChatCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            command.RestoreDurableState(PrimaryAlias, SecondaryAlias, Enabled, AutocompleteVisible);
        }

        /// <summary>This state with both aliases free of lone surrogates; reports each replaced property.</summary>
        internal RbxWorldPackageTextChatCommand WithWellFormedText(Action<string> replaced)
        {
            string primary = RbxWorldPackageSerializer.ReplaceLoneSurrogates(PrimaryAlias);
            string secondary = RbxWorldPackageSerializer.ReplaceLoneSurrogates(SecondaryAlias);
            if (!ReferenceEquals(primary, PrimaryAlias))
            {
                replaced?.Invoke(nameof(PrimaryAlias));
            }

            if (!ReferenceEquals(secondary, SecondaryAlias))
            {
                replaced?.Invoke(nameof(SecondaryAlias));
            }

            return new RbxWorldPackageTextChatCommand(primary, secondary, Enabled, AutocompleteVisible);
        }

        public bool Equals(RbxWorldPackageTextChatCommand other)
        {
            return string.Equals(PrimaryAlias, other.PrimaryAlias, StringComparison.Ordinal)
                   && string.Equals(SecondaryAlias, other.SecondaryAlias, StringComparison.Ordinal)
                   && Enabled == other.Enabled && AutocompleteVisible == other.AutocompleteVisible;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackageTextChatCommand other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(PrimaryAlias ?? "") ^ (Enabled ? 1 : 2) ^ (AutocompleteVisible ? 4 : 8);
        }
    }

    /// <summary>
    /// The durable properties of one <c>SelectionBox</c> (the package's <c>selection_box</c> block). The adornee is the
    /// id of an instance of the same package, 0 for nil.
    /// </summary>
    public readonly struct RbxWorldPackageSelectionBox : IEquatable<RbxWorldPackageSelectionBox>
    {
        public RbxWorldPackageSelectionBox(ulong adorneeId, RbxColor3 color3, float transparency, bool visible,
            float lineThickness, RbxColor3 surfaceColor3, float surfaceTransparency)
        {
            AdorneeId = adorneeId;
            Color3 = color3;
            Transparency = transparency;
            Visible = visible;
            LineThickness = lineThickness;
            SurfaceColor3 = surfaceColor3;
            SurfaceTransparency = surfaceTransparency;
        }

        /// <summary>The adornee's instance id, 0 for nil.</summary>
        public ulong AdorneeId { get; }

        public RbxColor3 Color3 { get; }

        public float Transparency { get; }

        public bool Visible { get; }

        public float LineThickness { get; }

        public RbxColor3 SurfaceColor3 { get; }

        public float SurfaceTransparency { get; }

        /// <summary>The state of <paramref name="box"/> as it is now, its adornee as the live id (0 for nil).</summary>
        public static RbxWorldPackageSelectionBox Of(RbxSelectionBox box)
        {
            if (box == null)
            {
                throw new ArgumentNullException(nameof(box));
            }

            return new RbxWorldPackageSelectionBox(box.Adornee?.Id.Value ?? 0UL, box.Color3, box.Transparency, box.Visible,
                box.LineThickness, box.SurfaceColor3, box.SurfaceTransparency);
        }

        /// <summary>This state with another adornee id.</summary>
        public RbxWorldPackageSelectionBox WithAdorneeId(ulong adorneeId)
        {
            return new RbxWorldPackageSelectionBox(adorneeId, Color3, Transparency, Visible, LineThickness, SurfaceColor3,
                SurfaceTransparency);
        }

        /// <summary>This state with other colours.</summary>
        internal RbxWorldPackageSelectionBox WithColors(RbxColor3 color3, RbxColor3 surfaceColor3)
        {
            return new RbxWorldPackageSelectionBox(AdorneeId, color3, Transparency, Visible, LineThickness, surfaceColor3,
                SurfaceTransparency);
        }

        /// <summary>Puts this state on <paramref name="box"/> without advancing its revision; the adornee is resolved by the caller.</summary>
        public void ApplyTo(RbxSelectionBox box, RbxInstance adornee)
        {
            if (box == null)
            {
                throw new ArgumentNullException(nameof(box));
            }

            box.RestoreDurableState(adornee, Color3, Transparency, Visible, LineThickness, SurfaceColor3, SurfaceTransparency);
        }

        public bool Equals(RbxWorldPackageSelectionBox other)
        {
            return AdorneeId == other.AdorneeId && Color3 == other.Color3 && Transparency.Equals(other.Transparency)
                   && Visible == other.Visible && LineThickness.Equals(other.LineThickness)
                   && SurfaceColor3 == other.SurfaceColor3 && SurfaceTransparency.Equals(other.SurfaceTransparency);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackageSelectionBox other && Equals(other);
        }

        public override int GetHashCode()
        {
            return AdorneeId.GetHashCode() ^ (Transparency.GetHashCode() * 397) ^ (Visible ? 1 : 2);
        }
    }

    /// <summary>
    /// The durable properties of one <c>Highlight</c> (the package's <c>highlight</c> block). The adornee is the id of an
    /// instance of the same package, 0 for nil (which highlights the parent, as in Roblox).
    /// </summary>
    public readonly struct RbxWorldPackageHighlight : IEquatable<RbxWorldPackageHighlight>
    {
        public RbxWorldPackageHighlight(ulong adorneeId, RbxHighlightDepthMode depthMode, bool enabled,
            RbxColor3 fillColor, float fillTransparency, RbxColor3 outlineColor, float outlineTransparency)
        {
            AdorneeId = adorneeId;
            DepthMode = depthMode;
            Enabled = enabled;
            FillColor = fillColor;
            FillTransparency = fillTransparency;
            OutlineColor = outlineColor;
            OutlineTransparency = outlineTransparency;
        }

        /// <summary>The adornee's instance id, 0 for nil.</summary>
        public ulong AdorneeId { get; }

        public RbxHighlightDepthMode DepthMode { get; }

        public bool Enabled { get; }

        public RbxColor3 FillColor { get; }

        public float FillTransparency { get; }

        public RbxColor3 OutlineColor { get; }

        public float OutlineTransparency { get; }

        /// <summary>The state of <paramref name="highlight"/> as it is now, its adornee as the live id (0 for nil).</summary>
        public static RbxWorldPackageHighlight Of(RbxHighlight highlight)
        {
            if (highlight == null)
            {
                throw new ArgumentNullException(nameof(highlight));
            }

            return new RbxWorldPackageHighlight(highlight.Adornee?.Id.Value ?? 0UL, highlight.DepthMode,
                highlight.Enabled, highlight.FillColor, highlight.FillTransparency, highlight.OutlineColor,
                highlight.OutlineTransparency);
        }

        /// <summary>This state with another adornee id.</summary>
        public RbxWorldPackageHighlight WithAdorneeId(ulong adorneeId)
        {
            return new RbxWorldPackageHighlight(adorneeId, DepthMode, Enabled, FillColor, FillTransparency,
                OutlineColor, OutlineTransparency);
        }

        /// <summary>This state with other colours.</summary>
        internal RbxWorldPackageHighlight WithColors(RbxColor3 fillColor, RbxColor3 outlineColor)
        {
            return new RbxWorldPackageHighlight(AdorneeId, DepthMode, Enabled, fillColor, FillTransparency,
                outlineColor, OutlineTransparency);
        }

        /// <summary>Puts this state on <paramref name="highlight"/> without advancing its revision; the adornee is resolved by the caller.</summary>
        public void ApplyTo(RbxHighlight highlight, RbxInstance adornee)
        {
            if (highlight == null)
            {
                throw new ArgumentNullException(nameof(highlight));
            }

            highlight.RestoreDurableState(adornee, DepthMode, Enabled, FillColor, FillTransparency, OutlineColor,
                OutlineTransparency);
        }

        public bool Equals(RbxWorldPackageHighlight other)
        {
            return AdorneeId == other.AdorneeId && DepthMode == other.DepthMode && Enabled == other.Enabled
                   && FillColor == other.FillColor && FillTransparency.Equals(other.FillTransparency)
                   && OutlineColor == other.OutlineColor && OutlineTransparency.Equals(other.OutlineTransparency);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackageHighlight other && Equals(other);
        }

        public override int GetHashCode()
        {
            return AdorneeId.GetHashCode() ^ (FillTransparency.GetHashCode() * 397) ^ (Enabled ? 1 : 2)
                   ^ ((int)DepthMode << 3);
        }
    }
}
