# Attributes, tags, CollectionService, value objects, leaderstats

- `inst:SetAttribute(name, value)` / `inst:GetAttribute(name)` / `inst:GetAttributes()`.
  Value must be string, boolean, number, Vector3, Vector2, Color3, or UDim — anything else is
  rejected with BAD_ARGUMENT. SetAttribute needs WorldEdit. A new attribute name uses only ASCII
  letters, digits, `.`, `-`, `/` and `_` (at most 100 chars, no "RBX" prefix); anything else is
  BAD_ARGUMENT.
- `inst:AddTag(t)` / `RemoveTag(t)` / `HasTag(t)` / `GetTags()`. Add/Remove need WorldEdit. A new tag
  is at most 100 characters (BAD_ARGUMENT beyond).
- At most 256 attributes and 256 tags per instance; one more is BAD_ARGUMENT (a world must stay
  savable).
- `CollectionService:AddTag(inst, tag)`, `:RemoveTag`, `:HasTag`, `:GetTags(inst)`,
  `:GetTagged(tag)`, `:GetAllTags()`, plus per-tag `:GetInstanceAddedSignal(tag)` /
  `:GetInstanceRemovedSignal(tag)` and the placewide `.TagAdded` / `.TagRemoved` (fire when a tag
  enters use inside the DataModel — its first holder in the tree — and when it leaves use — its last
  holder in the tree loses it or leaves; a nil-parented holder counts for neither, and GetAllTags
  lists only tags in use; once per tag, not once per instance — use the per-tag signals for that).
  Entering/leaving the tree fires Added/Removed exactly like Add/RemoveTag would. Duplicate
  `AddTag` is a no-op; `RemoveTag` of a tag never held is a no-op. `GetCollection`, `ItemAdded`,
  `ItemRemoved` are Deprecated in the mirror and are simply absent here (unknown-member, not a
  NOT_IMPLEMENTED stub).
    local cs = game:GetService("CollectionService")
    cs:AddTag(part, "kill")
    cs:GetInstanceAddedSignal("kill"):Connect(function(inst) inst:Destroy() end)

```lua
local m = Instance.new("Model")
m:SetAttribute("Team", "Red")
m:SetAttribute("Score", 10)
m:SetAttribute("Spawn", Vector3.new(1, 2, 3))
m:AddTag("Enemy")
m.Parent = workspace
print(m:GetAttribute("Team") .. " " .. tostring(m:GetAttribute("Score")))
print(tostring(m:HasTag("Enemy")))  -- true
```

## Value objects

- `IntValue`, `NumberValue`, `StringValue`, `BoolValue`, `ObjectValue`, `Vector3Value`,
  `CFrameValue`, `Color3Value` — all `Instance.new`-creatable; every one `IsA("ValueBase")`, the
  common abstract ancestor (`ValueBase` itself is NOT creatable).
- One member, `.Value` (read+write, type-checked; numbers and numeric strings convert as in
  section datatypes): a wrong-type write raises BAD_ARGUMENT and
  leaves the old value AND revision untouched. Defaults: Int/Number 0, String "", Bool false,
  Object nil, Vector3/CFrame their datatype's zero/identity, Color3 black (CoreAI's own default;
  the mirror does not specify one). `.Changed` fires once with the new value on every real change
  (assigning the SAME value again does not fire).
- `IntValue.Value` rounds to the nearest integer, halfway away from zero (`2.5`->3, `-2.5`->-3);
  a value outside -2^63..2^63-1 is BAD_ARGUMENT.
  `StringValue.Value` is capped at 200,000 characters; longer raises "String too long".
  `ObjectValue.Value` accepts an Instance or nil.
- `leaderstats` is CONVENTION, not an API: a `Folder` named exactly `"leaderstats"` parented
  under a `Player`, holding named Value children — the standard way to show per-player stats.
    local coins = Instance.new("IntValue")
    coins.Name = "Coins"; coins.Value = 0
    local stats = Instance.new("Folder")
    stats.Name = "leaderstats"
    coins.Parent = stats
    stats.Parent = player
