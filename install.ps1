# =========================================================================
# UO Offline (ModernUO edition) — Windows Installer
#
# The Windows counterpart to install.sh. Same result: a fully offline
# single-player UO shard with the PlayerBots system, T2A era, localhost only.
#
# What this does:
#   1. Checks/installs .NET SDK 10 (per-user, no admin needed).
#   2. Clones ModernUO and deploys the PlayerBots source + data into it.
#   3. Builds ModernUO (bots compiled in) for Windows x64.
#   4. Downloads the UO Classic 7.0.23.1 game data from a community mirror
#      and installs it (or uses an existing install if found).
#   4b. Swaps in genuine T2A-era Felucca map art (intact Magincia) from the
#      UO Second Age distribution. Reversible; $InstallT2AMap = $false to skip.
#   5. Downloads Nerun's pre-T2A spawn map.
#   6. Downloads the ClassicUO client (Windows build).
#   7. Writes ModernUO + ClassicUO configs (T2A, localhost only).
#   8. Installs start/stop scripts and a Desktop shortcut.
#
# Run via install.bat (double-click — opens the GUI installer), or run this
# console version directly in PowerShell:
#   powershell -ExecutionPolicy Bypass -File install.ps1
#
# -NoRun: define the paths + step functions but run nothing — the GUI
# installer (install-gui.ps1) dot-sources this file as its engine and
# invokes the steps itself.
# =========================================================================
param([switch]$NoRun)
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# ---------------------------------------------------------------------------
# Paths and URLs
# ---------------------------------------------------------------------------
$InstallRoot   = Join-Path $env:USERPROFILE "uo-modernuo"
$ModernUORepo  = "https://github.com/modernuo/ModernUO.git"
$ModernUODir   = Join-Path $InstallRoot "ModernUO"
$DistDir       = Join-Path $ModernUODir "Distribution"
$CfgDir        = Join-Path $DistDir "Configuration"
$SpawnersDir   = Join-Path $DistDir "Spawners\uoclassic"

$ClassicUODir  = Join-Path $InstallRoot "ClassicUO"
$ClassicUOReleaseUrl = "https://api.github.com/repos/ClassicUO/ClassicUO/releases"

# Razor (Community Edition) — the classic UO assistant, loaded into
# ClassicUO as a plugin so clicking Play opens the game with Razor attached.
# $InstallRazor = $false to skip.
$InstallRazor   = $true
$RazorDir       = Join-Path $InstallRoot "Razor"
$RazorReleaseUrl = "https://api.github.com/repos/markdwags/Razor/releases/latest"

$UODataUrl     = "https://mirror.ashkantra.de/fullclients/7.0.23.1.exe"
$UODataVersion = "7.0.23.1"
$UODataDir     = Join-Path $InstallRoot "UOData\$UODataVersion"

$SpawnMapUrl   = "https://raw.githubusercontent.com/Nerun/runuo-nerun-distro/master/Distro/Data/Nerun's%20Distro/Spawns/uoclassic/UOClassic.map"

# Genuine T2A-era Felucca map art (intact Magincia, pre-destruction world),
# pulled from the official UO Second Age (client 5.0.8.3) distribution. The
# 7.0.23.1 data above ships modern map art with 15+ years of EA world edits;
# swapping these three files restores the T2A look. Set $InstallT2AMap = $false
# to keep modern map art. See docs/T2A-MAP.md.
$InstallT2AMap   = $true
$T2AInstallerUrl = "https://download.uosecondage.com/UOSA_Client_Setup.exe"
$T2ASrcDir       = Join-Path $InstallRoot "t2a-src"
$T2AMulFiles     = @("map0.mul", "statics0.mul", "staidx0.mul")

$DotnetRoot    = Join-Path $env:USERPROFILE ".dotnet"
$DotnetVersion = "10.0.201"

# Config defaults
$ExpansionId   = 1
$ExpansionName = "T2A"
$OwnerUser     = "admin"
$OwnerPass     = "admin"
$ListenAddr    = "127.0.0.1:2593"
$ShardName     = "UO Offline"

