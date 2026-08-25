# =========================================================================
# UO Offline — Windows GUI Installer
#
# A friendly wizard around the same install engine as install.ps1 (which is
# dot-sourced with -NoRun and driven step by step). Three screens:
#
#   1. Welcome  — what will happen, two options, one big Install button.
#   2. Progress — a checklist of every step with live status + a log.
#   3. Done     — Play Now / Close (or the error, if something failed).
#
# The engine runs in a background runspace so the window never freezes;
# output is streamed into the log through a synchronized queue.
#
# Run via install.bat (double-click), or:
#   powershell -ExecutionPolicy Bypass -File install-gui.ps1
# =========================================================================
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Definition
$EnginePath = Join-Path $ScriptDir "install.ps1"
if (-not (Test-Path $EnginePath)) {
    [System.Windows.Forms.MessageBox]::Show(
        "install.ps1 was not found next to this installer.`n`nRe-download the UO Offline zip and keep the folder together.",
        "UO Offline", "OK", "Error") | Out-Null
    exit 1
}

# Step names shown in the checklist — must mirror $InstallSteps in install.ps1.
# (The engine is the source of truth; these are read from it at runtime.)
$InstallRootLabel = Join-Path $env:USERPROFILE "uo-modernuo"

# ---------------------------------------------------------------------------
# Shared state between the UI thread and the worker runspace
# ---------------------------------------------------------------------------
$sync = [hashtable]::Synchronized(@{})
$sync.Log       = [System.Collections.Queue]::Synchronized((New-Object System.Collections.Queue))
$sync.Step      = -1        # index of the step currently running
$sync.StepNames = @()
$sync.Done      = $false
$sync.Error     = $null

# ---------------------------------------------------------------------------
# Look & feel
# ---------------------------------------------------------------------------
$colBack   = [System.Drawing.Color]::FromArgb(24, 22, 20)
$colPanel  = [System.Drawing.Color]::FromArgb(34, 31, 28)
$colGold   = [System.Drawing.Color]::FromArgb(212, 175, 55)
$colText   = [System.Drawing.Color]::FromArgb(230, 225, 214)
$colDim    = [System.Drawing.Color]::FromArgb(150, 143, 130)
$colGreen  = [System.Drawing.Color]::FromArgb(120, 190, 100)
$colRed    = [System.Drawing.Color]::FromArgb(220, 90, 80)
$fontTitle = New-Object System.Drawing.Font("Georgia", 20, [System.Drawing.FontStyle]::Bold)
$fontHead  = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
$fontBody  = New-Object System.Drawing.Font("Segoe UI", 10)
$fontMono  = New-Object System.Drawing.Font("Consolas", 9)

$form = New-Object System.Windows.Forms.Form
$form.Text            = "UO Offline — Installer"
$form.ClientSize      = New-Object System.Drawing.Size(820, 648)
$form.StartPosition   = "CenterScreen"
$form.FormBorderStyle = "FixedSingle"
$form.MaximizeBox     = $false
$form.BackColor       = $colBack
$icoPath = Join-Path $ScriptDir "uoico.ico"
if (Test-Path $icoPath) { $form.Icon = New-Object System.Drawing.Icon($icoPath) }

function NewLabel($text, $x, $y, $w, $h, $font, $color) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, $y)
    $l.Size = New-Object System.Drawing.Size($w, $h)
    $l.Font = $font; $l.ForeColor = $color; $l.BackColor = [System.Drawing.Color]::Transparent
    return $l
}

# ---------------------------------------------------------------------------
# Screen 1 — Welcome
# ---------------------------------------------------------------------------
$panelWelcome = New-Object System.Windows.Forms.Panel
$panelWelcome.Dock = "Fill"; $panelWelcome.BackColor = $colBack

$panelWelcome.Controls.Add((NewLabel "Ultima Online — Offline" 40 32 740 44 $fontTitle $colGold))
$panelWelcome.Controls.Add((NewLabel "A complete single-player UO shard on your own PC. No accounts, no internet after install." 42 82 740 24 $fontBody $colDim))

$introBox = New-Object System.Windows.Forms.Panel
$introBox.Location = New-Object System.Drawing.Point(40, 120)
$introBox.Size     = New-Object System.Drawing.Size(740, 240)
$introBox.BackColor = $colPanel
$panelWelcome.Controls.Add($introBox)

