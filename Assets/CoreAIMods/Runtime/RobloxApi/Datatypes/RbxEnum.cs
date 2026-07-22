using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Roblox.Datatypes
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
        public override string ToString() => $"Enum.{EnumType.Name}.{Name}";
    }

    /// <summary>
    /// One Roblox enum type (Enum.Material): a named, ordered set of items with by-name and
    /// by-value lookup. Items are created through the enum so identity stays interned.
    /// </summary>
    public sealed class RbxEnum
    {
        private readonly List<RbxEnumItem> _items = new List<RbxEnumItem>();
        private readonly Dictionary<string, RbxEnumItem> _byName =
            new Dictionary<string, RbxEnumItem>(StringComparer.Ordinal);

        public string Name { get; }

        public RbxEnum(string name, params (string name, int value)[] items)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            foreach ((string itemName, int value) in items)
            {
                var item = new RbxEnumItem(itemName, value, this);
                _items.Add(item);
                _byName.Add(itemName, item);
            }
        }

        /// <summary>GetEnumItems() — items in declaration order.</summary>
        public IReadOnlyList<RbxEnumItem> GetEnumItems() => _items;

        public bool TryGetItem(string itemName, out RbxEnumItem item) =>
            _byName.TryGetValue(itemName, out item);

        /// <summary>Indexer used by the Lua `Enum.Type.Item` path; unknown item is a hard error.</summary>
        public RbxEnumItem this[string itemName]
        {
            get
            {
                if (_byName.TryGetValue(itemName, out RbxEnumItem item))
                {
                    return item;
                }

                throw RobloxApiStubException.BadArgument(
                    $"'{itemName}' is not a valid member of Enum.{Name}.",
                    $"call Enum.{Name}:GetEnumItems() to list valid items");
            }
        }

        /// <summary>Roblox tostring format: "Enum.&lt;Type&gt;".</summary>
        public override string ToString() => $"Enum.{Name}";
    }

    /// <summary>
    /// The `Enum` global registry. MVP1 seeds the enums the MVP1 surface needs (Material,
    /// PartType, NormalId, Axis, RotationOrder); later MVPs register more. Accessing an
    /// unregistered enum raises the roadmap's loud stub (§5.1.6: "enum X arrives with its
    /// service").
    /// </summary>
    public sealed class RbxEnumRegistry
    {
        private readonly Dictionary<string, RbxEnum> _enums =
            new Dictionary<string, RbxEnum>(StringComparer.Ordinal);

        /// <summary>Creates a registry pre-seeded with the MVP1 enum set.</summary>
        public static RbxEnumRegistry CreateWithBuiltins()
        {
            var registry = new RbxEnumRegistry();
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
            registry.Register(new RbxEnum("NormalId",
                ("Right", 0), ("Top", 1), ("Back", 2), ("Left", 3), ("Bottom", 4), ("Front", 5)));
            registry.Register(new RbxEnum("Axis", ("X", 0), ("Y", 1), ("Z", 2)));
            registry.Register(new RbxEnum("RotationOrder",
                ("XYZ", 0), ("XZY", 1), ("YZX", 2), ("YXZ", 3), ("ZXY", 4), ("ZYX", 5)));
            return registry;
        }

        public void Register(RbxEnum rbxEnum)
        {
            if (rbxEnum == null)
            {
                throw new ArgumentNullException(nameof(rbxEnum));
            }

            _enums[rbxEnum.Name] = rbxEnum;
        }

        public bool TryGet(string enumName, out RbxEnum rbxEnum) =>
            _enums.TryGetValue(enumName, out rbxEnum);

        /// <summary>Lua `Enum.X` path; unknown enum raises the roadmap's loud stub (§5.1.6).</summary>
        public RbxEnum Get(string enumName)
        {
            if (_enums.TryGetValue(enumName, out RbxEnum rbxEnum))
            {
                return rbxEnum;
            }

            // TODO: MVP2+ — each service MVP registers its own enums (KeyCode/UserInputType in
            // MVP10, SignalBehavior in MVP2, ...); until then unknown access stays a loud stub.
            throw RobloxApiStubException.NotImplemented(
                $"Enum.{enumName}",
                "the MVP phase that ships its service",
                $"use one of the registered enums ({string.Join(", ", _enums.Keys)}) until then");
        }

        /// <summary>Enum:GetEnums() analog — all registered enum types.</summary>
        public IReadOnlyCollection<RbxEnum> GetEnums() => _enums.Values;
    }
}
