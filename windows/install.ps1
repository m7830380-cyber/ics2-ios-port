# Internet Cafe Simulator 2 Co-op for Windows: installer.
#
# Makes a separate co-op copy of YOUR Steam install of the game next to it
# (steamapps\common\Internet Cafe Simulator 2 Co-op). The Steam install is only read, never changed.
# Big files are hard links (no extra disk space); only the game code (Managed folder) and four small
# settings files are real copies. Saves and settings of the co-op copy are kept apart from the normal game.
#
# Uninstall: delete the "Internet Cafe Simulator 2 Co-op" folder and the desktop shortcut.
# -Dest <folder> -Unattended: install somewhere else without questions (no shortcut, firewall or saves).
param([string]$Game = "", [string]$Dest = "", [switch]$Unattended)

$ErrorActionPreference = "Stop"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Exe = "Internet Cafe Simulator 2.exe"
$Data = "Internet Cafe Simulator 2_Data"
$Product = "Internet Cafe Simulator 2"
$CoopProduct = "Internet Cafe Sim 2 Co-op"     # same length: patched in place in two files
$Copied = @("app.info", "globalgamemanagers", "ScriptingAssemblies.json", "RuntimeInitializeOnLoads.json", "boot.config")
$Manifest = Get-Content (Join-Path $Here "manifest.json") -Raw | ConvertFrom-Json

function Say($text, $color = "Gray") { Write-Host $text -ForegroundColor $color }

function Find-Game {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam", "HKCU:\SOFTWARE\Valve\Steam") {
        try {
            $p = (Get-ItemProperty $key -ErrorAction Stop)
            foreach ($v in $p.InstallPath, $p.SteamPath) { if ($v) { $roots.Add(($v -replace "/", "\")) } }
        } catch {}
    }
    $roots.Add("C:\Program Files (x86)\Steam")
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($r in $roots) {
        $libs.Add($r)
        $vdf = Join-Path $r "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs.Add(($m.Groups[1].Value -replace "\\\\", "\")) }
        }
    }
    foreach ($l in $libs | Select-Object -Unique) {
        foreach ($sub in "steamapps\common\Internet Cafe Simulator 2\windows_content", "steamapps\common\Internet Cafe Simulator 2") {
            $dir = Join-Path $l $sub
            if (Test-Path (Join-Path $dir $Exe)) { return $dir }
        }
    }
    return $null
}

function Patch-Product($path) {
    $latin = [Text.Encoding]::GetEncoding(28591)   # one byte per character: the file keeps its exact length
    $text = $latin.GetString([IO.File]::ReadAllBytes($path))
    if ($text.IndexOf($Product) -lt 0) { throw "$path does not contain the product name" }
    [IO.File]::WriteAllBytes($path, $latin.GetBytes($text.Replace($Product, $CoopProduct)))
}

Say ""
Say "Internet Cafe Simulator 2 Co-op for Windows ($($Manifest.build))" "Cyan"
Say "------------------------------------------------------------"

# ---- 1. the Steam install ------------------------------------------------------------------------
if (-not $Game) { $Game = Find-Game }
while (-not $Game -or -not (Test-Path (Join-Path $Game $Exe))) {
    Say "I could not find Internet Cafe Simulator 2 on this PC." "Yellow"
    $Game = (Read-Host "Paste the folder that contains '$Exe' (Steam > right-click the game > Manage > Browse local files)").Trim('"', ' ')
}
$Game = (Resolve-Path $Game).Path
Say "Game found: $Game"

$asm = Join-Path $Game "$Data\Managed\Assembly-CSharp.dll"
$hash = (Get-FileHash $asm -Algorithm SHA256).Hash.ToLower()
if ($hash -ne $Manifest.game_assembly_sha256) {
    Say "Your game version is different from the one this co-op build was made for" "Yellow"
    Say "(the game was probably updated). It may not start or may misbehave." "Yellow"
    if ($Unattended -or (Read-Host "Install anyway? [y/N]") -notmatch "^[yY]") { exit 1 }
}

# ---- 2. the co-op copy ----------------------------------------------------------------------------
$commonDir = Split-Path -Parent $Game
if ((Split-Path -Leaf $Game) -eq "windows_content") { $commonDir = Split-Path -Parent $commonDir }
if (-not $Dest) { $Dest = Join-Path $commonDir "Internet Cafe Simulator 2 Co-op" }
Say "Co-op copy:  $Dest"
New-Item -ItemType Directory -Force $Dest | Out-Null

# hard links need the same drive; otherwise everything is copied (about 7 GB)
$probe = Join-Path $Dest ".linktest"
Remove-Item $probe -Force -ErrorAction SilentlyContinue
$canLink = $true
try { New-Item -ItemType HardLink -Path $probe -Target (Join-Path $Game "UnityPlayer.dll") -ErrorAction Stop | Out-Null } catch { $canLink = $false }
Remove-Item $probe -Force -ErrorAction SilentlyContinue
if (-not $canLink) {
    $free = (Get-PSDrive ($Dest.Substring(0, 1))).Free / 1GB
    Say ("Hard links are not possible here, so the game files would be copied (7 GB, {0:N1} GB free)." -f $free) "Yellow"
    if ($free -lt 7.5) { Say "Not enough free space." "Red"; exit 1 }
    if ($Unattended -or (Read-Host "Copy 7 GB? [y/N]") -notmatch "^[yY]") { exit 1 }
}

# the running game holds its files: replacing them half way would leave a copy that does not start
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dest, [StringComparison]::OrdinalIgnoreCase) }
while ($running) {
    Say "The co-op game is running. Close it, then press Enter to go on." "Yellow"
    Read-Host | Out-Null
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dest, [StringComparison]::OrdinalIgnoreCase) }
}