# ---------------------------------------------------------------------------
# Pretty output
# ---------------------------------------------------------------------------
function Banner($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Say($m)    { Write-Host "--> $m" -ForegroundColor Cyan }
function Ok($m)     { Write-Host "[OK] $m" -ForegroundColor Green }
function Warn($m)   { Write-Host "[WARN] $m" -ForegroundColor Yellow }
# Die throws (instead of exit) so the GUI installer can catch a failed step
# and show it; the console runner at the bottom catches and prints red.
function Die($m)    { throw "INSTALL FAILED: $m" }

# ---------------------------------------------------------------------------
# Step 1 — Pre-flight
# ---------------------------------------------------------------------------
function Preflight {
  Banner "Pre-flight checks"
  if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Die "git is required. Install Git for Windows from https://git-scm.com/download/win and re-run."
  }
  New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
  Ok "Install root: $InstallRoot"
}

# ---------------------------------------------------------------------------
# Step 2 — .NET SDK (per-user, no admin)
# ---------------------------------------------------------------------------
function BootstrapDotnet {
  Banner "Bootstrapping .NET SDK $DotnetVersion"

  $dotnetExe = Join-Path $DotnetRoot "dotnet.exe"
  if (Test-Path $dotnetExe) {
    $sdks = & $dotnetExe --list-sdks 2>$null
    if ($sdks -match "^10\.") { Ok "Found compatible .NET SDK at $DotnetRoot"; $env:PATH = "$DotnetRoot;$env:PATH"; $env:DOTNET_ROOT = $DotnetRoot; return }
  }

  Say "Downloading dotnet-install.ps1..."
  $installer = Join-Path $InstallRoot "dotnet-install.ps1"
  Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer

  Say "Installing .NET SDK $DotnetVersion into $DotnetRoot..."
  & $installer -Version $DotnetVersion -InstallDir $DotnetRoot
  Remove-Item $installer -Force -ErrorAction SilentlyContinue

  $env:PATH = "$DotnetRoot;$env:PATH"
  $env:DOTNET_ROOT = $DotnetRoot
  if (-not (Test-Path $dotnetExe)) { Die "dotnet not installed at $dotnetExe" }
  Ok "Installed: $(& $dotnetExe --version)"
}

# ---------------------------------------------------------------------------
# Step 3 — Clone ModernUO (full history, required by Nerdbank.GitVersioning)
# ---------------------------------------------------------------------------
function FetchModernUO {
  Banner "Fetching ModernUO source"
  if (Test-Path (Join-Path $ModernUODir ".git")) {
    Say "ModernUO already cloned."
    Push-Location $ModernUODir
    if (Test-Path ".git\shallow") { git fetch --unshallow 2>$null; if ($LASTEXITCODE -ne 0) { git fetch --depth=2147483647 } }
    git fetch --all --tags
    git checkout main
    git pull --ff-only
    Pop-Location
  } else {
    Say "Cloning ModernUO (full history)..."
    git clone $ModernUORepo $ModernUODir
  }
  Ok "ModernUO source at $ModernUODir"
}

# ---------------------------------------------------------------------------
# Step 4 — Deploy PlayerBots into the ModernUO source tree (BEFORE build)
# ---------------------------------------------------------------------------
function InstallPlayerBots {
  Banner "Installing PlayerBots"
  $srcDir = Join-Path $ScriptDir "playerbots"
  if (-not (Test-Path $srcDir)) { Warn "No playerbots\ next to install.ps1; skipping bot install."; return }

  $srcTarget = Join-Path $ModernUODir "Projects\UOContent\CustomBots"

  Say "Deploying bot source -> $srcTarget"
  New-Item -ItemType Directory -Force -Path $srcTarget | Out-Null
  Copy-Item -Recurse -Force (Join-Path $srcDir "source\CustomBots\*") $srcTarget

  # Deploy every bot data directory present in the repo (Destinations,
  # Waypoints, Zones, PlayerBotChat). Navigation/fields_cache.bin is a
  # generated cache the bots rebuild on first run — not shipped.
  foreach ($sub in @("Destinations","Waypoints","Zones","PlayerBotChat")) {
    $from = Join-Path $srcDir "data\$sub"
    if (Test-Path $from) {
      $to = Join-Path $DistDir "Data\$sub"
      Say "Deploying $sub -> $to"
      New-Item -ItemType Directory -Force -Path $to | Out-Null
      Copy-Item -Recurse -Force (Join-Path $from "*") $to
    }
  }
  New-Item -ItemType Directory -Force -Path (Join-Path $DistDir "Data\Navigation") | Out-Null

  # Force a rebuild on next build by removing the marker dll.
  $dll = Join-Path $DistDir "ModernUO.dll"
  if (Test-Path $dll) { Remove-Item $dll -Force }
  Ok "PlayerBots deployed (compiled by the next ModernUO build)"
}

