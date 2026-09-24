using System;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Tween driver property IO over the part-property sink, the value objects, the Humanoid's
    /// numeric members and the camera rig. Reads box the same state the Lua property getters
    /// return; writes flow through the same setters Lua assignments use (part-sink and camera
    /// setters advance the revision here, value-object setters advance it themselves on real
    /// changes — mirroring TryWriteSpatial/TryWriteValue so a tweened write is
    /// indistinguishable from a scripted one, including the teleport note a scripted
    /// Position/CFrame/Orientation/Rotation write leaves for the physics relay).
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
                    // WHY a known-but-unsupported member: FieldOfView is real Roblox API with no
                    // backing in the camera rig yet, so the loud stub is the honest answer.
                    return TweenPropertySample.Unsupported("number");
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
                    PartProperties orientationProperties = _sink.GetPartPropertiesOrDefault(id);
                    _sink.SetCFrame(id, RbxCFrame.FromPosition(orientationProperties.Position)
                        * RbxCFrame.FromOrientation(
                            orientation.X * MathF.PI / 180f,
                            orientation.Y * MathF.PI / 180f,
                            orientation.Z * MathF.PI / 180f));
                    movesPart = true;
                    break;
                case "Rotation":
                    RbxVector3 rotation = (RbxVector3)value;
                    PartProperties rotationProperties = _sink.GetPartPropertiesOrDefault(id);
                    _sink.SetCFrame(id, RbxCFrame.FromPosition(rotationProperties.Position)
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
            if (propertyName != "CFrame" || _cameraRig == null)
            {
                return;
            }

            RbxCFrame cframe = (RbxCFrame)value;
            _cameraRig.SetCFrame(in cframe);
            _registry.AdvanceRevision(camera.Id);
        }
    }
}
