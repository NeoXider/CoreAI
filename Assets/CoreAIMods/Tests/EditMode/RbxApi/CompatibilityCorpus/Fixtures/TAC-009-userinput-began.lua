local UserInputService = game:GetService("UserInputService")

UserInputService.InputBegan:Connect(function(input, gameProcessedEvent)
    if gameProcessedEvent then
        return
    end

    if input.KeyCode == Enum.KeyCode.E then
        assert(input.UserInputType == Enum.UserInputType.Keyboard)
        workspace:SetAttribute("TierACorpusResult", "TAC-009-userinput-began")
    end
end)
