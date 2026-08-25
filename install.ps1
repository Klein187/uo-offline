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

# Updating an ALREADY-CLONED ModernUO is opt-in. A checkout that has built
# once is known-good; pulling upstream mid-install can drag in months of
# engine changes and turn a working shard into one that will not compile.
# Fresh installs always clone. Set to $true to track upstream on re-runs.
$UpdateModernUO = $false
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
# Native commands write ordinary progress to stderr - git announces
# "From https://github.com/..." on a perfectly good fetch, and 7-Zip and
# dotnet do much the same. With $ErrorActionPreference = "Stop", PowerShell
# turns every one of those lines into a terminating NativeCommandError, and
# inside the GUI installer's runspace (no console for stderr to land on)
# that kills the whole install on a command that actually succeeded.
#
# So route native tools through here. stderr is folded into the captured
# output as plain text, and success is judged by the exit code, which is
# the only thing that carries any meaning.
# ---------------------------------------------------------------------------
function Invoke-Native {
  param(
    [Parameter(Mandatory)][string]$Exe,
    [string[]]$Arguments = @(),
    [switch]$IgnoreExitCode
  )
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    $out  = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $prev
  }
  if (-not $IgnoreExitCode -and $code -ne 0) {
    $tail = ($out | Select-Object -Last 4) -join " | "
    throw "$Exe $($Arguments -join ' ') failed (exit $code): $tail"
  }
  return $out
}

# Run a PowerShell script that shells out to native tools of its own (the
# dotnet bootstrapper, ModernUO's publish.ps1). We cannot wrap their inner
# calls, so relax the preference across the whole thing; each caller already
# checks for the artifact it expects afterwards.
function Invoke-ScriptTolerant {
  param([Parameter(Mandatory)][scriptblock]$Body)
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try { & $Body } finally { $ErrorActionPreference = $prev }
}

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
  Invoke-ScriptTolerant { & $installer -Version $DotnetVersion -InstallDir $DotnetRoot }
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
    if (-not $UpdateModernUO) {
      Say "Leaving it at its current commit (set `$UpdateModernUO = `$true to track upstream)."
      Ok "ModernUO source at $ModernUODir"
      return
    }
    Push-Location $ModernUODir
    try {
      if (Test-Path ".git\shallow") {
        Invoke-Native git @("fetch", "--unshallow") -IgnoreExitCode | Out-Null
      }
      # --force because upstream moves tags (build-tool-latest is re-pointed
      # every release). Without it the whole fetch fails with
      # "would clobber existing tag" and takes the install down with it.
      Invoke-Native git @("fetch", "--all", "--tags", "--force") | Out-Null
      Invoke-Native git @("checkout", "main") | Out-Null
      Invoke-Native git @("pull", "--ff-only") | Out-Null
      Ok "Updated to latest main."
    } catch {
      # A clone that will not update is not fatal - whatever is on disk still
      # builds. The usual cause is local edits to tracked files, which is
      # exactly what the stock-file patches in INTEGRATION-NOTES.txt are.
      Warn "Could not update the existing ModernUO clone: $($_.Exception.Message)"
      Warn "Continuing with the checkout already on disk."
    } finally {
      Pop-Location
    }
  } else {
    Say "Cloning ModernUO (full history)..."
    Invoke-Native git @("clone", $ModernUORepo, $ModernUODir) | Out-Null
  }
  Ok "ModernUO source at $ModernUODir"
}

# ---------------------------------------------------------------------------
# Engine patches.
#
# Two stock ModernUO files need a small change for the bots to work
# properly, and they cannot live in CustomBots/ because they ARE engine
# files. They ship as unified diffs under patches/ and go on with
# git apply, which every install already has because it clones ModernUO.
#
# Never fatal. An upstream that has moved on will refuse a patch, and a
# shard missing them still runs - it just loses bot housing across
# restarts. INTEGRATION-NOTES.txt describes both by hand.
# ---------------------------------------------------------------------------
function ApplyEnginePatches {
  Banner "Applying engine patches"

  $patchDir = Join-Path $ScriptDir "patches"
  if (-not (Test-Path $patchDir)) { Say "No patches directory; nothing to apply."; return }

  $patches = Get-ChildItem -Path $patchDir -Filter "*.patch" | Sort-Object Name
  if ($patches.Count -eq 0) { Say "No patches to apply."; return }

  foreach ($patch in $patches) {
    $name = $patch.Name
    $path = $patch.FullName

    # Already applied? Reversing it cleanly is the test.
    Invoke-Native git @("-C", $ModernUODir, "apply", "--reverse", "--check", $path) -IgnoreExitCode | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok "$name (already applied)"; continue }

    Invoke-Native git @("-C", $ModernUODir, "apply", "--check", $path) -IgnoreExitCode | Out-Null
    if ($LASTEXITCODE -ne 0) {
      Warn "$name does not apply to this ModernUO checkout - skipping."
      Warn "See INTEGRATION-NOTES.txt if you need it applied by hand."
      continue
    }

    Invoke-Native git @("-C", $ModernUODir, "apply", $path) | Out-Null
    Ok "$name applied"
  }
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
  # publish.ps1 sets its own $ErrorActionPreference = "Stop", so a failing
  # build throws out of it rather than just returning non-zero. Catch that,
  # because the whole point is to look at the result and decide whether a
  # retry is worth it.
  $tryPublish = {
    try {
      Invoke-ScriptTolerant { & .\publish.ps1 release win x64 }
    } catch {
      Warn "Build attempt failed: $($_.Exception.Message)"
    }
  }

  Push-Location $ModernUODir
  try {
    & $tryPublish

    if (-not (Test-Path (Join-Path $DistDir "ModernUO.dll"))) {
      # A build can fail on stale intermediate output left behind by a
      # DIFFERENT .NET SDK - anyone with Visual Studio has a second one, and
      # whichever ran last wins. The giveaway is the build tool reporting
      # "'Cleaning project' failed with exit code 1", with a
      # ResolvePackageAssets NullReferenceException buried in the output.
      # Clearing obj/ and bin/ makes restore regenerate them; it costs a
      # minute and fixes it, so try once before giving up.
      Warn "Build produced no ModernUO.dll. Clearing stale build output and retrying once..."
      ClearBuildArtifacts
      & $tryPublish
    }
  } finally {
    Pop-Location
  }
  if (-not (Test-Path (Join-Path $DistDir "ModernUO.dll"))) { Die "Build produced no ModernUO.dll. Check output above." }
  Ok "Build artifacts at $DistDir"
}

