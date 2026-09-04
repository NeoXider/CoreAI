using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards the Forward+ opt-in on every lit Rbx shader.
    /// <para>
    /// WHY: the project renders Forward+ (<c>Assets/Settings/PC_Renderer.asset</c>,
    /// <c>m_RenderingMode: 2</c>), where URP 17 delivers the main light through the clustered light loop.
    /// A pass that calls <c>UniversalFragmentPBR</c> without declaring <c>_CLUSTER_LIGHT_LOOP</c> compiles
    /// the non-clustered variant and receives no main light at all — every Rbx part then renders on
    /// ambient alone, with no sun and no cast shadows, in the editor and in players alike. That shipped
    /// unnoticed for a long time because the result is a plausible-looking flat image rather than an
    /// error. This test makes the omission fail loudly instead.
    /// </para>
    /// </summary>
    public sealed class RbxShaderClusterLightLoopEditModeTests
    {
        private const string ShaderRoot = "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials";
        private const string ClusterKeyword = "_CLUSTER_LIGHT_LOOP";

        // WHY: checked against the installed URP 17.4.0 ShaderLibrary/Lighting.hlsl, which defines
        // exactly UniversalFragmentPBR, UniversalFragmentBlinnPhong and the baked-only
        // UniversalFragmentBakedLit. BakedLit never samples the cluster loop, and
        // UniversalFragmentPBRSimple does not exist in this URP version, so only the first two are watched.
        private static readonly string[] LitEntryPoints =
        {
            "UniversalFragmentPBR",
            "UniversalFragmentBlinnPhong"
        };

        [Test]
        public void EveryLitRbxShader_DeclaresTheClusterLightLoopKeyword()
        {
            Assert.IsTrue(Directory.Exists(ShaderRoot), $"'{ShaderRoot}' is missing");

            List<string> offenders = new();
            string[] shaders = Directory.GetFiles(ShaderRoot, "*.shader", SearchOption.AllDirectories);
            Assert.IsNotEmpty(shaders, "no Rbx shaders found to check");

            foreach (string path in shaders)
            {
                string source = File.ReadAllText(path);
                List<string> blocks = SplitPassBlocks(source);
                for (int index = 0; index < blocks.Count; index++)
                {
                    string block = blocks[index];
                    if (!CallsLightingEntryPoint(block, out string entryPoint))
                    {
                        // Unlit passes (the neon and fallback shaders) never sample the light loop.
                        continue;
                    }

                    if (!block.Contains(ClusterKeyword, StringComparison.Ordinal))
                    {
                        offenders.Add(Path.GetFileName(path) + " Pass #" + (index + 1) +
                            " calls " + entryPoint + " without '#pragma multi_compile _ " +
                            ClusterKeyword + "'");
                    }
                }
            }

            offenders.Sort();
            Assert.IsEmpty(offenders,
                "These shader passes light through a URP lighting entry point but do not declare " +
                "'#pragma multi_compile _ " + ClusterKeyword + "'. Under Forward+ they will render with " +
                "no main light — ambient only, no sun, no shadows:\n" + string.Join("\n", offenders));
        }

        /// <summary>Splits shader source into its Pass blocks; single-line UsePass lines are ignored.</summary>
        private static List<string> SplitPassBlocks(string source)
        {
            List<string> blocks = new();
            int searchFrom = 0;
            while (true)
            {
                int passIndex = IndexOfPassKeyword(source, searchFrom);
                if (passIndex < 0)
                {
                    return blocks;
                }

                int openIndex = source.IndexOf('{', passIndex);
                if (openIndex < 0)
                {
                    return blocks;
                }

                int depth = 0;
                for (int index = openIndex; index < source.Length; index++)
                {
                    if (source[index] == '{')
                    {
                        depth++;
                    }
                    else if (source[index] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            blocks.Add(source.Substring(openIndex, index - openIndex + 1));
                            searchFrom = index + 1;
                            break;
                        }
                    }
                }

                if (depth != 0)
                {
                    return blocks;
                }
            }
        }

        /// <summary>Finds a whole-word Pass keyword, skipping the Pass inside UsePass lines.</summary>
        private static int IndexOfPassKeyword(string source, int start)
        {
            int searchFrom = start;
            while (true)
            {
                int index = source.IndexOf("Pass", searchFrom, StringComparison.Ordinal);
                if (index < 0)
                {
                    return -1;
                }

                if (IsWordBoundary(source, index - 1) && IsWordBoundary(source, index + 4))
                {
                    return index;
                }

                searchFrom = index + 4;
            }
        }

        private static bool IsWordBoundary(string source, int index)
        {
            if (index < 0 || index >= source.Length)
            {
                return true;
            }

            char candidate = source[index];
            return !char.IsLetterOrDigit(candidate) && candidate != '_';
        }

        private static bool CallsLightingEntryPoint(string block, out string entryPoint)
        {
            foreach (string candidate in LitEntryPoints)
            {
                if (block.Contains(candidate, StringComparison.Ordinal))
                {
                    entryPoint = candidate;
                    return true;
                }
            }

            entryPoint = string.Empty;
            return false;
        }
    }
}
