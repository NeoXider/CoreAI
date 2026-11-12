using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>One sound a host can play: the id it answers to, its length and the host's own clip object.</summary>
    public readonly struct RbxSoundAsset
    {
        public RbxSoundAsset(string id, double lengthSeconds, object clip)
        {
            Id = id ?? string.Empty;
            LengthSeconds = lengthSeconds;
            Clip = clip;
        }

        /// <summary>The key or path the sound was found under.</summary>
        public string Id { get; }

        /// <summary>The clip's length in seconds; always finite and above zero.</summary>
        public double LengthSeconds { get; }

        /// <summary>What the audio backend plays (a Unity <c>AudioClip</c>); null in a headless world.</summary>
        public object Clip { get; }

        /// <summary>False for the default value.</summary>
        public bool IsValid => LengthSeconds > 0d;
    }

    /// <summary>Resolves an id no key was registered for (the Unity host: a <c>Resources</c> path).</summary>
    public delegate bool RbxSoundFallbackResolver(string soundId, out RbxSoundAsset asset);

    /// <summary>
    /// The sounds a host provides, under the same rule as the textures: <c>Sound.SoundId</c> names a key the host
    /// registered here, and an id no key matches goes to the host's fallback (the Unity host loads it as a
    /// <c>Resources</c> path, which works in a built player and on WebGL). A Roblox asset id
    /// (<c>rbxassetid://</c>, <c>rbxasset://</c>, the asset URLs) never reaches the fallback: CoreAI downloads
    /// nothing, so such an id loads only when a host registered that exact id as a key. One library serves every
    /// world a host runs; <see cref="GetIds"/> is what <c>SoundService:GetSoundIds()</c> lists.
    /// </summary>
    public sealed class RbxSoundLibrary
    {
        /// <summary>The longest key or id the library accepts, in characters.</summary>
        public const int MaxIdLength = 260;

        /// <summary>How many fallback answers (found or not) are remembered before the cache starts over.</summary>
        public const int MaxCachedFallbackAnswers = 256;

        private readonly Dictionary<string, RbxSoundAsset> _registered = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RbxSoundAsset> _fallbackHits = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fallbackMisses = new(StringComparer.Ordinal);
        private readonly RbxSoundFallbackResolver _fallback;
        private string[] _sortedIds = Array.Empty<string>();
        private bool _sortedIdsDirty;

        /// <param name="fallback">Resolves an unregistered id; null resolves nothing beyond the registered keys.</param>
        public RbxSoundLibrary(RbxSoundFallbackResolver fallback = null)
        {
            _fallback = fallback;
        }

        /// <summary>Bumped by every registration and removal, so a sound that failed to load tries again.</summary>
        public int Version { get; private set; }

        /// <summary>Registered keys.</summary>
        public int Count => _registered.Count;

        /// <summary>
        /// Registers <paramref name="clip"/> under <paramref name="key"/> (trimmed, case-sensitive); a key already
        /// registered is replaced. A Roblox asset id is accepted as a key, so a host can serve the ids of an
        /// imported place with its own clips.
        /// </summary>
        public void Register(string key, double lengthSeconds, object clip = null)
        {
            string trimmed = RequireId(key, nameof(key));
            if (double.IsNaN(lengthSeconds) || double.IsInfinity(lengthSeconds) || lengthSeconds <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(lengthSeconds), lengthSeconds,
                    "a sound needs a finite length above zero, got "
                    + lengthSeconds.ToString(CultureInfo.InvariantCulture));
            }

            _registered[trimmed] = new RbxSoundAsset(trimmed, lengthSeconds, clip);
            _sortedIdsDirty = true;
            Version++;
        }

        /// <summary>Removes a key; true when it was registered.</summary>
        public bool Remove(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !_registered.Remove(key.Trim()))
            {
                return false;
            }

            _sortedIdsDirty = true;
            Version++;
            return true;
        }

        /// <summary>Forgets every remembered fallback answer (a host that added files under its fallback).</summary>
        public void ClearFallbackCache()
        {
            _fallbackHits.Clear();
            _fallbackMisses.Clear();
            Version++;
        }

        /// <summary>
        /// The sound <paramref name="soundId"/> names: a registered key first, then the fallback for an id that is
        /// not a Roblox asset id. Both answers of the fallback are remembered.
        /// </summary>
        public bool TryResolve(string soundId, out RbxSoundAsset asset)
        {
            asset = default;
            if (string.IsNullOrWhiteSpace(soundId) || soundId.Length > MaxIdLength)
            {
                return false;
            }

            string id = soundId.Trim();
            if (_registered.TryGetValue(id, out asset))
            {
                return true;
            }

            if (_fallback == null || IsRobloxAssetId(id))
            {
                return false;
            }

            if (_fallbackHits.TryGetValue(id, out asset))
            {
                return true;
            }

            if (_fallbackMisses.Contains(id))
            {
                return false;
            }

            if (_fallbackHits.Count + _fallbackMisses.Count >= MaxCachedFallbackAnswers)
            {
                _fallbackHits.Clear();
                _fallbackMisses.Clear();
            }

            if (_fallback(id, out RbxSoundAsset found) && found.IsValid
                                                     && !double.IsInfinity(found.LengthSeconds))
            {
                asset = new RbxSoundAsset(id, found.LengthSeconds, found.Clip);
                _fallbackHits.Add(id, asset);
                return true;
            }

            _fallbackMisses.Add(id);
            asset = default;
            return false;
        }

        /// <summary>
        /// The registered keys in ordinal order. Fallback ids are not listed: a <c>Resources</c> folder cannot be
        /// enumerated in a built player.
        /// </summary>
        public IReadOnlyList<string> GetIds()
        {
            if (_sortedIdsDirty)
            {
                string[] ids = new string[_registered.Count];
                _registered.Keys.CopyTo(ids, 0);
                Array.Sort(ids, StringComparer.Ordinal);
                _sortedIds = ids;
                _sortedIdsDirty = false;
            }

            return _sortedIds;
        }

        /// <summary>True for content ids that point at Roblox's asset service, which CoreAI never downloads.</summary>
        public static bool IsRobloxAssetId(string id)
        {
            return !string.IsNullOrEmpty(id) && RbxGuiImage.IsRobloxAsset(id.Trim());
        }

        private static string RequireId(string id, string parameter)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a sound key cannot be empty", parameter);
            }

            string trimmed = id.Trim();
            if (trimmed.Length > MaxIdLength)
            {
                throw new ArgumentException("a sound key is at most " + MaxIdLength + " characters", parameter);
            }

            return trimmed;
        }
    }
}
