#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>Bakes renderer overrides into content-addressed materials shared by a model's castles.</summary>
    public static class BenchmarkCastleMaterialAssets
    {
        public static void Bake(GameObject root, string materialDirectory)
        {
            EnsureAssetFolder(materialDirectory);
            Dictionary<string, Material> palette = new(StringComparer.Ordinal);
            Dictionary<Texture, Texture> savedTextures = new();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                Material[] materials = renderer.sharedMaterials;
                for (int index = 0; index < materials.Length; index++)
                {
                    Material source = materials[index];
                    Shader shader = source != null ? source.shader :
                        Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    Material baked = source != null ? new Material(source) : new Material(shader);
                    ApplyOverrides(baked, block);
                    PersistTextures(baked, materialDirectory, savedTextures);
                    string key = DedupKey(baked);
                    string filename = "m_" + key + ".mat";
                    string path = materialDirectory + "/" + filename;
                    palette.TryGetValue(key, out Material shared);
                    shared = shared != null ? shared : AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (shared == null && AssetDatabase.IsValidFolder("Assets/Benchmark/Published"))
                    {
                        string model = Path.GetFileName(Path.GetDirectoryName(materialDirectory));
                        foreach (string guid in AssetDatabase.FindAssets("m_" + key + " t:Material",
                                     new[] { "Assets/Benchmark/Published" }))
                        {
                            string published = AssetDatabase.GUIDToAssetPath(guid);
                            if (published.EndsWith("/" + model + "/Materials/" + filename, StringComparison.Ordinal))
                            {
                                shared = AssetDatabase.LoadAssetAtPath<Material>(published);
                                break;
                            }
                        }
                    }
                    if (shared == null)
                    {
                        AssetDatabase.CreateAsset(baked, path);
                        shared = baked;
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(baked);
                    }

                    palette[key] = shared;
                    materials[index] = shared;
                }

                renderer.sharedMaterials = materials;
                renderer.SetPropertyBlock(null);
            }
        }

        /// <summary>Creates only the required asset folders without a project-wide refresh.</summary>
        public static void EnsureAssetFolder(string path)
        {
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            string[] components = normalized.Split('/');
            if (components.Length == 0 || components[0] != "Assets")
            {
                throw new ArgumentException("Expected an asset folder below Assets.", nameof(path));
            }

            string current = "Assets";
            for (int index = 1; index < components.Length; index++)
            {
                string next = current + "/" + components[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (Directory.Exists(next)) AssetDatabase.ImportAsset(next, ImportAssetOptions.ForceSynchronousImport);
                    else AssetDatabase.CreateFolder(current, components[index]);
                    if (!AssetDatabase.IsValidFolder(next)) throw new IOException("Cannot create asset folder " + next + ".");
                }

                current = next;
            }
        }

        private static void PersistTextures(Material material, string directory, Dictionary<Texture, Texture> saved)
        {
            Shader shader = material.shader;
            for (int index = 0; index < shader.GetPropertyCount(); index++)
            {
                if (shader.GetPropertyType(index) != ShaderPropertyType.Texture) continue;
                string property = shader.GetPropertyName(index);
                Texture texture = material.GetTexture(property);
                if (texture == null || AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string _, out long _)) continue;
                if (!saved.TryGetValue(texture, out Texture persistent))
                {
                    EnsureAssetFolder(directory + "/Textures");
                    if (texture is RenderTexture renderTexture)
                    {
                        RenderTexture previous = RenderTexture.active;
                        Texture2D snapshot = new(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
                        try
                        {
                            RenderTexture.active = renderTexture;
                            snapshot.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                            snapshot.Apply();
                        }
                        finally
                        {
                            RenderTexture.active = previous;
                        }

                        snapshot.wrapMode = texture.wrapMode;
                        snapshot.filterMode = texture.filterMode;
                        snapshot.anisoLevel = texture.anisoLevel;
                        persistent = snapshot;
                    }
                    else
                    {
                        persistent = UnityEngine.Object.Instantiate(texture);
                    }

                    persistent.name = texture.name;
                    persistent.hideFlags = HideFlags.None;
                    AssetDatabase.CreateAsset(persistent, directory + "/Textures/t_" + Guid.NewGuid().ToString("N") + ".asset");
                    saved.Add(texture, persistent);
                }

                material.SetTexture(property, persistent);
            }
        }

        private static void ApplyOverrides(Material material, MaterialPropertyBlock block)
        {
            Shader shader = material.shader;
            for (int index = 0; index < shader.GetPropertyCount(); index++)
            {
                int id = shader.GetPropertyNameId(index);
                if (!block.HasProperty(id))
                {
                    continue;
                }

                switch (shader.GetPropertyType(index))
                {
                    case ShaderPropertyType.Color: material.SetColor(id, block.GetColor(id)); break;
                    case ShaderPropertyType.Vector: material.SetVector(id, block.GetVector(id)); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: material.SetFloat(id, block.GetFloat(id)); break;
                    case ShaderPropertyType.Int: material.SetInteger(id, block.GetInt(id)); break;
                    case ShaderPropertyType.Texture: material.SetTexture(id, block.GetTexture(id)); break;
                }
            }
        }

        /// <summary>Includes shader, exact colors, textures, properties and rendering settings; excludes asset names.</summary>
        public static string DedupKey(Material material)
        {
            StringBuilder identity = new();
            Shader shader = material.shader;
            identity.Append(shader.name).Append('|').Append(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader)));
            identity.Append('|').Append(material.renderQueue).Append('|').Append(material.enableInstancing)
                .Append('|').Append(material.doubleSidedGI).Append('|').Append((int)material.globalIlluminationFlags);
            string[] keywords = material.shaderKeywords;
            Array.Sort(keywords, StringComparer.Ordinal);
            foreach (string keyword in keywords)
            {
                identity.Append('|').Append(keyword);
            }

            for (int index = 0; index < shader.GetPropertyCount(); index++)
            {
                string name = shader.GetPropertyName(index);
                identity.Append('|').Append(name).Append('=');
                switch (shader.GetPropertyType(index))
                {
                    case ShaderPropertyType.Color: Append(identity, material.GetColor(name)); break;
                    case ShaderPropertyType.Vector: Append(identity, material.GetVector(name)); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: identity.Append(material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture)); break;
                    case ShaderPropertyType.Int: identity.Append(material.GetInteger(name)); break;
                    case ShaderPropertyType.Texture:
                        Texture texture = material.GetTexture(name);
                        if (texture != null)
                        {
                            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string guid, out long localId))
                            {
                                identity.Append(guid).Append(':').Append(localId);
                            }
                            else
                            {
                                throw new InvalidOperationException("Texture must be persisted before computing a material deduplication key.");
                            }
                        }

                        Append(identity, material.GetTextureScale(name));
                        Append(identity, material.GetTextureOffset(name));
                        break;
                }
            }

            return HashIdentity(identity.ToString());
        }

        /// <summary>Stable filename scope prevents two runs or target labels from sharing one prefab.</summary>
        public static string ArtifactScope(string artifactId) => HashIdentity(artifactId).Substring(0, 16);

        private static string HashIdentity(string identity)
        {
            using SHA256 hash = SHA256.Create();
            byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(identity));
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }

        private static void Append(StringBuilder target, Vector4 value)
        {
            target.Append(value.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(value.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(value.z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(value.w.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
    }
    /// <summary>Fast asset tests for stable visual keys and material reuse across separate castle saves.</summary>
    public sealed class BenchmarkCastleMaterialAssetsTests
    {
        [NUnit.Framework.Test]
        public void PrefabArtifactScope_IsStableAndSeparatesRunsAndTargets()
        {
            string original = BenchmarkCastleMaterialAssets.ArtifactScope("BENCHMARK_run1_model__target1");
            NUnit.Framework.Assert.AreEqual(original,
                BenchmarkCastleMaterialAssets.ArtifactScope("BENCHMARK_run1_model__target1"));
            NUnit.Framework.Assert.AreNotEqual(original,
                BenchmarkCastleMaterialAssets.ArtifactScope("BENCHMARK_run2_model__target1"));
            NUnit.Framework.Assert.AreNotEqual(original,
                BenchmarkCastleMaterialAssets.ArtifactScope("BENCHMARK_run1_model__target2"));
        }

        [NUnit.Framework.Test]
        public void DedupKey_PreservesAlphaAndPropertiesButIgnoresNames()
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            Material first = new(shader) { color = new Color(0.25f, 0.5f, 0.75f, 1f), name = "first" };
            Material second = new(first) { name = "second" };
            try
            {
                NUnit.Framework.Assert.AreEqual(BenchmarkCastleMaterialAssets.DedupKey(first),
                    BenchmarkCastleMaterialAssets.DedupKey(second));
                second.color = new Color(0.25f, 0.5f, 0.75f, 0.5f);
                NUnit.Framework.Assert.AreNotEqual(BenchmarkCastleMaterialAssets.DedupKey(first),
                    BenchmarkCastleMaterialAssets.DedupKey(second));
                second.color = first.color;
                second.renderQueue = first.renderQueue + 1;
                NUnit.Framework.Assert.AreNotEqual(BenchmarkCastleMaterialAssets.DedupKey(first),
                    BenchmarkCastleMaterialAssets.DedupKey(second));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [NUnit.Framework.Test]
        public void Bake_RuntimeTextureIsPersistedAndSharedByIdenticalParts()
        {
            string directory = "Assets/__BenchmarkTextureTest_" + Guid.NewGuid().ToString("N");
            GameObject root = new("castle");
            Texture2D texture = new(2, 2);
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            Material material = new(shader) { mainTexture = texture };
            try
            {
                texture.SetPixel(0, 0, Color.magenta);
                texture.Apply();
                GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
                GameObject second = GameObject.CreatePrimitive(PrimitiveType.Cube);
                first.transform.SetParent(root.transform);
                second.transform.SetParent(root.transform);
                first.GetComponent<Renderer>().sharedMaterial = material;
                second.GetComponent<Renderer>().sharedMaterial = material;
                BenchmarkCastleMaterialAssets.Bake(root, directory);
                Material firstBaked = first.GetComponent<Renderer>().sharedMaterial;
                NUnit.Framework.Assert.AreSame(firstBaked, second.GetComponent<Renderer>().sharedMaterial);
                NUnit.Framework.Assert.IsTrue(AssetDatabase.Contains(firstBaked.mainTexture));
                NUnit.Framework.Assert.AreEqual(Color.magenta, ((Texture2D)firstBaked.mainTexture).GetPixel(0, 0));
                NUnit.Framework.Assert.AreEqual(1, Directory.GetFiles(directory + "/Textures", "*.asset").Length);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.DeleteAsset(directory);
            }
        }

        [NUnit.Framework.Test]
        public void Bake_IdenticalPartsAcrossCastlesShareOneMaterialAsset()
        {
            string directory = "Assets/__BenchmarkMaterialsTest_" + Guid.NewGuid().ToString("N");
            BenchmarkCastleMaterialAssets.EnsureAssetFolder(directory);
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                BenchmarkCastleMaterialAssets.Bake(first, directory);
                BenchmarkCastleMaterialAssets.Bake(second, directory);
                NUnit.Framework.Assert.AreSame(first.GetComponent<Renderer>().sharedMaterial,
                    second.GetComponent<Renderer>().sharedMaterial);
                NUnit.Framework.Assert.AreEqual(1, System.IO.Directory.GetFiles(directory, "*.mat").Length);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                AssetDatabase.DeleteAsset(directory);
            }
        }
    }

}
#endif
