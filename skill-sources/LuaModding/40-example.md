# A complete mod: a miniature falling-block game

Shows: Rbx-style build (Instance.new / workspace), reload-safe root, input with edge
detection, gravity accumulator, store persistence, an event another mod (or the host) can send.

```lua
-- Reload-safe root: drop the previous generation before building this one.
local prev = workspace:FindFirstChild('MiniGameRoot')
if prev then prev:Destroy() end
local root = Instance.new('Model')
root.Name = 'MiniGameRoot'
root.Parent = workspace

local score = tonumber(store_get('score')) or 0
local px, py = 0, 40                              -- studs (1 stud = 0.28 m)
local player = Instance.new('Part')
player.Name = 'MiniGamePlayer'
player.Size = Vector3.new(4, 4, 4)
player.Position = Vector3.new(px, py, 0)
player.Color = Color3.fromHex('#00CCFF')
player.Anchored = true
player.Parent = root

local was_w = false
hooks_every(0.05, function()                      -- input at 20 Hz
  if input_key('a') and px > -16 then px = px - 4 end
  if input_key('d') and px <  16 then px = px + 4 end
  local w = input_key('w')                        -- derived edge, never input_key_down here
  if w and not was_w then score = score + 1 end
  was_w = w
  player.Position = Vector3.new(px, py, 0)
end)

local acc = 0
hooks_every(0.1, function()                       -- gravity with accumulator
  acc = acc + (input_key('s') and 5 or 1)
  if acc < 5 then return end
  acc = 0
  py = py - 4
  if py <= 0 then
    py = 40
    score = score + 10
    store_set('score', tostring(score))
    report('landed, score=' .. score)
  end
end)

hooks_on('minigame_reset', function(evt, payload) -- external control via events_emit
  score = 0
  store_set('score', '0')
end)
```
