local config = Instance.new("Folder")
config.Name = "Config"
config.Parent = workspace
config:SetAttribute("Speed", 24)

local humanoid = Instance.new("Humanoid")
humanoid.Parent = workspace
humanoid.WalkSpeed = config:GetAttribute("Speed")

config:GetAttributeChangedSignal("Speed"):Connect(function()
    humanoid.WalkSpeed = config:GetAttribute("Speed")
    if humanoid.WalkSpeed == 32 then
        workspace:SetAttribute("TierACorpusResult", "TBC-009-attribute-driven-config")
    end
end)

config:SetAttribute("Speed", 32)
