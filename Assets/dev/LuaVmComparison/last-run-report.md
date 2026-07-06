# Lua VM comparison run — MoonSharp vs Lua-CSharp

Runners: MoonSharp, Lua-CSharp

## Sandbox — dangerous globals (want: absent on both)

| global | MoonSharp | Lua-CSharp |
|---|---|---|
| `os` | absent | absent |
| `io` | absent | absent |
| `debug` | absent | absent |
| `package` | PRESENT ⚠ | absent |
| `require` | absent | absent |
| `load` | absent | absent |
| `loadstring` | absent | absent |
| `dofile` | absent | absent |
| `loadfile` | absent | absent |

## Correctness

| case | expected | MoonSharp | Lua-CSharp | match |
|---|---|---|---|---|
| arithmetic | `15` | `15` | `15` | ✅ |
| string_ops | `AB5` | `AB5` | `AB5` | ✅ |
| table_ipairs | `6` | `6` | `6` | ✅ |
| closures | `2` | `2` | `2` | ✅ |
| recursion_fib15 | `610` | `610` | `610` | ✅ |
| metatables | `42` | `42` | `42` | ✅ |
| coroutines | `21` | `21` | `21` | ✅ |
| pcall_error | `false:str` | `false:str` | `false:str` | ✅ |

## Performance (mean per iteration; lower is better)

| case | MoonSharp µs | MoonSharp GC B | Lua-CSharp µs | Lua-CSharp GC B | speedup (MS/LC) |
|---|---|---|---|---|---|
| tight_loop_1e6 | 974910.8 | 0 | 81374.0 | 0 | 11.98× |
| fib30 | 6240500.6 | 0 | 1112661.7 | 0 | 5.61× |
| string_build_5k | 14185.1 | 0 | 70954.5 | 0 | 0.20× |
| table_churn_50k | 123211.5 | 0 | 19641.2 | 0 | 6.27× |
| host_call_1e5 | 329211.6 | 0 | 48983.2 | 0 | 6.72× |

_speedup >1 means Lua-CSharp is faster than MoonSharp on that case._

## Runaway halt — `while true do end` with a 500ms host budget

- **MoonSharp**: not halted — requires IDebugger (CoreAI: InstructionLimitDebugger); not run in this harness
- **Lua-CSharp**: HALTED ✅ — halted via CancellationToken after 502ms (LuaCanceledException)

