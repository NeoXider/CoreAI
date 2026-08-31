local connection
connection = workspace.ChildAdded:Connect(function(child)
    assert(child.Name == "TierAConnectedChild")
    assert(connection.Connected == true)
    connection:Disconnect()
    assert(connection.Connected == false)
    workspace:SetAttribute("TierACorpusResult", "TAC-004-signal-connect-disconnect")
end)

local child = Instance.new("Folder")
child.Name = "TierAConnectedChild"
child.Parent = workspace
