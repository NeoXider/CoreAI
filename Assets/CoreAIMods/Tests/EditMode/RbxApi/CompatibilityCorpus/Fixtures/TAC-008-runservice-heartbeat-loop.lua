local RunService = game:GetService("RunService")
local frames = 0
local connection

connection = RunService.Heartbeat:Connect(function(deltaTime)
    assert(deltaTime > 0)
    frames = frames + 1
    if frames == 3 then
        connection:Disconnect()
        workspace:SetAttribute("TierACorpusResult", "TAC-008-runservice-heartbeat-loop")
    end
end)
