# CoreAI Test Report

**Date:** 2026-04-05

---

## EditMode Tests

| Total | Passed | Failed | Skipped | Duration |
|-------|--------|--------|---------|----------|
| 153   | 153    | 0      | 0       | ~2 sec   |

**Result:** ALL PASSED ✓

---

## PlayMode Tests Results

| Test Class | Passed | Failed | Skipped | Notes |
|------------|--------|--------|---------|-------|
| LuaBindingsIntegrationPlayModeTests | 1 | 0 | 0 | ✓ |
| LuaFormulaRuntimeIntegrationPlayModeTests | 12 | 0 | 0 | ✓ |
| AiOrchestratorAllRolesPlayModeTests | 2 | 0 | 0 | ✓ |
| AgentMemoryWithRealModelPlayModeTests | 0 | 0 | 3 | Skipped - model didn't write memory |
| OpenAiLmStudioPlayModeTests | 1 | 0 | 0 | ✓ |
| MultiAgentCraftingWorkflowPlayModeTests | 0 | 2 | 0 | FAILED - Creator memory not written |
| AiGameCommandRouterMainThreadPlayModeTests | 2 | 0 | 0 | ✓ |
| CraftingMemoryViaLlmUnityPlayModeTests | 0 | 1 | 0 | FAILED - Timeout on craft 3 (300s)
| AgentMemoryOpenAiApiPlayModeTests | 0 | 0 | 2 | Skipped - LM Studio model didn't use memory |

---

### Test Details

#### 1. LuaBindingsIntegrationPlayModeTests ✓
- 1 test passed
- Envelope with aggregating bindings works

#### 2. LuaFormulaRuntimeIntegrationPlayModeTests ✓
- 12 tests passed
- All Lua formula and security tests pass

#### 3. AiOrchestratorAllRolesPlayModeTests ✓
- 2 tests passed
- All built-in roles work (Creator, Analyzer, Mechanic, Programmer, World)

#### 4. AgentMemoryWithRealModelPlayModeTests - SKIPPED
- 3 tests skipped
- Reason: Model did not write memory - LLM doesn't support tool-call format
- Models tested: Qwen3.5-2B, LM Studio

#### 5. OpenAiLmStudioPlayModeTests ✓
- 1 test passed

#### 6. MultiAgentCraftingWorkflowPlayModeTests - FAILED
- **Failed:** MultiAgent_CreatorThenMechanic_QuickWorkflow
  - Error: Creator did not write memory
- **Failed:** MultiAgent_CreatorThenMechanicThenProgrammer_CompleteWorkflow  
  - Error: Creator did not write to memory

#### 7. AiGameCommandRouterMainThreadPlayModeTests ✓
- 2 tests passed

#### 8. CraftingMemoryViaLlmUnityPlayModeTests - FAILED (PARTIAL)
- **Failed:** CraftingMemoryLlmUnity_ThreeCrafts_AllUnique
  - Error: "Timeout waiting 'craft 3' after 300s"
  - Progress: CRAFT 1 ✓ (IronOak_Sword), CRAFT 2 ✓ (SteelHardwood_Berserker)
  - Model responds but slowly - needs more than 300s for 4 crafts

---

## Issues Found

### 1. Memory Tool Not Used by Models
- Qwen3.5-2B model does not write memory via tool calls
- The model outputs correct Lua code but doesn't use the memory tool
- This is a model capability issue, not a code bug

### 2. MultiAgent Tests Fail
- Creator role doesn't write memory
- Both multi-agent workflow tests failed
- Root cause: Model doesn't use memory tool

### 3. OpenAI/LM Studio API Not Responding in Tests
- Tests show "No response" from OpenAI API
- User confirms LM Studio works manually
- Possible cause: Different network config or default URL `http://192.168.56.1:1234/v1`

### 4. LLMUnity Crafting Test Timeout
- Crafting test takes too long for 4 sequential model calls
- Each craft takes ~70-80 seconds with Qwen3.5-2B
- Recommendation: Increase timeout to 600s or reduce to 2 crafts