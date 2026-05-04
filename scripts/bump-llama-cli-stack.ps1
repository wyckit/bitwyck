# Bumps llama-cli.exe's per-thread stack reserve from 1 MB to 8 MB.
# Required because BitNet 1.58-bit kernels stack-overflow on prompts above
# ~870 chars when running with the default Windows 1 MB thread stack. With
# 8 MB the practical ceiling becomes the model's 8192-token context.
#
# Run once after building llama-cli.exe (and any time you rebuild BitNet).
# Reversible by re-running with /STACK:1048576.

param(
    [string] $Exe = "C:\Software\research\BitNet\build\bin\Release\llama-cli.exe",
    [int]    $StackBytes = 8388608  # 8 MB
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    Write-Error "llama-cli.exe not found at: $Exe"
    exit 1
}

# Locate editbin from any installed Visual Studio.
$editbin = Get-ChildItem -Path @(
    "C:\Program Files\Microsoft Visual Studio\2022\*\VC\Tools\MSVC\*\bin\Hostx64\x64\editbin.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\editbin.exe"
) -ErrorAction SilentlyContinue | Select-Object -Last 1

if (-not $editbin) {
    Write-Error "editbin.exe not found. Install Visual Studio with the C++ build tools."
    exit 1
}

$dumpbin = Join-Path (Split-Path $editbin.FullName -Parent) "dumpbin.exe"

Write-Host "Using editbin: $($editbin.FullName)"
Write-Host "Target binary: $Exe"
Write-Host

# Stop any holders so the file isn't locked.
Get-Process bitwyck,llama-cli,llama-server -ErrorAction SilentlyContinue | Stop-Process -Force

# Work in a temp dir — editbin can fail with LNK1342 if a sibling .bak file
# already exists or the directory is being scanned (antivirus, VS).
$work = Join-Path $env:TEMP "bitwyck-stackbump-$(Get-Random)"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$tmpExe = Join-Path $work "llama-cli.exe"

try {
    Copy-Item $Exe $tmpExe -Force

    & $editbin.FullName "/STACK:$StackBytes" $tmpExe
    if ($LASTEXITCODE -ne 0) { throw "editbin failed (exit $LASTEXITCODE)" }

    Copy-Item $tmpExe $Exe -Force

    Write-Host
    Write-Host "Stack reserve after edit:"
    & $dumpbin "/HEADERS" $Exe | Select-String "stack" | ForEach-Object { Write-Host "  $_" }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host
Write-Host "Done."