# Delete every Projects\*\obj and in. Pure build output - restore and the
# next build regenerate all of it.
function ClearBuildArtifacts {
  $projectsDir = Join-Path $ModernUODir "Projects"
  if (-not (Test-Path $projectsDir)) { return }

  $removed = 0
  foreach ($proj in (Get-ChildItem -Path $projectsDir -Directory -ErrorAction SilentlyContinue)) {
    foreach ($sub in @("obj", "bin")) {
      $dir = Join-Path $proj.FullName $sub
      if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
      }
    }
  }
  Say "Cleared $removed stale build output folder(s)."
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

  # It is a WinRAR self-extracting archive, which takes silent switches:
  # -s2 suppresses its window, -y answers its prompts, -d sets the target.
  # Try that first so the whole install can run start to finish without
  # anyone sitting in front of it. The target is quoted because a Windows
  # user name containing a space would otherwise split the argument.
  New-Item -ItemType Directory -Force -Path $UODataDir | Out-Null
  Say "Extracting the UO data (a few minutes; no window should appear)..."
  Start-Process -FilePath $exePath -ArgumentList "-s2 -y `"-d$UODataDir`"" -Wait

  $extracted = Get-ChildItem -Path (Join-Path $InstallRoot "UOData") -Recurse `
    -Filter "art.mul" -ErrorAction SilentlyContinue | Select-Object -First 1

  if (-not $extracted) {
    # Older or different builds of the self-extractor may not take those
    # switches. Fall back to running it the way a person would.
    Warn "Silent extraction produced nothing; running the installer interactively."
    Say "If a setup window appears, click through it (the default location is fine)."
    Start-Process -FilePath $exePath -WorkingDirectory $UODataDir -Wait
  }

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
      Invoke-Native $sevenZip.Source (@("x", "-y", "-o$extractDir", $uosaExe) + $T2AMulFiles) | Out-Null
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
    "serverListing.name": "$ShardName",
    "serverListing.serverName": "$ShardName",
    "accountHandler.enableAutoAccountCreation": "True"
  }
}
"@ | Set-Content (Join-Path $CfgDir "modernuo.json")
  Ok "Wrote modernuo.json"

  # The full schema, matching install.sh. An abbreviated file used to go
  # here, which left most flags to chance and set ContextMenus off while
  # setting ExpansionT2A on - the same bit, contradicting itself.
  @"
{
  "Id": $ExpansionId,
  "ClientFlags": "None",
  "SupportedFeatures": {
    "ExpansionT2A": true,
    "T2A": true,
    "UOR": false,
    "UOTD": false,
    "LBR": false,
    "AOS": false,
    "SixthCharacterSlot": false,
    "SE": false,
    "ML": false,
    "EighthAge": false,
    "NinthAge": false,
    "TenthAge": false,
    "IncreasedStorage": false,
    "SeventhCharacterSlot": false,
    "RoleplayFaces": false,
    "TrialAccount": false,
    "LiveAccount": true,
    "SA": false,
    "HS": false,
    "Gothic": false,
    "Rustic": false,
    "Jungle": false,
    "Shadowguard": false,
    "TOL": false,
    "EJ": false
  },
  "CharacterListFlags": {
    "Unk1": false,
    "OverwriteConfigButton": false,
    "OneCharacterSlot": false,
    "ExpansionNone": false,
    "ExpansionUOTD": false,
    "ExpansionLBR": false,
    "ExpansionT2A": true,
    "ExpansionUOR": false,
    "ContextMenus": true,
    "SlotLimit": false,
    "AOS": false,
    "SixthCharacterSlot": false,
    "SE": false,
    "ML": false,
    "KR": false,
    "UO3DClientType": false,
    "Unk3": false,
    "SeventhCharacterSlot": false,
    "Unk4": false,
    "NewMovementSystem": false,
    "NewFeluccaAreas": false
  },
  "HousingFlags": {
    "AOS": false,
    "HousingAOS": false,
    "SE": false,
    "ML": false,
    "Crystal": false,
    "SA": false,
    "HS": false,
    "Gothic": false,
    "Rustic": false,
    "Jungle": false,
    "Shadowguard": false,
    "TOL": false,
    "EJ": false
  },
  "MobileStatusVersion": 0,
  "MapSelectionFlags": {
    "Felucca": true,
    "Trammel": false,
    "Ilshenar": false,
    "Malas": false,
    "Tokuno": false,
    "TerMur": false
  }
}
"@ | Set-Content (Join-Path $CfgDir "expansion.json")
  Ok "Wrote expansion.json (T2A, Felucca-only)"

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
# Version stamp - what the launcher's update check compares against.
#
# Prefer the git sha of the source we are installing FROM, because that is
# exactly what the player has on disk. Downloaded zips carry no sha, so for
# those we fall back to the current branch head, which is accurate to within
# however long ago the zip was downloaded.
#
# Failing to write this is not an install failure. It only means the launcher
# will not offer updates, which is the quiet, safe direction to fail in.
# ---------------------------------------------------------------------------
function WriteVersionStamp {
  $repo   = "Klein187/uo-offline"
  $branch = "main"
  $sha    = ""

  try {
    Push-Location $ScriptDir
    try {
      $probe = (git rev-parse HEAD 2>$null)
      if ($LASTEXITCODE -eq 0 -and $probe) { $sha = "$probe".Trim() }
    } finally { Pop-Location }
  } catch { $sha = "" }

  if (-not $sha) {
    try {
      $head = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repo/commits/$branch" `
        -Headers @{ "User-Agent" = "uo-offline-installer" } -TimeoutSec 10
      $sha = $head.sha
    } catch { $sha = "" }
  }

  if (-not $sha) {
    Warn "Could not determine the source version; the launcher will not check for updates."
    return
  }

  $stamp = [ordered]@{
    Repo         = $repo
    Branch       = $branch
    Sha          = $sha
    InstalledUtc = (Get-Date).ToUniversalTime().ToString("o")
  }
  $stamp | ConvertTo-Json | Set-Content (Join-Path $InstallRoot "uo-offline-version.json")
  Ok "Version stamp: $($sha.Substring(0, [Math]::Min(7, $sha.Length)))"
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

# Ask GitHub whether there is a newer UO Offline before starting anything.
# The checker stays silent unless there is genuinely something new, and any
# failure at all - no internet, GitHub down, rate limited - falls straight
# through to launching the game.
`$verdict = "continue"
`$updater = Join-Path `$PSScriptRoot "update-check.ps1"
if (Test-Path `$updater) {
  try { `$verdict = (& `$updater | Select-Object -Last 1) } catch { `$verdict = "continue" }
}
if (`$verdict -eq "updating") { return }

