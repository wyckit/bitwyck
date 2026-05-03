# Cron Syntax

`ChronoScheduler` accepts a 5-field cron expression: `m h d M w`.

| Field | Range | Notes |
|---|---|---|
| `m` minute | 0–59 | |
| `h` hour | 0–23 | 24-hour |
| `d` day-of-month | 1–31 | |
| `M` month | 1–12 | |
| `w` day-of-week | 0–6 | 0 = Sunday |

## Operators

- `*` — wildcard (any value)
- `5` — literal
- `1,5,10` — value list
- `9-17` — inclusive range
- `*/15` — step (every 15 units starting at 0)

## Examples

| Expression | Meaning |
|---|---|
| `0 3 * * *` | Every day at 03:00 |
| `*/15 * * * *` | Every 15 minutes |
| `0 9-17 * * 1-5` | Every hour from 09:00 to 17:00 on weekdays |
| `30 2 1 * *` | 02:30 on the first of every month |
| `0 0 * * 0` | Midnight every Sunday |

## Resolution

The scheduler ticks every 30 seconds and tracks last-fire-per-job per minute,
so a job can fire at most once per minute even if its expression matches
multiple times within the tick window.
