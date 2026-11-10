<#
.SYNOPSIS
  Sends hostile and malformed commands to a real board over USB and checks that it stays alive.

.DESCRIPTION
  After every command the script sends PING and expects PONG, and it watches the output for the boot banner (OK:CODEBRIDGE_READY): a banner that
  appears after the first one means the board reset itself, which is a failure.

  Nothing here is meant to move anything: the commands use bad arguments, pins the firmware must refuse (the flash pins) and lengths and numbers
  that used to overflow. Even so, DISCONNECT motors, servos and anything else you care about before you run it: the commands that are valid
  (for example a very short tone) really run.

  Run it from the tests folder:   pwsh ./firmware_robustness.ps1 -Port COM3
  Exit code 0 when every check passed.
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
$serial.DtrEnable = $false      # opening the port must not reset the board
$serial.RtsEnable = $false
$serial.Open()

$script:resets = 0
$script:failures = New-Object System.Collections.Generic.List[string]
$script:checks = 0

function Read-Reply {
    # lines that are the boot banner count as a reset; the first line that is anything else is the reply
    while ($true) {
        try { $line = $serial.ReadLine().Trim() } catch [System.TimeoutException] { return $null }
        if ($line -eq 'OK:CODEBRIDGE_READY') { $script:resets++; continue }
        if ($line.Length -gt 0) { return $line }
    }
}

function Send([string]$command) {
    $serial.DiscardInBuffer()
    $serial.Write($command + "`n")
    return Read-Reply
}

function Alive {
    # A command that produced several lines (a bare carriage return counts as an end of line) leaves extra replies behind: skip up to six of them.
    $before = $script:resets
    $serial.DiscardInBuffer()
    $serial.Write("PING`n")
    foreach ($i in 1..6) {
        $reply = Read-Reply
        if ($null -eq $reply) { return $false }
        if ($reply -eq 'OK:PONG') { return ($script:resets -eq $before) }
    }
    return $false
}

function Check([string]$name, [string]$command, [string]$expectPrefix = '', [switch]$NoReply) {
    $script:checks++
    $resetsBefore = $script:resets
    $reply = if ($NoReply) { $serial.DiscardInBuffer(); $serial.Write($command + "`n"); '(no reply expected)' } else { Send $command }
    $problem = $null
    if ($null -eq $reply) { $problem = 'no reply' }
    elseif ($expectPrefix -and -not $reply.StartsWith($expectPrefix)) { $problem = "expected '$expectPrefix...', got '$reply'" }
    if (-not $problem -and $script:resets -ne $resetsBefore) { $problem = 'the board reset' }
    if (-not $problem -and -not (Alive)) { $problem = 'the board did not answer PING afterwards' }
    if ($problem) {
        $script:failures.Add("$name  [$command]  ->  $problem")
        Write-Host "  FAIL  $name  ($problem)" -ForegroundColor Red
        # try to get the line back in sync for the next check
        Start-Sleep -Milliseconds 1500
        $null = Alive
    } else {
        Write-Host "  ok    $name  ->  $reply"
    }
}

