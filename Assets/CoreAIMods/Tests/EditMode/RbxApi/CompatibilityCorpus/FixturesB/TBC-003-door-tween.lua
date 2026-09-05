local TweenService = game:GetService("TweenService")

local door = Instance.new("Part")
door.Name = "Door"
door.Position = Vector3.new(0, 5, 0)
door.Parent = workspace

local opened = TweenService:Create(door, TweenInfo.new(0.25), {
    Position = door.Position + Vector3.new(0, 10, 0),
    Transparency = 0.5,
})

opened.Completed:Connect(function(state)
    if state == Enum.PlaybackState.Completed and door.Transparency >= 0.5 then
        workspace:SetAttribute("TierACorpusResult", "TBC-003-door-tween")
    end
end)

opened:Play()
