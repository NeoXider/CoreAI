using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The Lua face of <c>workspace.Terrain</c> and <c>Region3</c> (P4). Composition hooks it in with
    /// one line each: <see cref="BuildRegion3Global"/> as the <c>Region3</c> global,
    /// <see cref="RegisterMethods"/> into the Instance method table, and
    /// <see cref="TryReadWorkspaceTerrain"/> in the Instance member read.
    /// </summary>
    /// <remarks>
    /// Every write is authorized like a Part property write (WorldEdit, the mutation envelope target,
    /// the world ACL's WriteProperty decision on the Terrain instance) and runs inside the
    /// server-generated mutation, so a later network rung replicates Terrain writes the way it
    /// replicates Part writes. The calls take and return studs and <c>Enum.Material</c> items exactly
    /// as <c>Terrain.yaml</c> shapes them.
    /// </remarks>
    internal static class LuaCsTerrainBindings
    {
        /// <summary>The declaring class every Terrain method is registered under.</summary>
        public const string DeclaringClass = RbxTerrain.ClassNameValue;

        /// <summary>
        /// Regions up to this many cells reuse a per-script scratch buffer; larger ones borrow a
        /// buffer for the one call, so a single huge read does not pin megabytes for the script's life.
        /// </summary>
        internal const int ScratchRetainCells = 262_144;

        private static readonly LuaTable Region3Meta = BuildRegion3Meta();

        private static readonly ConditionalWeakTable<LuaCsRbxModContext, Scratch> Scratches = new();

        private static readonly ConditionalWeakTable<RbxEnumRegistry, LuaValue[]> MaterialItems = new();

        // ---- Region3 ---------------------------------------------------------------------------

        /// <summary>Wraps a Region3 for Lua.</summary>
        public static LuaValue Wrap(RbxRegion3 region)
        {
            return Box(region, Region3Meta);
        }

        /// <summary>The <c>Region3</c> global: <c>Region3.new(min, max)</c>, each corner defaulting to the origin.</summary>
        public static LuaValue BuildRegion3Global()
        {
            LuaTable global = new();
            global["new"] = Fn("Region3.new", ctx => Wrap(new RbxRegion3(
                OptionalVector3(ctx, 0, "Region3.new", 1),
                OptionalVector3(ctx, 1, "Region3.new", 2))));
            return new LuaValue(global);
        }

        /// <summary>Reads a Region3 argument at VM slot <paramref name="index"/>, naming <paramref name="argumentNumber"/>.</summary>
        public static RbxRegion3 ReadRegion3(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxRegion3 region))
            {
                return region;
            }

            throw ExpectedArgument(what, "a Region3", value, argumentNumber);
        }

        private static LuaTable BuildRegion3Meta()
        {
            LuaValue expandToGrid = new(Fn("Region3.ExpandToGrid", ctx => Wrap(
                SelfRegion(ctx).ExpandToGrid(ReadDouble(ctx, 1, "Region3:ExpandToGrid", 1)))));
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Region3.__index", ctx =>
            {
                RbxRegion3 self = SelfRegion(ctx);
                string key = ReadString(ctx, 1, "Region3 member access");
                switch (key)
                {
                    case "CFrame": return LuaCsRbxDatatypeBindings.Wrap(self.CFrame);
                    case "Size": return LuaCsRbxDatatypeBindings.Wrap(self.Size);
                    case "ExpandToGrid": return expandToGrid;
                    default:
                        throw RbxError.BadArgument(key + " is not a valid member of Region3",
                            "use Region3.CFrame, Region3.Size or Region3:ExpandToGrid(resolution)");
                }
            });
            meta[Metamethods.NewIndex] = Fn("Region3.__newindex", _ =>
                throw RbxError.BadArgument("Region3 is read-only",
                    "build a new Region3 with Region3.new(min, max) instead of assigning to one"));
            // WHY the name: typeof reads a datatype's name from its __tostring function's name.
            meta[Metamethods.ToString] = Fn("Region3.__tostring", ctx => SelfRegion(ctx).ToString());
            return Lock(meta);
        }

        private static RbxRegion3 SelfRegion(LuaFunctionExecutionContext ctx)
        {
            LuaValue value = Arg(ctx, 0);
            if (TryUnbox(value, out RbxRegion3 region))
            {
                return region;
            }

            throw ExpectedArgument("Region3 method", "a Region3", value, 1);
        }

        private static RbxVector3 OptionalVector3(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            return Arg(ctx, index).Type == LuaValueType.Nil
                ? RbxVector3.Zero
                : ReadVector3(ctx, index, what, argumentNumber);
        }

        // ---- workspace.Terrain -----------------------------------------------------------------

        /// <summary>
        /// Mirror <c>Workspace.Terrain</c>: true with the wrapped Terrain when <paramref name="self"/> is
        /// the Workspace and <paramref name="key"/> is <c>Terrain</c>; false for every other read, and
        /// for a Workspace whose host left the Terrain block out.
        /// </summary>
        public static bool TryReadWorkspaceTerrain(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            value = LuaValue.Nil;
            if (!string.Equals(key, "Terrain", StringComparison.Ordinal) || self == null || !self.IsA("Workspace"))
            {
                return false;
            }

            RbxTerrain terrain = RbxTerrainBootstrap.Find(self);
            if (terrain == null)
            {
                return false;
            }

            value = context.WrapInstance(terrain);
            return true;
        }

        // ---- Terrain methods -------------------------------------------------------------------

        /// <summary>
        /// Adds every bound Terrain method through <paramref name="add"/> (name, function, declaring
        /// class), e.g. <c>LuaCsTerrainBindings.RegisterMethods(context, methods.Add)</c>.
        /// </summary>
        public static void RegisterMethods(LuaCsRbxModContext context, Action<string, LuaValue, string> add)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (add == null)
            {
                throw new ArgumentNullException(nameof(add));
            }

            add("FillBlock", Method(context, "Terrain:FillBlock", ctx =>
            {
                RbxTerrain terrain = SelfTerrain(ctx, "Terrain:FillBlock");
                RbxCFrame cframe = ReadCFrame(ctx, 1, "Terrain:FillBlock", 1);
                RbxVector3 size = ReadVector3(ctx, 2, "Terrain:FillBlock", 2);
                byte material = ReadTerrainMaterial(context, ctx, 3, "Terrain:FillBlock", 3);
                return Mutate(context, terrain, "Terrain:FillBlock", "invoke Terrain:FillBlock",
                    () => terrain.FillBlock(cframe, size, material));
            }), DeclaringClass);

            add("FillRegion", Method(context, "Terrain:FillRegion", ctx =>
            {
                RbxTerrain terrain = SelfTerrain(ctx, "Terrain:FillRegion");
                RbxRegion3 region = ReadRegion3(ctx, 1, "Terrain:FillRegion", 1);
                double resolution = ReadDouble(ctx, 2, "Terrain:FillRegion", 2);
                byte material = ReadTerrainMaterial(context, ctx, 3, "Terrain:FillRegion", 3);
                return Mutate(context, terrain, "Terrain:FillRegion", "invoke Terrain:FillRegion",
                    () => terrain.FillRegion(region, resolution, material));
            }), DeclaringClass);

            add("ReplaceMaterial", Method(context, "Terrain:ReplaceMaterial", ctx =>
            {
                RbxTerrain terrain = SelfTerrain(ctx, "Terrain:ReplaceMaterial");
                RbxRegion3 region = ReadRegion3(ctx, 1, "Terrain:ReplaceMaterial", 1);
                double resolution = ReadDouble(ctx, 2, "Terrain:ReplaceMaterial", 2);
                byte source = ReadTerrainMaterial(context, ctx, 3, "Terrain:ReplaceMaterial", 3);
                byte target = ReadTerrainMaterial(context, ctx, 4, "Terrain:ReplaceMaterial", 4);
                return Mutate(context, terrain, "Terrain:ReplaceMaterial", "invoke Terrain:ReplaceMaterial",
                    () => terrain.ReplaceMaterial(region, resolution, source, target));
            }), DeclaringClass);

            add("Clear", Method(context, "Terrain:Clear", ctx =>
            {
                RbxTerrain terrain = SelfTerrain(ctx, "Terrain:Clear");
                return Mutate(context, terrain, "Terrain:Clear", "invoke Terrain:Clear", terrain.Clear);
            }), DeclaringClass);

            add("WriteVoxels", Method(context, "Terrain:WriteVoxels", ctx =>
            {
                RbxTerrain terrain = SelfTerrain(ctx, "Terrain:WriteVoxels");
                RbxRegion3 region = ReadRegion3(ctx, 1, "Terrain:WriteVoxels", 1);
                double resolution = ReadDouble(ctx, 2, "Terrain:WriteVoxels", 2);
                TerrainCellBox box = RbxTerrain.ResolveVoxelRegion(region, resolution, "Terrain:WriteVoxels");
                // WHY the capability first: a script without WorldEdit must not get to spend the time of
                // unpacking millions of cells before it is told it cannot write any of them.
                context.RequireWorldEdit("Terrain:WriteVoxels");
                Scratch scratch = RentScratch(context, box.CellCount, out byte[] materials, out byte[] occupancy);
                try
                {
                    ReadMaterialArray(ctx, 3, box, materials);
                    ReadOccupancyArray(ctx, 4, box, occupancy);
                    return Mutate(context, terrain, "Terrain:WriteVoxels", "invoke Terrain:WriteVoxels",
                        () => terrain.WriteVoxels(region, resolution, materials, occupancy));
                }
                finally
                {
                    scratch?.Return(materials, occupancy);
                }
            }), DeclaringClass);

            add("ReadVoxels", MethodTuple(context, "Terrain:ReadVoxels", ctx =>
            {
                RbxTerrain terrain = SelfTerrain(ctx, "Terrain:ReadVoxels");
                RbxRegion3 region = ReadRegion3(ctx, 1, "Terrain:ReadVoxels", 1);
                double resolution = ReadDouble(ctx, 2, "Terrain:ReadVoxels", 2);
                TerrainCellBox box = RbxTerrain.ResolveVoxelRegion(region, resolution, "Terrain:ReadVoxels");
                Scratch scratch = RentScratch(context, box.CellCount, out byte[] materials, out byte[] occupancy);
                try
                {
                    terrain.ReadVoxels(region, resolution, materials, occupancy);
                    return BuildVoxelTables(context, box, materials, occupancy);
                }
                finally
                {
                    scratch?.Return(materials, occupancy);
                }
            }), DeclaringClass);

            add("WorldToCell", Method(context, "Terrain:WorldToCell", ctx =>
            {
                SelfTerrain(ctx, "Terrain:WorldToCell");
                return LuaCsRbxDatatypeBindings.Wrap(
                    RbxTerrain.WorldToCell(ReadVector3(ctx, 1, "Terrain:WorldToCell", 1)));
            }), DeclaringClass);

            add("CellCenterToWorld", Method(context, "Terrain:CellCenterToWorld", ctx =>
            {
                SelfTerrain(ctx, "Terrain:CellCenterToWorld");
                return LuaCsRbxDatatypeBindings.Wrap(RbxTerrain.CellCenterToWorld(
                    ReadCellIndex(ctx, 1, "Terrain:CellCenterToWorld", 1),
                    ReadCellIndex(ctx, 2, "Terrain:CellCenterToWorld", 2),
                    ReadCellIndex(ctx, 3, "Terrain:CellCenterToWorld", 3)));
            }), DeclaringClass);

            add("CellCornerToWorld", Method(context, "Terrain:CellCornerToWorld", ctx =>
            {
                SelfTerrain(ctx, "Terrain:CellCornerToWorld");
                return LuaCsRbxDatatypeBindings.Wrap(RbxTerrain.CellCornerToWorld(
                    ReadCellIndex(ctx, 1, "Terrain:CellCornerToWorld", 1),
                    ReadCellIndex(ctx, 2, "Terrain:CellCornerToWorld", 2),
                    ReadCellIndex(ctx, 3, "Terrain:CellCornerToWorld", 3)));
            }), DeclaringClass);

            add("CountCells", Method(context, "Terrain:CountCells", ctx =>
                (double)SelfTerrain(ctx, "Terrain:CountCells").CountCells()), DeclaringClass);
        }

        private static LuaValue Method(LuaCsRbxModContext context, string name,
            Func<LuaFunctionExecutionContext, LuaValue> body)
        {
            return new LuaValue(Fn(name, body, context));
        }

        private static LuaValue MethodTuple(LuaCsRbxModContext context, string name,
            Func<LuaFunctionExecutionContext, LuaValue[]> body)
        {
            return new LuaValue(FnMulti(name, body, context));
        }

        private static LuaValue Mutate(LuaCsRbxModContext context, RbxTerrain terrain, string member,
            string operation, Action write)
        {
            return context.ApplyServerGeneratedMutation(operation, () =>
            {
                RequireTerrainWrite(context, terrain, member);
                long revision = terrain.Store.Revision;
                write();
                if (terrain.Store.Revision != revision)
                {
                    context.RecordMutation(terrain);
                }

                return LuaValue.Nil;
            });
        }

        /// <summary>
        /// The Part-write authorization for a Terrain write: the WorldEdit capability, the envelope's
        /// target, then the world ACL's WriteProperty decision on the Terrain instance.
        /// </summary>
        private static void RequireTerrainWrite(LuaCsRbxModContext context, RbxTerrain terrain, string member)
        {
            context.RequireWorldEdit(member);
            context.RequireMutationTarget(terrain, "write terrain");
            context.Bindings.Registry.AuthorizeMutation(context.ActorContext.ActorId,
                context.ActorContext.Grants.IsUnrestricted, context.ActorContext.WorldId, terrain,
                WorldAclDecision.WriteProperty, "write terrain");
        }

        private static RbxTerrain SelfTerrain(LuaFunctionExecutionContext ctx, string member)
        {
            LuaValue value = Arg(ctx, 0);
            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy) && proxy.Instance is RbxTerrain terrain)
            {
                if (terrain.IsDestroyed)
                {
                    throw RbxError.InstanceDestroyed(member, terrain.Name, terrain.Id);
                }

                return terrain;
            }

            string method = member.Substring(member.IndexOf(':') + 1);
            throw RbxError.BadArgument(
                member + " must be called on the Terrain with a colon, got " + Describe(value) + " as self",
                "write workspace.Terrain:" + method + "(...)");
        }

        private static byte ReadTerrainMaterial(LuaCsRbxModContext context, LuaFunctionExecutionContext ctx,
            int index, string member, int argumentNumber)
        {
            RbxEnumItem item = ReadEnumArgument(ctx, index, context.Bindings.Enums, "Material", member,
                argumentNumber);
            return RbxTerrain.RequireTerrainMaterial(item.Name, item.Value, member, argumentNumber);
        }

        private static int ReadCellIndex(LuaFunctionExecutionContext ctx, int index, string member,
            int argumentNumber)
        {
            double value = ReadDouble(ctx, index, member, argumentNumber);
            if (double.IsNaN(value) || value < int.MinValue || value > int.MaxValue)
            {
                throw RbxError.BadArgument(
                    member + " argument " + argumentNumber + " must be a whole cell index, got " + value,
                    "pass cell coordinates as numbers, e.g. the X, Y and Z of terrain:WorldToCell(position)");
            }

            return (int)value;
        }

        // ---- Voxel arrays ----------------------------------------------------------------------

        private static LuaValue[] BuildVoxelTables(LuaCsRbxModContext context, in TerrainCellBox box,
            byte[] materials, byte[] occupancy)
        {
            LuaValue[] items = MaterialItemsOf(context.Bindings.Enums);
            LuaValue size = LuaCsRbxDatatypeBindings.Wrap(new RbxVector3(box.SizeX, box.SizeY, box.SizeZ));
            LuaTable materialRoot = new(box.SizeX, 1);
            LuaTable occupancyRoot = new(box.SizeX, 1);
            int index = 0;
            for (int x = 1; x <= box.SizeX; x++)
            {
                LuaTable materialRow = new(box.SizeY, 0);
                LuaTable occupancyRow = new(box.SizeY, 0);
                for (int y = 1; y <= box.SizeY; y++)
                {
                    LuaTable materialColumn = new(box.SizeZ, 0);
                    LuaTable occupancyColumn = new(box.SizeZ, 0);
                    for (int z = 1; z <= box.SizeZ; z++, index++)
                    {
                        materialColumn[z] = items[materials[index]];
                        occupancyColumn[z] = TerrainOccupancy.ToDouble(occupancy[index]);
                    }

                    materialRow[y] = materialColumn;
                    occupancyRow[y] = occupancyColumn;
                }

                materialRoot[x] = materialRow;
                occupancyRoot[x] = occupancyRow;
            }

            materialRoot["Size"] = size;
            occupancyRoot["Size"] = size;
            return new[] { new LuaValue(materialRoot), new LuaValue(occupancyRoot) };
        }

        private static void ReadMaterialArray(LuaFunctionExecutionContext ctx, int index, in TerrainCellBox box,
            byte[] materials)
        {
            const string what = "materials";
            LuaTable root = RequireArray(Arg(ctx, index), what, 0, 0, box.SizeX, "X", 3);
            int cell = 0;
            for (int x = 1; x <= box.SizeX; x++)
            {
                LuaTable row = RequireArray(ReadElement(root, x), what, x, 0, box.SizeY, "Y", 3);
                for (int y = 1; y <= box.SizeY; y++)
                {
                    LuaTable column = RequireArray(ReadElement(row, y), what, x, y, box.SizeZ, "Z", 3);
                    Span<LuaValue> span = column.GetArraySpan();
                    for (int z = 1; z <= box.SizeZ; z++, cell++)
                    {
                        LuaValue value = z <= span.Length && span[z - 1].Type != LuaValueType.Nil
                            ? span[z - 1]
                            : column[z];
                        materials[cell] = ReadMaterialElement(value, x, y, z);
                    }
                }
            }
        }

        private static byte ReadMaterialElement(LuaValue value, int x, int y, int z)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType != null
                                                       && string.Equals(item.EnumType.Name, "Material",
                                                           StringComparison.Ordinal))
            {
                return RbxTerrain.RequireTerrainMaterial(item.Name, item.Value, "Terrain:WriteVoxels", 3);
            }

            throw RbxError.BadArgument(
                "Terrain:WriteVoxels materials[" + x + "][" + y + "][" + z + "] is " + Describe(value)
                + "; every element must be an Enum.Material item",
                "fill the materials array with Enum.Material items, e.g. Enum.Material.Grass or Enum.Material.Air");
        }

        private static void ReadOccupancyArray(LuaFunctionExecutionContext ctx, int index, in TerrainCellBox box,
            byte[] occupancy)
        {
            const string what = "occupancy";
            LuaTable root = RequireArray(Arg(ctx, index), what, 0, 0, box.SizeX, "X", 4);
            int cell = 0;
            for (int x = 1; x <= box.SizeX; x++)
            {
                LuaTable row = RequireArray(ReadElement(root, x), what, x, 0, box.SizeY, "Y", 4);
                for (int y = 1; y <= box.SizeY; y++)
                {
                    LuaTable column = RequireArray(ReadElement(row, y), what, x, y, box.SizeZ, "Z", 4);
                    Span<LuaValue> span = column.GetArraySpan();
                    for (int z = 1; z <= box.SizeZ; z++, cell++)
                    {
                        LuaValue value = z <= span.Length && span[z - 1].Type != LuaValueType.Nil
                            ? span[z - 1]
                            : column[z];
                        if (value.Type != LuaValueType.Number || double.IsNaN(value.Read<double>()))
                        {
                            throw RbxError.BadArgument(
                                "Terrain:WriteVoxels occupancy[" + x + "][" + y + "][" + z + "] is "
                                + Describe(value) + "; every element must be a number from 0 to 1",
                                "fill the occupancy array with numbers, 1 for a full cell and 0 for an empty one");
                        }

                        occupancy[cell] = TerrainOccupancy.ToByte(value.Read<double>());
                    }
                }
            }
        }

        /// <summary>
        /// The table at <c>name[x][y]</c> (0 for an index not reached yet), refused unless it holds
        /// exactly <paramref name="expectedLength"/> entries. The label is built only on refusal.
        /// </summary>
        private static LuaTable RequireArray(LuaValue value, string name, int x, int y, int expectedLength,
            string axis, int argumentNumber)
        {
            if (!value.TryRead(out LuaTable table))
            {
                throw RbxError.BadArgument(
                    Label(name, x, y) + " is " + Describe(value) + "; expected a table of " + expectedLength
                    + " entries (argument " + argumentNumber + ")",
                    "pass a 3D array t[x][y][z] sized like the region in cells, as ReadVoxels returns it");
            }

            if ((expectedLength > 0 && ReadElement(table, expectedLength).Type == LuaValueType.Nil)
                || ReadElement(table, expectedLength + 1).Type != LuaValueType.Nil)
            {
                throw RbxError.BadArgument(
                    Label(name, x, y) + " does not hold exactly " + expectedLength + " entries along " + axis
                    + "; the region is " + expectedLength + " cells along " + axis + " (argument " + argumentNumber
                    + ")",
                    "size the arrays like the region in cells (region.Size / 4), e.g. from terrain:ReadVoxels(region, 4)");
            }

            return table;
        }

        private static string Label(string name, int x, int y)
        {
            string label = "Terrain:WriteVoxels " + name;
            if (x > 0)
            {
                label += "[" + x + "]";
            }

            if (y > 0)
            {
                label += "[" + y + "]";
            }

            return label;
        }

        private static LuaValue ReadElement(LuaTable table, int oneBasedIndex)
        {
            Span<LuaValue> span = table.GetArraySpan();
            if (oneBasedIndex >= 1 && oneBasedIndex <= span.Length && span[oneBasedIndex - 1].Type != LuaValueType.Nil)
            {
                return span[oneBasedIndex - 1];
            }

            return table[oneBasedIndex];
        }

        private static LuaValue[] MaterialItemsOf(RbxEnumRegistry enums)
        {
            return MaterialItems.GetValue(enums, registry =>
            {
                RbxEnum material = registry.Get("Material");
                LuaValue[] items = new LuaValue[TerrainMaterials.SlotCount];
                for (int slot = 0; slot < items.Length; slot++)
                {
                    if (!material.TryGetItemByValue(TerrainMaterials.GetEnumValue((byte)slot), out RbxEnumItem item))
                    {
                        throw new InvalidOperationException(
                            "Enum.Material has no item " + TerrainMaterials.GetName((byte)slot));
                    }

                    items[slot] = LuaCsRbxDatatypeBindings.Wrap(item);
                }

                return items;
            });
        }

        // ---- Scratch buffers -------------------------------------------------------------------

        private static Scratch RentScratch(LuaCsRbxModContext context, long cells, out byte[] materials,
            out byte[] occupancy)
        {
            int count = (int)cells;
            if (count > ScratchRetainCells)
            {
                materials = new byte[count];
                occupancy = new byte[count];
                return null;
            }

            Scratch scratch = Scratches.GetValue(context, _ => new Scratch());
            scratch.Rent(count, out materials, out occupancy);
            return scratch;
        }

        /// <summary>
        /// One script's reusable cell buffers. Rented while a call runs and returned after it; a call
        /// that re-enters (a handler reading voxels inside another read) gets fresh buffers.
        /// </summary>
        private sealed class Scratch
        {
            private byte[] _materials = Array.Empty<byte>();
            private byte[] _occupancy = Array.Empty<byte>();
            private bool _rented;

            public void Rent(int count, out byte[] materials, out byte[] occupancy)
            {
                if (_rented)
                {
                    materials = new byte[count];
                    occupancy = new byte[count];
                    return;
                }

                if (_materials.Length < count)
                {
                    int capacity = Math.Min(ScratchRetainCells, Math.Max(count, _materials.Length * 2));
                    _materials = new byte[capacity];
                    _occupancy = new byte[capacity];
                }

                _rented = true;
                materials = _materials;
                occupancy = _occupancy;
            }

            public void Return(byte[] materials, byte[] occupancy)
            {
                if (ReferenceEquals(materials, _materials) && ReferenceEquals(occupancy, _occupancy))
                {
                    _rented = false;
                }
            }
        }
    }
}
