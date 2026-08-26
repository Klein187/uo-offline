# =========================================================================
# update-check.ps1 - "is there a newer UO Offline?" check, run at launch.
#
# start.ps1 calls this before it starts anything. The rules it follows:
#
#   - No internet, GitHub down, rate-limited, anything at all goes wrong:
#     say nothing and let the game start. A failed check must never cost
#     the player their session.
#   - Already up to date: say nothing. No popup, no splash, no "you are on
#     the latest version" box.
#   - Behind: show one dialog listing what the update actually contains,
#     and let the player choose. Declining is remembered per version, so
#     the same update never nags twice.
#
# It writes exactly one line to stdout, which start.ps1 reads:
#   continue  - carry on and launch the game
#   updating  - the installer was started; do not launch the game
#
# Anything else this prints goes to stderr or nowhere, so start.ps1's
# parse stays simple.
# =========================================================================

$ErrorActionPreference = "Stop"

$InstallRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$StampPath   = Join-Path $InstallRoot "uo-offline-version.json"
$SkipPath    = Join-Path $InstallRoot "uo-offline-skipped.txt"

# How long we are willing to make the player wait on the network before
# giving up and just starting the game.
$TimeoutSec = 6

function Emit([string]$verdict) {
    Write-Output $verdict
}

# -------------------------------------------------------------------------
# Everything below is best-effort. One try/catch around the whole check
# means any unexpected failure lands on "continue" instead of a stack
# trace in the player's face.
# -------------------------------------------------------------------------
try {
    if (-not (Test-Path $StampPath)) {
        # No version stamp: installed before this feature existed, or the
        # stamp could not be written. Nothing to compare against.
        Emit "continue"
        return
    }

    $stamp = Get-Content $StampPath -Raw | ConvertFrom-Json
    $localSha = $stamp.Sha
    $repo     = $stamp.Repo
    $branch   = $stamp.Branch

    if ([string]::IsNullOrWhiteSpace($localSha) -or
        [string]::IsNullOrWhiteSpace($repo) -or
        [string]::IsNullOrWhiteSpace($branch)) {
        Emit "continue"
        return
    }

    # PowerShell 5.1 still defaults to TLS 1.0 on some boxes; GitHub needs 1.2.
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }

    $headers = @{ "User-Agent" = "uo-offline-launcher" }

    $head = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/commits/$branch" `
        -Headers $headers -TimeoutSec $TimeoutSec
    $remoteSha = $head.sha

    if ([string]::IsNullOrWhiteSpace($remoteSha) -or
        $remoteSha -notmatch '^[0-9a-f]{40}$' -or
        $remoteSha -eq $localSha) {
        # Up to date. Say nothing at all.
        Emit "continue"
        return
    }

    # Only offer a straight fast-forward. A custom build can have a different
    # SHA while already containing main, or can have local commits alongside
    # new upstream work. Replacing either with main would silently discard
    # those changes. Use the exact SHA fetched above so the comparison,
    # changelog and eventual download all describe the same commit.
    $cmp = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repo/compare/$localSha...$remoteSha" `
        -Headers $headers -TimeoutSec $TimeoutSec

    if ($cmp.status -ne "ahead") {
        Emit "continue"
        return
    }

    # The player already said "skip" to exactly this version.
    if (Test-Path $SkipPath) {
        $skipped = (Get-Content $SkipPath -Raw).Trim()
        if ($skipped -eq $remoteSha) {
            Emit "continue"
            return
        }
    }

    # What does the confirmed fast-forward contain?
    $lines = @()
    $count = $cmp.ahead_by
    foreach ($c in $cmp.commits) {
        # First line of the commit message is the summary.
        $subject = ($c.commit.message -split "`n")[0].Trim()
        if ($subject) { $lines += "  - $subject" }
    }
    # Newest first reads better in a changelog.
    [array]::Reverse($lines)

    if ($lines.Count -eq 0) {
        $lines = @("  - A new version is available on GitHub.")
    }

    $countText = "A new version of UO Offline is available."
    if ($count -gt 0) {
        if ($count -eq 1) { $countText = "1 update is available." }
        else { $countText = "$count updates are available." }
    }

    $body = $countText + "`r`n`r`nWhat's in it:`r`n`r`n" + ($lines -join "`r`n") +
        "`r`n`r`nUpdating re-runs the installer, which rebuilds the server with " +
        "the new bots. Your world, characters and accounts are kept."

    # ---------------------------------------------------------------------
    # The dialog. WinForms rather than a console prompt because the desktop
    # shortcut runs the launcher minimized - a Read-Host would be invisible
    # and the player would just see the game never start.
    # ---------------------------------------------------------------------
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "UO Offline - update available"
    $form.Size = New-Object System.Drawing.Size(560, 420)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    # The launcher is minimized, so without this the dialog can open behind
    # everything and look like a hang.
    $form.TopMost = $true

    $text = New-Object System.Windows.Forms.TextBox
    $text.Multiline = $true
    $text.ReadOnly = $true
    $text.ScrollBars = "Vertical"
    $text.Location = New-Object System.Drawing.Point(14, 14)
    $text.Size = New-Object System.Drawing.Size(520, 300)
    $text.Text = $body
    $text.BackColor = [System.Drawing.Color]::White
    $form.Controls.Add($text)

    $btnUpdate = New-Object System.Windows.Forms.Button
    $btnUpdate.Text = "Update Now"
    $btnUpdate.Location = New-Object System.Drawing.Point(14, 328)
    $btnUpdate.Size = New-Object System.Drawing.Size(120, 30)
    $btnUpdate.DialogResult = [System.Windows.Forms.DialogResult]::Yes
    $form.Controls.Add($btnUpdate)

    $btnLater = New-Object System.Windows.Forms.Button
    $btnLater.Text = "Play Now"
    $btnLater.Location = New-Object System.Drawing.Point(144, 328)
    $btnLater.Size = New-Object System.Drawing.Size(120, 30)
    $btnLater.DialogResult = [System.Windows.Forms.DialogResult]::No
    $form.Controls.Add($btnLater)

    $btnSkip = New-Object System.Windows.Forms.Button
    $btnSkip.Text = "Skip This Version"
    $btnSkip.Location = New-Object System.Drawing.Point(274, 328)
    $btnSkip.Size = New-Object System.Drawing.Size(140, 30)
    $btnSkip.DialogResult = [System.Windows.Forms.DialogResult]::Ignore
    $form.Controls.Add($btnSkip)

    $form.AcceptButton = $btnUpdate
    $form.CancelButton = $btnLater

    $answer = $form.ShowDialog()
    $form.Dispose()

    if ($answer -eq [System.Windows.Forms.DialogResult]::Ignore) {
        Set-Content -Path $SkipPath -Value $remoteSha -Encoding ASCII
        Emit "continue"
        return
    }

    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) {
        Emit "continue"
        return
    }

    # ---------------------------------------------------------------------
    # Update: fetch the exact commit the player just reviewed and hand off to
    # its installer. Never fetch the moving branch name here: it may advance
    # between the check and the click, making the code run differ from the
    # changelog.
    # ---------------------------------------------------------------------
    $work = Join-Path $env:TEMP ("uo-offline-update-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zip = Join-Path $work "source.zip"

    Invoke-WebRequest -Uri "https://github.com/$repo/archive/$remoteSha.zip" `
        -OutFile $zip -Headers $headers

    Expand-Archive -Path $zip -DestinationPath $work -Force

    $installer = Get-ChildItem -Path $work -Recurse -Filter "install.ps1" |
        Select-Object -First 1

    if ($null -eq $installer) {
        [System.Windows.Forms.MessageBox]::Show(
            "The update downloaded but install.ps1 was not in it. Starting the game normally.",
            "UO Offline") | Out-Null
        Emit "continue"
        return
    }

    # Visible window on purpose: the rebuild takes minutes and a silent
    # background job would look like the launcher did nothing.
    Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$($installer.FullName)`""
    ) -WorkingDirectory $installer.DirectoryName | Out-Null

    Emit "updating"
    return
}
catch {
    # Offline, DNS failure, GitHub down, rate limited, malformed json,
    # anything: the player just gets their game.
    Emit "continue"
}
