local TweenService = game:GetService("TweenService")
local part = Instance.new("Part")
part.Parent = workspace

local tween = TweenService:Create(part, TweenInfo.new(0.5), {
    Position = part.Position + Vector3.new(0, 10, 0),
    Transparency = 0.5,
})

tween.Completed:Connect(function(playbackState)
    if playbackState == Enum.PlaybackState.Completed then
        workspace:SetAttribute("TierACorpusResult", "TAC-019-tween-create")
    end
end)
tween:Play()
