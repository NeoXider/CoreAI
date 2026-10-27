# Editor runtime implementation candidate

Status: finalization in progress. Candidate files are staged only. No build, test, Unity, or live runtime verification has been performed by this worker.

## Scope and invariants

- Preserve legacy entry points using forwarding overloads.
- Lua Save & run uses the existing Hub save receipt and exact-source conflict check. Standalone Run returns Unsupported before effects.
- Python returns Unsupported with no runtime, store, network, or world effects.
- Immutable per-invocation limits: 100 ms and 500,000 VM steps by default; no global quota mutation or Task.Run.
- Initial Rbx resume cancellation cancels the coroutine-owned CTS so the xpcall guard remains effective. Registration and editor limits are detached after the first yield.
- Arbitrary native C# cannot be preempted; later resumes use normal host budgets.

## Verification

Milestone 1: static tracing confirmed initial-only Rbx cancellation/budgets, forwarding legacy overloads, and typed host failure wrapping. Corrected budget classification to follow IScriptHostFailure.HostException; cancellation keeps its explicit result. Added contract regressions and corrected aggregate undo/redo limits on coalesce/amend/undo/redo. Executable checks are reserved to the root agent.

## Candidate and baseline manifest

Hashes below were captured before finalization; refresh after each milestone. A missing baseline denotes a new file.

| Path | Live baseline SHA256 | Candidate SHA256 |
| --- | --- | --- |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeEditorContracts.cs | NEW | F0C3C996D65D0141761FE8947D29540639D8E3AE2AD5FD82CB6A54C5C066DD87 |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeEditorHistory.cs | NEW | 31E4833D117B0E228E5B636D25F270D145E5F004177619E53B41C26D986225FE |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeEditorTextUtility.cs | NEW | E335FAE76B2AF0A7AA06F6E98620398503C3BA8ABDCD7DAA897A147FEB1CAC3B |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeLineSelection.cs | NEW | B87B177C1BB0B871873B3C592D71114A3CE126995A0F86936D5165C86828647C |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeSyntaxPalette.cs | NEW | 9B131A7F8B3671B284B3E8E29F335FA7C8F526CC7DCE1A5DA7AD16D264F9B903 |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/PythonSyntaxHighlighter.cs | NEW | 66DC35F283E6813B294FA259F92A373102105358B253AB2AB4DA0360379DF45E |
| Assets/CoreAI/Runtime/Core/Features/CodeEditing/SyntaxHighlightText.cs | NEW | 87937A69EE48ECA91C2821F5AD4DB76C616484D1236E0EE311798A2FE73E9804 |
| Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/LuaCodePlaygroundRuntime.cs | NEW | 64B70C691C2C12B29187C11BA9D984E9C966AFCE69DFCF9AA9BEDB22841DD88F |
| Assets/CoreAIMods/Runtime/HubIntegration/LuaCsModRuntimeHubService.cs | CC1910A4BA74FCB4263A563B9C9BE99CC7B88E2DD60B2445BE56F1F73165C2EB | DC5DF3EBF6F1BB37DA18501E3FF9FA9627565528D51C37D6C86A2439AB465157 |
| Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs | FB84B2A24E6A851ECA0829F3387A504DE4D21E638B3941C442FA1E45C2D125A1 | C5DA56ED1DD15C35B0645CC6BB571334B7D9095466B65B086153B9E8DDA13CE5 |
| Assets/CoreAIMods/Runtime/LuaExecution/ILuaModRuntime.cs | 1176573072D350BFFCC71E98C5950568D9EE12FD556CDBBD459497C4A1EC973F | E8AC5D635B21E4D20584350D666821D99C677587F63981194D8F2BE776D232B0 |
| Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs | 40CC239B7F40270776E4479ED4A357E33A847D1D85C359E8F1F72B66A8E43CA4 | 1EDF1421E20C2B0FCC1D42B3C28B41D895470959BBFC0A165205AF2BDFB538A6 |
| Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsCoroutineHandle.cs | A90717AACBB9A274384321A7A83D10BE5DF004B8470CB2C1F2E390BE38D64B65 | 3172DF6839E9C38DDEB890C9702AB89C83675EFECA3E76865D7FB6652BBD0C15 |
| Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs | 9BFCB4FB4AFF78D9F7FADE8FCCF1A66E32E5A3924B511AD57FA03E26E1EC3C62 | 2BB4EBB40F97FEE2DEE7C269EDDAD3E40A6413082E8A9D43BB9E6F9C5DB093A9 |
| Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs | C0D5664A30877BEDFC1E9F905510C644D86DB935919AFE64638AC2647F2BB4B3 | B5704226E8E14DDF4AD75AFFFE629DFA93BEDBDECBF13F84BE1C92AC489DF4B3 |
| Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptCoroutine.cs | 994FFD2110BE828635768FA956C976C16CE42B97A49310857403F08F79257BD0 | 191FF392CB241E4F80A4F6A4AB5AF7E42A9EBF62512F8C17855CE15155216AF2 |
