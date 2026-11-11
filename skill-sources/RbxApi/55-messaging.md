# RemoteEvent, RemoteFunction, BindableEvent/Function between mods

- `RemoteEvent`/`UnreliableRemoteEvent` fire one-way; an `UnreliableRemoteEvent` payload over
  1,000 bytes raises PAYLOAD_TOO_LARGE, in solo too. `RemoteFunction:InvokeServer/InvokeClient`
  yields for a reply and is bounded to 30 scheduler seconds: a missing or stalled receiver raises
  an error instead of yielding forever (a deliberate deviation from Roblox). Wrap a fallible
  invoke in `pcall`.
- `BindableEvent` (`:Fire(...)`, `.Event`) and `BindableFunction` (`:Invoke(...)`, `.OnInvoke = fn`) work as in
  Roblox between scripts and mods: use them, not workspace attributes, to talk between mods. `Fire` never
  yields; `Invoke` yields until the callback returns, runs it as the mod that assigned it, raises the
  callback's error in the invoker, and waits (no timeout) while no `OnInvoke` is set. `OnInvoke` is write-only.
  Arguments and results are copied by the RemoteEvent rules: tables copied without metatable, non-string keys
  become strings, functions arrive as nil; a mixed array/dictionary table or a cycle raises BAD_ARGUMENT, more
  than 64 KiB PAYLOAD_TOO_LARGE.
