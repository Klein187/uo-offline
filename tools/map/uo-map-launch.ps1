# =========================================================================
# launch-map.ps1 — one click to the UO bot world map editor (Windows).
# Starts serve_map.py (hidden, via pythonw) only if it isn't already
# serving on :8777, waits for it to answer, then opens the map in the
# default browser. Clicking again when it's already up just opens a tab.
# =========================================================================
$ErrorActionPreference = 'SilentlyContinue'

$url   = 'http://localhost:8777/map.html'
$serve = Join-Path $env:USERPROFILE 'uo-map\serve_map.py'

function Test-Up {
    try {
        $c = New-Object Net.Sockets.TcpClient
        $c.Connect('127.0.0.1', 8777)
        $ok = $c.Connected
        $c.Close()
        return $ok
    } catch { return $false }
}

if (-not (Test-Up)) {
    $pyw = 'C:\Python314\pythonw.exe'
    if (-not (Test-Path $pyw)) {
        $cmd = Get-Command pythonw.exe -ErrorAction SilentlyContinue
        if ($cmd) { $pyw = $cmd.Source }
    }
    Start-Process -FilePath $pyw -ArgumentList "`"$serve`"" `
        -WorkingDirectory (Join-Path $env:USERPROFILE 'uo-map') -WindowStyle Hidden

    # wait up to ~5s for it to bind the port
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Up) { break }
    }
}

Start-Process $url
