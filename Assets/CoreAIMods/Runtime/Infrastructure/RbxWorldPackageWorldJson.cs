using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The <c>world.json</c> entry written straight from a payload, and the upper bound of its size measured
    /// by the same traversal without formatting a single number.
    /// </summary>
    /// <remarks>
    /// The bytes are exactly what <c>JsonConvert.SerializeObject</c> of the reader's DTOs writes with the
    /// package's settings (indented, invariant culture, the lone-surrogate replacement), so no reader sees a
    /// difference; <c>WorldPackageEncodeBehindEditModeTests</c> pins the equivalence. WHY not the serializer: it
    /// reflected over a DTO graph built first, which cost about half of every encode and a second copy of
    /// the world in memory; the bound is what lets the autosave written behind an AI edit encode behind it
    /// too, because it proves before the edit that the encode cannot fail a size limit.
    /// </remarks>
    internal static class RbxWorldPackageWorldJson
    {
        /// <summary>Longest text a finite <see cref="float"/> takes in the package JSON (on .NET and on Mono).</summary>
        internal const int MaximumFloatCharacters = 16;

        /// <summary>Longest text a finite <see cref="double"/> takes in the package JSON.</summary>
        internal const int MaximumDoubleCharacters = 25;

        /// <summary>Longest line break a writer on any supported platform emits.</summary>
        internal const int MaximumNewLineCharacters = 2;

        private const char NextLine = (char)0x0085;
        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly Dictionary<RbxPartShape, string> ShapeNames = NamesOf<RbxPartShape>();
        private static readonly Dictionary<AttributeValueKind, string> AttributeKindNames = NamesOf<AttributeValueKind>();
        private static readonly Dictionary<InstanceAccessScope, string> AccessScopeNames = NamesOf<InstanceAccessScope>();
        private static readonly Dictionary<RbxDevComputerMovementMode, string> MovementModeNames = NamesOf<RbxDevComputerMovementMode>();
        private static readonly Dictionary<RbxCameraMode, string> CameraModeNames = NamesOf<RbxCameraMode>();

        /// <summary>Writes <c>world.json</c> into a stream positioned at its end; its length is the entry size.</summary>
        internal static MemoryStream Write(RbxWorldPackagePayload payload, long capacityHint)
        {
            MemoryStream stream = new(capacityHint > 0L && capacityHint < int.MaxValue ? (int)capacityHint : 0);
            using (StreamWriter text = new(stream, StrictUtf8, 64 * 1024, true))
            using (JsonTextWriter json = new(text) { Formatting = Formatting.Indented, Culture = CultureInfo.InvariantCulture })
            {
                Emit(new WriterSink(json), payload);
            }

            return stream;
        }

        /// <summary>
        /// Bytes <see cref="Write"/> would produce, never fewer: structure and text are counted exactly,
        /// every float and double at its longest form and every line break at two characters.
        /// </summary>
        internal static long MeasureUpperBound(RbxWorldPackagePayload payload)
        {
            MeasuringSink sink = new(false, MaximumNewLineCharacters);
            Emit(sink, payload);
            return sink.Bytes;
        }

        /// <summary>The exact byte count <see cref="Write"/> produces; formats every number (tests and diagnostics).</summary>
        internal static long MeasureExact(RbxWorldPackagePayload payload)
        {
            MeasuringSink sink = new(true, Environment.NewLine.Length);
            Emit(sink, payload);
            return sink.Bytes;
        }

        /// <summary>UTF-8 bytes of <paramref name="value"/> as a JSON string body (no quotes), lone surrogates as U+FFFD.</summary>
        internal static long EscapedUtf8Length(string value)
        {
            long bytes = 0L;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current < 0x80)
                {
                    bytes += current < 0x20 || current == '"' || current == '\\'
                        ? EscapeLength(current)
                        : 1;
                }
                else if (current < 0x800)
                {
                    bytes += current == NextLine ? 6 : 2;
                }
                else if (char.IsHighSurrogate(current)
                         && index + 1 < value.Length
                         && char.IsLowSurrogate(value[index + 1]))
                {
                    bytes += 4;
                    index++;
                }
                else
                {
                    bytes += current == LineSeparator || current == ParagraphSeparator ? 6 : 3;
                }
            }

            return bytes;
        }

        private static int EscapeLength(char current)
        {
            switch (current)
            {
                case '\b':
                case '\t':
                case '\n':
                case '\f':
                case '\r':
                case '"':
                case '\\':
                    return 2;
                default:
                    return 6;
            }
        }

        private static void Emit(Sink sink, RbxWorldPackagePayload payload)
        {
            sink.StartObject();
            sink.Name("schema_version");
            sink.Number(RbxWorldPackageSerializer.CurrentWorldSchemaVersion);
            sink.Name("settings");
            sink.StartObject();
            Required(sink, "world_id", payload.Settings.WorldId);
            sink.Name("world_acl_version");
            if (payload.Tree.WorldAclVersion.HasValue)
            {
                sink.Number(payload.Tree.WorldAclVersion.Value);
            }
            else
            {
                sink.Null();
            }

            sink.Name("meters_per_stud");
            sink.Number(payload.Settings.MetersPerStud);
            sink.Name("gravity_studs_per_second_squared");
            sink.Number(payload.Settings.GravityStudsPerSecondSquared);
            Required(sink, "signal_behavior", payload.Settings.SignalBehavior);
            sink.EndObject();
            sink.Name("camera_cframe");
            if (payload.CameraCFrame.HasValue)
            {
                CFrame(sink, payload.CameraCFrame.Value);
            }
            else
            {
                sink.Null();
            }

            sink.Name("instances");
            sink.StartArray();
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                Instance(sink, node, payload.Parts, payload.PointLights, payload.Gui);
            }

            sink.EndArray();
            if (payload.Lighting.HasValue)
            {
                RbxLightingDurableState lighting = payload.Lighting.Value;
                sink.Name("lighting");
                sink.StartObject();
                sink.Name("clock_time");
                sink.Number(lighting.ClockTime);
                sink.Name("geographic_latitude");
                sink.Number(lighting.GeographicLatitude);
                sink.Name("brightness");
                sink.Number(lighting.Brightness);
                sink.Name("ambient");
                Vector(sink, lighting.Ambient.R, lighting.Ambient.G, lighting.Ambient.B);
                sink.Name("outdoor_ambient");
                Vector(sink, lighting.OutdoorAmbient.R, lighting.OutdoorAmbient.G, lighting.OutdoorAmbient.B);
                sink.EndObject();
            }

            if (payload.StarterPlayer.HasValue)
            {
                RbxWorldPackageStarterPlayer starterPlayer = payload.StarterPlayer.Value;
                sink.Name("starter_player");
                sink.StartObject();
                Required(sink, "dev_computer_movement_mode", NameOf(MovementModeNames, starterPlayer.DevComputerMovementMode));
                Required(sink, "camera_mode", NameOf(CameraModeNames, starterPlayer.CameraMode));
                sink.Name("camera_max_zoom_distance");
                sink.Number(starterPlayer.CameraMaxZoomDistance);
                sink.Name("camera_min_zoom_distance");
                sink.Number(starterPlayer.CameraMinZoomDistance);
                sink.EndObject();
            }

            if (payload.StarterGui.HasValue)
            {
                RbxWorldPackageStarterGui starterGui = payload.StarterGui.Value;
                sink.Name("starter_gui");
                sink.StartObject();
                sink.Name("show_development_gui");
                sink.Bool(starterGui.ShowDevelopmentGui);
                sink.Name("screen_orientation");
                sink.Number(starterGui.ScreenOrientation);
                sink.Name("core_gui_disabled");
                sink.StartArray();
                for (int index = 0; index < starterGui.CoreGuiDisabled.Count; index++)
                {
                    sink.Number(starterGui.CoreGuiDisabled[index]);
                }

                sink.EndArray();
                sink.EndObject();
            }

            sink.EndObject();
        }

        private static void Instance(
            Sink sink,
            InstanceSnapshot node,
            IReadOnlyDictionary<InstanceId, PartProperties> parts,
            IReadOnlyDictionary<InstanceId, RbxWorldPackagePointLight> lights,
            IReadOnlyDictionary<InstanceId, RbxWorldPackageGui> gui)
        {
            if (node.Tags == null)
            {
                throw new ArgumentNullException(nameof(node.Tags));
            }

            sink.StartObject();
            sink.Name("id");
            sink.Decimal(node.Id);
            sink.Name("parent_id");
            sink.Decimal(node.ParentId);
            Required(sink, "class_name", node.ClassName);
            Required(sink, "name", node.Name);
            sink.Name("archivable");
            sink.Bool(node.Archivable);
            sink.Name("owner_mod_id");
            sink.Text(node.OwnerModId);
            sink.Name("origin_tag");
            sink.Text(node.OriginTag);
            sink.Name("owner_actor_id");
            sink.Text(node.OwnerActorId);
            sink.Name("access_scope");
            sink.Text(node.AccessScope.HasValue ? NameOf(AccessScopeNames, node.AccessScope.Value) : null);
            sink.Name("revision");
            sink.Decimal(node.Revision);
            sink.Name("tags");
            sink.StartArray();
            foreach (string tag in node.Tags)
            {
                sink.Text(tag);
            }

            sink.EndArray();
            sink.Name("attributes");
            sink.StartArray();
            foreach (AttributeSnapshot attribute in node.Attributes)
            {
                sink.StartObject();
                Required(sink, "name", attribute.Name);
                sink.Name("kind");
                sink.Text(NameOf(AttributeKindNames, attribute.Kind));
                sink.Name("string_value");
                sink.Text(attribute.StringValue);
                sink.Name("number_value");
                sink.Number(attribute.NumberValue);
                sink.Name("bool_value");
                sink.Bool(attribute.BoolValue);
                sink.EndObject();
            }

            sink.EndArray();
            sink.Name("model");
            if (node.Model != null)
            {
                sink.StartObject();
                sink.Name("primary_part_id");
                sink.Decimal(node.Model.PrimaryPartId);
                sink.Name("has_stored_world_pivot");
                sink.Bool(node.Model.HasStoredWorldPivot);
                sink.Name("stored_world_pivot");
                if (node.Model.HasStoredWorldPivot)
                {
                    float[] pivot = RbxWorldPackageSerializer.ParseFloatComponents(node.Model.StoredWorldPivot, 12);
                    sink.StartArray();
                    foreach (float component in pivot)
                    {
                        sink.Number(component);
                    }

                    sink.EndArray();
                }
                else
                {
                    sink.Null();
                }

                sink.EndObject();
            }
            else
            {
                sink.Null();
            }

            sink.Name("part");
            if (parts.TryGetValue(new InstanceId(node.Id), out PartProperties part))
            {
                Part(sink, in part);
            }
            else
            {
                sink.Null();
            }

            sink.Name("click_detector");
            if (node.ClickDetector != null)
            {
                sink.StartObject();
                sink.Name("max_activation_distance");
                sink.Number(double.Parse(node.ClickDetector.MaxActivationDistance, CultureInfo.InvariantCulture));
                sink.EndObject();
            }
            else
            {
                sink.Null();
            }

            sink.Name("material_variant");
            if (node.MaterialVariant != null)
            {
                MaterialVariantSnapshot variant = node.MaterialVariant;
                sink.StartObject();
                Required(sink, "base_material", variant.BaseMaterial);
                sink.Name("base_material_value");
                sink.Number(variant.BaseMaterialValue);
                sink.Name("color_map");
                sink.Text(variant.ColorMap ?? string.Empty);
                sink.Name("normal_map");
                sink.Text(variant.NormalMap ?? string.Empty);
                sink.Name("roughness_map");
                sink.Text(variant.RoughnessMap ?? string.Empty);
                sink.Name("metalness_map");
                sink.Text(variant.MetalnessMap ?? string.Empty);
                sink.Name("studs_per_tile");
                sink.Number(float.Parse(variant.StudsPerTile, CultureInfo.InvariantCulture));
                sink.EndObject();
            }
            else
            {
                sink.Null();
            }

            sink.Name("value");
            if (node.Value != null)
            {
                sink.StartObject();
                sink.Name("string_value");
                sink.Text(node.Value.StringValue);
                sink.Name("object_target_id");
                sink.Decimal(node.Value.ObjectTargetId);
                sink.EndObject();
            }
            else
            {
                sink.Null();
            }

            sink.Name("humanoid");
            if (node.Humanoid != null)
            {
                HumanoidSnapshot humanoid = node.Humanoid;
                sink.StartObject();
                sink.Name("health");
                sink.Number(double.Parse(humanoid.Health, CultureInfo.InvariantCulture));
                sink.Name("max_health");
                sink.Number(double.Parse(humanoid.MaxHealth, CultureInfo.InvariantCulture));
                sink.Name("walk_speed");
                sink.Number(double.Parse(humanoid.WalkSpeed, CultureInfo.InvariantCulture));
                sink.Name("jump_power");
                sink.Number(double.Parse(humanoid.JumpPower, CultureInfo.InvariantCulture));
                sink.Name("jump_height");
                sink.Number(double.Parse(humanoid.JumpHeight, CultureInfo.InvariantCulture));
                sink.Name("use_jump_power");
                sink.Bool(humanoid.UseJumpPower);
                sink.Name("display_name");
                sink.Text(humanoid.DisplayName ?? string.Empty);
                sink.EndObject();
            }
            else
            {
                sink.Null();
            }

            if (lights.TryGetValue(new InstanceId(node.Id), out RbxWorldPackagePointLight light))
            {
                sink.Name("light");
                sink.StartObject();
                sink.Name("brightness");
                sink.Number(light.Brightness);
                sink.Name("color");
                Vector(sink, light.Color.R, light.Color.G, light.Color.B);
                sink.Name("enabled");
                sink.Bool(light.Enabled);
                sink.Name("shadows");
                sink.Bool(light.Shadows);
                sink.Name("range");
                sink.Number(light.Range);
                sink.EndObject();
            }

            if (gui.TryGetValue(new InstanceId(node.Id), out RbxWorldPackageGui block))
            {
                sink.Name("gui");
                Gui(sink, block);
            }

            sink.EndObject();
        }

        /// <summary>A <c>gui</c> block in the property order of its class, each value in its kind's JSON form.</summary>
        private static void Gui(Sink sink, RbxWorldPackageGui block)
        {
            sink.StartObject();
            for (int index = 0; index < block.Count; index++)
            {
                sink.Name(RbxWorldPackageGuiSchema.KeyAt(block.ClassName, index));
                RbxGuiValue value = block.ValueAt(index);
                switch (value.Kind)
                {
                    case RbxGuiValueKind.Bool:
                        sink.Bool(value.Flag);
                        break;
                    case RbxGuiValueKind.Int:
                        sink.Number(value.First);
                        break;
                    case RbxGuiValueKind.Float:
                        sink.Number(value.X);
                        break;
                    case RbxGuiValueKind.Color3:
                        Vector(sink, value.X, value.Y, value.Z);
                        break;
                    case RbxGuiValueKind.UDim:
                        sink.StartArray();
                        sink.Number(value.X);
                        sink.Number(value.First);
                        sink.EndArray();
                        break;
                    case RbxGuiValueKind.UDim2:
                        sink.StartArray();
                        sink.Number(value.X);
                        sink.Number(value.First);
                        sink.Number(value.Y);
                        sink.Number(value.Second);
                        sink.EndArray();
                        break;
                    case RbxGuiValueKind.Vector2:
                        sink.StartArray();
                        sink.Number(value.X);
                        sink.Number(value.Y);
                        sink.EndArray();
                        break;
                    default:
                        sink.Text(value.Text);
                        break;
                }
            }

            sink.EndObject();
        }

        private static void Part(Sink sink, in PartProperties part)
        {
            sink.StartObject();
            sink.Name("shape");
            sink.Text(NameOf(ShapeNames, part.Shape));
            sink.Name("shape_value");
            sink.Number((int)part.Shape);
            Required(sink, "material", part.Material.Name);
            sink.Name("material_value");
            sink.Number(part.Material.Value);
            sink.Name("material_variant");
            sink.Text(string.IsNullOrEmpty(part.MaterialVariant) ? null : part.MaterialVariant);
            sink.Name("cframe");
            CFrame(sink, part.CFrame);
            sink.Name("size");
            Vector(sink, part.Size.X, part.Size.Y, part.Size.Z);
            sink.Name("color");
            Vector(sink, part.Color.R, part.Color.G, part.Color.B);
            sink.Name("color_was_explicitly_set");
            sink.Bool(part.ColorWasExplicitlySet);
            sink.Name("anchored");
            sink.Bool(part.Anchored);
            sink.Name("transparency");
            sink.Number(part.Transparency);
            sink.Name("can_collide");
            sink.Bool(part.CanCollide);
            sink.EndObject();
        }

        /// <summary>
        /// A property the reader requires: a null value is refused as the DTO serializer refused it
        /// (<c>Required.Always</c>), so the writer never emits a package its reader cannot read.
        /// </summary>
        private static void Required(Sink sink, string property, string value)
        {
            if (value == null)
            {
                throw new JsonSerializationException(
                    "Cannot write a null value for property '" + property + "'. Property requires a value.");
            }

            sink.Name(property);
            sink.Text(value);
        }

        /// <summary>The names <c>ToString()</c> gives every defined value, looked up instead of formatted per node.</summary>
        private static Dictionary<TEnum, string> NamesOf<TEnum>()
            where TEnum : struct, Enum
        {
            Dictionary<TEnum, string> names = new();
            foreach (TEnum value in (TEnum[])Enum.GetValues(typeof(TEnum)))
            {
                names[value] = value.ToString();
            }

            return names;
        }

        private static string NameOf<TEnum>(Dictionary<TEnum, string> names, TEnum value)
            where TEnum : struct, Enum
        {
            return names.TryGetValue(value, out string name) ? name : value.ToString();
        }

        /// <summary>Digits of <paramref name="value"/> in invariant decimal.</summary>
        private static int DecimalDigits(ulong value)
        {
            int digits = 1;
            while (value >= 10UL)
            {
                value /= 10UL;
                digits++;
            }

            return digits;
        }

        /// <summary>The twelve components in <c>GetComponents()</c> order, without its array.</summary>
        private static void CFrame(Sink sink, in RbxCFrame cframe)
        {
            RbxVector3 right = cframe.XVector;
            RbxVector3 up = cframe.YVector;
            RbxVector3 back = cframe.ZVector;
            sink.StartArray();
            sink.Number(cframe.X);
            sink.Number(cframe.Y);
            sink.Number(cframe.Z);
            sink.Number(right.X);
            sink.Number(up.X);
            sink.Number(back.X);
            sink.Number(right.Y);
            sink.Number(up.Y);
            sink.Number(back.Y);
            sink.Number(right.Z);
            sink.Number(up.Z);
            sink.Number(back.Z);
            sink.EndArray();
        }

        private static void Vector(Sink sink, float x, float y, float z)
        {
            sink.StartArray();
            sink.Number(x);
            sink.Number(y);
            sink.Number(z);
            sink.EndArray();
        }

        /// <summary>The JSON tokens the traversal emits, in order.</summary>
        private abstract class Sink
        {
            public abstract void StartObject();

            public abstract void EndObject();

            public abstract void StartArray();

            public abstract void EndArray();

            /// <summary>A property name: an ASCII constant that needs no escape.</summary>
            public abstract void Name(string name);

            /// <summary>A string value, or null.</summary>
            public abstract void Text(string value);

            /// <summary>An unsigned id written as a decimal string (ids above 2^53 survive JSON readers).</summary>
            public abstract void Decimal(ulong value);

            /// <summary>A signed revision written as a decimal string.</summary>
            public abstract void Decimal(long value);

            public abstract void Number(int value);

            public abstract void Number(float value);

            public abstract void Number(double value);

            public abstract void Bool(bool value);

            public abstract void Null();
        }

        private sealed class WriterSink : Sink
        {
            private readonly JsonTextWriter _json;

            public WriterSink(JsonTextWriter json)
            {
                _json = json;
            }

            public override void StartObject() => _json.WriteStartObject();

            public override void EndObject() => _json.WriteEndObject();

            public override void StartArray() => _json.WriteStartArray();

            public override void EndArray() => _json.WriteEndArray();

            public override void Name(string name) => _json.WritePropertyName(name);

            public override void Text(string value)
            {
                if (value == null)
                {
                    _json.WriteNull();
                    return;
                }

                _json.WriteValue(RbxWorldPackageSerializer.ReplaceLoneSurrogates(value));
            }

            public override void Decimal(ulong value) => _json.WriteValue(value.ToString(CultureInfo.InvariantCulture));

            public override void Decimal(long value) => _json.WriteValue(value.ToString(CultureInfo.InvariantCulture));

            public override void Number(int value) => _json.WriteValue(value);

            public override void Number(float value) => _json.WriteValue(value);

            public override void Number(double value) => _json.WriteValue(value);

            public override void Bool(bool value) => _json.WriteValue(value);

            public override void Null() => _json.WriteNull();
        }

        /// <summary>
        /// Counts the bytes of the indented output token by token, with the writer's rules: a comma before
        /// every item but the first, a line break and two spaces per open container before every item and
        /// before the close of a non-empty container, one space after a property's colon.
        /// </summary>
        private sealed class MeasuringSink : Sink
        {
            private readonly bool _exactNumbers;
            private readonly int _newLine;
            private readonly List<bool> _hasItems = new();
            private bool _afterName;

            public MeasuringSink(bool exactNumbers, int newLine)
            {
                _exactNumbers = exactNumbers;
                _newLine = newLine;
            }

            public long Bytes { get; private set; }

            public override void StartObject()
            {
                BeforeValue();
                Bytes += 1;
                _hasItems.Add(false);
            }

            public override void EndObject()
            {
                Close();
            }

            public override void StartArray()
            {
                BeforeValue();
                Bytes += 1;
                _hasItems.Add(false);
            }

            public override void EndArray()
            {
                Close();
            }

            public override void Name(string name)
            {
                int top = _hasItems.Count - 1;
                if (_hasItems[top])
                {
                    Bytes += 1;
                }

                _hasItems[top] = true;
                Bytes += _newLine + 2L * _hasItems.Count + name.Length + 3;
                _afterName = true;
            }

            public override void Text(string value)
            {
                BeforeValue();
                Bytes += value == null ? 4 : EscapedUtf8Length(value) + 2;
            }

            public override void Decimal(ulong value)
            {
                BeforeValue();
                Bytes += DecimalDigits(value) + 2;
            }

            public override void Decimal(long value)
            {
                BeforeValue();
                Bytes += (value < 0L ? DecimalDigits((ulong)(-(value + 1L)) + 1UL) + 1 : DecimalDigits((ulong)value)) + 2;
            }

            public override void Number(int value)
            {
                BeforeValue();
                Bytes += value < 0 ? DecimalDigits((ulong)(-(long)value)) + 1 : DecimalDigits((ulong)value);
            }

            public override void Number(float value)
            {
                BeforeValue();
                Bytes += _exactNumbers ? FloatText(value).Length : MaximumFloatCharacters;
            }

            public override void Number(double value)
            {
                BeforeValue();
                Bytes += _exactNumbers ? DoubleText(value).Length : MaximumDoubleCharacters;
            }

            public override void Bool(bool value)
            {
                BeforeValue();
                Bytes += value ? 4 : 5;
            }

            public override void Null()
            {
                BeforeValue();
                Bytes += 4;
            }

            private void BeforeValue()
            {
                if (_afterName)
                {
                    Bytes += 1;
                    _afterName = false;
                    return;
                }

                int top = _hasItems.Count - 1;
                if (top < 0)
                {
                    return;
                }

                if (_hasItems[top])
                {
                    Bytes += 1;
                }

                _hasItems[top] = true;
                Bytes += _newLine + 2L * _hasItems.Count;
            }

            private void Close()
            {
                int top = _hasItems.Count - 1;
                bool hadItems = _hasItems[top];
                _hasItems.RemoveAt(top);
                if (hadItems)
                {
                    Bytes += _newLine + 2L * _hasItems.Count;
                }

                Bytes += 1;
            }

            private static string FloatText(float value)
            {
                return float.IsNaN(value) || float.IsInfinity(value)
                    ? "\"" + JsonConvert.ToString(value) + "\""
                    : JsonConvert.ToString(value);
            }

            private static string DoubleText(double value)
            {
                return double.IsNaN(value) || double.IsInfinity(value)
                    ? "\"" + JsonConvert.ToString(value) + "\""
                    : JsonConvert.ToString(value);
            }
        }
    }
}