# ---------------------------------------------------------------------------
# Step 5 — Build ModernUO
# ---------------------------------------------------------------------------
function BuildModernUO {
  Banner "Building ModernUO"
  $env:PATH = "$DotnetRoot;$env:PATH"
  $env:DOTNET_ROOT = $DotnetRoot

  if (Test-Path (Join-Path $DistDir "ModernUO.dll")) {
    Say "ModernUO already built. Skipping (delete Distribution\ModernUO.dll to force rebuild)."
    return
  }
  Push-Location $ModernUODir
  & .\publish.ps1 release win x64
  Pop-Location
  if (-not (Test-Path (Join-Path $DistDir "ModernUO.dll"))) { Die "Build produced no ModernUO.dll. Check output above." }
  Ok "Build artifacts at $DistDir"
}

# ---------------------------------------------------------------------------
# Step 5b — Felucca season -> Summer (leafy trees)
# ---------------------------------------------------------------------------
function FixFeluccaSeason {
  Banner "Setting Felucca season to Summer"
  $mapdef = Join-Path $ModernUODir "Distribution\Data\map-definitions.json"
  if (-not (Test-Path $mapdef)) { Warn "map-definitions.json not found. Skipping."; return }
  $txt = Get-Content $mapdef -Raw
  # within the Felucca block, change "season": 4 to 1
  $new = [regex]::Replace($txt, '("name":\s*"Felucca".*?)"season":\s*4', '${1}"season": 1', 'Singleline')
  if ($new -ne $txt) {
    Copy-Item $mapdef "$mapdef.original" -Force
    Set-Content $mapdef $new -NoNewline
    Ok "Felucca season set to Summer."
  } else {
    Say "Felucca already Summer (or pattern not found). Skipping."
  }
}

# ---------------------------------------------------------------------------
# Step 6 — UO game data: detect existing, else download + install
# ---------------------------------------------------------------------------
function FindOrDownloadUOData {
  Banner "Locating UO game data"
  $candidates = @(
    "${env:ProgramFiles(x86)}\Electronic Arts\Ultima Online Classic",
    "$env:ProgramFiles\Electronic Arts\Ultima Online Classic",
    "$env:ProgramFiles\EA Games\Ultima Online Classic",
    "$env:USERPROFILE\Ultima Online Classic",
    "$env:USERPROFILE\Desktop\Ultima Online Classic",
    $UODataDir
  )
  foreach ($c in $candidates) {
    if ((Test-Path (Join-Path $c "art.mul")) -and (Test-Path (Join-Path $c "map0.mul"))) {
      $script:UOData = $c; Ok "Found UO data: $c"; return
    }
  }

  # A previous run may have extracted into a NESTED folder under UOData
  # (some builds of the self-extractor create their own subdirectory) —
  # search recursively before re-running the interactive installer.
  $uoDataRoot = Join-Path $InstallRoot "UOData"
  if (Test-Path $uoDataRoot) {
    $preHit = Get-ChildItem -Path $uoDataRoot -Recurse -Filter "art.mul" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($preHit) { $script:UOData = $preHit.DirectoryName; Ok "Found UO data: $($preHit.DirectoryName)"; return }
  }

  Warn "No existing UO data found. Downloading UO Classic $UODataVersion (~929 MB, third-party mirror, EA content)."
  New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot "UOData") | Out-Null
  $exePath = Join-Path $InstallRoot "UOData\$UODataVersion.exe"

  if (-not (Test-Path $exePath)) {
    Say "Downloading (5-15 min)..."
    # The mirror 403s on default UA; send a browser one.
    $headers = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" }
    Invoke-WebRequest -Uri $UODataUrl -OutFile $exePath -Headers $headers
  } else { Say "Installer already at $exePath." }

  # It's a native Windows installer/self-extractor. It extracts relative to
  # the working directory, so run it FROM the UOData dir to land the files
  # in a predictable place. If a setup window appears, install to the
  # default location and click through it.
  New-Item -ItemType Directory -Force -Path $UODataDir | Out-Null
  Say "Running the UO data installer. If a setup window appears, click through it (default location is fine)."
  Start-Process -FilePath $exePath -WorkingDirectory $UODataDir -Wait

  # Locate art.mul. Check the candidates, the UOData dir, AND the script
  # folder (some builds of this installer extract next to where it was
  # launched from). Whatever folder holds art.mul becomes the data dir.
  $searchRoots = @($UODataDir, (Join-Path $InstallRoot "UOData"), $ScriptDir, "${env:ProgramFiles(x86)}", "$env:ProgramFiles") + $candidates
  foreach ($root in $searchRoots) {
    if (-not (Test-Path $root)) { continue }
    $hit = Get-ChildItem -Path $root -Recurse -Filter "art.mul" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) {
      $dir = $hit.DirectoryName
      # If it landed somewhere transient (the script folder), move it into UODataDir.
      if ($dir -ne $UODataDir -and $dir.StartsWith($ScriptDir)) {
        Say "Moving extracted UO data into $UODataDir"
        Get-ChildItem -Path $dir | Move-Item -Destination $UODataDir -Force
        $dir = $UODataDir
      }
      $script:UOData = $dir; Ok "UO data: $dir"; return
    }
  }
  Die "UO data installer ran but art.mul not found. Install UO Classic manually, then re-run."
}

