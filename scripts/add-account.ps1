# Adds a second (or third, ...) Claude account to Claude Status Bar (docs/multi-account.md):
# creates %LOCALAPPDATA%\ClaudeStatusBar\accounts\<n>\config, runs an interactive `claude` login
# pointed at it (CLAUDE_CONFIG_DIR), appends the entry to accounts.json, and restarts the app.
#
#   .\scripts\add-account.ps1                 Add a new account (interactive login)
#   .\scripts\add-account.ps1 -Label "Team"   ...with a display-name override
#   .\scripts\add-account.ps1 -Register <n>   Register a directory that is already logged in
#                                              (recovery if a previous run was interrupted)
#   .\scripts\add-account.ps1 -List           List configured accounts and their slot numbers
#   .\scripts\add-account.ps1 -Remove <n>     Remove account <n> from accounts.json (leaves its
#                                              config directory on disk -- it holds a login)
param(
    [string]$Label,
    [switch]$List,
    [int]$Remove = 0,
    [string]$Register
)

$ErrorActionPreference = 'Stop'

$dataRoot       = Join-Path $env:LOCALAPPDATA 'ClaudeStatusBar'
$accountsRoot   = Join-Path $dataRoot 'accounts'
$accountsJson   = Join-Path $dataRoot 'accounts.json'
$installDir     = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeStatusBar'
$exe            = Join-Path $installDir 'ClaudeStatusBar.exe'
$defaultClaudeDir  = Join-Path $env:USERPROFILE '.claude'
$defaultClaudeJson = Join-Path $env:USERPROFILE '.claude.json'

function Get-AccountsConfig {
    if (-not (Test-Path $accountsJson)) {
        # Mirrors Config/AccountsConfig.cs's Default(): the app creates this file itself on
        # first run too, so this only matters if the script runs before the app ever has.
        return [PSCustomObject]@{
            displayMode = 'perAccount'
            maxIcons    = 3
            accounts    = @([PSCustomObject]@{ configDir = $null; label = $null; enabled = $true })
        }
    }
    return Get-Content $accountsJson -Raw | ConvertFrom-Json
}

function Save-AccountsConfig($config) {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $json = $config | ConvertTo-Json -Depth 6
    $tmp = "$accountsJson.tmp"
    Set-Content -Path $tmp -Value $json -Encoding utf8
    Move-Item -Path $tmp -Destination $accountsJson -Force
}

# Slot number from a configDir like "...\accounts\<n>\config" -- the on-disk numbering this
# script owns; accounts.json itself has no separate "n" field, only the path.
function Get-SlotNumber([string]$configDir) {
    if ([string]::IsNullOrEmpty($configDir)) { return $null }
    if ($configDir -match '[\\/]accounts[\\/](\d+)[\\/]config$') { return [int]$Matches[1] }
    return $null
}

function Restart-StatusBar {
    Get-Process ClaudeStatusBar -ErrorAction SilentlyContinue | ForEach-Object {
        $_.Kill()
        $_.WaitForExit(5000) | Out-Null
    }
    if (Test-Path $exe) {
        Start-Process $exe
        Write-Host "Restarted: $exe"
    } else {
        Write-Host "Not installed yet -- run .\scripts\install.ps1 to start it with the new account list."
    }
}

# ---- -List ----

if ($List) {
    $config = Get-AccountsConfig
    Write-Host "Display mode: $($config.displayMode)   Max icons: $($config.maxIcons)"
    Write-Host ''
    $i = 0
    foreach ($acct in $config.accounts) {
        $slot = if ([string]::IsNullOrEmpty($acct.configDir)) { 'default' } else { Get-SlotNumber $acct.configDir }
        $label = if ($acct.label) { $acct.label } else { '(auto)' }
        $state = if ($acct.enabled) { 'enabled' } else { 'disabled' }
        Write-Host ("  [{0}] slot={1,-8} label={2,-16} {3}" -f $i, $slot, $label, $state)
        $i++
    }
    return
}

# ---- -Remove <n> ----

if ($Remove -gt 0) {
    $config = Get-AccountsConfig
    $target = $config.accounts | Where-Object { (Get-SlotNumber $_.configDir) -eq $Remove }
    if (-not $target) {
        Write-Host "No account with slot $Remove found. Run -List to see configured accounts." -ForegroundColor Yellow
        return
    }
    $config.accounts = @($config.accounts | Where-Object { $_ -ne $target })
    Save-AccountsConfig $config
    Write-Host "Removed account slot $Remove from accounts.json."
    Write-Host "Its login is untouched on disk at: $(Join-Path $accountsRoot "$Remove\config") -- delete that folder yourself if you no longer want it."
    Restart-StatusBar
    return
}

# ---- -Register (recovery: a config directory that is already logged in) ----

if ($Register -and -not $List -and $Remove -le 0) {
    $dir = if (Test-Path $Register) { (Resolve-Path $Register).Path } else { Join-Path $accountsRoot ($Register + "\config") }
    if (-not (Test-Path (Join-Path $dir ".claude.json"))) {
        Write-Host "No login found in $dir -- nothing to register." -ForegroundColor Yellow
        return
    }
    $config = Get-AccountsConfig
    if (@($config.accounts) | Where-Object { $_.configDir -eq $dir }) {
        Write-Host "$dir is already registered."
        return
    }
    $config.accounts = @($config.accounts) + [PSCustomObject]@{ configDir = $dir; label = $null; enabled = $true }
    Save-AccountsConfig $config
    Write-Host "Registered $dir."
    Restart-StatusBar
    return
}

# ---- add a new account ----

New-Item -ItemType Directory -Force -Path $accountsRoot | Out-Null
$slot = 1
while (Test-Path (Join-Path $accountsRoot "$slot\config")) { $slot++ }
$configDir = Join-Path $accountsRoot "$slot\config"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null

Write-Host "About to log into a NEW Claude account."
Write-Host "This runs 'claude' with CLAUDE_CONFIG_DIR pointed at a fresh, empty directory:"
Write-Host "  $configDir"
Write-Host "A browser window will open for you to log in. When the login is done, leave Claude"
Write-Host "Code with /exit so this script can finish and save the account."
Write-Host "Do NOT press Ctrl+C -- that kills this script too, and the account is not saved."
Write-Host "If that happens anyway, re-run with:  -Register $slot"
Write-Host ''

$env:CLAUDE_CONFIG_DIR = $configDir
try {
    & claude
} finally {
    Remove-Item Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
}

$identityFile = Join-Path $configDir '.claude.json'
if (-not (Test-Path $identityFile)) {
    Write-Host "No login was detected at $identityFile -- the account was not added. Re-run this script to try again." -ForegroundColor Yellow
    return
}

$config = Get-AccountsConfig
$config.accounts = @($config.accounts) + [PSCustomObject]@{
    configDir = $configDir
    label     = if ($Label) { $Label } else { $null }
    enabled   = $true
}
Save-AccountsConfig $config

Write-Host "Added account slot $slot ($configDir) to accounts.json."
Restart-StatusBar
