using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using Random = System.Random;

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

        // WHY (escape 1, finding 6): `float s = RbxSpace.MetersPerStud;` then `x * s;` never
        // matches ScaleArithmetic above — the constant name and the operator are on different
        // lines/tokens. This finds the assignment, then searches only the REST OF THE ENCLOSING
        // BLOCK (see FindAliasedScaleArithmetic) for the alias used next to `*`/`/`, so a same-name
        // parameter of an unrelated helper elsewhere in the file (e.g. a value legitimately read
        // once and threaded through as a parameter) is not mistaken for an alias of this one.
        private static readonly Regex ScaleVariableAssignment = new(
            @"\b([A-Za-z_]\w*)\s*=\s*(?:RbxSpace\s*\.\s*)?(?:MetersPerStud|StudsPerMeter)\s*;",
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

        // WHY (escape 2, finding 6): the raw-scale-literal check stays Runtime-only — outside the
        // engine-free runtime, a bare 0.28 has legitimate unrelated meanings (e.g. an unrelated
        // color/VFX-scale constant in Assets/CoreAI.Demos/QwenDemo/GenieDemo.cs), so widening that
        // heuristic there trades a real signal for noise. The named MetersPerStud/StudsPerMeter
        // symbols are unambiguous, so the arithmetic checks below are safe and widened to every
        // module that references RbxSpace.
        private static IEnumerable<string> ConversionAwareRoots()
        {
            yield return RuntimeRoot;
            yield return Path.Combine(Application.dataPath, "CoreAI.Demos");
            yield return Path.Combine(Application.dataPath, "CoreAiUnity");
        }

        private static IEnumerable<string> ConversionAwareSources()
        {
            foreach (string root in ConversionAwareRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }

        /// <summary>
        /// Finds a variable assigned directly from RbxSpace.MetersPerStud/StudsPerMeter and then
        /// used next to `*`/`/` before its enclosing block closes. Scope is tracked by brace depth
        /// over the whole (comment/string-stripped) file text rather than per line, so the alias can
        /// be used a few statements later — the exact shape ScaleArithmetic's per-line check misses.
        /// </summary>
        private static List<string> FindAliasedScaleArithmetic(string codeOnly, string fileLabel)
        {
            List<string> offenders = new();
            int[] depthAtIndex = new int[codeOnly.Length + 1];
            int depth = 0;
            for (int i = 0; i < codeOnly.Length; i++)
            {
                depthAtIndex[i] = depth;
                if (codeOnly[i] == '{')
                {
                    depth++;
                }
                else if (codeOnly[i] == '}')
                {
                    depth--;
                }
            }

            depthAtIndex[codeOnly.Length] = depth;

            foreach (Match assignment in ScaleVariableAssignment.Matches(codeOnly))
            {
                string name = assignment.Groups[1].Value;
                int declEnd = assignment.Index + assignment.Length;
                int declDepth = depthAtIndex[assignment.Index];

                int scopeEnd = codeOnly.Length;
                for (int i = declEnd; i < codeOnly.Length; i++)
                {
                    if (depthAtIndex[i] < declDepth)
                    {
                        scopeEnd = i;
                        break;
                    }
                }

                string scopeText = codeOnly.Substring(declEnd, scopeEnd - declEnd);
                Regex usage = new(
                    @"(?<![\w.])" + Regex.Escape(name) + @"\s*[*/]|[*/]\s*" + Regex.Escape(name)
                    + @"(?![\w.])");
                Match usageMatch = usage.Match(scopeText);
                if (!usageMatch.Success)
                {
                    continue;
                }

                int offset = declEnd + usageMatch.Index;
                int lineNumber = 1;
                for (int i = 0; i < offset && i < codeOnly.Length; i++)
                {
                    if (codeOnly[i] == '\n')
                    {
                        lineNumber++;
                    }
                }

                offenders.Add(
                    $"{fileLabel}:{lineNumber}: '{name}' aliases RbxSpace.MetersPerStud/StudsPerMeter "
                    + "and is used in arithmetic before its scope ends");
            }

            return offenders;
        }

        private static bool IsRobloxSpaceFile(string path)
        {
            return Path.GetFileName(path) == "RbxSpace.cs";
        }

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
            List<string> offenders = new();
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
            // WHY scope: Assets/CoreAI.Demos and Assets/CoreAiUnity both reference RbxSpace too
            // (finding 6, escape 2) — Runtime-only scanning let a demo or CoreAiUnity file
            // multiply/divide by the named constant directly with no test ever seeing it.
            List<string> offenders = new();
            foreach (string file in ConversionAwareSources())
            {
                if (IsRobloxSpaceFile(file))
                {
                    continue;
                }

                string codeOnly = CodeOnly(File.ReadAllText(file));
                foreach (string line in codeOnly.Split('\n'))
                {
                    if (ScaleArithmetic.IsMatch(line))
                    {
                        offenders.Add($"{file}: {line.Trim()}");
                    }
                }

                offenders.AddRange(FindAliasedScaleArithmetic(codeOnly, file));
            }

            Assert.IsEmpty(offenders,
                "stud<->meter arithmetic outside RbxSpace.cs bypasses the single conversion "
                + "boundary (D2) — call RbxSpace.*ToUnity/*FromUnity instead:\n"
                + string.Join("\n", offenders));
        }

        [Test]
        public void Lint_ConversionAwareScopeCoversDemosAndCoreAiUnity()
        {
            // WHY: proves the widened scope (escape 2) actually reaches real files in both extra
            // module trees, rather than the roots silently resolving to nothing (e.g. a typo'd
            // folder name that Directory.Exists quietly skips).
            string demosRoot = Path.Combine(Application.dataPath, "CoreAI.Demos");
            string unityRoot = Path.Combine(Application.dataPath, "CoreAiUnity");
            Assert.IsTrue(Directory.Exists(demosRoot), $"expected demos root at {demosRoot}");
            Assert.IsTrue(Directory.Exists(unityRoot), $"expected CoreAiUnity root at {unityRoot}");
            Assert.IsNotEmpty(Directory.GetFiles(demosRoot, "*.cs", SearchOption.AllDirectories));
            Assert.IsNotEmpty(Directory.GetFiles(unityRoot, "*.cs", SearchOption.AllDirectories));

            List<string> scannedRoots = new(ConversionAwareRoots());
            CollectionAssert.Contains(scannedRoots, RuntimeRoot);
            CollectionAssert.Contains(scannedRoots, demosRoot);
            CollectionAssert.Contains(scannedRoots, unityRoot);
        }

        [Test]
        public void Lint_FlagsScaleAssignedToVariableThenUsedInArithmetic()
        {
            // WHY: proves escape 1 is actually closed by running the detector against a synthetic
            // violating snippet, rather than trusting that "no real file matches" means it works.
            const string violating =
                "public float Convert(float x)\n"
                + "{\n"
                + "    float s = RbxSpace.MetersPerStud;\n"
                + "    return x * s;\n"
                + "}\n";

            List<string> offenders = FindAliasedScaleArithmetic(CodeOnly(violating), "synthetic.cs");

            Assert.IsNotEmpty(offenders,
                "the alias detector must flag 'float s = RbxSpace.MetersPerStud; ... x * s;'");
        }

        [Test]
        public void Lint_DoesNotFlagScaleAliasReadOutsideItsOwnScope()
        {
            // WHY: a value read once from RbxSpace and threaded as a parameter into a different,
            // unrelated method that happens to reuse the same parameter name (RbxTextureMaterialProvider's
            // real ComputeTextureScale(..., metersPerStud) shape) must not trip the detector — the
            // alias never leaves the method it was declared in.
            const string benign =
                "private static float Helper(float tileWidthStuds, float metersPerStud)\n"
                + "{\n"
                + "    return 1f / (tileWidthStuds * metersPerStud);\n"
                + "}\n"
                + "\n"
                + "private static void Caller()\n"
                + "{\n"
                + "    float metersPerStud = RbxSpace.MetersPerStud;\n"
                + "    Helper(4f, metersPerStud);\n"
                + "}\n";

            List<string> offenders = FindAliasedScaleArithmetic(CodeOnly(benign), "synthetic.cs");

            Assert.IsEmpty(offenders,
                "a same-named parameter in a different method must not be mistaken for the alias:\n"
                + string.Join("\n", offenders));
        }

        [Test]
        public void Lint_DoesNotFlagScaleReadUsedOnlyInComparison()
        {
            // WHY: reading the constant (defaults, tooltips, equality checks) stays legal — only
            // arithmetic on the alias should trip the detector.
            const string benign =
                "public bool IsDefaultScale(float s)\n"
                + "{\n"
                + "    float expected = RbxSpace.MetersPerStud;\n"
                + "    return s == expected;\n"
                + "}\n";

            List<string> offenders = FindAliasedScaleArithmetic(CodeOnly(benign), "synthetic.cs");

            Assert.IsEmpty(offenders,
                "an equality comparison against the alias is a legal read, not arithmetic:\n"
                + string.Join("\n", offenders));
        }

        [Test]
        public void Lint_BinderOutput_IsExactlyRobloxSpaceOutput()
        {
            // WHY: the semantic half of the lint — for a spread of poses/sizes the GameObject
            // the binder produces must equal RbxSpace's own numbers exactly, proving the
            // binder delegates instead of re-deriving (a hand-rolled copy would drift here).
            RbxSpace.ResetForTests(0.28f);
            GameObject root = new("LintRoot");
            try
            {
                InstanceGameObjectBinder binder = new(root.transform);
                InstanceRegistry registry = new(null, binder);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                RbxInstance part = registry.Create("Part");
                part.Parent = registry.WorldRoot;
                Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject partGo));

                Random rng = new(58);
                for (int i = 0; i < 50; i++)
                {
                    RbxCFrame cf = RandomCFrame(rng);
                    RbxVector3 size = new(NextExtent(rng), NextExtent(rng), NextExtent(rng));
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

        private static float NextCoord(Random rng)
        {
            return (float)(rng.NextDouble() * 500.0 - 250.0);
        }

        private static float NextExtent(Random rng)
        {
            return (float)(rng.NextDouble() * 64.0 + 0.05);
        }

        private static float NextAngle(Random rng)
        {
            return (float)(rng.NextDouble() * 720.0 - 360.0);
        }

        private static RbxCFrame RandomCFrame(Random rng)
        {
            RbxCFrame rotation = RbxCFrame.FromEulerAnglesXYZ(
                NextAngle(rng) * Mathf.Deg2Rad,
                NextAngle(rng) * Mathf.Deg2Rad,
                NextAngle(rng) * Mathf.Deg2Rad);
            return rotation + new RbxVector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
        }
    }
}