$introBox.Controls.Add((NewLabel "This installer will:" 20 16 690 24 $fontHead $colText))
$introSteps = @(
    "1.  Build the game server — with the living world of player-like bots compiled in",
    "2.  Download the ClassicUO game client and the Razor assistant (macros and hotkeys)",
    "3.  Fetch the UO art and map data (~1.3 GB, from community mirrors)",
    "4.  Configure everything for offline play on this PC only",
    "5.  Put a `"UO Offline`" shortcut on your desktop — one click starts the server and takes you in-game with Razor attached"
)
$yy = 48
foreach ($s in $introSteps) {
    $introBox.Controls.Add((NewLabel $s 28 $yy 690 ([int](24 * [Math]::Ceiling($s.Length / 95.0))) $fontBody $colText))
    $yy += [int](26 * [Math]::Ceiling($s.Length / 95.0))
}
$introBox.Controls.Add((NewLabel "Takes 15–25 minutes depending on your connection. Safe to re-run — finished steps are skipped." 28 ($yy + 6) 690 24 $fontBody $colDim))

$optBox = New-Object System.Windows.Forms.Panel
$optBox.Location = New-Object System.Drawing.Point(40, 376)
$optBox.Size     = New-Object System.Drawing.Size(740, 138)
$optBox.BackColor = $colPanel
$panelWelcome.Controls.Add($optBox)
$optBox.Controls.Add((NewLabel "Options" 20 12 300 24 $fontHead $colText))

$chkT2A = New-Object System.Windows.Forms.CheckBox
$chkT2A.Text = "Install the authentic T2A-era world map (recommended — pre-destruction Magincia)"
$chkT2A.Checked = $true
$chkT2A.Location = New-Object System.Drawing.Point(28, 42)
$chkT2A.Size = New-Object System.Drawing.Size(690, 24)
$chkT2A.Font = $fontBody; $chkT2A.ForeColor = $colText
$optBox.Controls.Add($chkT2A)

$chkRazor = New-Object System.Windows.Forms.CheckBox
$chkRazor.Text = "Install the Razor assistant (recommended — loads inside the game client)"
$chkRazor.Checked = $true
$chkRazor.Location = New-Object System.Drawing.Point(28, 70)
$chkRazor.Size = New-Object System.Drawing.Size(690, 24)
$chkRazor.Font = $fontBody; $chkRazor.ForeColor = $colText
$optBox.Controls.Add($chkRazor)

$chkMap = New-Object System.Windows.Forms.CheckBox
$chkMap.Text = "Install the map editor (waypoints, spawns and a live view of every bot)"
$chkMap.Checked = $true
$chkMap.Location = New-Object System.Drawing.Point(28, 98)
$chkMap.Size = New-Object System.Drawing.Size(690, 24)
$chkMap.Font = $fontBody; $chkMap.ForeColor = $colText
$optBox.Controls.Add($chkMap)

$panelWelcome.Controls.Add((NewLabel "Installs to:" 42 549 78 22 $fontBody $colDim))

# Editable on purpose: Change... is the easy path, but typing a path directly
# is quicker if you already know where it goes.
$txtPath = New-Object System.Windows.Forms.TextBox
$txtPath.Text = $InstallRootLabel
$txtPath.Location = New-Object System.Drawing.Point(124, 546)
$txtPath.Size = New-Object System.Drawing.Size(310, 26)
$txtPath.Font = $fontBody
$txtPath.BackColor = $colPanel; $txtPath.ForeColor = $colText
$txtPath.BorderStyle = "FixedSingle"
$panelWelcome.Controls.Add($txtPath)

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = "Change..."
$btnBrowse.Location = New-Object System.Drawing.Point(444, 544)
$btnBrowse.Size = New-Object System.Drawing.Size(100, 30)
$btnBrowse.Font = $fontBody
$btnBrowse.BackColor = $colPanel; $btnBrowse.ForeColor = $colText; $btnBrowse.FlatStyle = "Flat"
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Choose where to install UO Offline. Needs about 6 GB free."
    if (Test-Path $txtPath.Text) { $dlg.SelectedPath = $txtPath.Text }
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        # A folder picker returns the folder you clicked, so put our own
        # folder inside it - otherwise picking D:\Games would scatter
        # ModernUO, ClassicUO, Razor and 3 GB of game data across it.
        $chosen = $dlg.SelectedPath
        if ((Split-Path -Leaf $chosen) -ne "uo-modernuo") {
            $chosen = [IO.Path]::Combine($chosen, "uo-modernuo")
        }
        $txtPath.Text = $chosen
    }
    $dlg.Dispose()
})
$panelWelcome.Controls.Add($btnBrowse)

$btnInstall = New-Object System.Windows.Forms.Button
$btnInstall.Text = "Install"
$btnInstall.Location = New-Object System.Drawing.Point(600, 576)
$btnInstall.Size = New-Object System.Drawing.Size(180, 44)
$btnInstall.Font = $fontHead
$btnInstall.BackColor = $colGold
$btnInstall.ForeColor = $colBack
$btnInstall.FlatStyle = "Flat"
$panelWelcome.Controls.Add($btnInstall)

$btnQuit = New-Object System.Windows.Forms.Button
$btnQuit.Text = "Cancel"
$btnQuit.Location = New-Object System.Drawing.Point(470, 576)
$btnQuit.Size = New-Object System.Drawing.Size(115, 44)
$btnQuit.Font = $fontBody
$btnQuit.BackColor = $colPanel; $btnQuit.ForeColor = $colText; $btnQuit.FlatStyle = "Flat"
$btnQuit.Add_Click({ $form.Close() })
$panelWelcome.Controls.Add($btnQuit)

# ---------------------------------------------------------------------------
# Screen 2 — Progress (checklist + log)
# ---------------------------------------------------------------------------
$panelProgress = New-Object System.Windows.Forms.Panel
$panelProgress.Dock = "Fill"; $panelProgress.BackColor = $colBack; $panelProgress.Visible = $false

$panelProgress.Controls.Add((NewLabel "Installing..." 40 24 500 40 $fontTitle $colGold))
$lblStage = NewLabel "Preparing..." 42 68 730 24 $fontBody $colDim
$panelProgress.Controls.Add($lblStage)

$stepList = New-Object System.Windows.Forms.ListBox
$stepList.Location = New-Object System.Drawing.Point(40, 104)
$stepList.Size = New-Object System.Drawing.Size(280, 400)
$stepList.Font = $fontBody
$stepList.BackColor = $colPanel; $stepList.ForeColor = $colText
$stepList.BorderStyle = "None"
$stepList.SelectionMode = "None"
$stepList.ItemHeight = 26
$stepList.DrawMode = "OwnerDrawFixed"
$stepList.Add_DrawItem({
    param($s, $e)
    if ($e.Index -lt 0) { return }
    $e.DrawBackground()
    $item = $stepList.Items[$e.Index]
    $glyph = "  "; $color = $colDim
    if ($sync.Error -and $e.Index -eq $sync.Step) { $glyph = [string][char]0x2717; $color = $colRed }        # x
    elseif ($e.Index -lt $sync.Step -or $sync.Done -and -not $sync.Error) { $glyph = [string][char]0x2713; $color = $colGreen } # check
    elseif ($e.Index -eq $sync.Step) { $glyph = [string][char]0x25B8; $color = $colGold }                    # arrow
    $brushG = New-Object System.Drawing.SolidBrush($color)
    $brushT = New-Object System.Drawing.SolidBrush($(if ($e.Index -le $sync.Step) { $colText } else { $colDim }))
    $e.Graphics.DrawString($glyph, $fontBody, $brushG, $e.Bounds.X + 6, $e.Bounds.Y + 4)
    $e.Graphics.DrawString([string]$item, $fontBody, $brushT, $e.Bounds.X + 28, $e.Bounds.Y + 4)
    $brushG.Dispose(); $brushT.Dispose()
})
$panelProgress.Controls.Add($stepList)

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Location = New-Object System.Drawing.Point(336, 104)
$logBox.Size = New-Object System.Drawing.Size(444, 400)
$logBox.Multiline = $true; $logBox.ReadOnly = $true; $logBox.ScrollBars = "Vertical"
$logBox.Font = $fontMono
$logBox.BackColor = $colPanel; $logBox.ForeColor = $colText
$logBox.BorderStyle = "None"
$panelProgress.Controls.Add($logBox)

$progBar = New-Object System.Windows.Forms.ProgressBar
$progBar.Location = New-Object System.Drawing.Point(40, 568)
$progBar.Size = New-Object System.Drawing.Size(740, 18)
$progBar.Style = "Continuous"
$panelProgress.Controls.Add($progBar)

$lblPatience = NewLabel "Long quiet spells are normal (cloning, building, big downloads). This window stays responsive." 42 544 600 22 $fontBody $colDim
$panelProgress.Controls.Add($lblPatience)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text = "Cancel"
$btnCancel.Location = New-Object System.Drawing.Point(665, 592)
$btnCancel.Size = New-Object System.Drawing.Size(115, 36)
$btnCancel.Font = $fontBody
$btnCancel.BackColor = $colPanel; $btnCancel.ForeColor = $colText; $btnCancel.FlatStyle = "Flat"
$panelProgress.Controls.Add($btnCancel)

# ---------------------------------------------------------------------------
# Screen 3 — Done
# ---------------------------------------------------------------------------
$panelDone = New-Object System.Windows.Forms.Panel
$panelDone.Dock = "Fill"; $panelDone.BackColor = $colBack; $panelDone.Visible = $false

$lblDoneTitle = NewLabel "Install complete!" 40 40 700 44 $fontTitle $colGold
$panelDone.Controls.Add($lblDoneTitle)
$lblDoneBody = NewLabel "" 42 100 730 150 $fontBody $colText
$panelDone.Controls.Add($lblDoneBody)

$doneLog = New-Object System.Windows.Forms.TextBox
$doneLog.Location = New-Object System.Drawing.Point(40, 260)
$doneLog.Size = New-Object System.Drawing.Size(740, 250)
$doneLog.Multiline = $true; $doneLog.ReadOnly = $true; $doneLog.ScrollBars = "Vertical"
$doneLog.Font = $fontMono
$doneLog.BackColor = $colPanel; $doneLog.ForeColor = $colText
$doneLog.BorderStyle = "None"
$panelDone.Controls.Add($doneLog)

$btnPlay = New-Object System.Windows.Forms.Button
$btnPlay.Text = "Play Now"
$btnPlay.Location = New-Object System.Drawing.Point(600, 576)
$btnPlay.Size = New-Object System.Drawing.Size(180, 44)
$btnPlay.Font = $fontHead
$btnPlay.BackColor = $colGold; $btnPlay.ForeColor = $colBack; $btnPlay.FlatStyle = "Flat"
$btnPlay.Add_Click({
    $bat = Join-Path $InstallRootLabel "start.bat"
    if (Test-Path $bat) { Start-Process -FilePath $bat -WorkingDirectory $InstallRootLabel }
    $form.Close()
})
$panelDone.Controls.Add($btnPlay)

$btnClose = New-Object System.Windows.Forms.Button
$btnClose.Text = "Close"
$btnClose.Location = New-Object System.Drawing.Point(470, 576)
$btnClose.Size = New-Object System.Drawing.Size(115, 44)
$btnClose.Font = $fontBody
$btnClose.BackColor = $colPanel; $btnClose.ForeColor = $colText; $btnClose.FlatStyle = "Flat"
$btnClose.Add_Click({ $form.Close() })
$panelDone.Controls.Add($btnClose)

$form.Controls.Add($panelWelcome)
$form.Controls.Add($panelProgress)
$form.Controls.Add($panelDone)

# ---------------------------------------------------------------------------
# The worker — dot-sources the engine and walks its $InstallSteps.
# ---------------------------------------------------------------------------
$script:ps = $null
$script:rs = $null

function StartWorker($optT2A, $optRazor, $optMap, $installPath) {
    $script:rs = [runspacefactory]::CreateRunspace()
    $script:rs.Open()
    $script:rs.SessionStateProxy.SetVariable('sync',       $sync)
    $script:rs.SessionStateProxy.SetVariable('EnginePath', $EnginePath)
    $script:rs.SessionStateProxy.SetVariable('OptT2A',     $optT2A)
    $script:rs.SessionStateProxy.SetVariable('OptRazor',   $optRazor)
    $script:rs.SessionStateProxy.SetVariable('OptMap',     $optMap)
    $script:rs.SessionStateProxy.SetVariable('InstallChoice', $installPath)

    $script:ps = [powershell]::Create()
    $script:ps.Runspace = $script:rs
    [void]$script:ps.AddScript({
        try {
            . $EnginePath -NoRun
            if ($InstallChoice) { Set-InstallRoot $InstallChoice }
            $InstallT2AMap = $OptT2A
            $InstallRazor  = $OptRazor
            $InstallMapEditor = $OptMap

            # Re-point the engine's console voice at the GUI log.
            function Banner($m) { $sync.Log.Enqueue("") ; $sync.Log.Enqueue("=== $m ===") }
            function Say($m)    { $sync.Log.Enqueue("    $m") }
            function Ok($m)     { $sync.Log.Enqueue("[ok] $m") }
            function Warn($m)   { $sync.Log.Enqueue("[!!] $m") }

            $sync.StepNames = @($InstallSteps | ForEach-Object { $_.Name })
            for ($i = 0; $i -lt $InstallSteps.Count; $i++) {
                $sync.Step = $i
                & $InstallSteps[$i].Run | Out-Null
            }
            $sync.Step = $InstallSteps.Count
        } catch {
            $sync.Error = $_.Exception.Message
            $sync.Log.Enqueue("")
            $sync.Log.Enqueue("[XX] $($_.Exception.Message)")
        }
        $sync.Done = $true
    })
    [void]$script:ps.BeginInvoke()
}

# ---------------------------------------------------------------------------
# UI pump — drains the log queue, repaints the checklist, flips screens.
# ---------------------------------------------------------------------------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
    # Populate the checklist once the worker has read the engine's step list.
    if ($stepList.Items.Count -eq 0 -and $sync.StepNames.Count -gt 0) {
        foreach ($n in $sync.StepNames) { [void]$stepList.Items.Add($n) }
        $progBar.Maximum = $sync.StepNames.Count
    }

    $dirty = $false
    while ($sync.Log.Count -gt 0) {
        $line = $sync.Log.Dequeue()
        $logBox.AppendText($line + [Environment]::NewLine)
        if ($line -match '^(    |=== )') { $lblStage.Text = ($line -replace '^(=== |    )', '' -replace ' ===$', '') }
        $dirty = $true
    }

    if ($stepList.Items.Count -gt 0) {
        $running = [Math]::Min([Math]::Max($sync.Step, 0), $progBar.Maximum)
        if ($progBar.Value -ne $running) { $progBar.Value = $running }
        if ($dirty -or $sync.Done) { $stepList.Invalidate() }
    }

    if ($sync.Done) {
        $timer.Stop()
        $panelProgress.Visible = $false
        $doneLog.Text = $logBox.Text
        $doneLog.SelectionStart = $doneLog.Text.Length; $doneLog.ScrollToCaret()
        if ($sync.Error) {
            $lblDoneTitle.Text = "Something went wrong"
            $lblDoneTitle.ForeColor = $colRed
            $lblDoneBody.Text = "The install stopped at: $(if ($sync.StepNames.Count -gt 0 -and $sync.Step -lt $sync.StepNames.Count) { $sync.StepNames[$sync.Step] } else { 'unknown step' })`r`n`r`n$($sync.Error)`r`n`r`nFix the issue and run the installer again — completed steps are skipped, so it picks up where it left off."
            $btnPlay.Visible = $false
        } else {
            $lblDoneTitle.Text = "Install complete!"
            $lblDoneBody.Text = "Everything is ready. A `"UO Offline`" shortcut is on your desktop.`r`n`r`nClicking it starts the server, then opens the game with Razor attached and logs you straight into the shard (account: admin / admin).`r`n`r`nFirst boot takes a minute while the world generates its bot population."
        }
        $panelDone.Visible = $true
    }
})

$btnInstall.Add_Click({
    $panelWelcome.Visible = $false
    $panelProgress.Visible = $true
    $chosenPath = $txtPath.Text.Trim()
    if (-not $chosenPath) {
        [System.Windows.Forms.MessageBox]::Show(
            "Please choose a folder to install into.", "UO Offline") | Out-Null
        return
    }
    $script:InstallRootLabel = $chosenPath
    StartWorker $chkT2A.Checked $chkRazor.Checked $chkMap.Checked $chosenPath
    $timer.Start()
})

$btnCancel.Add_Click({
    $r = [System.Windows.Forms.MessageBox]::Show(
        "Stop the install? You can re-run it later — finished steps are kept.",
        "UO Offline", "YesNo", "Question")
    if ($r -eq "Yes") {
        try { if ($script:ps) { $script:ps.Stop() } } catch {}
        $form.Close()
    }
})

$form.Add_FormClosed({
    try { if ($script:ps) { $script:ps.Stop(); $script:ps.Dispose() } } catch {}
    try { if ($script:rs) { $script:rs.Close(); $script:rs.Dispose() } } catch {}
})

[void]$form.ShowDialog()
