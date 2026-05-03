# Bitwyck Architecture

## Goal

A C# meta-harness that wraps Microsoft's BitNet 1.58-bit inference engine and
exposes a true cognitive loop — sensory input, episodic recall, attentional
gating, deliberative reasoning, tool execution, and self-evolution — without
relying on a monolithic LLM to "know everything."

## Layering

```
┌─────────────────────────────────────────────────────────────────┐
│  Bitwyck.CLI       Commands (ask, repl, daemon, diagnose)       │
└────────────────────────────┬────────────────────────────────────┘
                             │  Generic Host DI
┌────────────────────────────▼────────────────────────────────────┐
│  Bitwyck.Runtime                                                │
│    Cognition      CognitiveLoop · ParallelCognitiveDispatcher  │
│    Sensors        Text · Webhook · Whisper · Vision            │
│    Memory         EngramAdapter · Recall · Commit · Compiler   │
│    Inference      BitNetServerHost · ServerClient · CliClient  │
│    Tooling        Interceptor · Registry · BuiltIns            │
│    Lifecycle      ChronoScheduler · IdentityStateUpdater       │
│                   SkillSynthesizer · SkillCompiler             │
└────────────────────────────┬────────────────────────────────────┘
                             │  abstract contracts only
┌────────────────────────────▼────────────────────────────────────┐
│  Bitwyck.Core     Interfaces · Models · Utilities (no I/O)     │
└─────────────────────────────────────────────────────────────────┘
```

## Cognitive cycle

The blueprint maps to concrete components:

| Stage | Component | Notes |
|---|---|---|
| Sensory ingestion | `Sensors/*` | Each sensor produces a `SensoryEvent`. |
| State retrieval | `EngramRecallService` + `IdentityStateStore` | Hybrid + graph-expanded recall. |
| Resource allocation | `ContextCompiler` | Token budget; aggressive prune. |
| System bias | `DefaultSystemBiasProvider` | Tier-aware temperature clamping. |
| Cognitive routing | `EnergyManager` (`ICognitiveRouter`) + `ModelCascade` | Recall confidence + payload size → tier. |
| Execution | `BitNetServerClient` / `BitNetCliClient` | OpenAI-compatible streaming. |
| Tool interception | `XmlToolInterceptor` | Streaming state machine; pause/exec/resume. |
| Parallel threading | `ParallelCognitiveDispatcher` | Fan-out to 1B sub-agents. |
| Chrono triggers | `ChronoScheduler` + `IChronoJob` | Cron-driven background ticks. |
| Skill synthesis | `SkillSynthesizer` (Roslyn) | Successful tool chains → compiled `ITool`. |
| Identity update | `IdentityStateUpdater` (nightly) | Episodic engrams → compressed state. |
| State commitment | `EngramCommitService` | Per-turn write-back into the brain. |

## Model cascade (System 1 / System 2)

| Tier | Model | Port | Triggers |
|---|---|---|---|
| Reflex_1B | Falcon3-1B-Instruct-1.58bit | 8081 | High-confidence recall (score ≥ 0.85), tool-args formatting |
| Standard_3B | Falcon3-3B-Instruct-1.58bit | 8082 | Default conversational tier |
| Deliberate_7B | Falcon3-7B-Instruct-1.58bit | 8083 (lazy) | Multi-step planning, code generation |
| DeepReason_10B | Falcon3-10B-Instruct-1.58bit | 8084 (lazy) | Novel problems, deep reasoning, dialectic synthesis |

`EnergyManager` returns a `RouteDecision` with both the selected tier and a
fallback chain so the loop can degrade gracefully when a higher tier is still
warming up or has crashed.

## Tool-call interception protocol

The harness instructs the LLM to emit calls as:

```
<call>tool_name|arg1|arg2|...</call>
```

The streaming token consumer is `XmlToolInterceptor`. Its state machine handles
tag boundaries that fall mid-chunk (`<`, `<call`, `<call>read_fi` may all
arrive separately). On full detection it pauses the stream, runs the tool via
`IToolRegistry`, and replaces the original `<call>...</call>` substring in
the assembled output with the tool's `<observation>...</observation>`. Multiple
calls per turn are supported and returned in detection order.

## Engram memory

Backed by `McpEngramMemory.Core` v0.9.0 — an in-process SQLite + ONNX
embedding system shared with RSRM and AgentNeo. The default namespace is
`bitwyck-episodic`. Each turn writes one `episodic-{timestamp}-{shortHash}`
entry that captures the trigger payload, route decision, final answer, and
chain of `(call, observation)` pairs.

## Skill synthesis

`SkillSynthesizer` watches for tool chains of length ≥ 4 that succeed and
were not previously synthesized. It generates a C# `ITool` source file whose
`ExecuteAsync` replays the chain end-to-end against the live `IToolRegistry`,
compiles the source via Roslyn, and adds the resulting type to a
`SkillLifecycle` state machine. Synthesized skills move
`Proposed → Sandboxed → UnderReview → Promoted` (or `Rejected`) before being
permanently registered.

## Autonomic chrono-triggers

`ChronoScheduler` is an `IHostedService` running a 30-second tick. Each tick
checks every registered `IChronoJob` against its cron expression. The
canonical job is `IdentityStateUpdater`, which fires nightly at 03:00, summarizes
the day's episodic engrams via the Deliberate_7B tier, and writes back an
updated `UserIdentityState` JSON file.

## Failure modes & degradation

- Sensors throw `SensorUnavailableException` when their backing model is
  missing; the loop logs degraded mode and continues without that channel.
- `BitNetServerHost` reports `Initializing` while a model loads; the
  `EnergyManager`'s fallback chain routes around in-flight tiers.
- Tool execution failures emit `<observation error="true">...</observation>`
  back into the prompt; the LLM is expected to recover.
- All exceptions inside `ChronoScheduler.TickAsync` are caught and logged so
  one bad job can't kill the daemon.
