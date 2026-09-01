using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Engine-free <see cref="IPartPropertySink"/>: stores BasePart spatial/appearance state in
    /// pure Roblox space (studs, right-handed) keyed by <see cref="InstanceId"/>, with no Unity
    /// materialization. Analog of <see cref="CoreAI.Mods.Rbx.Instances.InMemoryInstanceBackingBinder"/>
    /// for the part-property seam: the headless/solo default and the test double the Lua bindings
    /// use when no live <see cref="InstanceGameObjectBinder"/> is wired, so scripts read and write
    /// Part properties through the same <see cref="PartProperties"/> path the Unity binder uses.
    /// </summary>
    public sealed class InMemoryPartPropertySink : IPartPropertySink
    {
        private readonly Dictionary<InstanceId, PartProperties> _properties = new();

        public void SetCFrame(InstanceId id, in RbxCFrame cframe)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CFrame = cframe;
            _properties[id] = properties;
        }

        public void SetPosition(InstanceId id, RbxVector3 position)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Position = position;
            _properties[id] = properties;
        }

        public void SetSize(InstanceId id, RbxVector3 size)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Size = size;
            _properties[id] = properties;
        }

        public void SetColor(InstanceId id, RbxColor3 color)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Color = color;
            properties.ColorWasExplicitlySet = true;
            _properties[id] = properties;
        }

        public void SetAnchored(InstanceId id, bool anchored)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Anchored = anchored;
            _properties[id] = properties;
        }

        public void SetTransparency(InstanceId id, float transparency)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            // WHY: clamp to Roblox's [0, 1] like the Unity binder so both sinks store identically.
            properties.Transparency = transparency < 0f ? 0f : transparency > 1f ? 1f : transparency;
            _properties[id] = properties;
        }

        public void SetCanCollide(InstanceId id, bool canCollide)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.CanCollide = canCollide;
            _properties[id] = properties;
        }

        public void SetShape(InstanceId id, RbxPartShape shape)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Shape = shape;
            _properties[id] = properties;
        }

        public void SetMaterial(InstanceId id, in RbxMaterialId material)
        {
            PartProperties properties = GetPartPropertiesOrDefault(id);
            properties.Material = material;
            _properties[id] = properties;
        }

        public void SetPartProperties(InstanceId id, in PartProperties properties)
        {
            _properties[id] = properties;
        }

        public bool TryGetPartProperties(InstanceId id, out PartProperties properties)
        {
            return _properties.TryGetValue(id, out properties);
        }

        public PartProperties GetPartPropertiesOrDefault(InstanceId id)
        {
            return _properties.TryGetValue(id, out PartProperties properties)
                ? properties
                : PartProperties.CreateDefault();
        }
    }
}
