# Tries the Windows setup the way people meet it: installs the app, lets it update itself, changes the choices by
# running the setup again, and removes the app, looking each time at what is on the computer and what is not.
# Run by .github/workflows/downloads.yml on a computer that is thrown away afterwards:
#
#   pwsh packaging/windows/try-setup.ps1 <the setup> <a file to write the findings to>
#
# It installs and removes the app for the current user and puts stand-ins where the app keeps things, so it is not
# for a computer the app is in use on.
param([Parameter(Mandatory)][string]$Setup, [Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path

$name = 'Master PDF Modifier'
$folder = "$env:LOCALAPPDATA\Programs\$name"
$program = "$folder\MasterPdfModifier.exe"
$log = Join-Path $PWD 'setup.log'
$startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$name.lnk"
$desktop = "$([Environment]::GetFolderPath('Desktop'))\$name.lnk"
$pin = "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\$name.lnk"
$offer = 'HKCU:\Software\Classes\.pdf\OpenWithProgids'
$noted = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\OpenWithProgids'
$temp = [IO.Path]::GetTempPath().TrimEnd('\')

# What the app keeps for its user, and what it only leaves lying around.
$kept = "$env:APPDATA\PdfModifier\settings.json", "$env:LOCALAPPDATA\PdfModifier\tessdata\eng.traineddata"
$litter = "$env:LOCALAPPDATA\PdfModifier\update\9.9.9\MasterPdfModifier-Setup.exe", "$temp\PdfModifier\0123abcd\000001.pdf",
    "$temp\.net\MasterPdfModifier\0123abcd\MasterPdfModifier.dll", $pin

function Say([string]$text) { $text | Tee-Object -FilePath $Report -Append }
function Expect($condition, [string]$otherwise) { if (-not $condition) { throw $otherwise } }
function Offered { [bool](Get-ItemProperty $offer -Name 'MasterPdfModifier.pdf' -ErrorAction SilentlyContinue) }
function Noted { [bool](Get-ItemProperty $noted -Name 'MasterPdfModifier.pdf' -ErrorAction SilentlyContinue) }
function Listed { Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object DisplayName -eq $name }
function Open { Get-CimInstance Win32_Process -Filter "Name='MasterPdfModifier.exe'" }
function Close { Get-Process MasterPdfModifier -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }

# Starts a program and waits for it alone: the setup may leave the app running behind it.
function Run([string]$file, [string[]]$arguments, [int]$seconds = 300) {
    $run = Start-Process $file -ArgumentList $arguments -PassThru
    $null = $run.Handle
    if (-not $run.WaitForExit($seconds * 1000)) { throw "$file did not finish within $seconds seconds." }
    $run.ExitCode
}

function Install([string[]]$arguments) {
    $code = Run $Setup (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log") + $arguments)
    if ($code -ne 0) { Get-Content $log -Tail 40; throw "Setup failed (exit code $code)." }
}

function Remove([string[]]$arguments, [int]$seconds = 60) {
    $null = Run "$folder\unins000.exe" (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $arguments)
    # The remover hands over to a copy of itself; the folder goes a moment after it returns.
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Test-Path $folder) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
}

# Puts stand-ins where a used app has its things, so that removing it has something to remove.
function Litter {
    foreach ($file in $kept + $litter) {
        New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
        if (-not (Test-Path $file)) { Set-Content $file 'left by the app' }
    }
    if (-not (Test-Path $noted)) { New-Item $noted -Force | Out-Null }
    New-ItemProperty $noted -Name 'MasterPdfModifier.pdf' -PropertyType String -Value '' -Force | Out-Null
}

# Everything of the app that is still on the computer, apart from what it keeps for its user.
function Traces {
    $left = @(@($folder, $startMenu, $desktop, "$env:LOCALAPPDATA\PdfModifier\update", "$temp\PdfModifier", "$temp\.net\MasterPdfModifier", $pin) |
        Where-Object { Test-Path $_ })
    if (Offered) { $left += 'the entry under Open with' }
    if (Noted) { $left += 'what Windows noted about opening PDF files with it' }
    if (Test-Path 'HKCU:\Software\Classes\Applications\MasterPdfModifier.exe') { $left += 'its entry among the programs Windows knows' }
    if (Listed) { $left += 'its entry among the installed apps' }
    $left
}

Remove-Item $Report -ErrorAction SilentlyContinue

# 1. A first installation, with every choice as the setup offers it.
Install @()
$out = Join-Path $PWD 'check-installed-app.txt'
$check = Start-Process $program -ArgumentList '--check' -Wait -PassThru -RedirectStandardOutput $out
Get-Content $out | Tee-Object -FilePath $Report -Append
Expect ($check.ExitCode -eq 0) 'The installed app does not work.'
Expect (Select-String -Path $out -SimpleMatch 'Updates: Setup, from MasterPdfModifier-Setup.exe' -Quiet) 'The installed app does not know that the setup put it there.'
Expect (Test-Path $startMenu) 'There is no shortcut in the Start menu.'
Expect (-not (Test-Path $desktop)) 'A shortcut was put on the desktop without being asked for.'
Expect (Offered) 'The app is not offered for PDF files.'
$entry = Listed
Expect $entry 'The app is not among the installed apps.'
Expect ($entry.Publisher -and $entry.DisplayIcon -and $entry.EstimatedSize) 'The entry among the installed apps lacks its maker, icon or size.'
Say "Version $($entry.DisplayVersion) by $($entry.Publisher) installed as offered: in the Start menu, not on the desktop, under Open with, among the installed apps."

# 2. The app updates itself: it starts the setup and closes. The setup waits for that, installs over the app with
#    the choices of the first time, and starts the app again on the document it had open.
$app = Start-Process $program -PassThru
Start-Sleep -Seconds 10
Expect (-not $app.HasExited) 'The installed app does not stay open.'
$letter = Join-Path $PWD 'a letter.pdf'
$update = Start-Process $Setup -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NOCANCEL', '/NORESTART', '/UPDATE=1', "/REOPEN=`"$letter`"", "/LOG=$log" -PassThru
$null = $update.Handle
Start-Sleep -Seconds 5
Expect (-not $update.HasExited) 'The setup did not wait for the app to close.'
Stop-Process -Id $app.Id -Force
Expect ($update.WaitForExit(300000)) 'The setup did not finish the update.'
if ($update.ExitCode -ne 0) { Get-Content $log -Tail 40; throw "The update failed (exit code $($update.ExitCode))." }
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Seconds 1; $again = Open } until ($again -or (Get-Date) -gt $deadline)
Expect $again 'The app was not started again after the update.'
Expect (@($again).Count -eq 1) 'The app was started more than once after the update.'
Expect ($again.CommandLine.Contains("`"$letter`"")) "The app was not given its document back: $($again.CommandLine)"
Expect ((Test-Path $startMenu) -and -not (Test-Path $desktop) -and (Offered)) 'The update did not keep the choices of the first installation.'
Say 'Updated the way the app does it: the setup waited for the app to close, asked nothing, kept the choices and started the app again with its document.'

# 3. An app that is open is not removed.
Remove @() 5
Expect ((Test-Path $program) -and (Listed)) 'The app was removed while it was open.'
Close
Say 'Left alone while it was open.'

# 4. Running the setup again changes the choices: what is unticked goes.
Install @('/TASKS=desktopicon')
Expect ((Test-Path $desktop) -and -not (Test-Path $startMenu) -and -not (Offered)) 'Unticking the Start menu and Open with did not take them away.'
Install @('/TASKS=startmenuicon,openwith')
Expect ((Test-Path $startMenu) -and -not (Test-Path $desktop) -and (Offered)) 'Unticking the desktop shortcut did not take it away.'
Say 'Choices changed by running the setup again: shortcuts and the offer for PDF files come and go with their boxes.'

# 5. Removed by someone who wants to keep their settings: those stay, everything else goes.
Litter
Remove @('/KEEPDATA')
$left = Traces
Expect (-not $left) "Removing the app left behind: $($left -join ', ')"
$lost = $kept | Where-Object { -not (Test-Path $_) }
Expect (-not $lost) "Removing the app with /KEEPDATA took away: $($lost -join ', ')"
Say 'Removed with /KEEPDATA: the settings and the language data stayed, nothing else did.'

# 6. Installed once more with every box ticked, and removed the way Windows does it: nothing of it may stay.
Install @('/TASKS=startmenuicon,desktopicon,openwith')
Expect ((Test-Path $startMenu) -and (Test-Path $desktop) -and (Offered)) 'With every box ticked something was not made.'
Litter
Remove @()
$left = @(Traces) + @("$env:APPDATA\PdfModifier", "$env:LOCALAPPDATA\PdfModifier" | Where-Object { Test-Path $_ })
Expect (-not $left) "Removing the app left behind: $($left -join ', ')"
Say 'Removed: no program, no shortcut, no pin, nothing under Open with or among the installed apps, no settings, no language data, nothing in the temp folder.'
