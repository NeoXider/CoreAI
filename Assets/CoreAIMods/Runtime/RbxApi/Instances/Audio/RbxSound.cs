using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>Sound</c> (Sound.yaml; defaults from <c>Full-API-Dump.json</c>): <c>SoundId</c> (empty),
    /// <c>Volume</c> (0.5, held to [0, 10]), <c>Looped</c>, <c>PlaybackSpeed</c> (1), <c>TimePosition</c>,
    /// <c>TimeLength</c> (0 until loaded), <c>Playing</c>, <c>IsPlaying</c>/<c>IsPaused</c>, <c>IsLoaded</c>,
    /// <c>RollOffMinDistance</c> (10), <c>RollOffMaxDistance</c> (10000), <c>RollOffMode</c> (Inverse),
    /// <c>PlayOnRemove</c> and <c>SoundGroup</c>; <c>Play</c>/<c>Stop</c>/<c>Pause</c>/<c>Resume</c> and the
    /// <c>Played</c>/<c>Stopped</c>/<c>Paused</c>/<c>Resumed</c>/<c>Ended</c>/<c>Loaded</c>/<c>DidLoop</c> events,
    /// each passing the <c>SoundId</c>.
    /// </summary>
    /// <remarks>
    /// The instance holds the state and the rules; <see cref="RbxSoundPlayback"/> loads it from the host's
    /// <see cref="RbxSoundLibrary"/> and moves its clock on the scheduler, and <see cref="RbxSoundReconciler"/>
    /// makes it audible. So a world with no audio output (a server, a test, a browser before the first click)
    /// keeps the same timeline: <c>TimePosition</c> advances and <c>Ended</c> fires on time.
    /// <para>
    /// Every script-made change fires <c>Changed</c> and the property signal once; an equal write fires
    /// nothing. The clock's own movement (the advance of <c>TimePosition</c>, a loop wrap, the return to 0 at the
    /// end) fires no property signal, as a moving value is not watched through <c>Changed</c>.
    /// </para>
    /// </remarks>
    public sealed class RbxSound : RbxInstance
    {
        public const float DefaultVolume = 0.5f;
        public const float MaxVolume = 10f;
        public const float DefaultRollOffMinDistance = 10f;
        public const float DefaultRollOffMaxDistance = 10000f;

        private string _soundId = string.Empty;
        private float _volume = DefaultVolume;
        private bool _looped;
        private float _playbackSpeed = 1f;
        private double _timePosition;
        private double _scriptTimePosition;
        private double _timeLength;
        private bool _playing;
        private bool _isLoaded;
        private RbxSoundAsset _loadedAsset;
        private float _rollOffMinDistance = DefaultRollOffMinDistance;
        private float _rollOffMaxDistance = DefaultRollOffMaxDistance;
        private RbxRollOffMode _rollOffMode = RbxRollOffMode.Inverse;
        private bool _playOnRemove;
        private RbxSoundGroup _soundGroup;
        private int _loopCount;

        internal RbxSound(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Sound";
        }

        /// <summary>The content id: a key the host registered, or a path its fallback resolves.</summary>
        public string SoundId => _soundId;

        public float Volume => _volume;

        public bool Looped => _looped;

        public float PlaybackSpeed => _playbackSpeed;

        /// <summary>Seconds into the clip.</summary>
        public double TimePosition => _timePosition;

        /// <summary>The clip's length in seconds; 0 while the sound is not loaded.</summary>
        public double TimeLength => _timeLength;

        public bool Playing => _playing;

        /// <summary>Read-only twin of <see cref="Playing"/>.</summary>
        public bool IsPlaying => _playing;

        /// <summary>True whenever the sound is not playing: paused, stopped or never played.</summary>
        public bool IsPaused => !_playing;

        /// <summary>True once <see cref="SoundId"/> resolved to a clip of the host.</summary>
        public bool IsLoaded => _isLoaded;

        public float RollOffMinDistance => _rollOffMinDistance;

        public float RollOffMaxDistance => _rollOffMaxDistance;

        public RbxRollOffMode RollOffMode => _rollOffMode;

        public bool PlayOnRemove => _playOnRemove;

        /// <summary>The group whose volume multiplies this sound's, or null.</summary>
        public RbxSoundGroup SoundGroup => _soundGroup;

        /// <summary>Times the sound wrapped since it was last played or stopped.</summary>
        public int LoopCount => _loopCount;

        /// <summary>Bumped when a script moves the playhead (a write, Play, Stop), so an output seeks to it.</summary>
        public int SeekVersion { get; private set; }

        /// <summary>The clip the sound loaded; default while it is not loaded.</summary>
        public RbxSoundAsset LoadedAsset => _loadedAsset;

        /// <summary>
        /// The volume the sound is heard at before distance: <see cref="Volume"/> times its live
        /// <see cref="SoundGroup"/>'s volume (SoundGroup.yaml: "acts as a multiplier").
        /// </summary>
        public float EffectiveVolume => _soundGroup != null && !_soundGroup.IsDestroyed
            ? _volume * _soundGroup.Volume
            : _volume;

        public RbxScriptSignal Played => GetOrCreateSignal("Played");

        public RbxScriptSignal Paused => GetOrCreateSignal("Paused");

        public RbxScriptSignal Resumed => GetOrCreateSignal("Resumed");

        public RbxScriptSignal Stopped => GetOrCreateSignal("Stopped");

        public RbxScriptSignal Ended => GetOrCreateSignal("Ended");

        public RbxScriptSignal Loaded => GetOrCreateSignal("Loaded");

        public RbxScriptSignal DidLoop => GetOrCreateSignal("DidLoop");

        public void SetSoundId(string soundId)
        {
            string next = soundId ?? string.Empty;
            if (string.Equals(_soundId, next, StringComparison.Ordinal))
            {
                return;
            }

            _soundId = next;
            _loopCount = 0;
            NotifyPropertyChanged(nameof(SoundId));
            Unload();
        }

        public void SetVolume(double volume)
        {
            RequireFinite(volume, nameof(Volume));
            SetFloat(ref _volume, (float)Math.Max(0d, Math.Min(MaxVolume, volume)), nameof(Volume));
        }

        public void SetLooped(bool looped)
        {
            if (_looped == looped)
            {
                return;
            }

            _looped = looped;
            NotifyPropertyChanged(nameof(Looped));
        }

        public void SetPlaybackSpeed(double speed)
        {
            RequireFinite(speed, nameof(PlaybackSpeed));
            SetFloat(ref _playbackSpeed, (float)Math.Max(0d, speed), nameof(PlaybackSpeed));
        }

        /// <summary>
        /// Moves the playhead (held to [0, TimeLength] once loaded) and remembers the value, which the next
        /// <see cref="Play"/> starts from (Sound.yaml: "the last value set by a script").
        /// </summary>
        public void SetTimePosition(double seconds)
        {
            RequireFinite(seconds, nameof(TimePosition));
            double next = Math.Max(0d, seconds);
            if (_isLoaded)
            {
                next = Math.Min(next, _timeLength);
            }

            _scriptTimePosition = next;
            if (_timePosition.Equals(next))
            {
                return;
            }

            _timePosition = next;
            SeekVersion++;
            NotifyPropertyChanged(nameof(TimePosition));
        }

        /// <summary>
        /// <c>Playing = true</c> continues from the current <see cref="TimePosition"/> and <c>false</c> keeps it
        /// (Sound.yaml); neither fires <c>Played</c> or <c>Paused</c>, which only the methods fire.
        /// </summary>
        public void SetPlaying(bool playing)
        {
            SetPlayingCore(playing);
        }

        public void SetRollOffMinDistance(double studs)
        {
            RequireFinite(studs, nameof(RollOffMinDistance));
            SetFloat(ref _rollOffMinDistance, (float)Math.Max(0d, studs), nameof(RollOffMinDistance));
        }

        public void SetRollOffMaxDistance(double studs)
        {
            RequireFinite(studs, nameof(RollOffMaxDistance));
            SetFloat(ref _rollOffMaxDistance, (float)Math.Max(0d, studs), nameof(RollOffMaxDistance));
        }

        public void SetRollOffMode(RbxRollOffMode mode)
        {
            if (mode < RbxRollOffMode.Inverse || mode > RbxRollOffMode.InverseTapered)
            {
                throw RbxError.BadArgument(ClassName + ".RollOffMode got an item outside its enum",
                    "assign Enum.RollOffMode.Inverse, Linear, LinearSquare or InverseTapered");
            }

            if (_rollOffMode == mode)
            {
                return;
            }

            _rollOffMode = mode;
            NotifyPropertyChanged(nameof(RollOffMode));
        }

        public void SetPlayOnRemove(bool playOnRemove)
        {
            if (_playOnRemove == playOnRemove)
            {
                return;
            }

            _playOnRemove = playOnRemove;
            NotifyPropertyChanged(nameof(PlayOnRemove));
        }

        public void SetSoundGroup(RbxInstance group)
        {
            if (group != null && !(group is RbxSoundGroup))
            {
                throw RbxError.BadArgument(
                    ClassName + ".SoundGroup expects a SoundGroup or nil, got a " + group.ClassName,
                    "create one with Instance.new(\"SoundGroup\") (usually under SoundService) and assign it");
            }

            RbxSoundGroup next = (RbxSoundGroup)group;
            if (ReferenceEquals(_soundGroup, next))
            {
                return;
            }

            _soundGroup = next;
            NotifyPropertyChanged(nameof(SoundGroup));
        }

        /// <summary>
        /// Plays from the last <see cref="TimePosition"/> a script set (0 if none), then sets <see cref="Playing"/>
        /// and fires <c>Played</c>. Calling it on a playing sound restarts it.
        /// </summary>
        public void Play()
        {
            ThrowIfDestroyed("Play");
            double start = _isLoaded ? Math.Min(_scriptTimePosition, _timeLength) : _scriptTimePosition;
            _loopCount = 0;
            SeekVersion++;
            if (!_timePosition.Equals(start))
            {
                _timePosition = start;
                NotifyPropertyChanged(nameof(TimePosition));
            }

            SetPlayingCore(true);
            FireSignal("Played", _soundId);
        }

        /// <summary>Sets <see cref="Playing"/> to false and <see cref="TimePosition"/> to 0, then fires <c>Stopped</c>.</summary>
        public void Stop()
        {
            ThrowIfDestroyed("Stop");
            _loopCount = 0;
            _scriptTimePosition = 0d;
            SeekVersion++;
            SetPlayingCore(false);
            if (!_timePosition.Equals(0d))
            {
                _timePosition = 0d;
                NotifyPropertyChanged(nameof(TimePosition));
            }

            FireSignal("Stopped", _soundId);
        }

        /// <summary>Pauses a playing sound where it is and fires <c>Paused</c>; a sound that is not playing is left alone.</summary>
        public void Pause()
        {
            ThrowIfDestroyed("Pause");
            if (!_playing)
            {
                return;
            }

            SetPlayingCore(false);
            FireSignal("Paused", _soundId);
        }

        /// <summary>Continues a sound that is not playing from where it is and fires <c>Resumed</c>.</summary>
        public void Resume()
        {
            ThrowIfDestroyed("Resume");
            if (_playing)
            {
                return;
            }

            SetPlayingCore(true);
            FireSignal("Resumed", _soundId);
        }

        /// <summary>Takes the clip the library resolved: <c>IsLoaded</c>, <c>TimeLength</c>, then <c>Loaded</c>.</summary>
        internal void ApplyLoaded(in RbxSoundAsset asset)
        {
            if (!asset.IsValid || IsDestroyed)
            {
                return;
            }

            bool lengthChanged = !_timeLength.Equals(asset.LengthSeconds);
            _loadedAsset = asset;
            _timeLength = asset.LengthSeconds;
            _timePosition = Math.Min(_timePosition, _timeLength);
            _scriptTimePosition = Math.Min(_scriptTimePosition, _timeLength);
            bool wasLoaded = _isLoaded;
            _isLoaded = true;
            if (lengthChanged)
            {
                NotifyPropertyChanged(nameof(TimeLength));
            }

            if (!wasLoaded)
            {
                NotifyPropertyChanged(nameof(IsLoaded));
            }

            FireSignal("Loaded", _soundId);
        }

        /// <summary>
        /// Moves the clock by <paramref name="deltaSeconds"/> times <see cref="PlaybackSpeed"/>. At the end a looped
        /// sound wraps and fires <c>DidLoop(soundId, timesLooped)</c> once for the step; any other sound stops,
        /// returns to 0 and fires <c>Ended</c>.
        /// </summary>
        internal void Advance(double deltaSeconds)
        {
            if (!_playing || !_isLoaded || IsDestroyed || !(deltaSeconds > 0d) || !(_playbackSpeed > 0f))
            {
                return;
            }

            double length = _timeLength;
            double next = _timePosition + deltaSeconds * _playbackSpeed;
            if (next < length)
            {
                _timePosition = next;
                return;
            }

            if (_looped)
            {
                double wraps = Math.Floor(next / length);
                _timePosition = next - wraps * length;
                _loopCount = (int)Math.Min(int.MaxValue, _loopCount + wraps);
                FireSignal("DidLoop", _soundId, (double)_loopCount);
                return;
            }

            _timePosition = 0d;
            _loopCount = 0;
            SetPlayingCore(false);
            FireSignal("Ended", _soundId);
        }

        /// <summary>Copies the sound's properties; the copy loads its clip on its own.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxSound sound = (RbxSound)copy;
            sound._soundId = _soundId;
            sound._volume = _volume;
            sound._looped = _looped;
            sound._playbackSpeed = _playbackSpeed;
            sound._timePosition = _timePosition;
            sound._scriptTimePosition = _scriptTimePosition;
            sound._playing = _playing;
            sound._rollOffMinDistance = _rollOffMinDistance;
            sound._rollOffMaxDistance = _rollOffMaxDistance;
            sound._rollOffMode = _rollOffMode;
            sound._playOnRemove = _playOnRemove;
            sound._soundGroup = _soundGroup;
        }

        /// <summary>A cloned group reference points at the cloned group when it was cloned in the same call.</summary>
        private protected override void RemapClonedReferences(in CloneReferenceMap map)
        {
            if (_soundGroup != null)
            {
                _soundGroup = map.Resolve(_soundGroup) as RbxSoundGroup ?? _soundGroup;
            }
        }

        private void Unload()
        {
            _loadedAsset = default;
            if (!_isLoaded && _timeLength.Equals(0d))
            {
                return;
            }

            bool hadLength = !_timeLength.Equals(0d);
            _isLoaded = false;
            _timeLength = 0d;
            NotifyPropertyChanged(nameof(IsLoaded));
            if (hadLength)
            {
                NotifyPropertyChanged(nameof(TimeLength));
            }
        }

        private void SetPlayingCore(bool playing)
        {
            if (_playing == playing)
            {
                return;
            }

            _playing = playing;
            NotifyPropertyChanged(nameof(Playing));
            NotifyPropertyChanged(nameof(IsPlaying));
            NotifyPropertyChanged(nameof(IsPaused));
        }

        private void SetFloat(ref float field, float value, string propertyName)
        {
            if (field.Equals(value))
            {
                return;
            }

            field = value;
            NotifyPropertyChanged(propertyName);
        }

        private void RequireFinite(double value, string member)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw RbxError.BadArgument(
                    ClassName + "." + member + " needs a finite number, got "
                    + value.ToString(CultureInfo.InvariantCulture),
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }
        }
    }

    /// <summary>
    /// Roblox <c>SoundGroup</c>: a volume multiplier (default 0.5, held to [0, 10]) for every sound whose
    /// <c>SoundGroup</c> is set to it (SoundGroup.yaml). <c>SoundEffect</c> children are not applied.
    /// </summary>
    public sealed class RbxSoundGroup : RbxInstance
    {
        private float _volume = RbxSound.DefaultVolume;

        internal RbxSoundGroup(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "SoundGroup";
        }

        public float Volume => _volume;

        public void SetVolume(double volume)
        {
            if (double.IsNaN(volume) || double.IsInfinity(volume))
            {
                throw RbxError.BadArgument(
                    ClassName + ".Volume needs a finite number, got "
                    + volume.ToString(CultureInfo.InvariantCulture),
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }

            float next = (float)Math.Max(0d, Math.Min(RbxSound.MaxVolume, volume));
            if (_volume.Equals(next))
            {
                return;
            }

            _volume = next;
            NotifyPropertyChanged(nameof(Volume));
        }

        /// <summary>Copies the group's volume without signals or revision changes.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            ((RbxSoundGroup)copy)._volume = _volume;
        }
    }
}
