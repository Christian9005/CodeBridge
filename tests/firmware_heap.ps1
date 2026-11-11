<#
.SYNOPSIS
  Sends a board many rounds of mixed commands and watches its free heap, to find memory the firmware does not give back.

.DESCRIPTION
  The firmware allocates for strings, for the NeoPixel strip, the OLED, the LCD and the sensors (new/delete), for the sample buffers (malloc/free), and for
  the answers of the scans. A leak or a fragmentation shows as free heap that goes down round after round. Each round sends valid commands, commands that fail,
  lines that are too long, and the commands that allocate the most; then it reads the free heap with INFO.

  Fails if, after the warm-up rounds, the free heap ends more than -MaxLossBytes below where it started, or if it keeps falling (its line has a slope below
  -MaxSlopeBytesPer100Rounds), or if the board reset or stopped answering.

  The commands drive pins for real (a servo on GPIO 13, a stepper, a motor, a NeoPixel on 5): unplug anything that must not move.

.EXAMPLE
  pwsh ./firmware_heap.ps1 -Port COM3 -Rounds 600
#>
param(
    [Parameter(Mandatory)][string]$Port,
    [int]$Rounds = 600,
    [int]$WarmupRounds = 30,
    [int]$MaxLossBytes = 2048,
    [double]$MaxSlopeBytesPer100Rounds = -400,
    [int]$Baud = 115200,
    [int]$ReplyTimeoutMs = 4000
)

$ErrorActionPreference = 'Stop'
$serial = [System.IO.Ports.SerialPort]::new($Port, $Baud)
$serial.NewLine = "`n"
$serial.ReadTimeout = $ReplyTimeoutMs
$serial.DtrEnable = $false
$serial.RtsEnable = $false
$serial.Open()
$script:resets = 0

$script:latency = @{}
function Send([string]$command, [int]$timeoutMs = $ReplyTimeoutMs) {
    $serial.ReadTimeout = $timeoutMs
    $serial.DiscardInBuffer()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $serial.Write($command + "`n")
    $name = ($command -split ':')[0]
    if ($name.Length -gt 12) { $name = $name.Substring(0, 12) }
    while ($true) {
        try { $line = $serial.ReadLine().Trim() } catch [System.TimeoutException] { Note $name $watch.ElapsedMilliseconds $true; return $null }
        if ($line -ne 'OK:CODEBRIDGE_READY') { Note $name $watch.ElapsedMilliseconds $false }
        if ($line -eq 'OK:CODEBRIDGE_READY') { $script:resets++; continue }
        if ($line.Length -gt 0) { return $line }
    }
}

function Note([string]$name, [long]$ms, [bool]$timedOut) {
    if (-not $script:latency.ContainsKey($name)) { $script:latency[$name] = [pscustomobject]@{ Name = $name; Count = 0; TotalMs = 0; MaxMs = 0; Slow = 0; TimedOut = 0 } }
    $e = $script:latency[$name]
    $e.Count++; $e.TotalMs += $ms
    if ($ms -gt $e.MaxMs) { $e.MaxMs = $ms }
    if ($ms -ge 1000) { $e.Slow++ }
    if ($timedOut) { $e.TimedOut++ }
}

function FreeHeap {
    # After a line that was too long the board may still owe an answer: look for the one that carries the heap among the next few lines.
    $serial.ReadTimeout = $ReplyTimeoutMs
    $serial.DiscardInBuffer()
    $serial.Write("INFO`n")
    for ($i = 0; $i -lt 6; $i++) {
        try { $line = $serial.ReadLine().Trim() } catch [System.TimeoutException] { return $null }
        if ($line -match '"heap":(\d+)') {
            $heap = [int]$Matches[1]
            # firmware 0.9.3 and later also report the lowest free heap since boot and the largest block that can be allocated (a heap in pieces shows there)
            if ($line -match '"minheap":(\d+)') { $script:minHeap = [int]$Matches[1] }
            if ($line -match '"maxblock":(\d+)') { if ($null -eq $script:smallestBlock -or [int]$Matches[1] -lt $script:smallestBlock) { $script:smallestBlock = [int]$Matches[1] }; $script:lastBlock = [int]$Matches[1] }
            return $heap
        }
    }
    return $null
}

