# Quick Start

## Prerequisites

- Windows 11 (BitNet binaries assume Windows; Linux needs a separate build)
- .NET 10 SDK (`dotnet --version` ≥ 10.0)
- BitNet built at `C:/Software/research/BitNet/build/bin/Release/llama-server.exe`
- At least one model downloaded into `C:/Software/research/BitNet/models/`

If BitNet isn't built, see `C:/Software/research/BitNet/README.md`. The repo
records three Windows-Clang patches required at HEAD around 2026-04 (see the
`build-patches-windows-clang-19` engram entry for line-numbered fixes).

## Build

```powershell
dotnet build C:/Software/research/bitwyck/Bitwyck.slnx
```

Expect 4 projects, 0 warnings, 0 errors.

## Run unit tests

```powershell
dotnet test C:/Software/research/bitwyck/Bitwyck.slnx
```

The deterministic suite uses a `FakeBitNetClient` that returns canned
tool-call traces; no live model is required.

## Run with a real model

```powershell
$env:BITWYCK_ENABLE_LIVE = "1"
dotnet run --project C:/Software/research/bitwyck/src/Bitwyck.CLI -- ask "List the files in src/"
```

The first call boots `llama-server.exe` for `Reflex_1B` on port 8081 and
`Standard_3B` on port 8082. Heavier tiers (7B, 10B) are spawned on demand.

## Configuration

Settings live in `src/Bitwyck.CLI/appsettings.json`. Key sections:

- `Bitwyck:BitNet:Tiers` — model paths and ports per tier
- `Bitwyck:Engram` — database path (defaults to `%LOCALAPPDATA%/bitwyck/engram.db`)
- `Bitwyck:Tools:AllowedRoots` — file-tool sandboxing roots
- `Bitwyck:Tools:BashAllowList` — `run_bash` command prefix allow-list
- `Bitwyck:Webhook` — opt-in HTTP listener for external triggers
- `Bitwyck:Vision` — optional LLaVA model paths for the `VisionSensor`
- `Bitwyck:Audio` — optional Whisper model path for the `WhisperSensor`

Override any value with environment variables in standard ASP.NET form,
e.g. `Bitwyck__BitNet__DefaultThreads=8`.

## Daemon mode

```powershell
dotnet run --project C:/Software/research/bitwyck/src/Bitwyck.CLI -- daemon
```

Runs the chrono-scheduler in foreground. The default `IdentityStateUpdater`
job fires at 03:00 every night. Add custom `IChronoJob` implementations by
registering them in `Program.cs`.

## Diagnostics

```powershell
dotnet run --project C:/Software/research/bitwyck/src/Bitwyck.CLI -- diagnose
```

Reports per-subsystem health: BitNet binary present, model files found per
tier, engram DB writable, tool registry populated, sensors available.
