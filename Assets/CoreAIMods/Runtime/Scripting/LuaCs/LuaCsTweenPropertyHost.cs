using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Tween driver property IO over the part-property sink, the value objects, the Humanoid's
    /// numeric members, the camera rig (CFrame and the lens behind FieldOfView), the Lighting
    /// day-cycle properties, the Light properties and the GUI properties (numbers, Color3, UDim2). Reads box the same state the Lua property getters
    /// return; writes flow through the same setters Lua assignments use (part-sink and camera
    /// setters advance the revision here, value-object setters advance it themselves on real
    /// changes — mirroring TryWriteSpatial/TryWriteValue so a tweened write is
    /// indistinguishable from a scripted one, including the teleport note a scripted
    /// Position/CFrame/Orientation/Rotation write leaves for the physics relay, the
    /// Changed/GetPropertyChangedSignal notifications a scripted part or camera write fires, and the
    /// end of a Humanoid's MoveTo when its RootPart is moved).
    /// </summary>
    internal sealed class LuaCsTweenPropertyHost : ITweenPropertyHost
    {
        // WHY these bounds: (double)long.MaxValue rounds UP to 2^63, which no long can hold, so
        // the comparison has to happen in double space before the cast, never after it.
        private const double LongRangeUpperExclusive = 9223372036854775808d;
        private const double LongRangeLower = -9223372036854775808d;

        private readonly IPartPropertySink _sink;
        private readonly InstanceRegistry _registry;
        private readonly Func<RbxWorldPhysics> _worldPhysics;
        private readonly IRbxCameraRig _cameraRig;

        public LuaCsTweenPropertyHost(IPartPropertySink sink, InstanceRegistry registry)
            : this(sink, registry, null, null)
        {
        }

        /// <summary>
        /// Full host: <paramref name="worldPhysics"/> is read lazily on each spatial write (the
        /// bindings build physics after this host) and receives the teleport note;
        /// <paramref name="cameraRig"/> backs Camera.CFrame. Either may be null, which leaves
        /// that part unwired (no teleport note; Camera.CFrame reported as not tweenable).
        /// </summary>
        public LuaCsTweenPropertyHost(IPartPropertySink sink, InstanceRegistry registry,
            Func<RbxWorldPhysics> worldPhysics, IRbxCameraRig cameraRig)
        {
            _sink = sink;
            _registry = registry;
            _worldPhysics = worldPhysics;
            _cameraRig = cameraRig;
        }

        /// <inheritdoc />
        public TweenPropertySample Sample(RbxInstance target, string propertyName)
        {
            if (target == null || target.IsDestroyed || string.IsNullOrEmpty(propertyName))
            {
                return TweenPropertySample.Unknown();
            }

            if (target.IsA("BasePart"))
            {
                return SamplePart(target, propertyName);
            }

            if (target is RbxValueBase)
            {
                return SampleValue(target, propertyName);
            }

            if (target is RbxHumanoid humanoid)
            {
                return SampleHumanoid(humanoid, propertyName);
            }

            if (target.ClassName == "Camera")
            {
                return SampleCamera(propertyName);
            }

            if (target is RbxLighting lighting)
            {
                return SampleLighting(lighting, propertyName);
            }

            if (target is RbxLight light)
            {
                return SampleLight(light, propertyName);
            }

            if (target is RbxSound || target is RbxSoundGroup)
            {
                return SampleSound(target, propertyName);
            }

            if (target is RbxParticleEmitter emitter)
            {
                return SampleParticle(emitter, propertyName);
            }

            if (_registry != null
                && LuaCsRbxGuiBindings.TrySampleTween(_registry.Catalog, target, propertyName,
                    out TweenPropertySample guiSample))
            {
                return guiSample;
            }

            return TweenPropertySample.Unknown();
        }

        /// <inheritdoc />
        public void Write(RbxInstance target, string propertyName, object value)
        {
            if (target == null || target.IsDestroyed)
            {
                return;
            }

            if (target.IsA("BasePart"))
            {
                WritePart(target, propertyName, value);
                return;
            }

            if (target is RbxValueBase)
            {
                WriteValue(target, propertyName, value);
                return;
            }

            if (target is RbxHumanoid humanoid)
            {
                WriteHumanoid(humanoid, propertyName, value);
                return;
            }

            if (target.ClassName == "Camera")
            {
                WriteCamera(target, propertyName, value);
                return;
            }

            if (target is RbxLighting lighting)
            {
                WriteLighting(lighting, propertyName, value);
                return;
            }

            if (target is RbxLight light)
            {
                WriteLight(light, propertyName, value);
                return;
            }

            if (target is RbxSound || target is RbxSoundGroup)
            {
                WriteSound(target, propertyName, value);
                return;
            }

            if (target is RbxParticleEmitter emitter)
            {
                WriteParticle(emitter, propertyName, value);
                return;
            }

            if (_registry != null)
            {
                LuaCsRbxGuiBindings.TryWriteTween(_registry, target, propertyName, value);
            }
        }

        private TweenPropertySample SamplePart(RbxInstance target, string propertyName)
        {
            PartProperties properties = _sink.GetPartPropertiesOrDefault(target.Id);
            switch (propertyName)
            {
                case "Position":
                    return TweenPropertySample.SupportedValue(properties.Position, "Vector3");
                case "Size":
                    return TweenPropertySample.SupportedValue(properties.Size, "Vector3");
                case "CFrame":
                    return TweenPropertySample.SupportedValue(properties.CFrame, "CFrame");
                case "Orientation":
                    (float ox, float oy, float oz) orientation =
                        properties.CFrame.ToOrientation();
                    return TweenPropertySample.SupportedValue(new RbxVector3(
                        orientation.ox * 180f / MathF.PI,
                        orientation.oy * 180f / MathF.PI,
                        orientation.oz * 180f / MathF.PI), "Vector3");
                case "Rotation":
                    (float rx, float ry, float rz) rotation =
                        properties.CFrame.ToEulerAnglesXYZ();
                    return TweenPropertySample.SupportedValue(new RbxVector3(
                        rotation.rx * 180f / MathF.PI,
                        rotation.ry * 180f / MathF.PI,
                        rotation.rz * 180f / MathF.PI), "Vector3");
                case "Color":
                    return TweenPropertySample.SupportedValue(properties.Color, "Color3");
                case "Transparency":
                    return TweenPropertySample.SupportedValue(
                        (double)properties.Transparency, "number");
                case "Anchored":
                case "CanCollide":
                    return TweenPropertySample.Unsupported("boolean");
                case "Shape":
                case "Material":
                    return TweenPropertySample.Unsupported("EnumItem");
                case "MaterialVariant":
                    return TweenPropertySample.Unsupported("string");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        private static TweenPropertySample SampleValue(RbxInstance target, string propertyName)
        {
            if (propertyName != "Value")
            {
                return TweenPropertySample.Unknown();
            }

            switch (target)
            {
                case RbxIntValue intValue:
                    return TweenPropertySample.SupportedValue((double)intValue.Value, "number");
                case RbxNumberValue numberValue:
                    return TweenPropertySample.SupportedValue(numberValue.Value, "number");
                case RbxVector3Value vector3Value:
                    return TweenPropertySample.SupportedValue(vector3Value.Value, "Vector3");
                case RbxCFrameValue cframeValue:
                    return TweenPropertySample.SupportedValue(cframeValue.Value, "CFrame");
                case RbxColor3Value color3Value:
                    return TweenPropertySample.SupportedValue(color3Value.Value, "Color3");
                case RbxBoolValue _:
                    return TweenPropertySample.Unsupported("boolean");
                case RbxStringValue _:
                    return TweenPropertySample.Unsupported("string");
                default:
                    return TweenPropertySample.Unsupported("Instance");
            }
        }

        private static TweenPropertySample SampleHumanoid(RbxHumanoid humanoid,
            string propertyName)
        {
            switch (propertyName)
            {
                case "Health":
                    return TweenPropertySample.SupportedValue(humanoid.Health, "number");
                case "MaxHealth":
                    return TweenPropertySample.SupportedValue(humanoid.MaxHealth, "number");
                case "WalkSpeed":
                    return TweenPropertySample.SupportedValue(humanoid.WalkSpeed, "number");
                case "JumpPower":
                    return TweenPropertySample.SupportedValue(humanoid.JumpPower, "number");
                case "JumpHeight":
                    return TweenPropertySample.SupportedValue(humanoid.JumpHeight, "number");
                case "UseJumpPower":
                case "Jump":
                    return TweenPropertySample.Unsupported("boolean");
                case "DisplayName":
                    return TweenPropertySample.Unsupported("string");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        private TweenPropertySample SampleCamera(string propertyName)
        {
            switch (propertyName)
            {
                case "CFrame":
                    return _cameraRig != null
                        ? TweenPropertySample.SupportedValue(_cameraRig.GetCFrame(), "CFrame")
                        : TweenPropertySample.Unsupported("CFrame");
                case "FieldOfView":
                    // WHY the rig decides: a host rig without a lens answers NaN, and a tween of a lens
                    // that cannot be set must fail at Create like any other untweenable member.
                    double lens = _cameraRig != null ? _cameraRig.GetFieldOfView() : double.NaN;
                    return double.IsNaN(lens)
                        ? TweenPropertySample.Unsupported("number")
                        : TweenPropertySample.SupportedValue(RbxCameraProjection.ClampFieldOfView(lens), "number");
                case "CameraType":
                    return TweenPropertySample.Unsupported("EnumItem");
                case "CameraSubject":
                    return TweenPropertySample.Unsupported("Instance");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        private void WritePart(RbxInstance target, string propertyName, object value)
        {
            InstanceId id = target.Id;
            bool movesPart = false;
            PartProperties before = _sink.GetPartPropertiesOrDefault(id);
            switch (propertyName)
            {
                case "Position":
                    _sink.SetPosition(id, (RbxVector3)value);
                    movesPart = true;
                    break;
                case "Size":
                    _sink.SetSize(id, (RbxVector3)value);
                    break;
                case "CFrame":
                    _sink.SetCFrame(id, (RbxCFrame)value);
                    movesPart = true;
                    break;
                case "Orientation":
                    RbxVector3 orientation = (RbxVector3)value;
                    _sink.SetCFrame(id, RbxCFrame.FromPosition(before.Position)
                        * RbxCFrame.FromOrientation(
                            orientation.X * MathF.PI / 180f,
                            orientation.Y * MathF.PI / 180f,
                            orientation.Z * MathF.PI / 180f));
                    movesPart = true;
                    break;
                case "Rotation":
                    RbxVector3 rotation = (RbxVector3)value;
                    _sink.SetCFrame(id, RbxCFrame.FromPosition(before.Position)
                        * RbxCFrame.FromEulerAnglesXYZ(
                            rotation.X * MathF.PI / 180f,
                            rotation.Y * MathF.PI / 180f,
                            rotation.Z * MathF.PI / 180f));
                    movesPart = true;
                    break;
                case "Color":
                    _sink.SetColor(id, (RbxColor3)value);
                    break;
                case "Transparency":
                    _sink.SetTransparency(id, (float)(double)value);
                    break;
                default:
                    return;
            }

            if (movesPart)
            {
                // WHY: the mirror's Touched "will not fire if the CFrame property was changed
                // such that the part overlaps another part", and a tween moves a part by
                // assigning its CFrame every frame exactly like a script loop does. Noting the
                // teleport the way the scripted Position/CFrame/Orientation/Rotation writes do
                // keeps the two indistinguishable: an overlap a tween drives a part into is not
                // a collision, and a physically moving part hitting it one frame later is.
                RbxWorldPhysics physics = _worldPhysics?.Invoke();
                physics?.NoteTeleport(id);
            }

            _registry.AdvanceRevision(id);
            PartProperties after = _sink.GetPartPropertiesOrDefault(id);
            LuaCsRbxInstanceBindings.NotifyPartChanges(target, propertyName, in before, in after);
            if (movesPart && before.CFrame != after.CFrame)
            {
                EndWalksOfMovedRootPart(target);
            }
        }

        /// <summary>
        /// Ends the MoveTo of every Humanoid whose RootPart is <paramref name="part"/>, after a tween
        /// changed that part's CFrame (Humanoid.yaml MoveTo: the walk "ends if ... a script changes the
        /// CFrame property of the humanoid's RootPart"; a tween is a script assigning it every frame).
        /// </summary>
        private static void EndWalksOfMovedRootPart(RbxInstance part)
        {
            // WHY a copy of LuaCsRbxInstanceBindings.EndWalksOfMovedRootPart, which is private to the
            // scripted-write path: a tweened move must end walks under exactly the same rule (only a
            // part named HumanoidRootPart, and only the Humanoids among its siblings, which is where the
            // character pipeline finds a RootPart), so a change to one belongs in both.
            if (!string.Equals(part.Name, RbxCharacterFactory.RootPartName, StringComparison.Ordinal))
            {
                return;
            }

            RbxInstance character = part.Parent;
            if (character == null || character.IsDestroyed)
            {
                return;
            }

            IReadOnlyList<RbxInstance> siblings = character.GetChildren();
            for (int index = 0; index < siblings.Count; index++)
            {
                if (siblings[index] is RbxHumanoid humanoid
                    && ReferenceEquals(humanoid.RootPart, part))
                {
                    humanoid.EndWalkForScriptedRootPartMove();
                }
            }
        }

        private static void WriteValue(RbxInstance target, string propertyName, object value)
        {
            if (propertyName != "Value")
            {
                return;
            }

            switch (target)
            {
                case RbxIntValue intValue:
                    WriteIntValue(intValue, (double)value);
                    break;
                case RbxNumberValue numberValue:
                    numberValue.Value = (double)value;
                    break;
                case RbxVector3Value vector3Value:
                    vector3Value.Value = (RbxVector3)value;
                    break;
                case RbxCFrameValue cframeValue:
                    cframeValue.Value = (RbxCFrame)value;
                    break;
                case RbxColor3Value color3Value:
                    color3Value.Value = (RbxColor3)value;
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Writes one interpolated number into an IntValue without ever throwing: the Lua
        /// setter's rounding (half away from zero) for in-range values, saturation at the int64
        /// bounds beyond them, and no write for NaN.
        /// </summary>
        private static void WriteIntValue(RbxIntValue intValue, double number)
        {
            // WHY guarded here: this runs inside the Heartbeat driver, where a throw is nobody's
            // to catch, and (long) of an out-of-range double is unspecified in C# (x64 yields
            // long.MinValue, so a goal of 1e30 would land on a huge NEGATIVE number).
            if (double.IsNaN(number))
            {
                return;
            }

            if (number >= LongRangeUpperExclusive)
            {
                intValue.Value = long.MaxValue;
                return;
            }

            if (number <= LongRangeLower)
            {
                intValue.Value = long.MinValue;
                return;
            }

            double rounded = Math.Round(number, MidpointRounding.AwayFromZero);
            intValue.Value = rounded >= LongRangeUpperExclusive ? long.MaxValue : (long)rounded;
        }

        private void WriteHumanoid(RbxHumanoid humanoid, string propertyName, object value)
        {
            double number = (double)value;
            switch (propertyName)
            {
                case "Health":
                    humanoid.Health = number;
                    break;
                case "MaxHealth":
                    humanoid.MaxHealth = number;
                    break;
                case "WalkSpeed":
                    humanoid.WalkSpeed = number;
                    break;
                case "JumpPower":
                    humanoid.JumpPower = number;
                    break;
                case "JumpHeight":
                    humanoid.JumpHeight = number;
                    break;
                default:
                    return;
            }

            _registry.AdvanceRevision(humanoid.Id);
        }

        private void WriteCamera(RbxInstance camera, string propertyName, object value)
        {
            if (_cameraRig == null)
            {
                return;
            }

            if (propertyName == "CFrame")
            {
                RbxCFrame cframe = (RbxCFrame)value;
                LuaCsRbxInstanceBindings.SetCameraCFrame(_cameraRig, camera, in cframe);
                _registry.AdvanceRevision(camera.Id);
                return;
            }

            if (propertyName == "FieldOfView"
                && LuaCsRbxInstanceBindings.TrySetCameraFieldOfView(_cameraRig, camera, (double)value))
            {
                _registry.AdvanceRevision(camera.Id);
            }
        }

        private static TweenPropertySample SampleLighting(RbxLighting lighting, string propertyName)
        {
            switch (propertyName)
            {
                case "ClockTime":
                    return TweenPropertySample.SupportedValue((double)lighting.ClockTime, "number");
                case "GeographicLatitude":
                    return TweenPropertySample.SupportedValue((double)lighting.GeographicLatitude, "number");
                case "Brightness":
                    return TweenPropertySample.SupportedValue((double)lighting.Brightness, "number");
                case "Ambient":
                    return TweenPropertySample.SupportedValue(lighting.Ambient, "Color3");
                case "OutdoorAmbient":
                    return TweenPropertySample.SupportedValue(lighting.OutdoorAmbient, "Color3");
                case "EnvironmentDiffuseScale":
                    return TweenPropertySample.SupportedValue((double)lighting.EnvironmentDiffuseScale, "number");
                case "EnvironmentSpecularScale":
                    return TweenPropertySample.SupportedValue((double)lighting.EnvironmentSpecularScale, "number");
                case "FogStart":
                    return TweenPropertySample.SupportedValue((double)lighting.FogStart, "number");
                case "FogEnd":
                    return TweenPropertySample.SupportedValue((double)lighting.FogEnd, "number");
                case "FogColor":
                    return TweenPropertySample.SupportedValue(lighting.FogColor, "Color3");
                case "TimeOfDay":
                    return TweenPropertySample.Unsupported("string");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        /// <summary>Writes one interpolated Lighting value; the class advances its own revision on a real change.</summary>
        private static void WriteLighting(RbxLighting lighting, string propertyName, object value)
        {
            // WHY: this runs inside the Heartbeat driver, where the setters' non-finite refusal would be
            // nobody's to catch (see WriteIntValue); an overflowed step is skipped instead.
            if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
            {
                return;
            }

            switch (propertyName)
            {
                case "ClockTime":
                    lighting.SetClockTime((double)value);
                    break;
                case "GeographicLatitude":
                    lighting.SetGeographicLatitude((double)value);
                    break;
                case "Brightness":
                    lighting.SetBrightness((double)value);
                    break;
                case "Ambient":
                    lighting.SetAmbient((RbxColor3)value);
                    break;
                case "OutdoorAmbient":
                    lighting.SetOutdoorAmbient((RbxColor3)value);
                    break;
                case "EnvironmentDiffuseScale":
                    lighting.SetEnvironmentDiffuseScale((double)value);
                    break;
                case "EnvironmentSpecularScale":
                    lighting.SetEnvironmentSpecularScale((double)value);
                    break;
                case "FogStart":
                    lighting.SetFogStart((double)value);
                    break;
                case "FogEnd":
                    lighting.SetFogEnd((double)value);
                    break;
                case "FogColor":
                    lighting.SetFogColor((RbxColor3)value);
                    break;
            }
        }

        private static TweenPropertySample SampleLight(RbxLight light, string propertyName)
        {
            switch (propertyName)
            {
                case "Brightness":
                    return TweenPropertySample.SupportedValue((double)light.Brightness, "number");
                case "Color":
                    return TweenPropertySample.SupportedValue(light.Color, "Color3");
                case "Range" when light is RbxPointLight pointLight:
                    return TweenPropertySample.SupportedValue((double)pointLight.Range, "number");
                case "Enabled":
                case "Shadows":
                    return TweenPropertySample.Unsupported("boolean");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        private void WriteLight(RbxLight light, string propertyName, object value)
        {
            if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
            {
                return;
            }

            int before = light.StateVersion;
            switch (propertyName)
            {
                case "Brightness":
                    light.SetBrightness((double)value);
                    break;
                case "Color":
                    light.SetColor((RbxColor3)value);
                    break;
                case "Range" when light is RbxPointLight pointLight:
                    pointLight.SetRange((double)value);
                    break;
                default:
                    return;
            }

            if (light.StateVersion != before)
            {
                _registry.AdvanceRevision(light.Id);
            }
        }

        /// <summary>
        /// The tweenable audio numbers (MVP17): <c>Sound.Volume</c> (the fade idiom), <c>PlaybackSpeed</c>,
        /// <c>TimePosition</c> and the roll-off distances, and <c>SoundGroup.Volume</c>; the booleans and the id are
        /// not interpolated, as everywhere.
        /// </summary>
        private static TweenPropertySample SampleSound(RbxInstance target, string propertyName)
        {
            if (target is RbxSoundGroup group)
            {
                return propertyName == "Volume"
                    ? TweenPropertySample.SupportedValue((double)group.Volume, "number")
                    : TweenPropertySample.Unknown();
            }

            RbxSound sound = (RbxSound)target;
            switch (propertyName)
            {
                case "Volume":
                    return TweenPropertySample.SupportedValue((double)sound.Volume, "number");
                case "PlaybackSpeed":
                    return TweenPropertySample.SupportedValue((double)sound.PlaybackSpeed, "number");
                case "TimePosition":
                    return TweenPropertySample.SupportedValue(sound.TimePosition, "number");
                case "RollOffMinDistance":
                    return TweenPropertySample.SupportedValue((double)sound.RollOffMinDistance, "number");
                case "RollOffMaxDistance":
                    return TweenPropertySample.SupportedValue((double)sound.RollOffMaxDistance, "number");
                case "Looped":
                case "Playing":
                case "PlayOnRemove":
                    return TweenPropertySample.Unsupported("boolean");
                case "SoundId":
                    return TweenPropertySample.Unsupported("string");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        private void WriteSound(RbxInstance target, string propertyName, object value)
        {
            if (!(value is double number) || double.IsNaN(number) || double.IsInfinity(number))
            {
                return;
            }

            if (target is RbxSoundGroup group)
            {
                if (propertyName == "Volume" && !group.Volume.Equals((float)number))
                {
                    group.SetVolume(number);
                    _registry.AdvanceRevision(group.Id);
                }

                return;
            }

            RbxSound sound = (RbxSound)target;
            switch (propertyName)
            {
                case "Volume":
                    sound.SetVolume(number);
                    break;
                case "PlaybackSpeed":
                    sound.SetPlaybackSpeed(number);
                    break;
                case "TimePosition":
                    // WHY no revision: TimePosition is not replicated (Sound.yaml NotReplicated) and not durable.
                    sound.SetTimePosition(number);
                    return;
                case "RollOffMinDistance":
                    sound.SetRollOffMinDistance(number);
                    break;
                case "RollOffMaxDistance":
                    sound.SetRollOffMaxDistance(number);
                    break;
                default:
                    return;
            }

            _registry.AdvanceRevision(sound.Id);
        }

        /// <summary>
        /// A ParticleEmitter tweens its <c>Rate</c> and <c>Acceleration</c>; its ranges, sequences and the
        /// <c>Vector2</c> spread are not interpolated (MVP17).
        /// </summary>
        private static TweenPropertySample SampleParticle(RbxParticleEmitter emitter, string propertyName)
        {
            switch (propertyName)
            {
                case "Rate":
                    return TweenPropertySample.SupportedValue((double)emitter.Rate, "number");
                case "SpreadAngle":
                    return TweenPropertySample.Unsupported("Vector2");
                case "Acceleration":
                    return TweenPropertySample.SupportedValue(emitter.Acceleration, "Vector3");
                case "Enabled":
                    return TweenPropertySample.Unsupported("boolean");
                case "Lifetime":
                case "Speed":
                    return TweenPropertySample.Unsupported("NumberRange");
                case "Size":
                case "Transparency":
                    return TweenPropertySample.Unsupported("NumberSequence");
                case "Color":
                    return TweenPropertySample.Unsupported("ColorSequence");
                case "Texture":
                    return TweenPropertySample.Unsupported("string");
                case "EmissionDirection":
                    return TweenPropertySample.Unsupported("EnumItem");
                default:
                    return TweenPropertySample.Unknown();
            }
        }

        private void WriteParticle(RbxParticleEmitter emitter, string propertyName, object value)
        {
            int before = emitter.StateVersion;
            switch (propertyName)
            {
                case "Rate" when value is double rate && !double.IsNaN(rate) && !double.IsInfinity(rate):
                    emitter.SetRate(rate);
                    break;
                case "Acceleration" when value is RbxVector3 acceleration && IsFinite(acceleration.X)
                                                                        && IsFinite(acceleration.Y)
                                                                        && IsFinite(acceleration.Z):
                    emitter.SetAcceleration(acceleration);
                    break;
                default:
                    return;
            }

            if (emitter.StateVersion != before)
            {
                _registry.AdvanceRevision(emitter.Id);
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
