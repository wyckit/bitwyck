# Cognitive Cycle — Per-Turn Execution Trace

## High-level sequence

```
SensoryEvent
   │
   ▼
EngramRecallService ──── hybrid+graph search ────► [Engram, Engram, …]
   │
   ▼
IdentityStateStore ──── load JSON ────► UserIdentityState
   │
   ▼
EnergyManager ──── score recall, length, identity ────► RouteDecision (tier + fallback chain)
   │
   ▼
ISystemBiasProvider ──── apply tier-aware overrides ────► SystemBias
   │
   ▼
ContextCompiler ──── prune to budget ────► IReadOnlyList<InferenceMessage>
   │
   ▼
IBitNetInferenceClient.StreamAsync ──── OpenAI/SSE ────► IAsyncEnumerable<InferenceTokenChunk>
   │
   ▼
XmlToolInterceptor ──── detect <call>...</call> ────► (pause, dispatch, inject <observation>...</observation>, resume)
   │                                                       │
   │                                                       ▼
   │                                              IToolRegistry.ExecuteAsync
   │                                                       │
   ▼◄──────────────────────────────────────────────────────┘
final assembled output
   │
   ▼
SkillSynthesizer (if novel chain ≥ 4 succeeded) ──── Roslyn compile ──► new ITool added to registry (after lifecycle review)
   │
   ▼
EngramCommitService ──── write episodic entry ────► engram.db
```

## Streaming + tool interception in detail

The interceptor maintains a sliding text buffer over the token stream:

1. Every chunk's `Token` is appended to the buffer.
2. The buffer is scanned for a literal `<call>`. If not found and no partial
   match exists at the tail, the chunk is forwarded to the consumer as-is.
3. If a partial match (e.g., the buffer ends with `<ca`) sits at the tail, the
   forwarder holds back enough trailing characters so a tag straddling the
   boundary isn't split.
4. Once `<call>` is found, the scan continues for `</call>`.
5. When the closing tag arrives, the inner content is parsed via
   `XmlCallParser.TryParseInner`, the tool is dispatched, and the
   `<call>...</call>` substring is replaced by `<observation>...</observation>`.
6. After replacement, the loop continues with the post-call buffer.

## Routing heuristic (default)

```
score = recalled[0].Score if recalled.Count > 0 else 0
if score ≥ 0.85           → Reflex_1B        (familiar; trust memory)
elif payload.Length > 1500 → DeepReason_10B   (long; needs deep reasoning)
elif no recall            → DeepReason_10B   (novel; explore)
elif payload.Length > 400 → Deliberate_7B    (medium; some planning)
else                      → Standard_3B      (default conversational tier)
```

`RouteDecision.FallbackChain` always lists every tier from the chosen one
downward, so if the primary is unavailable the loop tries the next.

## Skill-synthesis trigger

A chain becomes a candidate when:
- All `ToolResult.Success == true`
- `len(ToolCalls) >= 4`
- The exact (toolName, args-shape) sequence has not been synthesized before
- The original goal is non-trivial (length > 32 chars)

The synthesizer emits source like `Synth_<12-hex>.cs` containing a class
implementing `ITool` whose `ExecuteAsync` replays each step. Roslyn compiles
in-memory; the resulting type enters `SkillLifecycle.Proposed`. Promotion to
the live registry currently requires manual or chrono-job confirmation.

## State commitment payload

```jsonc
{
  "id": "episodic-20260503-153214-7a3b",
  "namespace": "bitwyck-episodic",
  "category": "turn",
  "metadata": {
    "correlationId": "...",
    "triggerChannel": "Text",
    "selectedTier": "Standard_3B",
    "toolCallCount": 2,
    "promptTokens": 312,
    "completionTokens": 187
  },
  "text": "<trigger>...</trigger>\n<route>...</route>\n<answer>...</answer>\n<calls>...</calls>"
}
```