# ---------------------------------------------------------------------------
# Step 6b — Swap in genuine T2A-era Felucca map art
#
# The UO data dir is shared by BOTH the ModernUO server and the ClassicUO
# client, so swapping map0/statics0/staidx0 here updates rendering AND
# server-side collision/spawn at once, with no desync. radarcol/tiledata are
# left modern (stable across eras). Fully reversible — the modern files are
# backed up to _backup-modern-map\ first. See docs/T2A-MAP.md.
# ---------------------------------------------------------------------------
function SwapT2AMap {
  Banner "Installing T2A-era map art"
  if (-not $InstallT2AMap) { Say "InstallT2AMap is off; keeping modern map art."; return }
  if (-not $script:UOData) { Warn "UO data dir not resolved; skipping T2A map swap."; return }

  $backupDir = Join-Path $script:UOData "_backup-modern-map"
  if (Test-Path (Join-Path $backupDir "map0.mul")) {
    Say "T2A map already swapped (modern backup exists). Skipping."
    return
  }

  # 1. Obtain the UOSA installer (cached so re-runs don't re-download ~349 MB).
  New-Item -ItemType Directory -Force -Path $T2ASrcDir | Out-Null
  $uosaExe = Join-Path $T2ASrcDir "UOSA_Client_Setup.exe"
  if (-not (Test-Path $uosaExe)) {
    Say "Downloading UO Second Age client (~349 MB, EA content via uosecondage.com) for its T2A map art..."
    $headers = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" }
    Invoke-WebRequest -Uri $T2AInstallerUrl -OutFile $uosaExe -Headers $headers
  } else { Say "UOSA installer already cached at $uosaExe." }

  # 2. Extract the three map files. Prefer 7-Zip (reads the NSIS archive
  #    directly); fall back to a silent install into a scratch folder.
  $extractDir = Join-Path $T2ASrcDir "uosa-install"
  New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

  $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
  if (-not $sevenZip) { $sevenZip = Get-Command 7za -ErrorAction SilentlyContinue }

  $haveMuls = $true
  foreach ($f in $T2AMulFiles) { if (-not (Test-Path (Join-Path $extractDir $f))) { $haveMuls = $false } }

  if (-not $haveMuls) {
    if ($sevenZip) {
      Say "Extracting T2A map files with 7-Zip..."
      & $sevenZip.Source x -y "-o$extractDir" $uosaExe @T2AMulFiles | Out-Null
    } elseif ($extractDir -match '\s') {
      Warn "7-Zip not found and the extract path contains spaces (the silent UOSA installer cannot handle that)."
      Warn "Install 7-Zip from https://www.7-zip.org and re-run, or follow docs/T2A-MAP.md manually. Keeping modern map."
      return
    } else {
      Say "7-Zip not found; running the UOSA installer silently into $extractDir..."
      # NSIS switches: /S = silent, /D = install dir (must be last, unquoted).
      Start-Process -FilePath $uosaExe -ArgumentList "/S", "/D=$extractDir" -Wait
      # The installer drops a Start-Menu shortcut for the legacy 2D client we
      # don't use (we run ClassicUO). Remove it.
      $sm = Join-Path ([Environment]::GetFolderPath("Programs")) "Ultima Online"
      if (Test-Path $sm) { Remove-Item $sm -Recurse -Force -ErrorAction SilentlyContinue }
    }
  }

  # Locate the three muls (a silent install may nest them).
  $srcMap = @{}
  foreach ($f in $T2AMulFiles) {
    $hit = Get-ChildItem -Path $extractDir -Recurse -Filter $f -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $srcMap[$f] = $hit.FullName }
  }
  foreach ($f in $T2AMulFiles) {
    if (-not $srcMap.ContainsKey($f)) { Warn "T2A $f not found after extract; aborting swap (modern map kept)."; return }
  }

  # 3. Back up the modern files (the 3 swapped + radarcol/tiledata for safety).
  New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
  foreach ($f in @("map0.mul", "statics0.mul", "staidx0.mul", "radarcol.mul", "tiledata.mul")) {
    $live = Join-Path $script:UOData $f
    if (Test-Path $live) { Copy-Item $live (Join-Path $backupDir $f) -Force }
  }
  Ok "Backed up modern map -> $backupDir"

  # 4. Copy the T2A files over the live data dir.
  foreach ($f in $T2AMulFiles) { Copy-Item $srcMap[$f] (Join-Path $script:UOData $f) -Force }
  Ok "T2A map art installed (intact Magincia). Revert: copy _backup-modern-map\* back over the data dir."
}

