# Complex PlayMode Scenarios

This folder is for long-running, behavior-heavy, production-like PlayMode scenarios (assembly
`CoreAI.Tests.PlayMode.Scenarios`; both fixtures are compiled only with `COREAI_LLM` and not on WebGL):

- `FullPipelineE2EPlayModeTests` - one RPG session against a real model: SkillSet catalog and crafting
  tools, memory read-back in a later turn, multi-turn chat history, and routing from the Crafting to the
  Combat skill (`FullPipeline_Skills_Tools_Memory_MultiTurn`).
- `MerchantBehaviorChatWithToolsPlayModeTests` - merchant economy rules without a model
  (`MerchantEconomy_BuyWithoutDiscount_DoesNotMutateOnInsufficientGold`,
  `MerchantEconomy_DiscountEnablesPurchaseWithinPlayerBudget`) and the `[Explicit]` live negotiation
  `MerchantChatWithTools_FullNegotiationFlow_CompletesPurchase` (greet, list goods, fail an expensive
  purchase, negotiate a discount, buy).

Keep these scenarios separate from fast smoke/integration PlayMode tests so they can be run as an isolated suite.
The shared test rules are in [Tests/README.md](../../../README.md).
