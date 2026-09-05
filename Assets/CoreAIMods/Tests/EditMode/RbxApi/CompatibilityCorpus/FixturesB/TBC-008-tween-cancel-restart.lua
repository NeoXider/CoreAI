local TweenService = game:GetService("TweenService")

local part = Instance.new("Part")
part.Parent = workspace

local first = TweenService:Create(part, TweenInfo.new(2), { Transparency = 1 })
local states = {}
first.Completed:Connect(function(state)
    table.insert(states, state)
    if state == Enum.PlaybackState.Cancelled then
        workspace:SetAttribute("TierACorpusResult", "TBC-008-tween-cancel-restart")
    end
end)

first:Play()
first:Cancel()