# ---------------------------------------------------------------------------
# Step 7 — Nerun's spawn map
# ---------------------------------------------------------------------------
function FetchSpawnMap {
  Banner "Fetching Nerun's pre-T2A spawn map"
  New-Item -ItemType Directory -Force -Path $SpawnersDir | Out-Null
  $target = Join-Path $SpawnersDir "UOClassic.map"
  if ((Test-Path $target) -and (Get-Item $target).Length -gt 0) { Say "Spawn map already present."; return }
  Say "Downloading from Nerun's repository..."
  Invoke-WebRequest -Uri $SpawnMapUrl -OutFile $target
  if ((Get-Content $target -First 1) -match '<!doctype|<html') { Remove-Item $target; Die "Spawn map download looks like HTML." }
  Ok "Spawn map: $target"
}

# ---------------------------------------------------------------------------
# Step 8 — ClassicUO (Windows build)
# ---------------------------------------------------------------------------
function InstallClassicUO {
  Banner "Downloading ClassicUO client (Windows)"
  if ((Test-Path $ClassicUODir) -and (Get-ChildItem $ClassicUODir -ErrorAction SilentlyContinue)) {
    Say "ClassicUO already present. Skipping."; return
  }
  New-Item -ItemType Directory -Force -Path $ClassicUODir | Out-Null
  $tmpZip = Join-Path $InstallRoot ".classicuo.zip"

  Say "Querying GitHub for the latest Windows release..."
  $rel = Invoke-RestMethod -Uri "$ClassicUOReleaseUrl/latest" -Headers @{ "User-Agent"="uo-offline-installer" }
  $asset = $rel.assets | Where-Object { $_.browser_download_url -match "win" } | Select-Object -First 1
  if (-not $asset) {
    $rel = Invoke-RestMethod -Uri "$ClassicUOReleaseUrl/tags/ClassicUO-dev-release" -Headers @{ "User-Agent"="uo-offline-installer" }
    $asset = $rel.assets | Where-Object { $_.browser_download_url -match "win" } | Select-Object -First 1
  }
  if (-not $asset) { Die "Could not find a ClassicUO Windows release." }

  Say "Downloading: $($asset.browser_download_url)"
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmpZip
  Say "Extracting..."
  Expand-Archive -Path $tmpZip -DestinationPath $ClassicUODir -Force
  Remove-Item $tmpZip -Force

  $cuo = Get-ChildItem $ClassicUODir -Recurse -Filter "ClassicUO.exe" | Select-Object -First 1
  if ($cuo) { Set-Content (Join-Path $InstallRoot ".classicuo-bin-path") $cuo.FullName; Ok "ClassicUO: $($cuo.FullName)" }
  else { Warn "ClassicUO extracted but ClassicUO.exe not located; start script will search at launch." }
}

