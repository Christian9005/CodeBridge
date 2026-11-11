<#
.SYNOPSIS
  Checks the GPIO interrupt commands (GINT, GINTP, GINTD) of a real board with nothing wired to it.

.DESCRIPTION
  A pin that is an input with nothing connected follows its internal resistor: switching it between pull-up (PM:pin:2) and pull-down (PM:pin:3) makes a real
  edge that the interrupt sees. The script makes a known number of edges on each pin and checks that GINTP reports exactly that many, that a pin keeps
  counting when an earlier one is detached (the table of handlers is shifted), and that the table can be filled and emptied again.

  It cannot show the race between a pulse and the poll (that window is a few instructions wide); it shows that the counts are right and stay right
  after the code that guards that window.

  Uses GPIO 4, 5, 23, 16, 17, 18, 19 and 21 as inputs with a pull resistor: disconnect anything that is attached to them. (Not GPIO 15: it is a boot pin
  with resistors on the DevKit and counts spurious edges.)

.EXAMPLE
  pwsh ./firmware_interrupts.ps1 -Port COM3
#>
param(
    [Parameter(Mandatory)][string]$Port,
    [int]$Baud = 115200,
    [int]$ReplyTimeoutMs = 3000
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

# n edges on a pin (n is even, so the pin ends where it began): the pin is pulled up when the interrupt is attached, so the first switch is a falling edge
function Edges([int]$pin, [int]$n) {
    for ($i = 0; $i -lt $n; $i++) { $null = Send ("PM:{0}:{1}" -f $pin, $(if ($i % 2 -eq 0) { 3 } else { 2 })) }
}

# what GINTP says, as pin -> count
function Poll {
    $reply = Send 'GINTP'
    $counts = @{}
    if ($reply -and $reply.StartsWith('OK:') -and $reply -ne 'OK:NONE') {
        foreach ($item in $reply.Substring(3).Split(',')) {
            $f = $item.Split(':')
            if ($f.Count -ge 2) { $counts[[int]$f[0]] = [int]$f[1] }
        }
    }
    return $counts
}

try {
    Start-Sleep -Milliseconds 2500
    $up = $false
    foreach ($try in 1..6) { $r = Send 'PING'; if ($r -like '*OK:PONG') { $up = $true; break }; Start-Sleep -Milliseconds 1000 }
    if (-not $up) { throw "The board on $Port does not answer PING." }
    Write-Host ('Firmware: ' + (Send 'VER'))

    # a clean table, whatever an earlier run left
    foreach ($pin in 4, 5, 23, 16, 17, 18, 19, 21) { $null = Send "GINTD:$pin" }

    Write-Host "`nOne pin"
    Expect 'attach on CHANGE' ((Send 'GINT:4:3') -eq 'OK')
    $null = Poll
    Edges 4 10
    $c = Poll
    Expect '10 switches, 10 edges counted' ($c[4] -eq 10) "(got $($c[4]))"
    Expect 'a poll clears what it reported' ((Send 'GINTP') -eq 'OK:NONE')
    Edges 4 4
    $null = Poll
    Edges 4 6
    $c = Poll
    Expect 'counting starts again after a poll' ($c[4] -eq 6) "(got $($c[4]))"

    Write-Host "`nSeveral pins, then the first one is detached (the others move down one slot)"
    Expect 'attach pin 5' ((Send 'GINT:5:3') -eq 'OK')
    Expect 'attach pin 23' ((Send 'GINT:23:3') -eq 'OK')
    $null = Poll
    Edges 4 2; Edges 5 4; Edges 23 8
    $c = Poll
    Expect 'each pin has its own count' (($c[4] -eq 2) -and ($c[5] -eq 4) -and ($c[23] -eq 8)) "(got 4=$($c[4]) 5=$($c[5]) 23=$($c[23]))"
    Expect 'detach the first' ((Send 'GINTD:4') -eq 'OK')
    $null = Poll
    Edges 4 4; Edges 5 6; Edges 23 8
    $c = Poll
    Expect 'the detached pin no longer counts' (-not $c.ContainsKey(4)) "(got $($c[4]))"
    Expect 'pin 5 kept counting after the move' ($c[5] -eq 6) "(got $($c[5]))"
    Expect 'pin 23 kept counting after the move' ($c[23] -eq 8) "(got $($c[23]))"
    Expect 'detach a pin that is not attached' ((Send 'GINTD:4') -like 'ERR*')
    Expect 'detach the middle one' ((Send 'GINTD:5') -eq 'OK')
    $null = Poll
    Edges 23 4
    $c = Poll
    Expect 'the last one is now alone and still counts' ($c[23] -eq 4 -and $c.Count -eq 1) "(got 23=$($c[23]), $($c.Count) pins)"

    Write-Host "`nFull table"
    $pins = 4, 5, 16, 17, 18, 19, 21
    foreach ($pin in $pins) { $null = Send "GINT:${pin}:3" }   # with pin 23 that is 8
    Expect 'a ninth interrupt is refused' ((Send 'GINT:13:3') -like 'ERR*')
    $null = Poll
    $expected = @{}
    foreach ($pin in (@(23) + $pins)) { $n = 2 * (1 + ($pin % 4)); Edges $pin $n; $expected[$pin] = $n }
    $c = Poll
    $wrong = @($expected.Keys | Where-Object { $c[$_] -ne $expected[$_] })
    Expect 'eight pins count at the same time' ($wrong.Count -eq 0) ("(wrong: " + (($wrong | ForEach-Object { "$_=" + $c[$_] + " want " + $expected[$_] }) -join ', ') + ')')
    foreach ($pin in (@(23) + $pins)) { $null = Send "GINTD:$pin" }
    Expect 'the table is empty again' ((Send 'GINTP') -eq 'OK:NONE')
    Expect 'and can be filled again' ((Send 'GINT:4:3') -eq 'OK')
    $null = Send 'GINTD:4'

    Write-Host "`nBad input"
    Expect 'a flash pin is refused' ((Send 'GINT:6:3') -like 'ERR*')
    Expect 'a pin that is not a number is refused' ((Send 'GINT:abc:3') -like 'ERR*')
    Expect 'an edge out of range is refused' ((Send 'GINT:4:9') -like 'ERR*')
    Expect 'the board still answers' ((Send 'PING') -eq 'OK:PONG')
}
finally {
    foreach ($pin in 4, 5, 23, 16, 17, 18, 19, 21) { $null = Send "GINTD:$pin" }
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
