using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// One item of a Roblox enum (Enum.Material.Wood): Name + Value + a reference back to its
    /// enum type. Reference equality is identity — the registry interns one instance per item,
    /// so Lua-side `==` on marshalled items works like Roblox.
    /// </summary>
    public sealed class RbxEnumItem
    {
        public string Name { get; }
        public int Value { get; }
        public RbxEnum EnumType { get; }

        internal RbxEnumItem(string name, int value, RbxEnum enumType)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
            EnumType = enumType ?? throw new ArgumentNullException(nameof(enumType));
        }

        /// <summary>Roblox tostring format: "Enum.&lt;Type&gt;.&lt;Item&gt;".</summary>
        public override string ToString()
        {
            return $"Enum.{EnumType.Name}.{Name}";
        }
    }

    /// <summary>
    /// One Roblox enum type (Enum.Material): a named, ordered set of items with by-name and
    /// by-value lookup. Items are created through the enum so identity stays interned.
    /// </summary>
    public sealed class RbxEnum
    {
        private readonly List<RbxEnumItem> _items = new();
        private readonly Dictionary<string, RbxEnumItem> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<int, RbxEnumItem> _byValue = new();

        public string Name { get; }

        public RbxEnum(string name, params (string name, int value)[] items)
            : this(name, (IReadOnlyList<(string name, int value)>)items)
        {
        }

        public RbxEnum(string name, IReadOnlyList<(string name, int value)> items)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            foreach ((string itemName, int value) in items)
            {
                RbxEnumItem item = new(itemName, value, this);
                _items.Add(item);
                _byName.Add(itemName, item);
                // WHY: first declaration wins on duplicate values so by-value lookup stays
                // deterministic for enums with aliased items.
                if (!_byValue.ContainsKey(value))
                {
                    _byValue.Add(value, item);
                }
            }
        }

        /// <summary>GetEnumItems() — items in declaration order.</summary>
        public IReadOnlyList<RbxEnumItem> GetEnumItems()
        {
            return _items;
        }

        public bool TryGetItem(string itemName, out RbxEnumItem item)
        {
            return _byName.TryGetValue(itemName, out item);
        }

        /// <summary>By-value lookup (input events resolve Enum.KeyCode items from raw values).</summary>
        public bool TryGetItemByValue(int value, out RbxEnumItem item)
        {
            return _byValue.TryGetValue(value, out item);
        }

        /// <summary>Indexer used by the Lua `Enum.Type.Item` path; unknown item is a hard error.</summary>
        public RbxEnumItem this[string itemName]
        {
            get
            {
                if (_byName.TryGetValue(itemName, out RbxEnumItem item))
                {
                    return item;
                }

                throw RbxApiStubException.BadArgument(
                    $"'{itemName}' is not a valid member of Enum.{Name}.",
                    $"call Enum.{Name}:GetEnumItems() to list valid items");
            }
        }

        /// <summary>Roblox tostring format: "Enum.&lt;Type&gt;".</summary>
        public override string ToString()
        {
            return $"Enum.{Name}";
        }
    }

    /// <summary>
    /// The `Enum` global registry. MVP1 seeds the enums the MVP1 surface needs (Material,
    /// PartType, CameraType, NormalId, Axis, RotationOrder, plus the input slice: KeyCode,
    /// UserInputType, UserInputState, MouseBehavior); later MVPs register more. Accessing an
    /// unregistered enum raises the roadmap's loud stub (§5.1.6: "enum X arrives with its
    /// service").
    /// </summary>
    public sealed class RbxEnumRegistry
    {
        private readonly Dictionary<string, RbxEnum> _enums = new(StringComparer.Ordinal);

        /// <summary>Creates a registry pre-seeded with the MVP1 enum set.</summary>
        public static RbxEnumRegistry CreateWithBuiltins()
        {
            RbxEnumRegistry registry = new();
            registry.Register(new RbxEnum("Material",
                ("Plastic", 256), ("SmoothPlastic", 272), ("Neon", 288),
                ("Wood", 512), ("WoodPlanks", 528),
                ("Marble", 784), ("Basalt", 788), ("Slate", 800), ("CrackedLava", 804),
                ("Concrete", 816), ("Limestone", 820), ("Granite", 832), ("Pavement", 836),
                ("Brick", 848), ("Pebble", 864), ("Cobblestone", 880), ("Rock", 896),
                ("Sandstone", 912),
                ("CorrodedMetal", 1040), ("DiamondPlate", 1056), ("Foil", 1072), ("Metal", 1088),
                ("Grass", 1280), ("LeafyGrass", 1284), ("Sand", 1296), ("Fabric", 1312),
                ("Snow", 1328), ("Mud", 1344), ("Ground", 1360), ("Asphalt", 1376), ("Salt", 1392),
                ("Ice", 1536), ("Glacier", 1552), ("Glass", 1568), ("ForceField", 1584),
                ("Air", 1792), ("Water", 2048),
                ("Cardboard", 2304), ("Carpet", 2305), ("CeramicTiles", 2306),
                ("ClayRoofTiles", 2307), ("RoofShingles", 2308), ("Leather", 2309),
                ("Plaster", 2310), ("Rubber", 2311)));
            registry.Register(new RbxEnum("PartType",
                ("Ball", 0), ("Block", 1), ("Cylinder", 2), ("Wedge", 3), ("CornerWedge", 4)));
            registry.Register(new RbxEnum("CameraType",
                ("Fixed", 0), ("Attach", 1), ("Watch", 2), ("Track", 3), ("Follow", 4),
                ("Custom", 5), ("Scriptable", 6), ("Orbital", 7)));
            registry.Register(new RbxEnum("NormalId",
                ("Right", 0), ("Top", 1), ("Back", 2), ("Left", 3), ("Bottom", 4), ("Front", 5)));
            registry.Register(new RbxEnum("Axis", ("X", 0), ("Y", 1), ("Z", 2)));
            registry.Register(new RbxEnum("RotationOrder",
                ("XYZ", 0), ("XZY", 1), ("YZX", 2), ("YXZ", 3), ("ZXY", 4), ("ZYX", 5)));
            registry.Register(CreateKeyCode());
            registry.Register(new RbxEnum("UserInputType",
                ("MouseButton1", 0), ("MouseButton2", 1), ("MouseButton3", 2), ("MouseWheel", 3),
                ("MouseMovement", 4), ("Touch", 7), ("Keyboard", 8), ("Focus", 9),
                ("Accelerometer", 10), ("Gyro", 11),
                ("Gamepad1", 12), ("Gamepad2", 13), ("Gamepad3", 14), ("Gamepad4", 15),
                ("Gamepad5", 16), ("Gamepad6", 17), ("Gamepad7", 18), ("Gamepad8", 19),
                ("TextInput", 20), ("InputMethod", 21), ("None", 22)));
            registry.Register(new RbxEnum("UserInputState",
                ("Begin", 0), ("Change", 1), ("End", 2), ("Cancel", 3), ("None", 4)));
            registry.Register(new RbxEnum("MouseBehavior",
                ("Default", 0), ("LockCenter", 1), ("LockCurrentPosition", 2)));
            registry.Register(new RbxEnum("PlayerExitReason",
                ("Unknown", 0), ("PlatformKick", 1), ("CreatorKick", 2)));
            // WHY: MVP8 slice 8.4 — tween enums arrive with TweenService (mirror-valued 1:1;
            // EasingStyle Linear 0..Cubic 10, EasingDirection In 0..InOut 2, PlaybackState
            // Begin 0..Cancelled 5).
            registry.Register(new RbxEnum("EasingStyle",
                ("Linear", 0), ("Sine", 1), ("Back", 2), ("Quad", 3), ("Quart", 4),
                ("Quint", 5), ("Bounce", 6), ("Elastic", 7), ("Exponential", 8),
                ("Circular", 9), ("Cubic", 10)));
            registry.Register(new RbxEnum("EasingDirection",
                ("In", 0), ("Out", 1), ("InOut", 2)));
            registry.Register(new RbxEnum("PlaybackState",
                ("Begin", 0), ("Delayed", 1), ("Playing", 2), ("Paused", 3),
                ("Completed", 4), ("Cancelled", 5)));
            // WHY exactly two items: the mirror's RaycastFilterType.yaml lists Exclude 0 and
            // Include 1 and nothing else. The pre-2022 Blacklist/Whitelist spellings were retired
            // rather than kept as aliases, so shipping them would teach scripts a name a Roblox
            // round-trip cannot carry back.
            registry.Register(new RbxEnum("RaycastFilterType",
                ("Exclude", 0), ("Include", 1)));
            return registry;
        }

        /// <summary>Enum.KeyCode with the full Roblox item set (names AND values 1:1, gamepad
        /// buttons at 1000+); letters/digits/World keys are generated from their contiguous
        /// Roblox value ranges.</summary>
        private static RbxEnum CreateKeyCode()
        {
            List<(string name, int value)> items = new()
            {
                ("Unknown", 0), ("Backspace", 8), ("Tab", 9), ("Clear", 12), ("Return", 13),
                ("Pause", 19), ("Escape", 27), ("Space", 32), ("QuotedDouble", 34), ("Hash", 35),
                ("Dollar", 36), ("Percent", 37), ("Ampersand", 38), ("Quote", 39),
                ("LeftParenthesis", 40), ("RightParenthesis", 41), ("Asterisk", 42), ("Plus", 43),
                ("Comma", 44), ("Minus", 45), ("Period", 46), ("Slash", 47)
            };
            string[] digitNames =
            {
                "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine"
            };
            for (int i = 0; i < digitNames.Length; i++)
            {
                items.Add((digitNames[i], 48 + i));
            }

            items.AddRange(new (string, int)[]
            {
                ("Colon", 58), ("Semicolon", 59), ("LessThan", 60), ("Equals", 61),
                ("GreaterThan", 62), ("Question", 63), ("At", 64), ("LeftBracket", 91),
                ("BackSlash", 92), ("RightBracket", 93), ("Caret", 94), ("Underscore", 95),
                ("Backquote", 96)
            });
            for (int i = 0; i < 26; i++)
            {
                items.Add((((char)('A' + i)).ToString(), 97 + i));
            }

            items.AddRange(new (string, int)[]
            {
                ("LeftCurly", 123), ("Pipe", 124), ("RightCurly", 125), ("Tilde", 126),
                ("Delete", 127)
            });
            for (int i = 0; i <= 95; i++)
            {
                items.Add(("World" + i, 160 + i));
            }

            string[] keypadDigits =
            {
                "KeypadZero", "KeypadOne", "KeypadTwo", "KeypadThree", "KeypadFour", "KeypadFive",
                "KeypadSix", "KeypadSeven", "KeypadEight", "KeypadNine"
            };
            for (int i = 0; i < keypadDigits.Length; i++)
            {
                items.Add((keypadDigits[i], 256 + i));
            }

            items.AddRange(new (string, int)[]
            {
                ("KeypadPeriod", 266), ("KeypadDivide", 267), ("KeypadMultiply", 268),
                ("KeypadMinus", 269), ("KeypadPlus", 270), ("KeypadEnter", 271),
                ("KeypadEquals", 272), ("Up", 273), ("Down", 274), ("Right", 275), ("Left", 276),
                ("Insert", 277), ("Home", 278), ("End", 279), ("PageUp", 280), ("PageDown", 281)
            });
            for (int i = 1; i <= 15; i++)
            {
                items.Add(("F" + i, 281 + i));
            }

            items.AddRange(new (string, int)[]
            {
                ("NumLock", 300), ("CapsLock", 301), ("ScrollLock", 302), ("RightShift", 303),
                ("LeftShift", 304), ("RightControl", 305), ("LeftControl", 306), ("RightAlt", 307),
                ("LeftAlt", 308), ("RightMeta", 309), ("LeftMeta", 310), ("LeftSuper", 311),
                ("RightSuper", 312), ("Mode", 313), ("Compose", 314), ("Help", 315), ("Print", 316),
                ("SysReq", 317), ("Break", 318), ("Menu", 319), ("Power", 320), ("Euro", 321),
                ("Undo", 322),
                ("ButtonX", 1000), ("ButtonY", 1001), ("ButtonA", 1002), ("ButtonB", 1003),
                ("ButtonR1", 1004), ("ButtonL1", 1005), ("ButtonR2", 1006), ("ButtonL2", 1007),
                ("ButtonR3", 1008), ("ButtonL3", 1009), ("ButtonStart", 1010),
                ("ButtonSelect", 1011), ("DPadLeft", 1012), ("DPadRight", 1013), ("DPadUp", 1014),
                ("DPadDown", 1015), ("Thumbstick1", 1016), ("Thumbstick2", 1017)
            });
            return new RbxEnum("KeyCode", items);
        }

        public void Register(RbxEnum rbxEnum)
        {
            if (rbxEnum == null)
            {
                throw new ArgumentNullException(nameof(rbxEnum));
            }

            _enums[rbxEnum.Name] = rbxEnum;
        }

        public bool TryGet(string enumName, out RbxEnum rbxEnum)
        {
            return _enums.TryGetValue(enumName, out rbxEnum);
        }

        /// <summary>Lua `Enum.X` path; unknown enum raises the roadmap's loud stub (§5.1.6).</summary>
        public RbxEnum Get(string enumName)
        {
            if (_enums.TryGetValue(enumName, out RbxEnum rbxEnum))
            {
                return rbxEnum;
            }

            // TODO: MVP2+ — each service MVP registers its own enums (SignalBehavior in MVP2,
            // EasingStyle in MVP8, ...); until then unknown access stays a loud stub.
            throw RbxApiStubException.NotImplemented(
                $"Enum.{enumName}",
                "the MVP phase that ships its service",
                $"use one of the registered enums ({string.Join(", ", _enums.Keys)}) until then");
        }

        /// <summary>Enum:GetEnums() analog — all registered enum types.</summary>
        public IReadOnlyCollection<RbxEnum> GetEnums()
        {
            return _enums.Values;
        }
    }
}