try {
    Write-Host "Opening $Port..."
    Start-Sleep -Milliseconds 300
    if (-not (Alive)) { throw "The board on $Port does not answer PING. Is it running the CodeBridge firmware?" }
    Write-Host ('Firmware: ' + (Send 'VER'))

    Write-Host "`nPins the firmware must refuse (flash pins, out of range, negative)"
    foreach ($pin in 6, 7, 8, 9, 10, 11) {
        Check "digital write to flash pin $pin" "DW:${pin}:1" 'ERR'
        Check "pin mode on flash pin $pin" "PM:${pin}:1" 'ERR'
    }
    Check 'pin 99' 'DW:99:1' 'ERR'
    Check 'pin -1' 'DW:-1:1' 'ERR'
    Check 'pin 2147483647' 'DW:2147483647:1' 'ERR'
    Check 'pin -2147483648' 'DW:-2147483648:1' 'ERR'
    Check 'servo on a flash pin' 'SA:7' 'ERR'
    Check 'neopixel on a flash pin' 'NI:8:5' 'ERR'
    Check 'tone on a flash pin' 'TN:9:440:10' 'ERR'
    Check 'dht on a flash pin' 'DHTR:10:22' 'ERR'
    Check 'ultrasonic on flash pins' 'USR:6:7' 'ERR'
    Check 'spi chip select on a flash pin' 'SW:11:AABB' 'ERR'
    Check 'one-wire scan on a flash pin' 'OWS:6' 'ERR'
    Check 'motor on flash pins' 'MI:6:7:8' 'ERR'
    Check 'stepper on flash pins' 'STI:6:7:8:9:2048' 'ERR'

    Write-Host "`nNumbers that used to overflow or never end"
    Check 'stepper steps-per-rev that wraps the multiplication to zero' 'STI:25:26:27:14:65536' 'ERR'
    Check 'stepper set up normally' 'STI:25:26:27:14:2048' 'OK'
    Check 'stepper speed that wraps the multiplication' 'STS:25:1:2147483647' 'OK'
    Check 'stepper with a billion steps' 'STS:25:1000000000:10' 'ERR'
    Check 'stepper with the smallest int steps' 'STS:25:-2147483648:10' 'ERR'
    Check 'stepper move that would last for hours' 'STS:25:100000:1' 'ERR'
    Check 'tone for 24 days' 'TN:2:440:2147483647' 'ERR'
    Check 'tone at a frequency the timer cannot make' 'TN:2:440000000:10' 'ERR'
    Check 'servo angle out of range is clamped, not an error' 'SA:13' 'OK'
    Check 'servo angle 99999' 'SV:13:99999' 'OK'
    Check 'one-wire read of a negative length' 'OWR:4:0000000000000000:-5' 'ERR'
    Check 'one-wire read of a huge length' 'OWR:4:0000000000000000:2000000000' 'ERR'
    Check 'one-wire read without an address' 'OWR:4::8' 'ERR'
    Check 'spi read of a negative length' 'SR:5:-1' 'ERR'
    Check 'spi read of a huge length' 'SR:5:2000000000' 'ERR'
    Check 'i2c read of a huge length' 'IR:60:2000000000' 'ERR'
    Check 'neopixel with a negative count' 'NI:5:-3' 'ERR'
    Check 'neopixel with a huge count' 'NI:5:2147483647' 'ERR'

    Write-Host "`nSlots are reused when a client connects again (these used to run out)"
    foreach ($i in 1..12) { $null = Send 'SA:13'; $null = Send 'SD:13' }
    Check 'servo attach after 12 attach/detach cycles' 'SA:13' 'OK'
    foreach ($i in 1..6) { $null = Send 'MI:25:26:-1' }
    Check 'motor set up the same way six times' 'MI:25:26:-1' 'OK'
    foreach ($i in 1..6) { $null = Send 'STI:25:26:27:14:2048' }
    Check 'stepper set up the same way six times' 'STI:25:26:27:14:2048' 'OK'
    foreach ($i in 1..20) { $null = Send "NT:${i}" }
    Check 'silencing 20 pins does not use up PWM channels' 'TN:2:440:5' 'OK'

    Write-Host "`nMalformed lines"
    Check 'empty command' '' '' -NoReply
    Check 'only a colon' ':' 'ERR'
    Check 'only colons' ':::::::::::::::::' 'ERR'
    Check 'unknown command' 'NOSUCHCOMMAND:1:2:3' 'ERR'
    Check 'a command with no arguments that needs them' 'DW' 'ERR'
    Check 'letters where numbers go' 'DW:abc:xyz' 'ERR'
    Check 'a hex string with non-hex letters' 'SW:5:ZZZZZZ' 'OK'
    Check 'an odd-length hex string' 'IW:60:ABC' 'ERR'
    Check 'a lowercase command' 'ping' ''
    Check 'a line of 400 characters' ('DW:2:' + ('1' * 395)) ''
    Check 'a line longer than the buffer (3000 characters)' ('X' * 3000) 'ERR'
    Check 'a line of 20000 characters' ('PING:' + ('A' * 20000)) ''
    Check 'bytes that are not text' ([string]::new([char[]]((1..9) + (11..12) + (14..31) + (127..160)))) '' -NoReply
    Check 'a carriage return in the middle' "PI`rNG" '' -NoReply
    Check 'a thousand colons then a number' ((':' * 1000) + '5') 'ERR'

    Write-Host "`nA burst of 300 lines without waiting for the answers"
    $serial.DiscardInBuffer()
    for ($i = 0; $i -lt 300; $i++) { $serial.Write("PING`n") }
    Start-Sleep -Milliseconds 2000
    $serial.DiscardInBuffer()
    $script:checks++
    if (Alive) { Write-Host '  ok    still answers after the burst' } else { $script:failures.Add('burst of 300 PINGs: the board stopped answering'); Write-Host '  FAIL  burst' -ForegroundColor Red }

    Write-Host "`nHostile commands that need no actuator, repeated 200 times"
    $script:checks++
    $stop = $false
    foreach ($i in 1..200) {
        foreach ($cmd in 'DW:99:1', 'STS:25:99999999:10', 'OWR:4::-1', 'TN:6:1:1', 'SA:-1') {
            $reply = Send $cmd
            if ($null -eq $reply) { $stop = $true; break }
        }
        if ($stop) { break }
    }
    if (-not $stop -and (Alive)) { Write-Host '  ok    1000 hostile commands in a row' } else { $script:failures.Add('1000 hostile commands in a row: the board stopped answering'); Write-Host '  FAIL  repeated hostile commands' -ForegroundColor Red }
}
finally {
    if ($serial.IsOpen) { $serial.Close() }
}

Write-Host ''
Write-Host ("{0} checks, {1} failed, the board reset {2} time(s) after the first banner." -f $script:checks, $script:failures.Count, $script:resets)
if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
exit 0
