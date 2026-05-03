# Bitwyck — Autonomous Cognitive Harness

A C# .NET 10 meta-harness around BitNet 1.58-bit models that treats the LLM as a
stateless logic processor. Memory, tools, planning, and self-evolution live in
the C# scaffolding, not in the model.

## Architecture in one paragraph

A trigger (text, webhook, audio, vision, or a chrono-tick) becomes a
`SensoryEvent`. The harness recalls relevant engrams from a SQLite-backed
`McpEngramMemory.Core` brain, loads the cross-session `UserIdentityState`,
applies system bias overrides (temperature, persona, risk tolerance), and
routes the turn to the smallest-sufficient BitNet model tier (1B / 3B / 7B /
10B). The model streams output; an XML interceptor watches for
`<call>tool|args</call>` blocks, pauses generation, executes the tool, and
injects `<observation>...</observation>` back into the prompt. Successful
multi-step tool chains can be promoted by the skill synthesizer into compiled
C# `ITool` classes added to the registry. A nightly chrono-trigger summarizes
the day's episodic engrams into an updated `UserIdentityState`.

## Build

```powershell
dotnet build C:/Software/research/bitwyck/Bitwyck.slnx
dotnet test  C:/Software/research/bitwyck/Bitwyck.slnx
```

Requires `.NET 10 SDK` (verified with 10.0.201).

## Quick start

```powershell
# One-shot prompt (mocked LLM in tests, real BitNet when configured):
dotnet run --project C:/Software/research/bitwyck/src/Bitwyck.CLI -- ask "List the files in src and summarize"

# Diagnose subsystems (server alive, models present, engram OK):
dotnet run --project C:/Software/research/bitwyck/src/Bitwyck.CLI -- diagnose

# Daemon — runs the chrono-scheduler in foreground, listens for sensors:
dotnet run --project C:/Software/research/bitwyck/src/Bitwyck.CLI -- daemon
```

Models live at `C:/Software/research/BitNet/models/Falcon3-{1B,3B,7B,10B}-Instruct-1.58bit/`.

The default `appsettings.json` wires:
- Reflex_1B → port 8081
- Standard_3B → port 8082
- Deliberate_7B → port 8083 (lazy)
- DeepReason_10B → port 8084 (lazy)

## Project layout

```
src/
├── Bitwyck.Core/         interfaces, models, parsers (no I/O)
├── Bitwyck.Runtime/      inference, memory, tooling, cognition, lifecycle, sensors
└── Bitwyck.CLI/          entry point + commands

tests/
└── Bitwyck.Tests/        xUnit unit + integration tests

docs/
├── architecture/         Architecture.md, CognitiveCycle.md
├── guides/               QuickStart.md, ToolAuthoring.md
└── reference/            CronSyntax.md
```

## End-to-end cycle

1. **Awake** — external trigger or chrono fires
2. **Sense** — sensor normalizes input → `SensoryEvent`
3. **Recall** — engram hybrid search + identity state load
4. **Compile** — `ContextCompiler` token-budgets the prompt
5. **Route** — `EnergyManager` picks tier (high recall confidence → 1B; novel/long → 10B)
6. **Execute** — `IBitNetInferenceClient.StreamAsync` returns tokens
7. **Intercept** — `XmlToolInterceptor` detects `<call>...</call>`, runs the tool, injects observation, resumes
8. **Synthesize** — if a 4+-step novel chain succeeded, `SkillSynthesizer` writes a new `ITool` C# file
9. **Commit** — final answer + chain-of-action committed back to engram via `EngramCommitService`
10. **Sleep** — nightly chrono-job updates `UserIdentityState`

## License & status

Internal / research. Built atop Microsoft BitNet (MIT) and McpEngramMemory.Core.