# ---------------------------------------------------------------------------
# Step 8b — Razor (Community Edition)
#
# Razor runs INSIDE ClassicUO as a plugin (the modern, supported way to use
# it): WriteClassicUOSettings points ClassicUO's "plugins" list at Razor.exe,
# so launching the game brings up Razor attached to the client — macros,
# hotkeys, agents, the works.
# ---------------------------------------------------------------------------
function InstallRazor {
  Banner "Downloading Razor assistant"
  if (-not $InstallRazor) { Say "InstallRazor is off; skipping."; return }

  $razorExe = Join-Path $RazorDir "Razor.exe"
  if (Test-Path $razorExe) {
    Say "Razor already present. Skipping."
    Set-Content (Join-Path $InstallRoot ".razor-bin-path") $razorExe
    return
  }

  Say "Querying GitHub for the latest Razor CE release..."
  $rel = Invoke-RestMethod -Uri $RazorReleaseUrl -Headers @{ "User-Agent"="uo-offline-installer" }
  $asset = $rel.assets | Where-Object { $_.name -match "x64" -and $_.name -match "\.zip$" } | Select-Object -First 1
  if (-not $asset) { $asset = $rel.assets | Where-Object { $_.name -match "\.zip$" } | Select-Object -First 1 }
  if (-not $asset) { Warn "No Razor release zip found; skipping Razor (game still works without it)."; return }

  $tmpZip = Join-Path $InstallRoot ".razor.zip"
  Say "Downloading: $($asset.browser_download_url)"
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmpZip
  New-Item -ItemType Directory -Force -Path $RazorDir | Out-Null
  Say "Extracting..."
  Expand-Archive -Path $tmpZip -DestinationPath $RazorDir -Force
  Remove-Item $tmpZip -Force

  $hit = Get-ChildItem $RazorDir -Recurse -Filter "Razor.exe" | Select-Object -First 1
  if ($hit) {
    Set-Content (Join-Path $InstallRoot ".razor-bin-path") $hit.FullName
    Ok "Razor: $($hit.FullName)"
  } else {
    Warn "Razor extracted but Razor.exe not located; the game will launch without it."
  }
}

# ---------------------------------------------------------------------------
# Step 9 — ModernUO config
# ---------------------------------------------------------------------------
function WriteModernUOConfig {
  Banner "Writing ModernUO configuration"
  New-Item -ItemType Directory -Force -Path $CfgDir | Out-Null
  $uoData = $script:UOData.Replace([char]92,[char]47)

  @"
{
  "assemblyDirectories": ["./Assemblies"],
  "dataDirectories": ["$uoData"],
  "listeners": ["$ListenAddr"],
  "settings": {
    "accountHandler.maxAccountsPerIP": "10",
    "autosave.enabled": "true",
    "autosave.saveDelay": "00:05:00",
    "serverList.address": "127.0.0.1",
    "serverList.autoDetect": "false",
    "serverListing.name": "$ShardName"
  }
}
"@ | Set-Content (Join-Path $CfgDir "modernuo.json")
  Ok "Wrote modernuo.json"

  @"
{
  "Id": $ExpansionId,
  "ClientFlags": "None",
  "SupportedFeatures": { "ExpansionT2A": true, "T2A": true, "LiveAccount": true },
  "CharacterListFlags": { "ExpansionT2A": true },
  "MapSelectionFlags": { "Felucca": true, "Trammel": false, "Ilshenar": false, "Malas": false, "Tokuno": false, "TerMur": false }
}
"@ | Set-Content (Join-Path $CfgDir "expansion.json")
  Ok "Wrote expansion.json (T2A, Felucca-only)"
  Warn "NOTE: expansion.json here is abbreviated. If ModernUO rejects it, copy the full schema from the Linux install.sh write_modernuo_config function."

  # The Young player system is a UO:R-era feature that did not exist in T2A.
  # Left on, young characters also get a Trammel-only public moongate list,
  # which filters down to nothing on this Felucca-only shard and makes the
  # city moongates silently do nothing for every non-staff player.
  $FlagsDir = Join-Path $CfgDir "FeatureFlags"
  New-Item -ItemType Directory -Force -Path $FlagsDir | Out-Null
  @"
[
  {
    "Key": "young_player_system",
    "Description": "UO:R-era new player (Young) system. Off for T2A: no (Young) name suffix, no young monster protection, no Haven transport, no New Player Ticket, and no Trammel-only public moongate list.",
    "Enabled": false,
    "DefaultEnabled": true,
    "Category": "Content",
    "LastModified": "2026-08-23T00:00:00Z",
    "LastModifiedBy": "T2A ruleset"
  }
]
"@ | Set-Content (Join-Path $FlagsDir "flags.json")
  Ok "Wrote FeatureFlags/flags.json (Young player system off - not a T2A feature)"
}

