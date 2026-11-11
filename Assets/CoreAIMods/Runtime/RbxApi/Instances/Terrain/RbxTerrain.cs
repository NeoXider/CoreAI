using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// Mirror <c>Terrain</c>: the voxel grid under <c>workspace.Terrain</c>, 4 x 4 x 4-stud cells that
    /// each hold an <c>Enum.Material</c> and an occupancy. This class carries the Roblox-shaped calls in
    /// studs; the cells live in <see cref="Store"/>, which the renderer, the codec and the gate read.
    /// </summary>
    /// <remarks>
    /// Recorded deviations (see README.md): cells draw as full cubes (no smoothing); occupancy is kept
    /// to 1/255; the class sits under PVInstance, not BasePart; <c>FillBlock</c> fills the cells whose
    /// centres are inside the box. The instance is not creatable; the Lua layer refuses to destroy or
    /// reparent it.
    /// </remarks>
    public sealed class RbxTerrain : RbxInstance
    {
        /// <summary>The Roblox ClassName.</summary>
        public const string ClassNameValue = "Terrain";

        /// <summary>
        /// Most cells one <c>FillBlock</c> may sweep (its axis-aligned bounds, after clipping to the
        /// Terrain bounds); the same number as the mirror's region limit.
        /// </summary>
        public const long MaxFillBlockCells = TerrainRegionValidator.MaxRegionVoxels;

        private const double CellStuds = TerrainBoxRasterizer.CellStuds;

        internal RbxTerrain(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Store = new TerrainChunkStore();
        }

        /// <summary>The cells of this Terrain.</summary>
        public TerrainChunkStore Store { get; }

        /// <summary>
        /// The catalog row: concrete, not creatable through <c>Instance.new</c>, not a service.
        /// </summary>
        /// <remarks>
        /// WHY PVInstance and not the mirror's BasePart: every BasePart path in CoreAI (the GameObject
        /// binder, the part property sink, the package serializer, tweens) gives the instance a part
        /// body, and Terrain has none, so under BasePart the world would grow a stray 4 x 1 x 2 brick.
        /// </remarks>
        public static ClassDescriptor CreateDescriptor()
        {
            return new ClassDescriptor(ClassNameValue, "PVInstance", false, false, false,
                descriptor => new RbxTerrain(descriptor));
        }

        /// <summary>
        /// Records the mirror's Terrain members this slice does not ship, so a script that reaches for
        /// one gets the loud stub naming what to use instead. Call once, right after registering
        /// <see cref="CreateDescriptor"/>.
        /// </summary>
        public static void RegisterKnownUnimplementedMembers(ClassCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            const string shapes =
                "a block world has square cells: build the shape from FillBlock calls (a rotated FillBlock "
                + "fills the cells whose centres are inside it)";
            const string channels = "use ReadVoxels and WriteVoxels; Water is a material cell here";
            const string copy = "keep the cells with ReadVoxels and put them back with WriteVoxels";
            const string look = "Terrain colour and water look come from the material catalog in a block world";
            const string legacy = "the legacy cell API was removed from Roblox; use ReadVoxels and WriteVoxels";
            catalog.RegisterKnownUnimplementedMembers(ClassNameValue,
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("FillBall", shapes),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("FillCylinder", shapes),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("FillWedge", shapes),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("ReadVoxelChannels", channels),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("WriteVoxelChannels", channels),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("CopyRegion", copy),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("PasteRegion", copy),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("WorldToCellPreferEmpty",
                    "use WorldToCell and step one cell along the face normal yourself"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("WorldToCellPreferSolid",
                    "use WorldToCell and step one cell against the face normal yourself"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("GetMaterialColor", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("SetMaterialColor", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("WaterColor", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("WaterReflectance", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("WaterTransparency", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("WaterWaveSize", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("WaterWaveSpeed", look),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("MaxExtents",
                    "the Terrain bounds are a host setting; writes outside them are refused with their extent"),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("AutowedgeCell", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("AutowedgeCells", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("ConvertToSmooth", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("GetCell", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("SetCell", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("SetCells", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("GetWaterCell", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedMethod("SetWaterCell", legacy),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("IsSmooth", legacy));
        }

        /// <summary>
        /// Mirror <c>Terrain:FillBlock(cframe, size, material)</c>: fills the cells whose centres lie in
        /// the oriented box (studs). Cells outside the Terrain bounds are skipped.
        /// </summary>
        public void FillBlock(in RbxCFrame cframe, RbxVector3 size, byte material)
        {
            const string member = "Terrain:FillBlock";
            ThrowIfDestroyedTerrain(member);
            RequireFinite(cframe.Position, member, "cframe position", 1);
            RequireFinite(cframe.XVector, member, "cframe rotation", 1);
            RequireFinite(cframe.YVector, member, "cframe rotation", 1);
            RequireFinite(cframe.ZVector, member, "cframe rotation", 1);
            if (!IsFinite(size) || size.X < 0f || size.Y < 0f || size.Z < 0f)
            {
                throw RbxError.BadArgument(
                    member + " size must be finite and not negative, got (" + size + ")",
                    "pass the block size in studs, e.g. Vector3.new(4, 4, 4) for one cell");
            }

            TerrainBoxRasterizer rasterizer = new(cframe, size);
            TerrainCellBox swept = Store.Limits.Clip(rasterizer.CellBounds);
            if (swept.CellCount > MaxFillBlockCells)
            {
                throw RbxError.BadArgument(
                    member + " block covers " + swept.CellCount + " cells; one call fills at most "
                    + MaxFillBlockCells,
                    "fill a large area in several FillBlock calls of at most 256 x 64 x 256 cells each");
            }

            Store.FillOrientedBox(rasterizer, material, TerrainOccupancy.Full);
        }

        /// <summary>Mirror <c>Terrain:FillRegion(region, resolution, material)</c>.</summary>
        public void FillRegion(in RbxRegion3 region, double resolution, byte material)
        {
            const string member = "Terrain:FillRegion";
            ThrowIfDestroyedTerrain(member);
            TerrainCellBox box = TerrainRegionValidator.Validate(region, resolution, member);
            Store.FillBox(box, material, TerrainOccupancy.Full);
        }

        /// <summary>
        /// Mirror <c>Terrain:ReadVoxels(region, resolution)</c> into caller buffers (z fastest, then y,
        /// then x, like <c>materials[x][y][z]</c>); returns the cells read.
        /// </summary>
        public TerrainCellBox ReadVoxels(in RbxRegion3 region, double resolution, byte[] materials,
            byte[] occupancy)
        {
            const string member = "Terrain:ReadVoxels";
            ThrowIfDestroyedTerrain(member);
            TerrainCellBox box = TerrainRegionValidator.Validate(region, resolution, member);
            Store.ReadBox(box, materials, occupancy);
            return box;
        }

        /// <summary>The cells <c>ReadVoxels</c>/<c>WriteVoxels</c> would cover, after the mirror's rules.</summary>
        public static TerrainCellBox ResolveVoxelRegion(in RbxRegion3 region, double resolution, string member)
        {
            return TerrainRegionValidator.Validate(region, resolution, member);
        }

        /// <summary>
        /// Mirror <c>Terrain:WriteVoxels(region, resolution, materials, occupancy)</c> from buffers laid
        /// out like <see cref="ReadVoxels"/>.
        /// </summary>
        public void WriteVoxels(in RbxRegion3 region, double resolution, byte[] materials, byte[] occupancy)
        {
            const string member = "Terrain:WriteVoxels";
            ThrowIfDestroyedTerrain(member);
            TerrainCellBox box = TerrainRegionValidator.Validate(region, resolution, member);
            Store.WriteBox(box, materials, occupancy);
        }

        /// <summary>Mirror <c>Terrain:Clear()</c>.</summary>
        public void Clear()
        {
            ThrowIfDestroyedTerrain("Terrain:Clear");
            Store.Clear();
        }

        /// <summary>Mirror <c>Terrain:ReplaceMaterial(region, resolution, source, target)</c>; returns the cells changed.</summary>
        public long ReplaceMaterial(in RbxRegion3 region, double resolution, byte source, byte target)
        {
            const string member = "Terrain:ReplaceMaterial";
            ThrowIfDestroyedTerrain(member);
            TerrainCellBox box = TerrainRegionValidator.Validate(region, resolution, member);
            return Store.ReplaceMaterial(box, source, target);
        }

        /// <summary>Mirror <c>Terrain:CountCells()</c>: the non-empty cells.</summary>
        public long CountCells()
        {
            ThrowIfDestroyedTerrain("Terrain:CountCells");
            return Store.SolidCellCount;
        }

        /// <summary>Mirror <c>Terrain:WorldToCell(position)</c>: the cell holding the point, as whole numbers.</summary>
        public static RbxVector3 WorldToCell(RbxVector3 position)
        {
            return new RbxVector3(
                (float)Math.Floor(position.X / CellStuds),
                (float)Math.Floor(position.Y / CellStuds),
                (float)Math.Floor(position.Z / CellStuds));
        }

        /// <summary>Mirror <c>Terrain:CellCenterToWorld(x, y, z)</c>.</summary>
        public static RbxVector3 CellCenterToWorld(int x, int y, int z)
        {
            return new RbxVector3(
                (float)((x + 0.5d) * CellStuds), (float)((y + 0.5d) * CellStuds), (float)((z + 0.5d) * CellStuds));
        }

        /// <summary>Mirror <c>Terrain:CellCornerToWorld(x, y, z)</c>: the cell's minimum corner.</summary>
        public static RbxVector3 CellCornerToWorld(int x, int y, int z)
        {
            return new RbxVector3((float)(x * CellStuds), (float)(y * CellStuds), (float)(z * CellStuds));
        }

        /// <summary>
        /// The slot of an <c>Enum.Material</c> item for a Terrain call, or BAD_ARGUMENT naming the
        /// Terrain materials when the item is not one (Plastic, Neon, Glass, ...).
        /// </summary>
        public static byte RequireTerrainMaterial(string materialName, int materialValue, string member,
            int argumentNumber)
        {
            if (TerrainMaterials.TryGetSlot(materialValue, out byte slot)
                && string.Equals(TerrainMaterials.GetName(slot), materialName, StringComparison.Ordinal))
            {
                return slot;
            }

            throw RbxError.BadArgument(
                member + " argument " + argumentNumber + ": Enum.Material." + materialName
                + " is not a Terrain material",
                "use a Terrain material: " + TerrainMaterials.DescribeAccepted());
        }

        private void ThrowIfDestroyedTerrain(string member)
        {
            if (IsDestroyed)
            {
                throw RbxError.InstanceDestroyed(member, Name, Id);
            }
        }

        private static void RequireFinite(RbxVector3 value, string member, string what, int argumentNumber)
        {
            if (!IsFinite(value))
            {
                throw RbxError.BadArgument(
                    member + " argument " + argumentNumber + ": " + what + " is not finite (" + value + ")",
                    "pass a CFrame built from finite numbers");
            }
        }

        private static bool IsFinite(RbxVector3 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X)
                   && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
                   && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        }
    }
}
