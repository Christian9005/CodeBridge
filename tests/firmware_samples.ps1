<#
.SYNOPSIS
  Checks that reading a full sample buffer (SRD) loses no frames, on a real board.

.DESCRIPTION
  A channel is set up on GPIO 34 (an analog input, nothing needs to be wired) at 10,000 Hz with the largest buffer (4,096 frames) that keeps the oldest
  frames when it is full, and left to fill. Then it is read the way a client does, asking for more frames than the board answers with, again and again.

  Checks that every answer is one line of a bounded size, that the frames of an answer and of the answers that follow one another have consecutive sequence
  numbers (a frame taken out of the channel and not sent would leave a gap) and that the buffer can be drained.

  An older firmware put all the frames of a read in one String: with a full buffer that is more than the largest free block, and a String that cannot grow
  stops without an error after the frames were already taken out of the channel.

.EXAMPLE
  pwsh ./firmware_samples.ps1 -Port COM3
#>
param(
    [Parameter(Mandatory)][string]$Port,
    [int]$Baud = 115200,
    [int]$ReplyTimeoutMs = 8000
)

$ErrorActionPreference = 'Stop'
$serial = [System.IO.Ports.SerialPort]::new($Port, $Baud)
$serial.NewLine = "`n"
$serial.ReadTimeout = $ReplyTimeoutMs
$serial.DtrEnable = $false
$serial.RtsEnable = $false
$serial.Open()
$script:failures = New-Object System.Collections.Generic.List[string]
$script:checks = 0

function Send([string]$command) {
    $serial.DiscardInBuffer()
    $serial.Write($command + "`n")
    while ($true) {
        try { $line = $serial.ReadLine().Trim() } catch [System.TimeoutException] { return $null }
        if ($line.Length -gt 0) { return $line }
    }
}

function Expect([string]$name, [bool]$condition, [string]$detail = '') {
    $script:checks++
    if ($condition) { Write-Host "  ok    $name" }
    else { $script:failures.Add("$name  $detail"); Write-Host "  FAIL  $name  $detail" -ForegroundColor Red }
}

$channel = $null
try {
    Start-Sleep -Milliseconds 2500
    $up = $false
    foreach ($try in 1..6) { $r = Send 'PING'; if ($r -like '*OK:PONG') { $up = $true; break }; Start-Sleep -Milliseconds 1000 }
    if (-not $up) { throw "The board on $Port does not answer PING." }
    Write-Host ('Firmware: ' + (Send 'VER'))
    $info = Send 'INFO'
    Write-Host "INFO: $info"

    $null = Send 'SSTOP:0'
    $config = Send 'SCFG:34:1:0:10000:4096:1:512'
    Expect 'the channel is set up' ($config -like 'OK*') "(got $config)"
    $channel = if ($config -match '^OK:(.+)$') { $Matches[1] } else { '0' }
    Start-Sleep -Milliseconds 1500    # 4,096 frames at 10,000 Hz take 0.4 s

    Write-Host "`nReading a full buffer, asking for 4096 frames each time"
    $previous = $null
    $total = 0
    $reads = 0
    $longest = 0
    $gap = $null
    $badLine = $null
    for ($i = 1; $i -le 12; $i++) {
        $reply = Send "SRD:${channel}:4096"
        if ($null -eq $reply) { $badLine = "read $i had no answer"; break }
        if ($reply -eq 'OK:NONE') { break }
        if (-not $reply.StartsWith('OK:')) { $badLine = "read ${i}: $($reply.Substring(0, [Math]::Min(60, $reply.Length)))"; break }
        $reads++
        if ($reply.Length -gt $longest) { $longest = $reply.Length }
        foreach ($frame in $reply.Substring(3).Split(';')) {
            $f = $frame.Split(',')
            if ($f.Count -ne 3) { $badLine = "read ${i}: a frame that is not seq,us,value: '$frame'"; break }
            $seq = [int64]$f[0]
            if ($null -ne $previous -and $null -eq $gap -and $seq -ne $previous + 1) { $gap = "after sequence $previous came $seq (read $i)" }
            $previous = $seq
            $total++
        }
        if ($badLine) { break }
    }
    Write-Host "  $reads reads, $total frames, the longest answer $longest characters"
    Expect 'every answer is a well-formed line' ($null -eq $badLine) "($badLine)"
    Expect 'an answer stays within a bounded size' ($longest -le 4200) "(longest $longest)"
    Expect 'frames were read' ($total -gt 0)
    Expect 'consecutive frames have consecutive sequence numbers (none taken out and not sent)' ($null -eq $gap) "($gap)"
    Expect 'asking for 4096 frames needs more than one read when they do not fit in one answer' ($reads -ge 2 -or $total -le 120) "($reads reads for $total frames)"
    Expect 'a small read still works' ((Send "SRD:${channel}:3") -like 'OK:*')
    Expect 'a read of 0 frames is refused' ((Send "SRD:${channel}:0") -like 'ERR*')
    Expect 'a channel that does not exist is refused' ((Send 'SRD:9:5') -like 'ERR*')
    Expect 'the board still answers' ((Send 'PING') -eq 'OK:PONG')
}
finally {
    $null = Send 'SSTOP:0'
    if ($serial.IsOpen) { $serial.Close() }
}

Write-Host ''
Write-Host ("{0} checks, {1} failed." -f $script:checks, $script:failures.Count)
if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
exit 0
