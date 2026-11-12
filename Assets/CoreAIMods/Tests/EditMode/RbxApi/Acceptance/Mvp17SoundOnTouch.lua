-- MVP17 sound-on-touch fixture: a pad that plays its chime when something touches it, once per touch while it is
-- not already playing, heard from the pad. "chime" is a sound the host registered (SoundService:GetSoundIds()).
local pad = Instance.new("Part")
pad.Name = "ChimePad"
pad.Anchored = true
pad.Size = Vector3.new(4, 1, 4)
pad.Position = Vector3.new(0, 0.5, 0)
pad.Parent = workspace

local chime = Instance.new("Sound")
chime.Name = "Chime"
chime.SoundId = "chime"
chime.Volume = 0.8
chime.RollOffMaxDistance = 60
chime.Parent = pad

local plays = 0
chime.Played:Connect(function(soundId)
    plays = plays + 1
    store_set("played", soundId .. " x" .. plays)
end)
chime.Ended:Connect(function(soundId)
    store_set("ended", soundId)
end)

pad.Touched:Connect(function(hit)
    if chime.IsPlaying then
        return
    end
    chime:Play()
end)

store_set("ready", tostring(chime.IsLoaded) .. "|" .. table.concat(game:GetService("SoundService"):GetSoundIds(), ","))
