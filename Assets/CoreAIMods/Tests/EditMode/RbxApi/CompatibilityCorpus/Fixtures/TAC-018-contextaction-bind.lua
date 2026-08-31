local ContextActionService = game:GetService("ContextActionService")

ContextActionService:BindAction("Reload", function(actionName, inputState, inputObject)
    if inputState ~= Enum.UserInputState.Begin then
        return Enum.ContextActionResult.Pass
    end

    workspace:SetAttribute("TierACorpusResult", "TAC-018-contextaction-bind")
end, true, Enum.KeyCode.R, Enum.KeyCode.ButtonX)
