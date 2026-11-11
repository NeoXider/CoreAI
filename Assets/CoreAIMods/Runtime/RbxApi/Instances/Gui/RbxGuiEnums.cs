using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Roblox <c>Enum.TextXAlignment</c> values.</summary>
    public enum RbxTextXAlignment
    {
        Left = 0,
        Right = 1,
        Center = 2
    }

    /// <summary>Roblox <c>Enum.TextYAlignment</c> values.</summary>
    public enum RbxTextYAlignment
    {
        Top = 0,
        Center = 1,
        Bottom = 2
    }

    /// <summary>Roblox <c>Enum.FillDirection</c> values.</summary>
    public enum RbxFillDirection
    {
        Horizontal = 0,
        Vertical = 1
    }

    /// <summary>Roblox <c>Enum.HorizontalAlignment</c> values.</summary>
    public enum RbxHorizontalAlignment
    {
        Center = 0,
        Left = 1,
        Right = 2
    }

    /// <summary>Roblox <c>Enum.VerticalAlignment</c> values.</summary>
    public enum RbxVerticalAlignment
    {
        Center = 0,
        Top = 1,
        Bottom = 2
    }

    /// <summary>Roblox <c>Enum.SortOrder</c> values (<c>Custom</c> is deprecated and sorts like child order).</summary>
    public enum RbxGuiSortOrder
    {
        Name = 0,
        Custom = 1,
        LayoutOrder = 2
    }

    /// <summary>Roblox <c>Enum.ScaleType</c> values.</summary>
    public enum RbxScaleType
    {
        Stretch = 0,
        Slice = 1,
        Tile = 2,
        Fit = 3,
        Crop = 4
    }

    /// <summary>Roblox <c>Enum.AspectType</c> values.</summary>
    public enum RbxAspectType
    {
        FitWithinMaxSize = 0,
        ScaleWithParentSize = 1
    }

    /// <summary>Roblox <c>Enum.DominantAxis</c> values.</summary>
    public enum RbxDominantAxis
    {
        Width = 0,
        Height = 1
    }

    /// <summary>Roblox <c>Enum.ZIndexBehavior</c> values.</summary>
    public enum RbxZIndexBehavior
    {
        Global = 0,
        Sibling = 1
    }

    /// <summary>Roblox <c>Enum.AutomaticSize</c> values.</summary>
    public enum RbxAutomaticSize
    {
        None = 0,
        X = 1,
        Y = 2,
        XY = 3
    }

    /// <summary>Roblox <c>Enum.SizeConstraint</c> values.</summary>
    public enum RbxSizeConstraint
    {
        RelativeXY = 0,
        RelativeXX = 1,
        RelativeYY = 2
    }

    /// <summary>Roblox <c>Enum.ApplyStrokeMode</c> values.</summary>
    public enum RbxApplyStrokeMode
    {
        Contextual = 0,
        Border = 1
    }

    /// <summary>Roblox <c>Enum.TextTruncate</c> values.</summary>
    public enum RbxTextTruncate
    {
        None = 0,
        AtEnd = 1,
        SplitWord = 2
    }

    /// <summary>Roblox <c>Enum.BorderMode</c> values.</summary>
    public enum RbxBorderMode
    {
        Outline = 0,
        Middle = 1,
        Inset = 2
    }

    /// <summary>Roblox <c>Enum.ScrollingDirection</c> values.</summary>
    public enum RbxScrollingDirection
    {
        X = 1,
        Y = 2,
        XY = 4
    }

    /// <summary>Roblox <c>Enum.StartCorner</c> values.</summary>
    public enum RbxStartCorner
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3
    }

    /// <summary>Roblox <c>Enum.BorderStrokePosition</c> values.</summary>
    public enum RbxBorderStrokePosition
    {
        Outer = 0,
        Center = 1,
        Inner = 2
    }

    /// <summary>
    /// The enums the GUI classes read and write, with the items and values of <c>Full-API-Dump.json</c>. They are
    /// registered into a world's <see cref="RbxEnumRegistry"/> on demand, so a registry made without them (a test, a
    /// custom host) gains them the first time the GUI bindings are built.
    /// </summary>
    public static class RbxGuiEnums
    {
        /// <summary>Registers every GUI enum the registry does not hold yet.</summary>
        public static void EnsureRegistered(RbxEnumRegistry registry)
        {
            if (registry == null)
            {
                return;
            }

            Add(registry, "TextXAlignment", ("Left", 0), ("Right", 1), ("Center", 2));
            Add(registry, "TextYAlignment", ("Top", 0), ("Center", 1), ("Bottom", 2));
            Add(registry, "Font", ("Legacy", 0), ("Arial", 1), ("ArialBold", 2), ("SourceSans", 3),
                ("SourceSansBold", 4), ("SourceSansLight", 5), ("SourceSansItalic", 6), ("Bodoni", 7),
                ("Garamond", 8), ("Cartoon", 9), ("Code", 10), ("Highway", 11), ("SciFi", 12), ("Arcade", 13),
                ("Fantasy", 14), ("Antique", 15), ("SourceSansSemibold", 16), ("Gotham", 17), ("GothamMedium", 18),
                ("GothamBold", 19), ("GothamBlack", 20), ("AmaticSC", 21), ("Bangers", 22), ("Creepster", 23),
                ("DenkOne", 24), ("Fondamento", 25), ("FredokaOne", 26), ("GrenzeGotisch", 27), ("IndieFlower", 28),
                ("JosefinSans", 29), ("Jura", 30), ("Kalam", 31), ("LuckiestGuy", 32), ("Merriweather", 33),
                ("Michroma", 34), ("Nunito", 35), ("Oswald", 36), ("PatrickHand", 37), ("PermanentMarker", 38),
                ("Roboto", 39), ("RobotoCondensed", 40), ("RobotoMono", 41), ("Sarpanch", 42), ("SpecialElite", 43),
                ("TitilliumWeb", 44), ("Ubuntu", 45), ("BuilderSans", 46), ("BuilderSansMedium", 47),
                ("BuilderSansBold", 48), ("BuilderSansExtraBold", 49), ("Arimo", 50), ("ArimoBold", 51),
                ("Unknown", 100));
            Add(registry, "FillDirection", ("Horizontal", 0), ("Vertical", 1));
            Add(registry, "HorizontalAlignment", ("Center", 0), ("Left", 1), ("Right", 2));
            Add(registry, "VerticalAlignment", ("Center", 0), ("Top", 1), ("Bottom", 2));
            Add(registry, "SortOrder", ("Name", 0), ("Custom", 1), ("LayoutOrder", 2));
            Add(registry, "ScaleType", ("Stretch", 0), ("Slice", 1), ("Tile", 2), ("Fit", 3), ("Crop", 4));
            Add(registry, "AspectType", ("FitWithinMaxSize", 0), ("ScaleWithParentSize", 1));
            Add(registry, "DominantAxis", ("Width", 0), ("Height", 1));
            Add(registry, "ZIndexBehavior", ("Global", 0), ("Sibling", 1));
            Add(registry, "AutomaticSize", ("None", 0), ("X", 1), ("Y", 2), ("XY", 3));
            Add(registry, "SizeConstraint", ("RelativeXY", 0), ("RelativeXX", 1), ("RelativeYY", 2));
            Add(registry, "ApplyStrokeMode", ("Contextual", 0), ("Border", 1));
            Add(registry, "TextTruncate", ("None", 0), ("AtEnd", 1), ("SplitWord", 2));
            Add(registry, "BorderMode", ("Outline", 0), ("Middle", 1), ("Inset", 2));
            Add(registry, "ScrollingDirection", ("X", 1), ("Y", 2), ("XY", 4));
            Add(registry, "StartCorner", ("TopLeft", 0), ("TopRight", 1), ("BottomLeft", 2), ("BottomRight", 3));
            Add(registry, "FrameStyle", ("Custom", 0), ("ChatBlue", 1), ("RobloxSquare", 2), ("RobloxRound", 3),
                ("ChatGreen", 4), ("ChatRed", 5), ("DropShadow", 6));
            Add(registry, "ButtonStyle", ("Custom", 0), ("RobloxButtonDefault", 1), ("RobloxButton", 2),
                ("RobloxRoundButton", 3), ("RobloxRoundDefaultButton", 4), ("RobloxRoundDropdownButton", 5));
            Add(registry, "CoreGuiType", ("PlayerList", 0), ("Health", 1), ("Backpack", 2), ("Chat", 3), ("All", 4),
                ("EmotesMenu", 5), ("SelfView", 6), ("Captures", 7), ("AvatarSwitcher", 8), ("ExperienceShop", 9));
            Add(registry, "ScreenInsets", ("None", 0), ("DeviceSafeInsets", 1), ("CoreUISafeInsets", 2),
                ("TopbarSafeInsets", 3));
            Add(registry, "SafeAreaCompatibility", ("None", 0), ("FullscreenExtension", 1));
            Add(registry, "LineJoinMode", ("Round", 0), ("Bevel", 1), ("Miter", 2));
            Add(registry, "BorderStrokePosition", ("Outer", 0), ("Center", 1), ("Inner", 2));
            Add(registry, "ResamplerMode", ("Default", 0), ("Pixelated", 1));
            Add(registry, "ElasticBehavior", ("WhenScrollable", 0), ("Always", 1), ("Never", 2));
            Add(registry, "ScrollBarInset", ("None", 0), ("ScrollBar", 1), ("Always", 2));
            Add(registry, "VerticalScrollBarPosition", ("Right", 0), ("Left", 1));
            Add(registry, "ScreenOrientation", ("LandscapeLeft", 0), ("LandscapeRight", 1), ("LandscapeSensor", 2),
                ("Portrait", 3), ("Sensor", 4));
            Add(registry, "TextDirection", ("Auto", 0), ("LeftToRight", 1), ("RightToLeft", 2));
            Add(registry, "StrokeSizingMode", ("FixedSize", 0), ("ScaledSize", 1));
            Add(registry, "UIFlexAlignment", ("None", 0), ("Fill", 1), ("SpaceAround", 2), ("SpaceBetween", 3),
                ("SpaceEvenly", 4));
            Add(registry, "ItemLineAlignment", ("Automatic", 0), ("Start", 1), ("Center", 2), ("End", 3),
                ("Stretch", 4));
        }

        private static void Add(RbxEnumRegistry registry, string name, params (string name, int value)[] items)
        {
            if (!registry.TryGet(name, out _))
            {
                registry.Register(new RbxEnum(name, items));
            }
        }
    }
}
