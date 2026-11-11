using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Resolves the images a runtime screen names (<c>background-image: resource("Icons/sword")</c>,
    /// <c>url("sword")</c>, <c>&lt;Image image="sword"/&gt;</c>). Runtime screens cannot load project files,
    /// so a host game (or a mod binding) registers textures and sprites by key; <c>resource()</c> paths
    /// fall back to <see cref="Resources.Load(string)"/>, which works in a built player and on WebGL.
    /// </summary>
    public interface IRuntimeUiAssetResolver
    {
        /// <summary>Resolves an image reference.</summary>
        /// <param name="reference">The path or key as written.</param>
        /// <param name="isResource">True for <c>resource()</c>; false for <c>url()</c> or a bare key.</param>
        /// <param name="background">The image.</param>
        bool TryResolveBackground(string reference, bool isResource, out Background background);
    }

    /// <summary>The default <see cref="IRuntimeUiAssetResolver"/>: a key registry plus a cached Resources fallback.</summary>
    public sealed class RuntimeUiAssetRegistry : IRuntimeUiAssetResolver
    {
        private readonly Dictionary<string, Background> _registered = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Background> _loaded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

        /// <summary>Creates the registry.</summary>
        /// <param name="loadFromResources">When true, unregistered <c>resource()</c> paths and keys try <see cref="Resources.Load(string)"/>.</param>
        public RuntimeUiAssetRegistry(bool loadFromResources = true)
        {
            LoadFromResources = loadFromResources;
        }

        /// <summary>Whether unregistered references try <see cref="Resources.Load(string)"/>.</summary>
        public bool LoadFromResources { get; set; }

        /// <summary>Registers a texture under a key.</summary>
        public void Register(string key, Texture2D texture)
        {
            if (string.IsNullOrWhiteSpace(key) || texture == null)
            {
                return;
            }

            _registered[key.Trim()] = Background.FromTexture2D(texture);
        }

        /// <summary>Registers a sprite under a key.</summary>
        public void Register(string key, Sprite sprite)
        {
            if (string.IsNullOrWhiteSpace(key) || sprite == null)
            {
                return;
            }

            _registered[key.Trim()] = Background.FromSprite(sprite);
        }

        /// <summary>Removes a key.</summary>
        public bool Remove(string key)
        {
            return key != null && _registered.Remove(key.Trim());
        }

        /// <inheritdoc />
        public bool TryResolveBackground(string reference, bool isResource, out Background background)
        {
            background = default;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return false;
            }

            string key = reference.Trim();
            if (_registered.TryGetValue(key, out background))
            {
                return true;
            }

            if (!LoadFromResources)
            {
                return false;
            }

            string path = StripExtension(key);
            if (_loaded.TryGetValue(path, out background))
            {
                return true;
            }

            if (_missing.Contains(path))
            {
                return false;
            }

            Texture2D texture = Resources.Load<Texture2D>(path);
            if (texture != null)
            {
                background = Background.FromTexture2D(texture);
                _loaded[path] = background;
                return true;
            }

            Sprite sprite = Resources.Load<Sprite>(path);
            if (sprite != null)
            {
                background = Background.FromSprite(sprite);
                _loaded[path] = background;
                return true;
            }

            _missing.Add(path);
            return false;
        }

        private static string StripExtension(string path)
        {
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            return dot > slash + 1 ? path.Substring(0, dot) : path;
        }
    }
}
