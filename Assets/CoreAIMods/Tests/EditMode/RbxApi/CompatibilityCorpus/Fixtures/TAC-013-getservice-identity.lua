local Workspace = game:GetService("Workspace")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

assert(Workspace == workspace)
assert(game:FindService("RunService") == RunService)
assert(RunService.ClassName == "RunService")
assert(UserInputService:IsA("Instance"))
workspace:SetAttribute("TierACorpusResult", "TAC-013-getservice-identity")
