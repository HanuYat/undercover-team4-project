# PostToolUse hook: auto-format the .cs file Claude just edited with CSharpier.
# Reads the hook payload (JSON) from stdin, pulls tool_input.file_path,
# and runs the local `dotnet csharpier` tool on it. Best-effort: never blocks
# the edit — any failure exits 0 silently.

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    $payload = $raw | ConvertFrom-Json
    $path = $payload.tool_input.file_path
    if (-not $path) { exit 0 }

    # Only C# source files.
    if ($path -notmatch '\.cs$') { exit 0 }
    # Skip third-party / imported assets (see CLAUDE.md — do not reformat vendored code).
    if ($path -match '[\\/]Imported[\\/]') { exit 0 }
    if (-not (Test-Path -LiteralPath $path)) { exit 0 }

    # Run from project root so the local tool manifest (dotnet-tools.json) resolves.
    $root = $env:CLAUDE_PROJECT_DIR
    if (-not $root) { $root = (Get-Location).Path }

    Push-Location $root
    try {
        dotnet csharpier format "$path" 2>&1 | Out-Null
    }
    finally {
        Pop-Location
    }
    exit 0
}
catch {
    exit 0
}
