# Luau syntax, tasks and coroutines, signal and budget limits, clocks, reloading

- Luau syntax IS accepted and auto-downleveled to Lua 5.2 before compiling: `+=` (and
  `-=/*=//=/%=/^=/..=`), `continue`, `` `str{}` `` interpolation, if-then-else expressions, and
  type annotations/casts all work. Plain Lua 5.2 is unaffected. Nesting deeper than 200 levels
  is a syntax error.
- `typeof(v)` returns Roblox type names ("Instance", "Vector3", "EnumItem", ...). `warn(...)`
  writes to the mod log at Warn level (get_mod_logs shows it). Errors of a one-off execute_lua
  chunk carry no `[mod:...]` prefix.
- Signals: `:Connect()`, `:Once()`, and `:Wait()` work; firing queues handlers, and the queue flushes
  at every script-resumption point of the frame loop. `:ConnectParallel()` is `:Connect()` (runs
  serially, logs once per mod). Each handler gets its own copy of a table argument (no metatable).
  A handler that keeps re-firing its own signal is cut after 10 generations (SIGNAL_CASCADE);
  more than 16,384 handler calls of one mod in one resumption point are dropped (BUDGET_EXCEEDED).
- `task.wait/spawn/defer/delay/cancel` work. `task.synchronize/desynchronize` are no-ops that
  log once per mod. `task.wait(math.huge)`, `task.delay(math.huge, f)` and
  `WaitForChild(name, math.huge)` park until cancelled or the mod unloads; a NaN duration is
  BAD_ARGUMENT. `task.cancel` on a finished task does nothing. A `task.defer` from a handler runs
  in the same resumption point.
- `task.spawn/defer/delay` also take a task handle they returned (`task.spawn(t) == t`): a parked
  task resumes, a deferred/delayed one is rescheduled; a finished task, one inside a wait, another
  mod's task, or a `coroutine.create` thread is BAD_ARGUMENT. `coroutine.yield()` inside a task
  parks it until `task.spawn(t, ...)`, whose extra args are what yield returns; inside a handler
  or the main chunk it is CONTEXT_VIOLATION. `task.wait`, `signal:Wait`, `WaitForChild` and `InvokeServer`
  inside a `coroutine.create` coroutine raise CONTEXT_VIOLATION — use `task.spawn` instead.
- `coroutine.resume` only resumes a `coroutine.create` coroutine. Given a task, handler or main-chunk
  thread (a `coroutine.running()` value) it returns `false, "cannot resume a task or signal-handler
  thread with coroutine.resume; ..."` and leaves the thread alone: resume a parked task with
  `task.spawn(t)`, where `t` is the handle task.spawn/defer/delay returned.
- Budgets: an xpcall handler does not run for a budget cut. Only the code that
  `coroutine.resume`d a `coroutine.create` coroutine that was cut sees `false, err`, and that
  coroutine is dead. A `coroutine.create` body shares the memory budget of the code that resumes it.
  `string.find/match/gmatch/gsub` stop after 5,000,000 matcher steps per call and
  `gsub`/`string.format` results are capped at 1,000,000 chars (both catchable errors). Yielding
  (task.wait, coroutine.yield) inside a `__tostring`, a `table.sort` comparator, a
  `__pairs`/`__ipairs` metamethod, `__index` or a gsub replacement function raises "attempt to yield
  across a C-call boundary" (as in Luau); yielding inside pcall is fine. A `coroutine.create` body
  (and a thread task.spawn runs at once) gets at most what the code resuming it has left of steps,
  time and memory. Library functions that call back into your Lua share one cap of 128 levels: a
  pcall/xpcall body or a gsub function counts 1; a `__tostring` via
  tostring/print/warn/string.format, a `table.sort` comparator, `__pairs`/`__ipairs`,
  `coroutine.resume` or an immediate task.spawn counts 2 (pcall nests 128 deep, sort/tostring 63).
  The next raises a catchable "C stack overflow (...)". Plain recursion is not limited this way.
- Clocks: `os.time()` (Unix seconds; `os.time({year=,month=,day=[,hour=,min=,sec=]})` reads the
  table as UTC, hour defaults to 12, a field that is not a number counts as missing — a missing
  year/month/day is BAD_ARGUMENT — and a date before 1970 returns nil), `os.clock()`, `time()`
  (scaled game time), and `workspace:GetServerTimeNow()` (Unix seconds every player agrees on: a
  client reads the server's clock once joined, from then on it never goes backwards, and it stands
  still while the server's clock does). `os.date(fmt[, t])` (strftime conversions, `"*t"`) and
  `os.difftime` exist and are UTC with or without `"!"`; `DateTime` is not implemented.
- Standard library = Luau's: `table.find/clear/create/clone/freeze/isfrozen/move/maxn`, `unpack`,
  `string.split`, `string.format` (`%*`, C output: `%d` truncates, `%x` of -1), `utf8.*`, `bit32.*`,
  `math.lerp/map/log10/isnan/isinf/isfinite`, `coroutine.wrap/isyieldable/close`,
  `debug.traceback/info/profilebegin`, `newproxy`, `gcinfo`. Strings hold UTF-16 chars: `#s` counts
  chars, `utf8.len` code points, and utf8 positions are the ones `string.sub` takes. `table.freeze`
  stops library writes (insert, remove, sort, rawset, ...) but not `t.k = v`. One call builds at
  most 1,048,576 elements; `unpack` returns at most 8,000. `buffer`, `vector` and
  `string.pack/unpack` are NOT_IMPLEMENTED (use `string.char`/`string.byte`, `Vector3`);
  `loadstring` returns nil, "loadstring() is not available"; `setfenv` is refused. `goto`,
  `::labels::` and `_ENV` run here but not in Roblox: do not write them.
- Reloading: `manage_mods reload` (and the Hub's Save & run) first removes the previous run's
  startup objects — what its main chunk built before its first yield and the mod still owns — so a
  mod that builds its scene at startup never duplicates it; objects made later by handlers, tasks or
  players stay. Pass keep_objects=true to keep everything and build next to it. Two budget cuts in a
  row quarantine the mod and keep it from starting with the next game.
- A player who leaves takes the mods loaded for them along: they are unloaded (after their running
  code returns) and start again on the next world load.