# what a round sends: [command, timeout in ms]
$round = @(
    @('PING'), @('VER'), @('DW:2:1'), @('DW:2:0'), @('PM:2:1'), @('AR:34'), @('PW:2:128:5000'), @('PW:2:0:5000'),
    @('SA:13'), @('SV:13:45'), @('SV:13:135'), @('SD:13'),
    @('MI:25:26:-1'), @('MS:25:50'), @('MX:25'),
    @('STI:25:26:27:14:2048'), @('STS:25:4:60'),
    @('NI:5:8'), @('NS:0:255:0:0'), @('NA:0:40:0'), @('NH'), @('NR:0:10:20:30:40:50:60'), @('NC'),
    @('NI:5:400'), @('NI:5:8'),
    @('IS'), @('IR:60:4'), @('IW:60:AABB'), @('IWR:60:0:AABB'), @('IRR:60:0:4'),
    @('OWS:4'), @('OWR:4:0000000000000000:8'), @('OWT:4'),
    @('DHTR:4:22'), @('USR:12:14'), @('BMI:118'), @('BHI:35'), @('LI:39:16:2'), @('OI:60:128:64'),
    @('SCFG:34:1:0:100:64:0:8'), @('SRD:0:4'), @('SSTOP:0'),
    @('DW:6:1'), @('DW:abc'), @('NOSUCHCOMMAND:1:2:3'), @(':::::'), @('STS:25:1000000000:10'), @('TN:2:440:5'),
    @(('X' * 600)), @(('DW:2:' + ('1' * 450))), @(('PING:' + ('A' * 2000)))
)

"Port $Port, $Rounds rounds of $($round.Count) commands (the first $WarmupRounds are warm-up)."
# Opening the port can reset the board and leave noise on the line: give it time to boot and ask a few times, accepting a PONG that has noise in front of it.
Start-Sleep -Milliseconds 2500
$start = $null
foreach ($try in 1..6) {
    $start = Send 'PING'
    if ($start -like '*OK:PONG') { break }
    Start-Sleep -Milliseconds 1000
}
if ($start -notlike '*OK:PONG') { throw "The board on $Port does not answer PING ($start)." }
$script:resets = 0
$heap0 = FreeHeap
if ($null -eq $heap0) { throw 'INFO gave no heap: is this the CodeBridge firmware?' }
"Free heap at the start: $heap0 bytes"

$heaps = New-Object System.Collections.Generic.List[int]
$noAnswer = 0
$silent = New-Object System.Collections.Generic.List[string]
$recent = New-Object System.Collections.Generic.List[string]
$stalls = 0
$problems = New-Object System.Collections.Generic.List[string]
for ($r = 1; $r -le $Rounds; $r++) {
    foreach ($entry in $round) {
        $reply = Send $entry[0]
        $recent.Add($entry[0].Substring(0, [Math]::Min(16, $entry[0].Length)) + '=' + $(if ($null -eq $reply) { 'none' } else { $reply.Substring(0, [Math]::Min(10, $reply.Length)) }))
        if ($recent.Count -gt 6) { $recent.RemoveAt(0) }
        if ($null -eq $reply) { $noAnswer++; if ($silent.Count -lt 8) { $silent.Add("round ${r}: " + $entry[0].Substring(0, [Math]::Min(40, $entry[0].Length))) } }
    }
    # the heavy ones, now and then: the Wi-Fi scan allocates the most
    if (($r % 25) -eq 0) { $null = Send 'WSCAN' 20000 }
    if (($r % 5) -eq 0) { $null = Send 'WSTAT' }
    $heap = FreeHeap
    if ($null -eq $heap) {
        # a board that goes quiet: how long for, and does it come back?
        $quiet = [Diagnostics.Stopwatch]::StartNew()
        $tail = ($recent -join ' | ')
        for ($try = 1; $try -le 15 -and $null -eq $heap; $try++) { Start-Sleep -Milliseconds 1000; $heap = FreeHeap }
        if ($null -eq $heap) {
            $problems.Add("round ${r}: the board did not answer INFO and was still silent after $([int]$quiet.Elapsed.TotalSeconds) s. Last commands: $tail")
            break
        }
        $problems.Add("round ${r}: INFO gave no heap at first and answered $([int]$quiet.Elapsed.TotalSeconds) s later. Last commands: $tail")
        $stalls++
    }
    $heaps.Add($heap)
    if (($r % 50) -eq 1 -or $r -eq $Rounds) { "  round {0,4}: free heap {1,7} bytes   resets {2}   no answer {3}" -f $r, $heap, $script:resets, $noAnswer }
}
if ($serial.IsOpen) { $serial.Close() }

