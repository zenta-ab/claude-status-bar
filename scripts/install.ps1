# Installs Claude Status Bar for the current user: publishes a Release build to
# %LOCALAPPDATA%\Programs\ClaudeStatusBar, starts it at Windows sign-in, and pins the tray
# icon next to the clock. Re-run after changing code. -Uninstall reverses everything.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$repo       = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeStatusBar'
$exe        = Join-Path $installDir 'ClaudeStatusBar.exe'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName  = 'ClaudeStatusBar'

function Stop-StatusBar {
    # Killing the app closes its job object, which takes the claude.exe child down with it.
    Get-Process ClaudeStatusBar -ErrorAction SilentlyContinue | ForEach-Object {
        $_.Kill()
        $_.WaitForExit(5000) | Out-Null
    }
}

if ($Uninstall) {
    Stop-StatusBar
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    Write-Host "Uninstalled. Logs remain in $env:LOCALAPPDATA\ClaudeStatusBar\logs"
    return
}

Stop-StatusBar

# global.json pins the SDK, and dotnet resolves it from the working directory.
Push-Location $repo
try {
    # Property form on purpose: '--self-contained false' has produced a 116 MB binary here.
    dotnet publish src\ClaudeStatusBar\ClaudeStatusBar.csproj -c Release -r win-x64 `
        -p:SelfContained=false -p:PublishSingleFile=true -o $installDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

Set-ItemProperty -Path $runKey -Name $valueName -Value ('"' + $exe + '"')
Start-Process $exe

# Windows creates the tray entry the first time the icon appears, keyed by executable path.
# IsPromoted=1 is what "show next to the clock" writes; it is undocumented, so failure is non-fatal.
$settingsKey = 'HKCU:\Control Panel\NotifyIconSettings'
$deadline = (Get-Date).AddSeconds(30)
$pinned = $false
while (-not $pinned -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    Get-ChildItem $settingsKey -ErrorAction SilentlyContinue | ForEach-Object {
        $entry = Get-ItemProperty $_.PSPath
        if ($entry.ExecutablePath -ieq $exe) {
            Set-ItemProperty -Path $_.PSPath -Name IsPromoted -Value 1 -Type DWord
            $pinned = $true
        }
    }
}

Write-Host "Installed: $exe"
Write-Host "Autostart: HKCU Run\$valueName"
if ($pinned) { Write-Host 'Tray icon: pinned next to the clock' }
else { Write-Host 'Tray icon: not pinned automatically; drag it out of the ^ overflow once' }
