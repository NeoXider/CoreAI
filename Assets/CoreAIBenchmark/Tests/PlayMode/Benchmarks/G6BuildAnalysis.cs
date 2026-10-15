#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// One built part as the G6 grader reads it back from the world: shape, material, size and the full
    /// CFrame in Roblox studs, with the rotation in Roblox's row-major <c>GetComponents</c> layout.
    /// </summary>
    internal sealed class G6Part
    {
        public string Name = "";
        public string Shape = "Block";
        public string Material = "Plastic";
        public double SizeX = 4, SizeY = 1, SizeZ = 2;
        public double X, Y, Z;

        /// <summary>R00, R01, R02, R10, ... R22; column j is the part's local axis j in world space.</summary>
        public double[] R = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        public int ColorR = 163, ColorG = 162, ColorB = 165;
        public double Transparency;

        /// <summary>
        /// A part placed like <c>CFrame.new(x, y, z) * CFrame.Angles(rx, ry, rz)</c> with angles in
        /// degrees (Roblox XYZ order: R = Rx * Ry * Rz).
        /// </summary>
        public static G6Part Create(string name, string shape, string material,
            double sizeX, double sizeY, double sizeZ, double x, double y, double z,
            double rxDegrees = 0, double ryDegrees = 0, double rzDegrees = 0)
        {
            double[] rx = RotationX(rxDegrees * Math.PI / 180.0);
            double[] ry = RotationY(ryDegrees * Math.PI / 180.0);
            double[] rz = RotationZ(rzDegrees * Math.PI / 180.0);
            return new G6Part
            {
                Name = name,
                Shape = shape,
                Material = material,
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ,
                X = x,
                Y = y,
                Z = z,
                R = Multiply(Multiply(rx, ry), rz)
            };
        }

        private static double[] RotationX(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new[] { 1, 0, 0, 0, c, -s, 0, s, c };
        }

        private static double[] RotationY(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new[] { c, 0, s, 0, 1, 0, -s, 0, c };
        }

        private static double[] RotationZ(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new[] { c, -s, 0, s, c, 0, 0, 0, 1 };
        }

        private static double[] Multiply(double[] a, double[] b)
        {
            double[] r = new double[9];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
                }
            }

            return r;
        }
    }

    /// <summary>What <see cref="G6BuildAnalysis.Analyze"/> measured; the G6 grade quotes these in its details.</summary>
    internal sealed class G6BuildMetrics
    {
        /// <summary>Parts analysed.</summary>
        public int Parts;

        /// <summary>Parts joined to the ground plane through a chain of touching parts.</summary>
        public int Grounded;

        public readonly List<string> FloatingNames = new();

        /// <summary>Parts identical to an earlier part (shape, size, position and rotation).</summary>
        public int Duplicates;

        public readonly List<string> DuplicateNames = new();

        /// <summary>Block pairs with overlapping faces in one plane, facing the same way (z-fighting).</summary>
        public int ZFightingPairs;

        public int ZFightingParts;
        public readonly List<string> ZFightingExamples = new();

        /// <summary>Parts entirely inside an opaque block, so never seen.</summary>
        public int Buried;

        public readonly List<string> BuriedNames = new();

        /// <summary>Cylinders whose cross-section is not round and Balls whose sides differ.</summary>
        public int Distorted;

        public readonly List<string> DistortedNames = new();

        /// <summary>Parts thinner than 0.05 studs on some axis.</summary>
        public int Degenerate;

        /// <summary>Height of the ground surface (median top over the build volume, clamped to 0..4).</summary>
        public double GroundLevel;

        /// <summary>Plan-view cells (1 sq studs) where something stands 2+ studs above the ground surface.</summary>
        public int FootprintCells;

        /// <summary>5th-95th percentile extent of the footprint along x and z, in studs.</summary>
        public double SpreadX;

        public double SpreadZ;

        /// <summary>Highest top above the ground surface, in studs.</summary>
        public double MaxHeight;

        /// <summary>Separate grounded objects: groups of touching parts, flat base slabs left out.</summary>
        public int SceneObjects;

        /// <summary>Open (unroofed, ground-level) area enclosed by ground-standing walls, in stud².</summary>
        public int CourtyardCells;

        /// <summary>
        /// Closing radius that sealed the enclosure: openings up to twice this wide were bridged; -1 when
        /// no enclosure was found, 0 when the walls are sealed.
        /// </summary>
        public int ClosingRadius = -1;

        /// <summary>Most common crest height of the curtain walls above the ground surface.</summary>
        public double WallTop;

        /// <summary>Compact masses on the curtain rising 3+ studs above its crest.</summary>
        public int Towers;

        /// <summary>Footprint of the largest interior building at least as tall as the walls (0 = none).</summary>
        public int KeepCells;

        public double KeepTop;

        /// <summary>Buildings (16+ sq studs footprint, 4+ studs tall) standing inside the walls, clear of them.</summary>
        public int InteriorBuildings;

        /// <summary>Towers and interior buildings capped mostly by Wedge, CornerWedge or Ball parts.</summary>
        public int RoofedStructures;

        public int RoofParts;
        public int RoofPartsWithRoofMaterial;

        /// <summary>Separate objects standing well inside the walls (the keep counts as one).</summary>
        public int CourtyardObjects;

        /// <summary>Separate objects standing beyond the walls.</summary>
        public int SurroundingObjects;

        public int Floating => Parts - Grounded;
        public double GroundedShare => Parts == 0 ? 0 : (double)Grounded / Parts;
        public bool Enclosed => ClosingRadius >= 0;

        /// <summary>Widest ground-level opening the enclosure needed bridging, in studs (0 when sealed).</summary>
        public int GateWidth => ClosingRadius <= 0 ? 0 : 2 * ClosingRadius;
    }

    /// <summary>One geometry checkpoint of the G6 grade, before the scenario maps it onto the harness grading.</summary>
    internal readonly struct G6Check
    {
        public G6Check(string id, string description, double weight, bool passed, string detail, bool adherence)
        {
            Id = id;
            Description = description;
            Weight = weight;
            Passed = passed;
            Detail = detail;
            Adherence = adherence;
        }

        public string Id { get; }
        public string Description { get; }
        public double Weight { get; }
        public bool Passed { get; }
        public string Detail { get; }

        /// <summary>True for a construction rule the prompt states; false for a composition goal.</summary>
        public bool Adherence { get; }
    }

    /// <summary>
    /// Deterministic geometry analysis of a G6 build: reads every <c>Castle*</c> part back through the
    /// world's Lua surface and measures composition (grounding, spread, enclosure, gate, towers, keep,
    /// roofs, objects in and around the castle) and construction faults (duplicates, z-fighting,
    /// buried, distorted and degenerate parts).
    /// <para>
    /// Parts are treated as their oriented Size boxes, which is what the runtime renders a Ball,
    /// Cylinder or Wedge inside (InstanceGameObjectBinder stretches each mesh to Size). Plan-view
    /// signals come from a 1-stud grid over the build volume (x, z in -64..64): each cell keeps the
    /// highest box top above it and whether a ground-standing solid wall passes through it.
    /// </para>
    /// </summary>
    internal static class G6BuildAnalysis
    {
        /// <summary>Delegate shape of <c>RbxBenchmarkWorld.TryEvaluateLua</c>.</summary>
        internal delegate bool LuaEvaluator(string lua, out string output);

        /// <summary>Parts beyond this many are not read back; the grade notes the cut.</summary>
        internal const int MaxParts = 2000;

        // WHY: the executor clips a chunk's return value at 4000 characters (MaxResultSummaryLength), so
        // the dump pages its records under this budget and the reader asks for the next page.
        internal const int PageBudget = 3500;

        private const string DumpLuaTemplate =
            "local START, BUDGET, PREFIX = __START__, __BUDGET__, '__PREFIX__'\n" +
            "local function num(v)\n" +
            "  local s = string.format('%.3f', v)\n" +
            "  s = string.gsub(s, '0+$', '')\n" +
            "  s = string.gsub(s, '%.$', '')\n" +
            "  if s == '-0' then s = '0' end\n" +
            "  return s\n" +
            "end\n" +
            "local function enum(v) return (string.gsub(tostring(v), '^Enum%.%w+%.', '')) end\n" +
            "local parts = {}\n" +
            "for _, inst in ipairs(workspace:GetDescendants()) do\n" +
            "  if inst:IsA('Part') and string.sub(inst.Name, 1, #PREFIX) == PREFIX then\n" +
            "    parts[#parts + 1] = inst\n" +
            "  end\n" +
            "end\n" +
            "local lines, used, last = {}, 0, START - 1\n" +
            "for i = START, #parts do\n" +
            "  local p = parts[i]\n" +
            "  local s, c, col = p.Size, {p.CFrame:GetComponents()}, p.Color\n" +
            "  local f = {(string.gsub(string.sub(p.Name, 1, 40), '[^%w_]', '_')), enum(p.Shape), enum(p.Material),\n" +
            "    num(s.X), num(s.Y), num(s.Z)}\n" +
            "  for j = 1, 12 do f[#f + 1] = num(c[j]) end\n" +
            "  f[#f + 1] = num(math.floor(col.R * 255 + 0.5))\n" +
            "  f[#f + 1] = num(math.floor(col.G * 255 + 0.5))\n" +
            "  f[#f + 1] = num(math.floor(col.B * 255 + 0.5))\n" +
            "  f[#f + 1] = num(p.Transparency)\n" +
            "  local line = table.concat(f, ',')\n" +
            "  if #lines > 0 and used + #line + 1 > BUDGET then break end\n" +
            "  lines[#lines + 1] = line\n" +
            "  used = used + #line + 1\n" +
            "  last = i\n" +
            "end\n" +
            "return 'G6P|' .. #parts .. '|' .. last .. '\\n' .. table.concat(lines, '\\n')";

        private const int RecordFields = 22;

        /// <summary>The read-back chunk for one page starting at 1-based part index <paramref name="start"/>.</summary>
        internal static string DumpLua(int start, string prefix)
        {
            return DumpLuaTemplate
                .Replace("__START__", start.ToString(CultureInfo.InvariantCulture))
                .Replace("__BUDGET__", PageBudget.ToString(CultureInfo.InvariantCulture))
                .Replace("__PREFIX__", prefix);
        }

        /// <summary>
        /// Parses one page of the dump (<c>G6P|total|last</c>, then one record per line) and appends its
        /// parts. Lines after the declared records are ignored.
        /// </summary>
        internal static bool TryParsePage(string output, int start, List<G6Part> into, out int total, out int last,
            out string error)
        {
            total = 0;
            last = 0;
            error = null;
            if (string.IsNullOrEmpty(output))
            {
                error = "empty read-back";
                return false;
            }

            string[] lines = output.Replace("\r", "").Split('\n');
            string[] header = lines[0].Split('|');
            if (header.Length != 3 || header[0] != "G6P"
                                   || !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                       out total)
                                   || !int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                       out last))
            {
                error = "bad read-back header: " + Clip(lines[0]);
                return false;
            }

            int expected = last - start + 1;
            if (expected < 0 || last > total || lines.Length - 1 < expected)
            {
                error = $"read-back page {start}..{last} of {total} carries {lines.Length - 1} records";
                return false;
            }

            for (int i = 1; i <= expected; i++)
            {
                if (!TryParseRecord(lines[i], out G6Part part))
                {
                    error = "bad read-back record: " + Clip(lines[i]);
                    return false;
                }

                into.Add(part);
            }

            return true;
        }

        /// <summary>
        /// Parses one dump record: name, shape, material, size xyz, the 12 CFrame components, colour rgb
        /// (0..255) and transparency, comma-separated.
        /// </summary>
        internal static bool TryParseRecord(string line, out G6Part part)
        {
            part = null;
            string[] f = (line ?? "").Split(',');
            if (f.Length != RecordFields)
            {
                return false;
            }

            double[] n = new double[RecordFields - 3];
            for (int i = 3; i < RecordFields; i++)
            {
                if (!double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i - 3])
                    || double.IsNaN(n[i - 3]) || double.IsInfinity(n[i - 3]))
                {
                    return false;
                }
            }

            part = new G6Part
            {
                Name = f[0],
                Shape = f[1],
                Material = f[2],
                SizeX = n[0],
                SizeY = n[1],
                SizeZ = n[2],
                X = n[3],
                Y = n[4],
                Z = n[5],
                R = new[] { n[6], n[7], n[8], n[9], n[10], n[11], n[12], n[13], n[14] },
                ColorR = (int)n[15],
                ColorG = (int)n[16],
                ColorB = (int)n[17],
                Transparency = n[18]
            };
            return true;
        }

        /// <summary>
        /// Reads up to <see cref="MaxParts"/> prefixed parts page by page. Returns null and sets
        /// <paramref name="error"/> when a page fails; <paramref name="total"/> is the world's count.
        /// </summary>
        internal static List<G6Part> ReadParts(LuaEvaluator evaluate, string prefix, out int total, out string error)
        {
            List<G6Part> parts = new();
            total = 0;
            error = null;
            int start = 1;
            for (int page = 0; page < MaxParts; page++)
            {
                if (!evaluate(DumpLua(start, prefix), out string output))
                {
                    error = $"read-back chunk failed at part {start}";
                    return null;
                }

                if (!TryParsePage(output, start, parts, out total, out int last, out error))
                {
                    return null;
                }

                if (last >= total || parts.Count >= MaxParts)
                {
                    return parts;
                }

                if (last < start)
                {
                    error = $"read-back made no progress at part {start}";
                    return null;
                }

                start = last + 1;
            }

            return parts;
        }

        // WHY: every threshold below is quoted to the model in the G6 goal (GameCastleScenarioG6); the
        // goal tests pin the wording to these values, so changing one means changing the prompt too.
        internal const double MinGroundedShare = 0.95;
        internal const int MinFootprintArea = 1500;
        internal const double MinSpan = 48;
        internal const double MinRelief = 16;
        internal const int MinSceneObjects = 6;
        internal const int MinGateClosingRadius = 2;
        internal const int MinTowers = 4;
        internal const int MinRoofedStructures = 3;
        internal const int MinCourtyardObjects = 5;
        internal const int MinSurroundingObjects = 4;
        internal const double MaxZFightingShare = 0.10;
        internal const double MaxHiddenShare = 0.05;
        internal const double MaxDistortedShare = 0.05;

        /// <summary>
        /// The geometry checkpoints of a G6 grade. A castle build (<paramref name="castle"/>) is judged on
        /// walls, gate, towers, keep, roofs and what stands in and around the courtyard; a custom-subject
        /// build only on subject-neutral composition (spread, height, separate objects). Both share the
        /// construction rules: grounding, duplicates, z-fighting, hidden and distorted parts.
        /// </summary>
        internal static List<G6Check> Checks(G6BuildMetrics m, bool castle)
        {
            List<G6Check> checks = new();
            bool spread = m.FootprintCells >= MinFootprintArea && m.SpreadX >= MinSpan && m.SpreadZ >= MinSpan;
            checks.Add(new G6Check("spread_out", "spread the build across the area", castle ? 5 : 10, spread,
                $"footprint {m.FootprintCells} sq studs spanning {F(m.SpreadX)} x {F(m.SpreadZ)} studs " +
                $"(needs {MinFootprintArea} sq studs, {F(MinSpan)} x {F(MinSpan)})", false));

            if (castle)
            {
                AddCastleChecks(checks, m);
            }
            else
            {
                checks.Add(new G6Check("vertical_relief", "gave the build real height", 6, m.MaxHeight >= MinRelief,
                    $"tallest point {F(m.MaxHeight)} studs above the ground (needs {F(MinRelief)})", false));
                checks.Add(new G6Check("separate_objects", "composed separate objects rather than one heap", 10,
                    m.SceneObjects >= MinSceneObjects,
                    $"{m.SceneObjects} separate grounded objects (needs {MinSceneObjects})", false));
            }

            double parts = Math.Max(1, m.Parts);
            checks.Add(new G6Check("grounded", "rested every part on the ground or on another part", 7,
                m.Parts > 0 && m.GroundedShare >= MinGroundedShare,
                $"{m.Grounded}/{m.Parts} grounded ({Percent(m.GroundedShare)}, needs {Percent(MinGroundedShare)})" +
                Examples("floating", m.FloatingNames), true));
            checks.Add(new G6Check("no_duplicates", "placed no duplicate parts", 2,
                m.Parts > 0 && m.Duplicates == 0,
                $"{m.Duplicates} parts repeat an earlier part's size and placement" +
                Examples("e.g.", m.DuplicateNames), true));
            checks.Add(new G6Check("no_z_fighting", "avoided flush overlapping faces", 3,
                m.Parts > 0 && m.ZFightingParts <= MaxZFightingShare * parts,
                $"{m.ZFightingParts} parts ({Percent(m.ZFightingParts / parts)}, max {Percent(MaxZFightingShare)}) " +
                $"in {m.ZFightingPairs} flush overlaps" + Examples("e.g.", m.ZFightingExamples), true));
            checks.Add(new G6Check("no_hidden_parts", "left no part hidden or paper-thin", 2,
                m.Parts > 0 && m.Buried + m.Degenerate <= MaxHiddenShare * parts,
                $"{m.Buried} inside an opaque block, {m.Degenerate} thinner than 0.05 studs " +
                $"({Percent((m.Buried + m.Degenerate) / parts)}, max {Percent(MaxHiddenShare)})" +
                Examples("e.g.", m.BuriedNames), true));
            checks.Add(new G6Check("round_shapes", "kept Cylinders and Balls round", 3,
                m.Parts > 0 && m.Distorted <= MaxDistortedShare * parts,
                $"{m.Distorted} squashed ({Percent(m.Distorted / parts)}, max {Percent(MaxDistortedShare)})" +
                Examples("e.g.", m.DistortedNames), true));
            return checks;
        }

        private static void AddCastleChecks(List<G6Check> checks, G6BuildMetrics m)
        {
            checks.Add(new G6Check("castle_enclosure", "enclosed an open courtyard with curtain walls", 10, m.Enclosed,
                m.Enclosed
                    ? $"{m.CourtyardCells} sq studs of open courtyard inside walls cresting at {F(m.WallTop)} studs"
                    : $"no enclosure: {m.CourtyardCells} sq studs of walled open ground (needs {MinCourtyardCells})",
                false));

            string gate = !m.Enclosed ? "no enclosure to pass through"
                : m.ClosingRadius == 0 ? "walls sealed: no opening at ground level"
                : m.ClosingRadius < MinGateClosingRadius ? "only a gap narrower than 3 studs"
                : $"ground-level opening up to {m.GateWidth} studs wide";
            checks.Add(new G6Check("castle_gate", "left a walkable gate through the walls", 8,
                m.Enclosed && m.ClosingRadius >= MinGateClosingRadius, gate, false));

            string towers = m.Enclosed
                ? $"{m.Towers} towers (6x6+ studs, 4+ studs above the {F(m.WallTop)}-stud wall crest)"
                : "no curtain wall to stand on";
            checks.Add(new G6Check("castle_towers", "raised towers above the wall line", 4, m.Towers >= 2, towers,
                false));
            checks.Add(new G6Check("castle_four_towers", $"raised at least {MinTowers} towers", 3,
                m.Towers >= MinTowers, towers, false));

            checks.Add(new G6Check("castle_keep", "built a keep inside the courtyard", 8, m.KeepCells > 0,
                m.KeepCells > 0
                    ? $"keep with a {m.KeepCells} sq studs footprint, {F(m.KeepTop)} studs tall; " +
                      $"{m.InteriorBuildings} structures inside the walls"
                    : $"no free-standing building of {MinKeepCells}+ sq studs as tall as the walls; " +
                      $"{m.InteriorBuildings} structures inside the walls", false));
            checks.Add(new G6Check("castle_roofs", $"roofed at least {MinRoofedStructures} towers or buildings", 5,
                m.RoofedStructures >= MinRoofedStructures,
                $"{m.RoofedStructures} roofed structures; {m.RoofPartsWithRoofMaterial}/{m.RoofParts} roof parts " +
                "in roofing materials", false));
            checks.Add(new G6Check("courtyard_life", "furnished the courtyard", 4,
                m.CourtyardObjects >= MinCourtyardObjects,
                $"{m.CourtyardObjects} separate objects inside the walls (needs {MinCourtyardObjects})", false));
            checks.Add(new G6Check("surroundings", "built a world beyond the walls", 4,
                m.SurroundingObjects >= MinSurroundingObjects,
                $"{m.SurroundingObjects} separate objects beyond the walls (needs {MinSurroundingObjects})", false));
        }

        private static string F(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Percent(double share)
        {
            return (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static string Examples(string label, List<string> names)
        {
            return names.Count == 0 ? "" : $"; {label} {string.Join(", ", names)}";
        }

        private const int Grid = 128;
        private const double GridOrigin = -64.0;

        // WHY: parts placed by hand-typed coordinates often leave hairline gaps; half a stud still reads as
        // touching on screen, while a 0.65-stud gap under a canopy reads as a hovering roof.
        internal const double ContactTolerance = 0.25;
        private const double GroundContactY = 0.5;
        internal const double MinDimension = 0.05;
        private const double RasterEpsilon = 1e-3;
        internal const double RoundTolerance = 0.15;
        private const double MinZFightingArea = 1.0;
        private const int MaxExamples = 4;

        internal const double StructureRise = 2;
        internal const double WallFootRise = 3;
        internal const double WallCrestRise = 5;
        internal const double MinWallThickness = 1.5;
        internal const int MaxClosingRadius = 8;
        internal const int MinCourtyardCells = 400;
        internal const double OpenYardRise = 2.5;
        internal const double RingDepth = 6;
        internal const double TowerRise = 4;
        private const double MinTallRise = 6;
        private const double MinBuildingRise = 4;
        internal const int MinTowerCells = 36;
        private const int MinBuildingCells = 16;
        internal const int MinKeepCells = 36;
        private const double RoofedShare = 0.3;
        private const double BaseSlabHeight = 2;
        private const double BaseSlabArea = 64;
        internal const double OutsideMargin = 3;

        private static readonly HashSet<string> RoofShapes =
            new(StringComparer.Ordinal) { "Wedge", "CornerWedge", "Ball" };

        private static readonly HashSet<string> RoofMaterials = new(StringComparer.Ordinal)
        {
            "ClayRoofTiles", "RoofShingles", "Slate", "WoodPlanks", "Wood", "Metal", "CorrodedMetal",
            "CeramicTiles", "DiamondPlate", "Foil", "Fabric"
        };

        /// <summary>A part as an oriented box with its world AABB.</summary>
        private sealed class Solid
        {
            public int Index;
            public G6Part Part;
            public double Cx, Cy, Cz;
            public readonly double[] Ax = new double[3];
            public readonly double[] Ay = new double[3];
            public readonly double[] Az = new double[3];
            public readonly double[] H = new double[3];
            public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
            public double Thickness;
            public double Length;
            public bool Opaque;

            public double Volume => 8 * H[0] * H[1] * H[2];

            /// <summary>A flat slab covering ground: terrain, floor, moat, road, drawbridge deck.</summary>
            public bool IsBaseSlab => MaxY - MinY <= BaseSlabHeight && (MaxX - MinX) * (MaxZ - MinZ) >= BaseSlabArea;
        }

        /// <summary>The column a box occupies above one grid cell.</summary>
        private readonly struct Column
        {
            public Column(int cell, double low, double high, Solid solid)
            {
                Cell = cell;
                Low = low;
                High = high;
                Solid = solid;
            }

            public int Cell { get; }
            public double Low { get; }
            public double High { get; }
            public Solid Solid { get; }
        }

        /// <summary>Measures a build. <paramref name="parts"/> is the read-back list in world order.</summary>
        internal static G6BuildMetrics Analyze(IReadOnlyList<G6Part> parts)
        {
            G6BuildMetrics m = new() { Parts = parts.Count };
            if (parts.Count == 0)
            {
                return m;
            }

            Solid[] solids = new Solid[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                G6Part p = parts[i];
                solids[i] = ToSolid(i, p);
                if (p.SizeX < MinDimension || p.SizeY < MinDimension || p.SizeZ < MinDimension)
                {
                    m.Degenerate++;
                }

                if (IsDistorted(p))
                {
                    m.Distorted++;
                    AddExample(m.DistortedNames, p.Name);
                }
            }

            List<(int a, int b)> pairs = CandidatePairs(solids, ContactTolerance);
            List<int>[] contacts = new List<int>[solids.Length];
            for (int i = 0; i < contacts.Length; i++)
            {
                contacts[i] = new List<int>();
            }

            foreach ((int a, int b) in pairs)
            {
                if (ObbOverlap(solids[a], solids[b], ContactTolerance))
                {
                    contacts[a].Add(b);
                    contacts[b].Add(a);
                }
            }

            bool[] grounded = MeasureSupport(solids, contacts, m);
            MeasureFaults(solids, pairs, m);
            MeasurePlan(solids, contacts, grounded, m);
            return m;
        }

        /// <summary>
        /// A round shape whose Size is not round: the runtime stretches the mesh to the Size box, so a
        /// Cylinder with Size.Y != Size.Z renders as an oval slab and a Ball with unequal sides as an egg.
        /// </summary>
        internal static bool IsDistorted(G6Part p)
        {
            switch (p.Shape)
            {
                case "Cylinder":
                    return !NearlyEqual(p.SizeY, p.SizeZ);
                case "Ball":
                    return !NearlyEqual(p.SizeX, p.SizeY) || !NearlyEqual(p.SizeY, p.SizeZ)
                                                          || !NearlyEqual(p.SizeX, p.SizeZ);
                default:
                    return false;
            }
        }

        private static bool NearlyEqual(double a, double b)
        {
            double larger = Math.Max(Math.Abs(a), Math.Abs(b));
            return larger <= 0 || Math.Abs(a - b) <= RoundTolerance * larger;
        }

        private static void AddExample(List<string> examples, string name)
        {
            if (examples.Count < MaxExamples)
            {
                examples.Add(name);
            }
        }

        private static Solid ToSolid(int index, G6Part p)
        {
            Solid s = new()
            {
                Index = index,
                Part = p,
                Cx = p.X,
                Cy = p.Y,
                Cz = p.Z,
                Opaque = p.Transparency < 0.5
            };
            double[] r = p.R != null && p.R.Length == 9 ? p.R : new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            for (int j = 0; j < 3; j++)
            {
                s.Ax[j] = r[j];
                s.Ay[j] = r[3 + j];
                s.Az[j] = r[6 + j];
            }

            s.H[0] = Math.Abs(p.SizeX) / 2;
            s.H[1] = Math.Abs(p.SizeY) / 2;
            s.H[2] = Math.Abs(p.SizeZ) / 2;
            double ex = 0, ey = 0, ez = 0;
            for (int j = 0; j < 3; j++)
            {
                ex += Math.Abs(s.Ax[j]) * s.H[j];
                ey += Math.Abs(s.Ay[j]) * s.H[j];
                ez += Math.Abs(s.Az[j]) * s.H[j];
            }

            s.MinX = s.Cx - ex;
            s.MaxX = s.Cx + ex;
            s.MinY = s.Cy - ey;
            s.MaxY = s.Cy + ey;
            s.MinZ = s.Cz - ez;
            s.MaxZ = s.Cz + ez;

            // WHY: a wall's thickness is its thinnest horizontal dimension; for an upright part that is
            // the smaller of the two local axes lying flat, for a tilted one the plan-view extent.
            int up = 0;
            for (int j = 1; j < 3; j++)
            {
                if (Math.Abs(s.Ay[j]) > Math.Abs(s.Ay[up]))
                {
                    up = j;
                }
            }

            bool upright = Math.Abs(s.Ay[up]) > 0.97;
            s.Thickness = upright
                ? 2 * Math.Min(s.H[(up + 1) % 3], s.H[(up + 2) % 3])
                : Math.Min(2 * ex, 2 * ez);
            s.Length = upright
                ? 2 * Math.Max(s.H[(up + 1) % 3], s.H[(up + 2) % 3])
                : Math.Max(2 * ex, 2 * ez);
            return s;
        }

        /// <summary>Pairs whose AABBs overlap within <paramref name="tolerance"/> (sweep along x).</summary>
        private static List<(int a, int b)> CandidatePairs(Solid[] solids, double tolerance)
        {
            int[] order = new int[solids.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => solids[a].MinX.CompareTo(solids[b].MinX));
            List<(int a, int b)> pairs = new();
            for (int oi = 0; oi < order.Length; oi++)
            {
                Solid a = solids[order[oi]];
                for (int oj = oi + 1; oj < order.Length; oj++)
                {
                    Solid b = solids[order[oj]];
                    if (b.MinX > a.MaxX + 2 * tolerance)
                    {
                        break;
                    }

                    if (b.MinY <= a.MaxY + 2 * tolerance && a.MinY <= b.MaxY + 2 * tolerance
                                                         && b.MinZ <= a.MaxZ + 2 * tolerance
                                                         && a.MinZ <= b.MaxZ + 2 * tolerance)
                    {
                        pairs.Add((Math.Min(a.Index, b.Index), Math.Max(a.Index, b.Index)));
                    }
                }
            }

            return pairs;
        }

        private static bool[] MeasureSupport(Solid[] solids, List<int>[] contacts, G6BuildMetrics m)
        {
            // WHY: a part is grounded when a chain of touching parts reaches the ground plane y = 0, so a
            // flag on a pole on a tower counts and a roof hovering above its tower does not.
            bool[] grounded = new bool[solids.Length];
            Queue<int> queue = new();
            foreach (Solid s in solids)
            {
                if (s.MinY <= GroundContactY)
                {
                    grounded[s.Index] = true;
                    queue.Enqueue(s.Index);
                }
            }

            while (queue.Count > 0)
            {
                foreach (int next in contacts[queue.Dequeue()])
                {
                    if (!grounded[next])
                    {
                        grounded[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            for (int i = 0; i < solids.Length; i++)
            {
                if (grounded[i])
                {
                    m.Grounded++;
                }
                else
                {
                    AddExample(m.FloatingNames, solids[i].Part.Name);
                }
            }

            return grounded;
        }

        /// <summary>Separating-axis test of two oriented boxes, each grown by <paramref name="tolerance"/>.</summary>
        private static bool ObbOverlap(Solid a, Solid b, double tolerance)
        {
            double[,] rot = new double[3, 3];
            double[,] abs = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    rot[i, j] = a.Ax[i] * b.Ax[j] + a.Ay[i] * b.Ay[j] + a.Az[i] * b.Az[j];
                    abs[i, j] = Math.Abs(rot[i, j]) + 1e-9;
                }
            }

            double dx = b.Cx - a.Cx, dy = b.Cy - a.Cy, dz = b.Cz - a.Cz;
            double[] t = new double[3];
            double[] ea = new double[3];
            double[] eb = new double[3];
            for (int i = 0; i < 3; i++)
            {
                t[i] = dx * a.Ax[i] + dy * a.Ay[i] + dz * a.Az[i];
                ea[i] = a.H[i] + tolerance;
                eb[i] = b.H[i] + tolerance;
            }

            for (int i = 0; i < 3; i++)
            {
                double rb = eb[0] * abs[i, 0] + eb[1] * abs[i, 1] + eb[2] * abs[i, 2];
                if (Math.Abs(t[i]) > ea[i] + rb)
                {
                    return false;
                }
            }

            for (int j = 0; j < 3; j++)
            {
                double ra = ea[0] * abs[0, j] + ea[1] * abs[1, j] + ea[2] * abs[2, j];
                double d = t[0] * rot[0, j] + t[1] * rot[1, j] + t[2] * rot[2, j];
                if (Math.Abs(d) > ra + eb[j])
                {
                    return false;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                int i1 = (i + 1) % 3, i2 = (i + 2) % 3;
                for (int j = 0; j < 3; j++)
                {
                    int j1 = (j + 1) % 3, j2 = (j + 2) % 3;
                    double ra = ea[i1] * abs[i2, j] + ea[i2] * abs[i1, j];
                    double rb = eb[j1] * abs[i, j2] + eb[j2] * abs[i, j1];
                    double d = t[i2] * rot[i1, j] - t[i1] * rot[i2, j];
                    if (Math.Abs(d) > ra + rb)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void MeasureFaults(Solid[] solids, List<(int a, int b)> pairs, G6BuildMetrics m)
        {
            string[] keys = new string[solids.Length];
            HashSet<string> seen = new(StringComparer.Ordinal);
            bool[] duplicate = new bool[solids.Length];
            foreach (Solid s in solids)
            {
                keys[s.Index] = DuplicateKey(s.Part);
                if (!seen.Add(keys[s.Index]))
                {
                    duplicate[s.Index] = true;
                    m.Duplicates++;
                    AddExample(m.DuplicateNames, s.Part.Name);
                }
            }

            bool[] buried = new bool[solids.Length];
            bool[] fighting = new bool[solids.Length];
            foreach ((int a, int b) in pairs)
            {
                Solid sa = solids[a], sb = solids[b];
                if (keys[a] == keys[b])
                {
                    continue;
                }

                if (!buried[a] && !duplicate[a] && Contains(sb, sa))
                {
                    buried[a] = true;
                }
                else if (!buried[b] && !duplicate[b] && Contains(sa, sb))
                {
                    buried[b] = true;
                }

                if (sa.Part.Shape == "Block" && sb.Part.Shape == "Block"
                                             && CoplanarOverlap(sa, sb) >= MinZFightingArea)
                {
                    m.ZFightingPairs++;
                    fighting[a] = true;
                    fighting[b] = true;
                    AddExample(m.ZFightingExamples, sa.Part.Name + "/" + sb.Part.Name);
                }
            }

            for (int i = 0; i < solids.Length; i++)
            {
                if (buried[i])
                {
                    m.Buried++;
                    AddExample(m.BuriedNames, solids[i].Part.Name);
                }

                if (fighting[i])
                {
                    m.ZFightingParts++;
                }
            }
        }

        private static string DuplicateKey(G6Part p)
        {
            StringBuilder sb = new();
            sb.Append(p.Shape).Append('|');
            foreach (double v in new[] { p.SizeX, p.SizeY, p.SizeZ, p.X, p.Y, p.Z })
            {
                sb.Append(Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture)).Append('|');
            }

            foreach (double v in p.R)
            {
                sb.Append(Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture)).Append('|');
            }

            return sb.ToString();
        }

        /// <summary>True when every corner of <paramref name="inner"/> lies inside the opaque block <paramref name="outer"/>.</summary>
        private static bool Contains(Solid outer, Solid inner)
        {
            if (outer.Part.Shape != "Block" || !outer.Opaque || inner.Volume > outer.Volume)
            {
                return false;
            }

            for (int corner = 0; corner < 8; corner++)
            {
                double sx = (corner & 1) == 0 ? -1 : 1;
                double sy = (corner & 2) == 0 ? -1 : 1;
                double sz = (corner & 4) == 0 ? -1 : 1;
                double px = inner.Cx + sx * inner.H[0] * inner.Ax[0] + sy * inner.H[1] * inner.Ax[1]
                            + sz * inner.H[2] * inner.Ax[2];
                double py = inner.Cy + sx * inner.H[0] * inner.Ay[0] + sy * inner.H[1] * inner.Ay[1]
                            + sz * inner.H[2] * inner.Ay[2];
                double pz = inner.Cz + sx * inner.H[0] * inner.Az[0] + sy * inner.H[1] * inner.Az[1]
                            + sz * inner.H[2] * inner.Az[2];
                double dx = px - outer.Cx, dy = py - outer.Cy, dz = pz - outer.Cz;
                for (int j = 0; j < 3; j++)
                {
                    double local = dx * outer.Ax[j] + dy * outer.Ay[j] + dz * outer.Az[j];
                    if (Math.Abs(local) > outer.H[j] + 0.02)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Largest area (sq studs) over which a face of <paramref name="a"/> and a face of <paramref name="b"/>
        /// lie in one plane facing the same way: the overlap the renderer z-fights on.
        /// </summary>
        private static double CoplanarOverlap(Solid a, Solid b)
        {
            double best = 0;
            for (int ja = 0; ja < 3; ja++)
            {
                double nx = a.Ax[ja], ny = a.Ay[ja], nz = a.Az[ja];
                for (int jb = 0; jb < 3; jb++)
                {
                    double dot = nx * b.Ax[jb] + ny * b.Ay[jb] + nz * b.Az[jb];
                    if (Math.Abs(dot) < 0.9995)
                    {
                        continue;
                    }

                    double ca = nx * a.Cx + ny * a.Cy + nz * a.Cz;
                    double cb = nx * b.Cx + ny * b.Cy + nz * b.Cz;
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        // WHY: undersides facing down are hidden on the ground and never in the hero
                        // camera's view; two walls standing on the same ground share one, harmlessly.
                        if (sign * ny < -0.7 || Math.Abs(ca + sign * a.H[ja] - (cb + sign * b.H[jb])) > 0.02)
                        {
                            continue;
                        }

                        double overlap = AxisOverlap(a, b, (ja + 1) % 3) * AxisOverlap(a, b, (ja + 2) % 3);
                        best = Math.Max(best, overlap);
                    }
                }
            }

            return best;
        }

        /// <summary>Overlap length of the two boxes projected on <paramref name="a"/>'s local axis <paramref name="j"/>.</summary>
        private static double AxisOverlap(Solid a, Solid b, int j)
        {
            double ux = a.Ax[j], uy = a.Ay[j], uz = a.Az[j];
            double ca = ux * a.Cx + uy * a.Cy + uz * a.Cz;
            double cb = ux * b.Cx + uy * b.Cy + uz * b.Cz;
            double rb = 0;
            for (int k = 0; k < 3; k++)
            {
                rb += Math.Abs(ux * b.Ax[k] + uy * b.Ay[k] + uz * b.Az[k]) * b.H[k];
            }

            double lo = Math.Max(ca - a.H[j], cb - rb);
            double hi = Math.Min(ca + a.H[j], cb + rb);
            return Math.Max(0, hi - lo);
        }

        private static void MeasurePlan(Solid[] solids, List<int>[] contacts, bool[] grounded, G6BuildMetrics m)
        {
            int cells = Grid * Grid;
            double[] top = new double[cells];
            int[] topPart = new int[cells];
            for (int c = 0; c < cells; c++)
            {
                topPart[c] = -1;
            }

            List<Column> columns = new();
            foreach (Solid s in solids)
            {
                Rasterize(s, columns);
            }

            foreach (Column column in columns)
            {
                if (column.High > top[column.Cell] || topPart[column.Cell] < 0 && column.High >= top[column.Cell])
                {
                    top[column.Cell] = column.High;
                    topPart[column.Cell] = column.Solid.Index;
                }
            }

            double[] sorted = (double[])top.Clone();
            Array.Sort(sorted);
            double ground = Math.Max(0, Math.Min(4, sorted[cells / 2]));
            m.GroundLevel = ground;

            bool[] wall = new bool[cells];
            foreach (Column column in columns)
            {
                if (IsWallColumn(column, ground))
                {
                    wall[column.Cell] = true;
                }
            }

            List<int> xs = new();
            List<int> zs = new();
            for (int c = 0; c < cells; c++)
            {
                if (top[c] - ground >= StructureRise)
                {
                    m.FootprintCells++;
                    xs.Add(c % Grid);
                    zs.Add(c / Grid);
                    m.MaxHeight = Math.Max(m.MaxHeight, top[c] - ground);
                }
            }

            if (m.FootprintCells > 0)
            {
                xs.Sort();
                zs.Sort();
                m.SpreadX = Percentile(xs, 0.95) - Percentile(xs, 0.05) + 1;
                m.SpreadZ = Percentile(zs, 0.95) - Percentile(zs, 0.05) + 1;
            }

            bool[] eligible = new bool[solids.Length];
            foreach (Solid s in solids)
            {
                eligible[s.Index] = grounded[s.Index] && !s.IsBaseSlab;
            }

            m.SceneObjects = CountObjects(eligible, contacts);
            MeasureEnclosure(solids, contacts, eligible, columns, top, topPart, wall, ground, m);
        }

        // WHY: a curtain wall stands on the ground, is at least 5 studs tall and too thick to be a bar,
        // post or door; lintels and arches start high, so a gate under them stays open.
        private static bool IsWallColumn(Column column, double ground)
        {
            return column.Solid.Opaque && column.Solid.Thickness >= MinWallThickness
                                       && column.Low <= ground + WallFootRise
                                       && column.High >= ground + WallCrestRise;
        }

        private static double Percentile(List<int> sorted, double q)
        {
            return sorted[(int)Math.Round(q * (sorted.Count - 1))];
        }

        /// <summary>Groups of touching parts among the eligible ones.</summary>
        private static int CountObjects(bool[] eligible, List<int>[] contacts)
        {
            bool[] visited = new bool[eligible.Length];
            Stack<int> stack = new();
            int objects = 0;
            for (int start = 0; start < eligible.Length; start++)
            {
                if (!eligible[start] || visited[start])
                {
                    continue;
                }

                objects++;
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    foreach (int next in contacts[stack.Pop()])
                    {
                        if (eligible[next] && !visited[next])
                        {
                            visited[next] = true;
                            stack.Push(next);
                        }
                    }
                }
            }

            return objects;
        }

        /// <summary>Adds the column (y range) the box occupies above every grid-cell centre it covers.</summary>
        private static void Rasterize(Solid s, List<Column> columns)
        {
            int x0 = Math.Max(0, (int)Math.Floor(s.MinX - GridOrigin));
            int x1 = Math.Min(Grid - 1, (int)Math.Floor(s.MaxX - GridOrigin));
            int z0 = Math.Max(0, (int)Math.Floor(s.MinZ - GridOrigin));
            int z1 = Math.Min(Grid - 1, (int)Math.Floor(s.MaxZ - GridOrigin));
            for (int gz = z0; gz <= z1; gz++)
            {
                for (int gx = x0; gx <= x1; gx++)
                {
                    double dx = GridOrigin + gx + 0.5 - s.Cx;
                    double dz = GridOrigin + gz + 0.5 - s.Cz;
                    double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
                    bool hit = true;
                    for (int j = 0; j < 3 && hit; j++)
                    {
                        // WHY: in the box frame the vertical line through the cell centre is q0 + y * u, so
                        // clipping it against each half extent gives the exact height range the box covers
                        // there. The epsilon makes a face lying exactly on a cell centre (an 11-stud tower at
                        // x = 33) land the same way whether its rotation reads back as an exact 0 or as noise.
                        double half = s.H[j] + RasterEpsilon;
                        double q0 = s.Ax[j] * dx - s.Ay[j] * s.Cy + s.Az[j] * dz;
                        double u = s.Ay[j];
                        if (Math.Abs(u) < 1e-9)
                        {
                            hit = Math.Abs(q0) <= half;
                            continue;
                        }

                        double t1 = (-half - q0) / u;
                        double t2 = (half - q0) / u;
                        lo = Math.Max(lo, Math.Min(t1, t2));
                        hi = Math.Min(hi, Math.Max(t1, t2));
                        hit = lo <= hi;
                    }

                    if (hit && !double.IsInfinity(lo) && !double.IsInfinity(hi))
                    {
                        columns.Add(new Column(gz * Grid + gx, lo, hi, s));
                    }
                }
            }
        }

        private static void MeasureEnclosure(Solid[] solids, List<int>[] contacts, bool[] standing,
            List<Column> columns, double[] top, int[] topPart, bool[] wall, double ground, G6BuildMetrics m)
        {
            int cells = Grid * Grid;
            int[] yardCells = new int[MaxClosingRadius + 1];
            bool[][] yards = new bool[MaxClosingRadius + 1][];
            for (int k = 0; k <= MaxClosingRadius; k++)
            {
                // WHY: closing (dilate then erode) by k bridges ground-level openings up to 2k studs wide,
                // so the smallest k that encloses the courtyard measures the gate. Only the flood from the
                // border sees the bridged mask; the courtyard itself is cut by the real walls, so the
                // closing never fills a narrow passage between the keep and the curtain.
                bool[] closed = k == 0 ? wall : Erode(Dilate(wall, k), k);
                bool[] outside = FloodFromBorder(closed);
                bool[] enclosed = new bool[cells];
                for (int c = 0; c < cells; c++)
                {
                    enclosed[c] = !outside[c] && !wall[c];
                }

                yards[k] = ToMask(Largest(Components(enclosed, false)));
                for (int c = 0; c < cells; c++)
                {
                    if (yards[k][c] && top[c] - ground <= OpenYardRise)
                    {
                        yardCells[k]++;
                    }
                }
            }

            m.CourtyardCells = yardCells[MaxClosingRadius];
            if (m.CourtyardCells < MinCourtyardCells)
            {
                return;
            }

            int radius = 0;
            while (yardCells[radius] < 0.8 * m.CourtyardCells)
            {
                radius++;
            }

            m.ClosingRadius = radius;
            bool[] yard = yards[radius];
            bool[] nearYard = Dilate(yard, 1);
            List<int> hullCells = new();
            List<List<int>> yardWalls = new();
            foreach (List<int> component in Components(wall, true))
            {
                if (Any(component, nearYard))
                {
                    yardWalls.Add(component);
                    hullCells.AddRange(component);
                }
            }

            for (int c = 0; c < cells; c++)
            {
                if (yard[c])
                {
                    hullCells.Add(c);
                }
            }

            List<(double x, double z)> hull = ConvexHull(hullCells);
            if (hull.Count < 3)
            {
                return;
            }

            // WHY: depth inside the convex hull of the walled area separates the curtain (near the hull
            // boundary) from a keep or hall standing in the courtyard, even when a wide gap lets the
            // outside reach the keep's face.
            double[] depth = new double[cells];
            for (int c = 0; c < cells; c++)
            {
                depth[c] = SignedDepth(hull, c % Grid + 0.5, c / Grid + 0.5);
            }

            bool[] ring = new bool[cells];
            List<int> ringCells = new();
            foreach (List<int> component in yardWalls)
            {
                foreach (int c in component)
                {
                    if (depth[c] <= RingDepth)
                    {
                        ring[c] = true;
                        ringCells.Add(c);
                    }
                }
            }

            if (ringCells.Count == 0)
            {
                return;
            }

            bool[] curtain = new bool[solids.Length];
            foreach (Column column in columns)
            {
                if (ring[column.Cell] && IsWallColumn(column, ground))
                {
                    curtain[column.Solid.Index] = true;
                }
            }

            m.WallTop = CrestHeight(solids, curtain, ringCells, top) - ground;
            bool[] nearRing = Dilate(ring, 1);
            bool[] tall = new bool[cells];
            for (int c = 0; c < cells; c++)
            {
                tall[c] = top[c] - ground >= Math.Max(m.WallTop + TowerRise, MinTallRise);
            }

            List<List<int>> structures = new();
            foreach (List<int> component in Components(tall, true))
            {
                if (component.Count >= MinTowerCells && Any(component, nearRing))
                {
                    m.Towers++;
                    structures.Add(component);
                }
            }

            bool[] interior = new bool[cells];
            for (int c = 0; c < cells; c++)
            {
                interior[c] = depth[c] > RingDepth + 1 && !nearRing[c] && top[c] - ground >= MinBuildingRise;
            }

            foreach (List<int> component in Components(interior, true))
            {
                if (component.Count < MinBuildingCells)
                {
                    continue;
                }

                m.InteriorBuildings++;
                structures.Add(component);
                double componentTop = 0;
                foreach (int c in component)
                {
                    componentTop = Math.Max(componentTop, top[c] - ground);
                }

                if (component.Count >= MinKeepCells && componentTop >= m.WallTop && component.Count > m.KeepCells)
                {
                    m.KeepCells = component.Count;
                    m.KeepTop = componentTop;
                }
            }

            MeasureRoofs(solids, structures, topPart, m);
            MeasureObjects(solids, contacts, standing, curtain, hull, m);
        }

        private static void MeasureRoofs(Solid[] solids, List<List<int>> structures, int[] topPart, G6BuildMetrics m)
        {
            HashSet<int> roofParts = new();
            foreach (List<int> component in structures)
            {
                int roofCells = 0;
                foreach (int c in component)
                {
                    int p = topPart[c];
                    if (p >= 0 && RoofShapes.Contains(solids[p].Part.Shape))
                    {
                        roofCells++;
                        roofParts.Add(p);
                    }
                }

                if (roofCells >= RoofedShare * component.Count)
                {
                    m.RoofedStructures++;
                }
            }

            m.RoofParts = roofParts.Count;
            foreach (int p in roofParts)
            {
                if (RoofMaterials.Contains(solids[p].Part.Material))
                {
                    m.RoofPartsWithRoofMaterial++;
                }
            }
        }

        /// <summary>
        /// Counts separate objects well inside the walls and beyond them among the grounded, non-slab
        /// parts; curtain parts are left out so a stall leaning on the wall stays its own object.
        /// </summary>
        private static void MeasureObjects(Solid[] solids, List<int>[] contacts, bool[] standing, bool[] curtain,
            List<(double x, double z)> hull, G6BuildMetrics m)
        {
            bool[] inside = new bool[solids.Length];
            bool[] beyond = new bool[solids.Length];
            foreach (Solid s in solids)
            {
                if (!standing[s.Index] || curtain[s.Index])
                {
                    continue;
                }

                double d = SignedDepth(hull, s.Cx - GridOrigin, s.Cz - GridOrigin);
                inside[s.Index] = d > RingDepth;
                beyond[s.Index] = d < -OutsideMargin;
            }

            m.CourtyardObjects = CountObjects(inside, contacts);
            m.SurroundingObjects = CountObjects(beyond, contacts);
        }

        /// <summary>Convex hull (counter-clockwise) of the cell centres, in grid units.</summary>
        private static List<(double x, double z)> ConvexHull(List<int> cellList)
        {
            List<(double x, double z)> points = new();
            HashSet<int> seen = new();
            foreach (int c in cellList)
            {
                if (seen.Add(c))
                {
                    points.Add((c % Grid + 0.5, c / Grid + 0.5));
                }
            }

            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            if (points.Count < 3)
            {
                return points;
            }

            List<(double x, double z)> hull = new();
            for (int pass = 0; pass < 2; pass++)
            {
                int floor = hull.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    (double x, double z) p = pass == 0 ? points[i] : points[points.Count - 1 - i];
                    while (hull.Count >= floor + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }

                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return hull;
        }

        private static double Cross((double x, double z) o, (double x, double z) a, (double x, double z) b)
        {
            return (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);
        }

        /// <summary>Distance from the point to the hull boundary: positive inside, negative outside.</summary>
        private static double SignedDepth(List<(double x, double z)> hull, double x, double z)
        {
            bool inside = true;
            double nearest = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                (double x, double z) a = hull[i];
                (double x, double z) b = hull[(i + 1) % hull.Count];
                if (Cross(a, b, (x, z)) < 0)
                {
                    inside = false;
                }

                double ex = b.x - a.x, ez = b.z - a.z;
                double lengthSquared = ex * ex + ez * ez;
                double t = lengthSquared <= 0 ? 0 : Math.Max(0, Math.Min(1, ((x - a.x) * ex + (z - a.z) * ez) / lengthSquared));
                double px = a.x + t * ex - x, pz = a.z + t * ez - z;
                nearest = Math.Min(nearest, Math.Sqrt(px * px + pz * pz));
            }

            return inside ? nearest : -nearest;
        }

        /// <summary>
        /// Crest height of the curtain: the length-weighted median top of its wall-shaped parts (8+ studs
        /// long, at least three times longer than thick), so towers, merlons and gatehouses on the ring do
        /// not lift it. A curtain without such parts falls back to the median top of its cells.
        /// </summary>
        private static double CrestHeight(Solid[] solids, bool[] curtain, List<int> ringCells, double[] top)
        {
            List<(double top, double weight)> crests = new();
            double total = 0;
            foreach (Solid s in solids)
            {
                if (curtain[s.Index] && s.Length >= 8 && s.Length >= 3 * s.Thickness)
                {
                    crests.Add((s.MaxY, s.Length));
                    total += s.Length;
                }
            }

            if (crests.Count == 0)
            {
                List<double> tops = new();
                foreach (int c in ringCells)
                {
                    tops.Add(top[c]);
                }

                tops.Sort();
                return tops[tops.Count / 2];
            }

            crests.Sort((a, b) => a.top.CompareTo(b.top));
            double running = 0;
            foreach ((double crest, double weight) in crests)
            {
                running += weight;
                if (running >= total / 2)
                {
                    return crest;
                }
            }

            return crests[crests.Count - 1].top;
        }

        private static List<int> Largest(List<List<int>> components)
        {
            List<int> best = new();
            foreach (List<int> component in components)
            {
                if (component.Count > best.Count)
                {
                    best = component;
                }
            }

            return best;
        }

        private static bool[] ToMask(List<int> cellList)
        {
            bool[] mask = new bool[Grid * Grid];
            foreach (int c in cellList)
            {
                mask[c] = true;
            }

            return mask;
        }

        private static bool Any(List<int> component, bool[] mask)
        {
            foreach (int c in component)
            {
                if (mask[c])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Connected components of the set cells, 8-connected when <paramref name="diagonal"/>, else 4-connected.</summary>
        private static List<List<int>> Components(bool[] mask, bool diagonal)
        {
            List<List<int>> result = new();
            bool[] visited = new bool[mask.Length];
            Stack<int> stack = new();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start])
                {
                    continue;
                }

                List<int> component = new();
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    component.Add(c);
                    int cx = c % Grid, cz = c / Grid;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= Grid || nz >= Grid || !diagonal && dx != 0 && dz != 0)
                            {
                                continue;
                            }

                            int n = nz * Grid + nx;
                            if (mask[n] && !visited[n])
                            {
                                visited[n] = true;
                                stack.Push(n);
                            }
                        }
                    }
                }

                result.Add(component);
            }

            return result;
        }

        /// <summary>Cells reachable from the grid border through unblocked cells (4-connected).</summary>
        private static bool[] FloodFromBorder(bool[] blocked)
        {
            bool[] reached = new bool[blocked.Length];
            Queue<int> queue = new();
            for (int i = 0; i < Grid; i++)
            {
                Seed(i, 0);
                Seed(i, Grid - 1);
                Seed(0, i);
                Seed(Grid - 1, i);
            }

            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int cx = c % Grid, cz = c / Grid;
                Seed(cx - 1, cz);
                Seed(cx + 1, cz);
                Seed(cx, cz - 1);
                Seed(cx, cz + 1);
            }

            return reached;

            void Seed(int x, int z)
            {
                if (x < 0 || z < 0 || x >= Grid || z >= Grid)
                {
                    return;
                }

                int c = z * Grid + x;
                if (!blocked[c] && !reached[c])
                {
                    reached[c] = true;
                    queue.Enqueue(c);
                }
            }
        }

        /// <summary>Chebyshev dilation by <paramref name="radius"/> cells (outside the grid counts as empty).</summary>
        private static bool[] Dilate(bool[] mask, int radius)
        {
            return Morph(mask, radius, true);
        }

        /// <summary>Chebyshev erosion by <paramref name="radius"/> cells (outside the grid counts as set).</summary>
        private static bool[] Erode(bool[] mask, int radius)
        {
            return Morph(mask, radius, false);
        }

        private static bool[] Morph(bool[] mask, int radius, bool dilate)
        {
            bool[] rows = new bool[mask.Length];
            bool[] result = new bool[mask.Length];
            for (int z = 0; z < Grid; z++)
            {
                for (int x = 0; x < Grid; x++)
                {
                    rows[z * Grid + x] = Window(mask, x, z, radius, dilate, true);
                }
            }

            for (int z = 0; z < Grid; z++)
            {
                for (int x = 0; x < Grid; x++)
                {
                    result[z * Grid + x] = Window(rows, x, z, radius, dilate, false);
                }
            }

            return result;
        }

        private static bool Window(bool[] mask, int x, int z, int radius, bool dilate, bool alongX)
        {
            for (int d = -radius; d <= radius; d++)
            {
                int nx = alongX ? x + d : x;
                int nz = alongX ? z : z + d;
                bool inside = nx >= 0 && nz >= 0 && nx < Grid && nz < Grid;
                bool value = inside ? mask[nz * Grid + nx] : !dilate;
                if (dilate && value)
                {
                    return true;
                }

                if (!dilate && !value)
                {
                    return false;
                }
            }

            return !dilate;
        }

        private static string Clip(string s)
        {
            return s == null ? "" : s.Length <= 80 ? s : s.Substring(0, 80) + "...";
        }
    }
}
#endif
#endif
