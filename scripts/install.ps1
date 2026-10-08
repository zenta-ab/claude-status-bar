# Installs Claude Status Bar for the current user: publishes a Release build to
# %LOCALAPPDATA%\Programs\ClaudeStatusBar, starts it at Windows sign-in, adds it to the Start
# menu (to start it again after Exit), and pins the tray icon next to the clock. Re-run after changing code. -Uninstall reverses everything except the
# logins the app keeps in its data folder; add -RemoveLogins to delete those too.
param(
    [switch]$Uninstall,
    [switch]$RemoveLogins
)

$ErrorActionPreference = 'Stop'
$repo       = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeStatusBar'
$exe        = Join-Path $installDir 'ClaudeStatusBar.exe'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName  = 'ClaudeStatusBar'
$exitEvent  = 'Local\ClaudeStatusBar-Exit'   # StatusBarApplicationContext.ExitEventName
$accountsRoot = Join-Path $env:LOCALAPPDATA 'ClaudeStatusBar\accounts'
$shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Status Bar.lnk'

if ($RemoveLogins -and -not $Uninstall) {
    throw '-RemoveLogins only applies together with -Uninstall.'
}

function Stop-StatusBar {
    $running = @(Get-Process ClaudeStatusBar -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    # Ask the app to exit through its normal shutdown: it closes each Claude child's input and
    # waits for it to exit by itself. Killing it instead would still take the children down (they
    # are in a kill-on-close job), just without that clean end.
    $signalled = $false
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting($exitEvent)
        try { [void]$ev.Set(); $signalled = $true } finally { $ev.Dispose() }
    } catch {
        # No such event: an older build, or an app that is still starting. Fall through to the kill.
    }

    if ($signalled) {
        $deadline = (Get-Date).AddSeconds(15)
        foreach ($p in $running) {
            $left = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
            [void]$p.WaitForExit($left)
        }
    }

    Get-Process ClaudeStatusBar -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host 'The app did not exit on request; stopping it.'
        $_.Kill()
        $_.WaitForExit(5000) | Out-Null
    }
}

if ($Uninstall) {
    Stop-StatusBar
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

    if ($RemoveLogins) {
        # Only after the app has exited. No `claude auth logout`: that would revoke the grant on the
        # server, which may affect a login held elsewhere; deleting the folder just forgets it here.
        if (Test-Path $accountsRoot) {
            $attributes = [System.IO.File]::GetAttributes($accountsRoot)
            if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                # A junction/symlink: remove the link only, never what it points at.
                [System.IO.Directory]::Delete($accountsRoot)
            } else {
                [System.IO.Directory]::Delete($accountsRoot, $true)   # does not follow nested reparse points (Remove-Item in PowerShell 5.1 can)
            }
            Write-Host "Removed the app's Claude Code logins: $accountsRoot"
        } else {
            Write-Host 'No logins to remove.'
        }
    } elseif (Test-Path $accountsRoot) {
        Write-Host "The Claude Code logins the app keeps are still in $accountsRoot"
        Write-Host '  (delete them with: .\scripts\install.ps1 -Uninstall -RemoveLogins)'
    }
    Write-Host "Uninstalled. Logs remain in $env:LOCALAPPDATA\ClaudeStatusBar\logs"
    return
}

Stop-StatusBar

# global.json pins the SDK, and dotnet resolves it from the working directory.
Push-Location $repo
try {
    # Property form on purpose: '--self-contained false' has produced a 116 MB binary here.
    dotnet publish src\Windows\ClaudeStatusBar.csproj -c Release -r win-x64 `
        -p:SelfContained=false -p:PublishSingleFile=true -o $installDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

Set-ItemProperty -Path $runKey -Name $valueName -Value ('"' + $exe + '"')

# Exit in the tray menu ends the app until the next sign-in; the Start menu entry brings it back.
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = 'Claude Status Bar'
$link.Save()
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
Write-Host "Start menu: $shortcut"
if ($pinned) { Write-Host 'Tray icon: pinned next to the clock' }
else { Write-Host 'Tray icon: not pinned automatically; drag it out of the ^ overflow once' }