# ---------------------------------------------------------------------------
# Step 10 — ClassicUO settings.json
# ---------------------------------------------------------------------------
function WriteClassicUOSettings {
  Banner "Writing ClassicUO settings.json"
  if (-not (Test-Path $ClassicUODir)) { Warn "ClassicUO dir missing; skipping."; return }
  $uoData = $script:UOData.Replace([char]92,[char]47)
  $targets = @($ClassicUODir)
  $binPath = Join-Path $InstallRoot ".classicuo-bin-path"
  if (Test-Path $binPath) { $nested = Split-Path -Parent (Get-Content $binPath); if ($nested -ne $ClassicUODir) { $targets += $nested } }

  # Razor rides along as a ClassicUO plugin when installed.
  $plugins = "[]"
  $razorBinPath = Join-Path $InstallRoot ".razor-bin-path"
  if (Test-Path $razorBinPath) {
    $razorExe = (Get-Content $razorBinPath).Replace([char]92,[char]47)
    if ($razorExe) { $plugins = "[`"$razorExe`"]" }
  }

  # save_password + auto_login: clicking the desktop shortcut goes straight
  # into the shard (the first login auto-creates the admin account).
  foreach ($t in $targets) {
    @"
{
  "username": "$OwnerUser",
  "password": "$OwnerPass",
  "ip": "127.0.0.1",
  "port": 2593,
  "ultimaonlinedirectory": "$uoData",
  "clientversion": "$UODataVersion",
  "lastservernum": 1,
  "last_server_name": "$ShardName",
  "fps": 60,
  "encryption": 0,
  "save_password": true,
  "auto_login": true,
  "plugins": $plugins
}
"@ | Set-Content (Join-Path $t "settings.json")
    Ok "Wrote $t\settings.json"
  }
  if ($plugins -ne "[]") { Ok "Razor wired in as a ClassicUO plugin." }
}

# ---------------------------------------------------------------------------
# Step 11 — start/stop scripts + Desktop shortcut
# ---------------------------------------------------------------------------
function InstallRuntimeScripts {
  Banner "Installing launcher scripts"
  $cuoBin = ""
  $binPath = Join-Path $InstallRoot ".classicuo-bin-path"
  if (Test-Path $binPath) { $cuoBin = Get-Content $binPath }

  $startPs1 = Join-Path $InstallRoot "start.ps1"
  @"
# One-click play: start the ModernUO server (minimized) unless one is
# already running, wait until it's actually listening on 2593, THEN launch
# ClassicUO — which loads Razor as its plugin (see settings.json) and
# auto-logs into the shard. Polling the port avoids the race where the
# client connects before the server has finished its (slow) first boot.
`$dist = "$DistDir"
`$dotnet = "$DotnetRoot\dotnet.exe"

function PortOpen {
  try {
    `$c = New-Object System.Net.Sockets.TcpClient
    `$c.Connect("127.0.0.1", 2593); `$c.Close(); return `$true
  } catch { return `$false }
}

if (PortOpen) {
  Write-Host "Server already running - launching the game."
} else {
  Start-Process -FilePath `$dotnet -ArgumentList "ModernUO.dll" -WorkingDirectory `$dist -WindowStyle Minimized | Out-Null
  Write-Host "Starting server, waiting for it to listen on 2593..."
  `$ready = `$false
  for (`$i = 0; `$i -lt 120; `$i++) {
    if (PortOpen) { `$ready = `$true; break }
    Start-Sleep -Seconds 1
  }
  if (-not `$ready) { Write-Host "Server didn't come up within 120s; check the server window." }
}

`$cuo = "$cuoBin"
if (`$cuo -and (Test-Path `$cuo)) { Start-Process -FilePath `$cuo -WorkingDirectory (Split-Path -Parent `$cuo) }
else { Write-Host "ClassicUO.exe not found; start it manually." }
"@ | Set-Content $startPs1
  Ok "Wrote start.ps1"

  # start.bat — double-clickable launcher that bypasses the execution policy
  # (running start.ps1 directly is blocked by default on Windows).
  @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"
"@ | Set-Content (Join-Path $InstallRoot "start.bat")
  Ok "Wrote start.bat"

  # Desktop shortcut to start.ps1, with the UO icon when the repo ships one.
  $iconSpec = "shell32.dll,18"
  $icoSrc = Join-Path $ScriptDir "uoico.ico"
  if (Test-Path $icoSrc) {
    $icoDst = Join-Path $InstallRoot "uoico.ico"
    Copy-Item $icoSrc $icoDst -Force
    $iconSpec = "$icoDst,0"
  }
  $wsh = New-Object -ComObject WScript.Shell
  $lnk = $wsh.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "UO Offline.lnk"))
  $lnk.TargetPath = "powershell.exe"
  $lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File `"$startPs1`""
  $lnk.WorkingDirectory = $InstallRoot
  $lnk.IconLocation = $iconSpec
  $lnk.Save()
  Ok "Desktop shortcut: UO Offline"
}

# ---------------------------------------------------------------------------
function Finish {
  Banner "Install complete"
  Write-Host @"

Install root:   $InstallRoot
Server:         $DistDir
Client:         $ClassicUODir
Razor:          $RazorDir  (loads inside ClassicUO as a plugin)
UO data:        $($script:UOData)
Listener:       $ListenAddr  (localhost only, offline)
Owner login:    $OwnerUser / $OwnerPass

To play:        Double-click the "UO Offline" desktop shortcut — it starts
                the server, then opens the game with Razor attached and
                logs you straight in. (or run $InstallRoot\start.bat)

First launch: create the owner account in-game ($OwnerUser/$OwnerPass),
make a character, then populate the world with the [-commands in
$InstallRoot\POPULATE-WORLD.txt (same as the Linux version).

"@
}

# ---------------------------------------------------------------------------
# The install sequence. The GUI installer (install-gui.ps1) dot-sources this
# file with -NoRun and drives these same steps itself, one checklist row per
# entry, so console and GUI installs can never drift apart.
# ---------------------------------------------------------------------------
$script:InstallSteps = @(
  @{ Name = "Check requirements";           Run = { Preflight } },
  @{ Name = "Install .NET (no admin)";      Run = { BootstrapDotnet } },
  @{ Name = "Download the ModernUO server"; Run = { FetchModernUO } },
  @{ Name = "Add the PlayerBots";           Run = { InstallPlayerBots } },
  @{ Name = "Build the server";             Run = { BuildModernUO } },
  @{ Name = "Set Felucca to summer";        Run = { FixFeluccaSeason } },
  @{ Name = "Get the UO game data";         Run = { FindOrDownloadUOData } },
  @{ Name = "Install T2A-era map art";      Run = { SwapT2AMap } },
  @{ Name = "Fetch the monster spawns";     Run = { FetchSpawnMap } },
  @{ Name = "Download ClassicUO client";    Run = { InstallClassicUO } },
  @{ Name = "Download Razor assistant";     Run = { InstallRazor } },
  @{ Name = "Write the configuration";      Run = { WriteModernUOConfig; WriteClassicUOSettings } },
  @{ Name = "Create launcher + shortcut";   Run = { InstallRuntimeScripts } }
)

if (-not $NoRun) {
  try {
    foreach ($step in $script:InstallSteps) { & $step.Run }
    Finish
  } catch {
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
  }
}
