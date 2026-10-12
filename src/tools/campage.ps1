# Serves campage.html on localhost:8765 and appends each POST /log body to
# -Log, prefixed with the PC's time. A plain TcpListener on the two loopback
# addresses, not HttpListener (http.sys may want a urlacl for a non-admin
# user) and not 0.0.0.0 (a firewall prompt would block an unattended run).
# Exits by itself after $Seconds.
param([Parameter(Mandatory)][string]$Log, [int]$Seconds = 4000)

$page = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'campage.html'))
$listeners = foreach ($a in [Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback) {
    $l = New-Object Net.Sockets.TcpListener($a, 8765)
    $l.Start()
    $l
}
Write-Output "campage listening on 127.0.0.1 and [::1] port 8765, log $Log"
$utf8 = New-Object Text.UTF8Encoding($false)
$sw = [Diagnostics.Stopwatch]::StartNew()

function Reply($stream, [string]$status, [string]$type, [byte[]]$body) {
    $head = [Text.Encoding]::ASCII.GetBytes(
        "HTTP/1.1 $status`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`nCache-Control: no-store`r`nConnection: close`r`n`r`n")
    $stream.Write($head, 0, $head.Length)
    $stream.Write($body, 0, $body.Length)
}

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $client = $null
    foreach ($l in $listeners) { if ($l.Pending()) { $client = $l.AcceptTcpClient(); break } }
    if (-not $client) { Start-Sleep -Milliseconds 20; continue }
    try {
        $client.ReceiveTimeout = 2000
        $s = $client.GetStream()
        # Headers byte by byte up to the blank line, so the body stays unread.
        $head = New-Object Text.StringBuilder
        while (-not $head.ToString().EndsWith("`r`n`r`n")) {
            $b = $s.ReadByte()
            if ($b -lt 0) { break }
            [void]$head.Append([char]$b)
        }
        $lines = $head.ToString() -split "`r`n"
        $method, $path = ($lines[0] -split ' ')[0, 1]
        $length = 0
        foreach ($h in $lines) { if ($h -match '^Content-Length:\s*(\d+)') { $length = [int]$Matches[1] } }
        $body = New-Object byte[] $length
        $got = 0
        while ($got -lt $length) {
            $n = $s.Read($body, $got, $length - $got)
            if ($n -le 0) { break }
            $got += $n
        }
        if ($method -eq 'POST' -and $path -eq '/log') {
            [IO.File]::AppendAllText($Log, "$((Get-Date).ToString('HH:mm:ss.fff')) $($utf8.GetString($body, 0, $got))`n", $utf8)
            Reply $s '204 No Content' 'text/plain' (New-Object byte[] 0)
        } elseif ($method -eq 'GET' -and $path -eq '/') {
            Reply $s '200 OK' 'text/html; charset=utf-8' $page
        } else {
            Reply $s '404 Not Found' 'text/plain' ([Text.Encoding]::ASCII.GetBytes('not found'))
        }
    } catch {
        Write-Output "request failed: $($_.Exception.Message)"
    } finally {
        $client.Close()
    }
}
foreach ($l in $listeners) { $l.Stop() }
Write-Output "campage stopped after $Seconds s"
