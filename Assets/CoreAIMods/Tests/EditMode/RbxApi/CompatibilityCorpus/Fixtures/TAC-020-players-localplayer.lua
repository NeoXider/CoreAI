local Players = game:GetService("Players")
local player = Players.LocalPlayer
local playerGui = player:WaitForChild("PlayerGui")

assert(playerGui.Parent == player)
workspace:SetAttribute("TierACorpusResult", "TAC-020-players-localplayer")
