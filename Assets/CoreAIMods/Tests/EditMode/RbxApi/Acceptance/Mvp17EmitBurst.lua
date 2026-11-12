-- MVP17 emit-burst fixture: a part that bursts sparks on demand. The emitter emits nothing by itself (Rate 0); each
-- burst is one Emit call, and the drawing shows it on its next frame. Apart from store_set (the tests' probe) it is
-- plain Roblox Lua.
local source = Instance.new("Part")
source.Name = "BurstSource"
source.Anchored = true
source.Size = Vector3.new(2, 1, 2)
source.Position = Vector3.new(0, 6, 0)
source.Parent = workspace

local sparks = Instance.new("ParticleEmitter")
sparks.Name = "Sparks"
sparks.Rate = 0
sparks.Lifetime = NumberRange.new(0.8, 1.2)
sparks.Speed = NumberRange.new(8, 12)
sparks.SpreadAngle = Vector2.new(360, 360)
sparks.Color = ColorSequence.new(Color3.fromRGB(255, 210, 60), Color3.fromRGB(255, 60, 20))
sparks.Size = NumberSequence.new({ NumberSequenceKeypoint.new(0, 0.6), NumberSequenceKeypoint.new(1, 0) })
sparks.Transparency = NumberSequence.new(0, 1)
sparks.Acceleration = Vector3.new(0, -20, 0)
sparks.Parent = source

local bursts = 0
local function burst()
    bursts = bursts + 1
    sparks:Emit(40)
    store_set("bursts", tostring(bursts))
end

burst()
task.delay(0.5, burst)
store_set("emitter", table.concat({ sparks.ClassName, tostring(sparks.Rate), tostring(sparks.Lifetime),
    typeof(sparks.Color), typeof(sparks.Size) }, "|"))
