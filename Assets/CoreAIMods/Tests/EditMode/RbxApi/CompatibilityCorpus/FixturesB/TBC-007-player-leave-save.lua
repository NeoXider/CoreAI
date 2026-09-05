local Players = game:GetService("Players")

Players.PlayerRemoving:Connect(function(player, reason)
    workspace:SetAttribute("SavedUserId", player.UserId)
    workspace:SetAttribute("TierACorpusResult", "TBC-007-player-leave-save")
end)

local me = Players:GetPlayers()[1]
me:Kick("session over")