# the line through the free heap after the warm-up
$after = @(for ($i = $WarmupRounds; $i -lt $heaps.Count; $i++) { [pscustomobject]@{ x = [double]$i; y = [double]$heaps[$i] } })
$slopePer100 = 0.0
if ($after.Count -ge 3) {
    $mx = ($after | ForEach-Object { $_.x } | Measure-Object -Average).Average
    $my = ($after | ForEach-Object { $_.y } | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    foreach ($p in $after) { $num += ($p.x - $mx) * ($p.y - $my); $den += ($p.x - $mx) * ($p.x - $mx) }
    if ($den -ne 0) { $slopePer100 = [math]::Round(100 * $num / $den, 1) }
}
$baseline = if ($after.Count) { $after[0].y } else { $heap0 }
$final = if ($heaps.Count) { $heaps[$heaps.Count - 1] } else { $heap0 }
$lowest = if ($heaps.Count) { ($heaps | Measure-Object -Minimum).Minimum } else { $heap0 }
$highest = if ($heaps.Count) { ($heaps | Measure-Object -Maximum).Maximum } else { $heap0 }
$lost = [int]($baseline - $final)

if ($script:resets -gt 0) { $problems.Add("the board reset $($script:resets) time(s)") }
if ($lost -gt $MaxLossBytes) { $problems.Add("the free heap ended $lost bytes below where it was after the warm-up (limit $MaxLossBytes)") }
if ($slopePer100 -lt $MaxSlopeBytesPer100Rounds) { $problems.Add("the free heap keeps falling: $slopePer100 bytes per 100 rounds (limit $MaxSlopeBytesPer100Rounds)") }
if ($noAnswer -gt 0) { $problems.Add("$noAnswer command(s) had no answer, the first ones: " + ($silent -join '; ')) }

''
"Commands that took a second or more (the rest answer in milliseconds):"
$script:latency.Values | Where-Object { $_.Slow -gt 0 -or $_.TimedOut -gt 0 } | Sort-Object MaxMs -Descending | ForEach-Object {
    "  {0,-13} {1,6} sent, {2,3} took 1 s or more, {3,3} without answer, slowest {4,6} ms, average {5,6} ms" -f $_.Name, $_.Count, $_.Slow, $_.TimedOut, $_.MaxMs, [int]($_.TotalMs / $_.Count)
}
"Slowest of the others: " + (($script:latency.Values | Where-Object { $_.Slow -eq 0 -and $_.TimedOut -eq 0 } | Sort-Object MaxMs -Descending | Select-Object -First 4 | ForEach-Object { "$($_.Name) $($_.MaxMs) ms" }) -join ', ')
''
"Free heap: start $heap0, after warm-up $baseline, final $final, lowest $lowest, highest $highest"
if ($null -ne $script:minHeap) { "Lowest free heap since boot (reported by the board): $($script:minHeap) bytes; largest free block: last $($script:lastBlock), smallest seen $($script:smallestBlock) bytes" }
"Trend after warm-up:$slopePer100 bytes per 100 rounds; net change $(-$lost) bytes over $($heaps.Count - $WarmupRounds) rounds ($(($heaps.Count - $WarmupRounds) * $round.Count) commands)"
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  PROBLEM: $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'The firmware gives its memory back.' -ForegroundColor Green
exit 0
