using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP1 conversion lint (§5.1.8 item 10, "usage lint clean"): RbxSpace is THE single
    /// stud/meter/chirality boundary. Complements the engine-reference fitness test
    /// (RbxDatatypesFitnessEditModeTests) with a source-level scan — no raw scale literal
    /// and no MetersPerStud/StudsPerMeter arithmetic anywhere outside RbxSpace.cs — plus a
    /// semantic check that the binder's output is bit-for-bit RbxSpace's output.
    /// </summary>
    [TestFixture]
    public sealed class Mvp1ConversionLintEditModeTests
    {
        // WHY: 0.28 and its inverse 3.5714… are the numerals a shortcut conversion would use;
        // any occurrence in code (not comments/strings) outside RbxSpace.cs is a second
        // conversion site — the design's primary failure mode (D2/D3).
        private static readonly Regex RawScaleLiteral = new(
            @"(?<![\w.])(0\.28|3\.5714\d*)f?(?![\w.])", RegexOptions.Compiled);

        // WHY: multiplying/dividing by the scale outside the adapter re-implements the boundary
        // even when the named constant is used. Reading the constant (defaults, tooltips,
        // equality checks) stays legal; arithmetic does not.
        private static readonly Regex ScaleArithmetic = new(
            @"(MetersPerStud|StudsPerMeter)\s*[*/]|[*/]\s*(RbxSpace\s*\.\s*)?(Default)?(MetersPerStud|StudsPerMeter)",
            RegexOptions.Compiled);

        private static readonly Regex BlockCommentRegex = new(
            @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex LineCommentRegex = new(@"//[^\r\n]*", RegexOptions.Compiled);

        private static readonly Regex StringLiteralRegex = new(
            "\"(\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);

        private static string RuntimeRoot =>
            Path.Combine(Application.dataPath, "CoreAIMods", "Runtime");

        private static IEnumerable<string> RuntimeSources()
        {
            // WHY: the whole CoreAIMods runtime is in scope — the Lua binding layer
            // (Runtime/Scripting) marshals spatial values too, not just Runtime/RbxApi.
            return Directory.GetFiles(RuntimeRoot, "*.cs", SearchOption.AllDirectories);
        }

        private static bool IsRobloxSpaceFile(string path) =>
            Path.GetFileName(path) == "RbxSpace.cs";

        private static string CodeOnly(string source)
        {
            source = BlockCommentRegex.Replace(source, " ");
            source = StringLiteralRegex.Replace(source, "\"\"");
            return LineCommentRegex.Replace(source, " ");
        }

        [Test]
        public void Lint_NoRawScaleLiteralOutsideRobloxSpace()
        {
            Assert.IsTrue(Directory.Exists(RuntimeRoot), $"runtime root not found: {RuntimeRoot}");
            var offenders = new List<string>();
            foreach (string file in RuntimeSources())
            {
                if (IsRobloxSpaceFile(file))
                {
                    continue;
                }

                foreach (string line in CodeOnly(File.ReadAllText(file)).Split('\n'))
                {
                    if (RawScaleLiteral.IsMatch(line))
                    {
                        offenders.Add($"{file}: {line.Trim()}");
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "raw stud-scale literals outside RbxSpace.cs are a second conversion site "
                + "(D3 — the scale lives ONLY in the RbxSpace constant):\n"
                + string.Join("\n", offenders));
        }

        [Test]
        public void Lint_NoScaleArithmeticOutsideRobloxSpace()
        {
            var offenders = new List<string>();
            foreach (string file in RuntimeSources())
            {
                if (IsRobloxSpaceFile(file))
                {
                    continue;
                }

                foreach (string line in CodeOnly(File.ReadAllText(file)).Split('\n'))
                {
                    if (ScaleArithmetic.IsMatch(line))
                    {
                        offenders.Add($"{file}: {line.Trim()}");
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "stud<->meter arithmetic outside RbxSpace.cs bypasses the single conversion "
                + "boundary (D2) — call RbxSpace.*ToUnity/*FromUnity instead:\n"
                + string.Join("\n", offenders));
        }

        [Test]
        public void Lint_BinderOutput_IsExactlyRobloxSpaceOutput()
        {
            // WHY: the semantic half of the lint — for a spread of poses/sizes the GameObject
            // the binder produces must equal RbxSpace's own numbers exactly, proving the
            // binder delegates instead of re-deriving (a hand-rolled copy would drift here).
            RbxSpace.ResetForTests(0.28f);
            var root = new GameObject("LintRoot");
            try
            {
                var binder = new InstanceGameObjectBinder(root.transform);
                var registry = new InstanceRegistry(null, binder);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                RbxInstance part = registry.Create("Part");
                part.Parent = registry.WorldRoot;
                Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject partGo));

                var rng = new System.Random(58);
                for (int i = 0; i < 50; i++)
                {
                    RbxCFrame cf = RandomCFrame(rng);
                    var size = new RbxVector3(NextExtent(rng), NextExtent(rng), NextExtent(rng));
                    binder.SetCFrame(part.Id, cf);
                    binder.SetSize(part.Id, size);

                    (Vector3 expectedPos, Quaternion expectedRot) = RbxSpace.ToUnityPose(cf);
                    Vector3 expectedScale = RbxSpace.SizeToUnity(size);

                    Assert.Less((partGo.transform.position - expectedPos).magnitude, 1e-4f,
                        $"iteration {i}: binder position diverged from RbxSpace");
                    Assert.Less(Quaternion.Angle(partGo.transform.rotation, expectedRot), 0.01f,
                        $"iteration {i}: binder rotation diverged from RbxSpace");
                    Assert.Less((partGo.transform.localScale - expectedScale).magnitude, 1e-4f,
                        $"iteration {i}: binder scale diverged from RbxSpace");
                }

                game.Destroy();
            }
            finally
            {
                Object.DestroyImmediate(root);
                RbxSpace.ResetForTests();
            }
        }

        private static float NextCoord(System.Random rng) =>
            (float)(rng.NextDouble() * 500.0 - 250.0);

        private static float NextExtent(System.Random rng) =>
            (float)(rng.NextDouble() * 64.0 + 0.05);

        private static float NextAngle(System.Random rng) =>
            (float)(rng.NextDouble() * 720.0 - 360.0);

        private static RbxCFrame RandomCFrame(System.Random rng)
        {
            RbxCFrame rotation = RbxCFrame.FromEulerAnglesXYZ(
                NextAngle(rng) * Mathf.Deg2Rad,
                NextAngle(rng) * Mathf.Deg2Rad,
                NextAngle(rng) * Mathf.Deg2Rad);
            return rotation + new RbxVector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
        }
    }
}
