# Bitwyck launcher (PowerShell) — usage: ./start-bitwyck.ps1 ask "your prompt"
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Args)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project src/Bitwyck.CLI -- @Args
} finally {
    Pop-Location
}
