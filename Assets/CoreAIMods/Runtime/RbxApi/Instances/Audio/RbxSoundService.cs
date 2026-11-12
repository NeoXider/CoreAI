using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// A copy of a sound that plays once and is no instance: what <c>SoundService:PlayLocalSound</c> and
    /// <c>PlayOnRemove</c> start. It keeps the original's clip, volume, speed, group and roll-off, and plays at
    /// the original's part while that part exists; it is never looped (SoundService.yaml: <c>Looped</c> does not
    /// carry over) and fires nothing.
    /// </summary>
    public sealed class RbxSoundLocalVoice
    {
        internal RbxSoundLocalVoice(ulong id, in RbxSoundAsset asset, RbxSound original, double startSeconds,
            InstanceId hostPartId)
        {
            Id = id;
            Asset = asset;
            TimePosition = Math.Max(0d, Math.Min(startSeconds, asset.LengthSeconds));
            Volume = original.Volume;
            PlaybackSpeed = original.PlaybackSpeed;
            SoundGroup = original.SoundGroup;
            RollOffMode = original.RollOffMode;
            RollOffMinDistance = original.RollOffMinDistance;
            RollOffMaxDistance = original.RollOffMaxDistance;
            HostPartId = hostPartId;
        }

        /// <summary>Unique within its service, never reused.</summary>
        public ulong Id { get; }

        public RbxSoundAsset Asset { get; }

        public double TimePosition { get; private set; }

        public float Volume { get; }

        public float PlaybackSpeed { get; }

        public RbxSoundGroup SoundGroup { get; }

        public RbxRollOffMode RollOffMode { get; }

        public float RollOffMinDistance { get; }

        public float RollOffMaxDistance { get; }

        /// <summary>The part the copy plays at, or <see cref="InstanceId.None"/> for a global copy.</summary>
        public InstanceId HostPartId { get; }

        /// <summary>True once the copy played to its end.</summary>
        public bool Finished { get; private set; }

        /// <summary>The volume before distance, with the group's multiplier.</summary>
        public float EffectiveVolume => SoundGroup != null && !SoundGroup.IsDestroyed ? Volume * SoundGroup.Volume : Volume;

        internal void Advance(double deltaSeconds)
        {
            if (Finished || !(deltaSeconds > 0d))
            {
                return;
            }

            if (!(PlaybackSpeed > 0f))
            {
                return;
            }

            TimePosition += deltaSeconds * PlaybackSpeed;
            if (TimePosition >= Asset.LengthSeconds)
            {
                TimePosition = Asset.LengthSeconds;
                Finished = true;
            }
        }
    }

    /// <summary>
    /// Roblox <c>SoundService</c>, the DataModel's audio service (SoundService.yaml, NotCreatable). It carries the
    /// host's <see cref="RbxSoundLibrary"/>, plays local copies with <see cref="PlayLocalSound"/>, and answers the
    /// CoreAI extension <c>GetSoundIds()</c> (DEV-133). A sound parented anywhere under it plays globally, as
    /// every sound not parented to a part does.
    /// </summary>
    public sealed class RbxSoundService : RbxInstance
    {
        /// <summary>The most local copies playing at once; a new one past it replaces the oldest.</summary>
        public const int MaxLocalVoices = 64;

        /// <summary>The most unknown ids one world notes before it stops noting new ones.</summary>
        public const int MaxNotedIds = 64;

        private static readonly string[] NoIds = Array.Empty<string>();

        private readonly List<RbxSoundLocalVoice> _localVoices = new();
        private readonly HashSet<string> _notedIds = new(StringComparer.Ordinal);
        private ulong _nextVoiceId;

        internal RbxSoundService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "SoundService";
        }

        /// <summary>The sounds this world can load; null until the host attaches its library.</summary>
        public RbxSoundLibrary Library { get; private set; }

        /// <summary>Local copies still playing, oldest first.</summary>
        public IReadOnlyList<RbxSoundLocalVoice> LocalVoices => _localVoices;

        /// <summary>Hands the world the host's sounds; null detaches them (every sound then stays as it is loaded).</summary>
        public void AttachLibrary(RbxSoundLibrary library)
        {
            Library = library;
        }

        /// <summary>The keys of <see cref="Library"/> in ordinal order; empty without a library.</summary>
        public IReadOnlyList<string> GetSoundIds()
        {
            return Library != null ? Library.GetIds() : NoIds;
        }

        /// <summary>
        /// Plays a copy of <paramref name="sound"/> once from its <c>TimePosition</c> (SoundService.yaml), at its part
        /// when it is parented to one in the Workspace and globally otherwise. Returns the copy, or null when the
        /// sound's id resolves to no clip of the host.
        /// </summary>
        public RbxSoundLocalVoice PlayLocalSound(RbxSound sound)
        {
            ThrowIfDestroyed("PlayLocalSound");
            if (sound == null)
            {
                throw RbxError.BadArgument("SoundService:PlayLocalSound expects a Sound at argument 1",
                    "pass the Sound to play, e.g. SoundService:PlayLocalSound(workspace.Part.Sound)");
            }

            return StartCopy(sound, ResolveHostPart(sound), sound.TimePosition);
        }

        /// <summary>The part a sound plays at: its parent when that is a BasePart inside the Workspace.</summary>
        public static InstanceId ResolveHostPart(RbxSound sound)
        {
            if (sound == null || sound.IsDestroyed)
            {
                return InstanceId.None;
            }

            RbxInstance parent = sound.Parent;
            RbxInstance workspace = sound.Registry?.WorldRoot;
            if (parent == null || workspace == null || parent.IsDestroyed || !parent.IsA("BasePart")
                || !parent.IsDescendantOf(workspace))
            {
                return InstanceId.None;
            }

            return parent.Id;
        }

        /// <summary>Starts a copy from <paramref name="startSeconds"/>; null when no clip can be found for it.</summary>
        internal RbxSoundLocalVoice StartCopy(RbxSound sound, InstanceId hostPartId, double startSeconds)
        {
            RbxSoundAsset asset = sound.LoadedAsset;
            if (!asset.IsValid && (Library == null || !Library.TryResolve(sound.SoundId, out asset)))
            {
                return null;
            }

            if (_localVoices.Count >= MaxLocalVoices)
            {
                _localVoices.RemoveAt(0);
            }

            RbxSoundLocalVoice voice = new(++_nextVoiceId, in asset, sound, startSeconds, hostPartId);
            _localVoices.Add(voice);
            return voice;
        }

        /// <summary>Moves every copy's clock and drops the finished ones.</summary>
        internal void AdvanceLocalVoices(double deltaSeconds)
        {
            int kept = 0;
            for (int index = 0; index < _localVoices.Count; index++)
            {
                RbxSoundLocalVoice voice = _localVoices[index];
                voice.Advance(deltaSeconds);
                if (!voice.Finished)
                {
                    _localVoices[kept++] = voice;
                }
            }

            if (kept < _localVoices.Count)
            {
                _localVoices.RemoveRange(kept, _localVoices.Count - kept);
            }
        }

        /// <summary>
        /// True the first time an id that loads nothing is noted in this world, so the explanation is logged once
        /// per id; false for an id already noted and once <see cref="MaxNotedIds"/> ids were.
        /// </summary>
        internal bool TryNoteUnknownId(string soundId)
        {
            string key = soundId ?? string.Empty;
            if (_notedIds.Count >= MaxNotedIds || _notedIds.Contains(key))
            {
                return false;
            }

            _notedIds.Add(key);
            return true;
        }

        /// <summary>The one line a mod log carries for a sound id that loads nothing.</summary>
        public static string DescribeUnknownId(string soundId, RbxSoundLibrary library)
        {
            string shown = soundId ?? string.Empty;
            if (shown.Length > 120)
            {
                shown = shown.Substring(0, 120) + "...";
            }

            string reason = RbxSoundLibrary.IsRobloxAssetId(shown)
                ? "is a Roblox asset id, and CoreAI downloads no Roblox assets"
                : "names no sound this host provides";
            int count = library?.Count ?? 0;
            return "[RbxApi] Sound.SoundId '" + shown + "' " + reason
                   + ", so the Sound never loads and stays silent (IsLoaded stays false, TimeLength 0, Loaded never "
                   + "fires). | fix: use an id from SoundService:GetSoundIds() (" + count
                   + (count == 1 ? " sound" : " sounds") + " registered here). A host provides a sound in two ways: "
                   + "register it under a key (RbxWorldHost.Sounds.Register(\"key\", clip)), or put an AudioClip under a "
                   + "Resources folder and use its path without the extension (\"Sounds/click\"). (Logged once per id.)";
        }
    }
}
