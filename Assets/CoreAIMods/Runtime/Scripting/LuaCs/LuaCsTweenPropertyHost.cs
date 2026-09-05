using System;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Tween driver property IO over the part-property sink and the value objects. Reads box
    /// the same state the Lua property getters return; writes flow through the same setters
    /// Lua assignments use (part-sink setters advance the revision here, value-object setters
    /// advance it themselves on real changes — mirroring TryWriteSpatial/TryWriteValue so a
    /// tweened write is indistinguishable from a scripted one).
    /// </summary>
    internal sealed class LuaCsTweenPropertyHost : ITweenPropertyHost
    {
        private readonly IPartPropertySink _sink;
        private readonly InstanceRegistry _registry;

        public LuaCsTweenPropertyHost(IPartPropertySink sink, InstanceRegistry registry)
        {
            _sink = sink;
            _registry = registry;
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

        private void WritePart(RbxInstance target, string propertyName, object value)
        {
            InstanceId id = target.Id;
            switch (propertyName)
            {
                case "Position":
                    _sink.SetPosition(id, (RbxVector3)value);
                    break;
                case "Size":
                    _sink.SetSize(id, (RbxVector3)value);
                    break;
                case "CFrame":
                    _sink.SetCFrame(id, (RbxCFrame)value);
                    break;
                case "Orientation":
                    RbxVector3 orientation = (RbxVector3)value;
                    PartProperties orientationProperties = _sink.GetPartPropertiesOrDefault(id);
                    _sink.SetCFrame(id, RbxCFrame.FromPosition(orientationProperties.Position)
                        * RbxCFrame.FromOrientation(
                            orientation.X * MathF.PI / 180f,
                            orientation.Y * MathF.PI / 180f,
                            orientation.Z * MathF.PI / 180f));
                    break;
                case "Rotation":
                    RbxVector3 rotation = (RbxVector3)value;
                    PartProperties rotationProperties = _sink.GetPartPropertiesOrDefault(id);
                    _sink.SetCFrame(id, RbxCFrame.FromPosition(rotationProperties.Position)
                        * RbxCFrame.FromEulerAnglesXYZ(
                            rotation.X * MathF.PI / 180f,
                            rotation.Y * MathF.PI / 180f,
                            rotation.Z * MathF.PI / 180f));
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
                    intValue.SetFromDouble((double)value);
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
    }
}
