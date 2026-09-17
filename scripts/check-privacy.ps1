# Fails if files that are about to be committed contain personal data. This repo is public, and
# agent-written code has twice leaked a real email address and real account UUIDs into docs and tests.
#
#   .\scripts\check-privacy.ps1            Check files staged for commit (what the pre-commit hook runs)
#   .\scripts\check-privacy.ps1 -All       Check every tracked file
#   .\scripts\check-privacy.ps1 -Install   Install the pre-commit hook for this clone
param([switch]$All, [switch]$Install)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ($Install) {
    $hookDir = Join-Path $repo '.githooks'
    New-Item -ItemType Directory -Force -Path $hookDir | Out-Null
    @'
#!/bin/sh
# Blocks commits containing personal data. Bypass with --no-verify only if you are certain.
exec powershell -NoProfile -ExecutionPolicy Bypass -File "$(git rev-parse --show-toplevel)/scripts/check-privacy.ps1"
'@ | Set-Content -Path (Join-Path $hookDir 'pre-commit') -Encoding ascii
    git -C $repo config core.hooksPath .githooks
    Write-Host "Pre-commit hook installed (core.hooksPath = .githooks)."
    return
}

# What counts as a placeholder rather than personal data.
#   - any address at a reserved example domain (RFC 2606) or a noreply address
#   - any UUID whose every dash-separated group is one repeated character (1111-1111-..., aaaa-bbbb-...)
#   - a user path whose name is an obvious stand-in
$allowedEmail = '@(example|invalid|test)\.(com|org|net)$|@localhost$|noreply'
$allowedPathName = '(?i)\\Users\\(someone|user|username|test|example)$'

function Test-PlaceholderUuid([string]$uuid) {
    # A placeholder UUID repeats one character per group, allowing for the version/variant
    # nibble that makes a UUID well-formed: 11111111-1111-4111-8111-111111111111,
    # aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee. Anything else is treated as real.
    $groups = $uuid.Split('-')
    if ($groups.Count -ne 5) { return $false }
    for ($i = 0; $i -lt 5; $i++) {
        $g = $groups[$i]
        if ($i -eq 2 -or $i -eq 3) { $g = $g.Substring(1) }   # skip version / variant nibble
        if ($g.Length -eq 0) { continue }
        foreach ($c in $g.ToCharArray()) { if ($c -ne $g[0]) { return $false } }
    }
    return $true
}

function Test-Allowed([string]$kind, [string]$hit) {
    switch ($kind) {
        'email address' { return ($hit -match $allowedEmail) }
        'UUID'          { return (Test-PlaceholderUuid $hit) }
        'user path'     { return ($hit -match $allowedPathName) }
        default         { return $false }
    }
}

# What is looked for. Anything matching that is not an obvious placeholder blocks the commit.
$patterns = @(
    @{ Name = 'email address'; Regex = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+[.][A-Za-z]{2,}' },
    @{ Name = 'UUID';          Regex = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}' },
    @{ Name = 'user path';     Regex = '[A-Za-z]:\\Users\\[A-Za-z0-9._-]+' },
    @{ Name = 'OAuth token';   Regex = 'sk-ant-[A-Za-z0-9]' }
)

if ($All) {
    $files = git -C $repo ls-files
} else {
    $files = git -C $repo diff --cached --name-only --diff-filter=ACM
}

$skip = '^(docs/reviews/|docs/design-previews/)'   # verbatim third-party review output, already vetted
$findings = @()

foreach ($f in $files) {
    if (-not $f -or $f -match $skip) { continue }
    $full = Join-Path $repo $f
    if (-not (Test-Path $full -PathType Leaf)) { continue }
    if ((Get-Item $full).Length -gt 2MB) { continue }

    $lineNo = 0
    foreach ($line in (Get-Content $full -ErrorAction SilentlyContinue)) {
        $lineNo++
        foreach ($p in $patterns) {
            foreach ($m in [regex]::Matches($line, $p.Regex)) {
                $hit = $m.Value
                if (-not (Test-Allowed $p.Name $hit)) {
                    $findings += [pscustomobject]@{ File = $f; Line = $lineNo; Kind = $p.Name; Text = $hit }
                }
            }
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host "check-privacy: clean, no personal data in $(@($files).Count) files."
    exit 0
}

Write-Host ''
Write-Host "check-privacy: BLOCKED -- personal data in files staged for commit:" -ForegroundColor Red
foreach ($f in $findings) { "  {0}:{1}  {2}: {3}" -f $f.File, $f.Line, $f.Kind, $f.Text | Write-Host }
Write-Host ''
Write-Host "Replace with placeholders (alex@example.com, 11111111-1111-4111-8111-111111111111, C:\Users\someone)"
Write-Host "or commit with --no-verify if you are certain."
exit 1
