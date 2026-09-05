workspace.Gravity = 60

local humanoid = Instance.new("Humanoid")
humanoid.Parent = workspace
humanoid.UseJumpPower = false
humanoid.JumpHeight = 20

humanoid.Jumping:Connect(function(active)
    if active and workspace.Gravity == 60 then
        workspace:SetAttribute("TierACorpusResult", "TBC-010-gravity-low-jump")
    end
end)

humanoid.Jump = true
