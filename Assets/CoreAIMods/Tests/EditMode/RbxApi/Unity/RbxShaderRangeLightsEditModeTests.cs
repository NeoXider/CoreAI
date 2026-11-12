using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards the range-shaped point and spot lights on every lit Rbx shader: each lights through
    /// <c>RbxFragmentPBR</c> from <c>RbxRangeLights.hlsl</c>, and none calls URP's <c>UniversalFragmentPBR</c>
    /// directly, whose inverse square leaves a torch's Range dark past the first block.
    /// </summary>
    public sealed class RbxShaderRangeLightsEditModeTests
    {
        private const string ShaderRoot = "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials";

        [Test]
        public void EveryLitRbxShader_ShadesPointLightsOutToTheirRange()
        {
            List<string> lit = new();
            List<string> offenders = new();
            foreach (string path in Directory.GetFiles(ShaderRoot, "*.shader", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(path);
                string name = Path.GetFileName(path);
                if (source.Contains("UniversalFragmentPBR(", StringComparison.Ordinal))
                {
                    offenders.Add(name + " calls UniversalFragmentPBR");
                }

                if (!source.Contains("RbxFragmentPBR(", StringComparison.Ordinal))
                {
                    continue;
                }

                lit.Add(name);
                if (!source.Contains("#include \"RbxRangeLights.hlsl\"", StringComparison.Ordinal))
                {
                    offenders.Add(name + " calls RbxFragmentPBR without including RbxRangeLights.hlsl");
                }
            }

            lit.Sort();
            CollectionAssert.AreEquivalent(new[]
            {
                "RbxProceduralSurface.shader", "RbxProceduralTransparent.shader", "RbxTexturedSurface.shader",
                "RbxWater.shader"
            }, lit, "the lit Rbx shaders");
            Assert.IsEmpty(offenders, string.Join("\n", offenders));
        }
    }
}