function PortOpen {
  try {
    `$c = New-Object System.Net.Sockets.TcpClient
    `$c.Connect("127.0.0.1", 2593); `$c.Close(); return `$true
  } catch { return `$false }
}

# First launch has no accounts yet, and ModernUO asks on the console whether
# to create the owner account. That question cannot be answered through a
# redirected stdin - the server treats redirected input as headless and
# refuses to read it - so the only way is for you to answer it. Show a normal
# window the first time instead of the usual minimized one.
`$firstRun = -not (Test-Path (Join-Path `$dist "Saves\Accounts\Accounts.bin"))

if (PortOpen) {
  Write-Host "Server already running - launching the game."
} elseif (`$firstRun) {
  Write-Host ""
  Write-Host "First launch - the server window will ask you two things:" -ForegroundColor Yellow
  Write-Host "  1. Create the owner account now? Answer  y" -ForegroundColor Yellow
  Write-Host "  2. Username and password. admin / admin is fine, it is your own machine." -ForegroundColor Yellow
  Write-Host "The game starts once the server finishes loading." -ForegroundColor Yellow
  Write-Host ""
  Start-Process -FilePath `$dotnet -ArgumentList "ModernUO.dll" -WorkingDirectory `$dist | Out-Null
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

  # The launcher's update checker, and the version stamp it compares against.
  $updSrc = Join-Path $ScriptDir "scripts\update-check.ps1"
  if (Test-Path $updSrc) {
    Copy-Item $updSrc (Join-Path $InstallRoot "update-check.ps1") -Force
    Ok "Wrote update-check.ps1"
  }
  WriteVersionStamp

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
  @{ Name = "Patch the engine";            Run = { ApplyEnginePatches } },
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