$managedRel = "$Data\Managed"
$files = Get-ChildItem $Game -Recurse -File
$i = 0
foreach ($f in $files) {
    $i++
    $rel = $f.FullName.Substring($Game.Length).TrimStart("\")
    $relDir = Split-Path -Parent $rel
    $dst = Join-Path $Dest $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    if (Test-Path $dst) { Remove-Item $dst -Force }   # never write through an old link into the Steam install
    $copy = ($relDir -eq $managedRel) -or ($relDir -eq $Data -and $Copied -contains $f.Name) -or (-not $canLink)
    if ($copy) { Copy-Item $f.FullName $dst }
    else {
        try { New-Item -ItemType HardLink -Path $dst -Target $f.FullName -ErrorAction Stop | Out-Null }
        catch { Copy-Item $f.FullName $dst }
    }
    if ($i % 25 -eq 0) { Write-Progress -Activity "Making the co-op copy" -Status $rel -PercentComplete (100 * $i / $files.Count) }
}
Write-Progress -Activity "Making the co-op copy" -Completed

# ---- 3. the co-op code ------------------------------------------------------------------------------
$managed = Join-Path $Dest $managedRel
Get-ChildItem (Join-Path $Here "Managed") -Filter *.dll | ForEach-Object { Copy-Item $_.FullName (Join-Path $managed $_.Name) -Force }

$dataDir = Join-Path $Dest $Data
$p = Join-Path $dataDir "ScriptingAssemblies.json"
$j = Get-Content $p -Raw | ConvertFrom-Json
foreach ($a in "ICS2Shim.dll", "ICS2iOS.dll") {
    if ($j.names -notcontains $a) { $j.names += $a; $j.types += 16 }
}
[IO.File]::WriteAllText($p, ($j | ConvertTo-Json -Depth 5 -Compress))

$p = Join-Path $dataDir "RuntimeInitializeOnLoads.json"
$j = Get-Content $p -Raw | ConvertFrom-Json
foreach ($init in @(@("EarlyInit", 4), @("Init", 1))) {
    if (-not ($j.root | Where-Object { $_.assemblyName -eq "ICS2iOS" -and $_.methodName -eq $init[0] })) {
        $j.root += [pscustomobject]@{ assemblyName = "ICS2iOS"; nameSpace = "ICS2iOS"; className = "Bootstrap"; methodName = $init[0]; loadTypes = $init[1]; isUnityClass = $false }
    }
}
[IO.File]::WriteAllText($p, ($j | ConvertTo-Json -Depth 5 -Compress))

Patch-Product (Join-Path $dataDir "app.info")
Patch-Product (Join-Path $dataDir "globalgamemanagers")

# window size/mode left by an older build of the co-op copy: the game picks its own again
$reg = "HKCU:\Software\Cheesecake Dev\$CoopProduct"
if (Test-Path $reg) {
    (Get-Item $reg).Property | Where-Object { $_ -like "Screenmanager*" } | ForEach-Object { Remove-ItemProperty $reg -Name $_ -ErrorAction SilentlyContinue }
}

# ---- 4. saves, shortcut, firewall ---------------------------------------------------------------------
if ($Unattended) { Say "Installed (unattended) to $Dest" "Green"; exit 0 }
$low = Join-Path $env:USERPROFILE "AppData\LocalLow\Cheesecake Dev"
$oldSaves = Join-Path $low $Product
$newSaves = Join-Path $low $CoopProduct
if ((Test-Path $oldSaves) -and -not (Test-Path $newSaves)) {
    if ((Read-Host "Copy your normal saves into the co-op copy (your normal saves stay as they are)? [Y/n]") -notmatch "^[nN]") {
        Copy-Item $oldSaves $newSaves -Recurse
        Say "Saves copied."
    }
}

$exePath = Join-Path $Dest $Exe
$lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "ICS2 Co-op.lnk"
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exePath
$s.WorkingDirectory = $Dest
$s.Save()
Say "Desktop shortcut: ICS2 Co-op"

Say ""
Say "Friends can only join you if Windows Firewall lets the game in (UDP port 27815)."
Say "Radmin VPN networks count as 'Public' networks, so the normal pop-up is often not enough."
if ((Read-Host "Add a firewall rule for the co-op copy now? Windows will ask for admin rights. [Y/n]") -notmatch "^[nN]") {
    # sent encoded: the path has spaces and "(x86)", which plain -Command arguments would break apart
    $cmd = "netsh advfirewall firewall delete rule name='ICS2 Co-op' | Out-Null; " +
           "netsh advfirewall firewall add rule name='ICS2 Co-op' dir=in action=allow program='$exePath' enable=yes profile=any protocol=udp; " +
           "exit `$LASTEXITCODE"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    try {
        $proc = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile", "-EncodedCommand", $enc
        if ($proc.ExitCode -eq 0) { Say "Firewall rule added." "Green" }
        else { Say "The firewall rule could not be added (code $($proc.ExitCode)). Allow the game when Windows asks, on Private AND Public networks." "Yellow" }
    }
    catch { Say "Skipped (admin rights refused). Allow the game when Windows asks, on Private AND Public networks." "Yellow" }
}

Say ""
Say "Done. Start 'ICS2 Co-op' from the desktop. Co-op button in the main menu or pause menu." "Green"
Say "Log file: $newSaves\InternetCafeSim2-iOS.log"
