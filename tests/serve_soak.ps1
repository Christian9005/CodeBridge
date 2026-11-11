<#
.SYNOPSIS
  Runs `FlowHost serve` for a long time and measures whether it stays healthy.

.DESCRIPTION
  Starts the FlowHost the extension ships, in serve mode, and every few seconds records its private memory, handles, threads and CPU, the size of its log and
  what it reported (passes, failed passes, disconnections). At the end it fits a straight line to the memory after a warm-up, and fails if:
    - the process died before the time was up,
    - no pass ran, or the passes did not keep coming until the end,
    - the memory grew faster than -MaxMemoryGrowthMbPerHour, or the handles by more than -MaxHandleGrowth,
    - the log went past its 5 MB limit,
    - it disconnected more than -AllowedDisconnects times,
    - Ctrl+C did not stop it (within 10 s).

  Port "simulator" needs no board. With a COM port the flow really runs on the board: unplug anything that must not move.

.EXAMPLE
  pwsh ./serve_soak.ps1 -Flow ../tests/golden/cbflow/released/0.8--SmartGreenhouse.cbflow -Port simulator -Minutes 30 -IntervalMs 50
  pwsh ./serve_soak.ps1 -Flow ../tests/golden/cbflow/released/0.5.4--Example3_NightLight.cbflow -Port COM3 -Minutes 30 -IntervalMs 1000
#>
param(
    [Parameter(Mandatory)][string]$Flow,
    [string]$Port = 'simulator',
    [int]$Minutes = 30,
    [int]$IntervalMs = 1000,
    [int]$SampleSeconds = 15,
    [string]$FlowHost = (Join-Path $PSScriptRoot '..\artifacts\flowhost\CodeBridge.FlowHost.exe'),
    [double]$MaxMemoryGrowthMbPerHour = 20,
    [int]$MaxHandleGrowth = 50,
    [int]$AllowedDisconnects = 0,
    # one folder per run (two runs started in the same second must not share files)
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) ('cb-serve-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)))
)

$ErrorActionPreference = 'Stop'
$FlowHost = (Resolve-Path $FlowHost).Path
$Flow = (Resolve-Path $Flow).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'serve.log'
$console = Join-Path $OutDir 'console.jsonl'
$errors = Join-Path $OutDir 'stderr.txt'
$csv = Join-Path $OutDir 'samples.csv'

$consoleCode = @'
using System; using System.Runtime.InteropServices; using System.Threading;
public static class SoakConsole {
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)] static extern bool FreeConsole();
  [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
  [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
  /// <summary>A process started by a tool can have Ctrl+C switched off, and its children inherit that: switch it on before starting the FlowHost.</summary>
  public static void EnableCtrlC() { SetConsoleCtrlHandler(IntPtr.Zero, false); }
  /// <summary>Sends Ctrl+C to the console of another process, the way a person in front of it would.</summary>
  public static bool CtrlC(uint pid) {
    FreeConsole();
    if (!AttachConsole(pid)) return false;
    SetConsoleCtrlHandler(IntPtr.Zero, true);
    var ok = GenerateConsoleCtrlEvent(0, 0);
    Thread.Sleep(500);
    FreeConsole();
    SetConsoleCtrlHandler(IntPtr.Zero, false);
    return ok;
  }
}
'@
Add-Type $consoleCode
[SoakConsole]::EnableCtrlC()

# Ctrl+C goes from a helper process: attaching to another console detaches the one it has, and this script still has to write its result.
function Send-CtrlC([int]$ProcessId) {
    $helper = Join-Path $OutDir 'ctrlc.ps1'
    $lines = @('Add-Type @''', $consoleCode, '''@', "[SoakConsole]::CtrlC($ProcessId) | Out-Null")
    Set-Content $helper $lines
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile', '-File', "`"$helper`"" -WindowStyle Hidden -Wait
}

$arguments = @('serve', '--flow', "`"$Flow`"", '--port', $Port, '--interval', $IntervalMs, '--log', "`"$log`"")
"Soak: $([IO.Path]::GetFileName($Flow)) on $Port every $IntervalMs ms for $Minutes min. Output: $OutDir"
$proc = Start-Process -FilePath $FlowHost -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $console -RedirectStandardError $errors
$started = Get-Date
$samples = New-Object System.Collections.Generic.List[object]
"seconds,privateMb,workingSetMb,handles,threads,cpuSeconds,logKb,passes,failedPasses,disconnects" | Set-Content $csv

function Read-Events {
    # the console file is being written: open it shared
    $lines = @()
    try {
        $stream = [IO.File]::Open($console, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object IO.StreamReader($stream)
        while (-not $reader.EndOfStream) { $lines += $reader.ReadLine() }
        $reader.Dispose()
    } catch { }
    $passes = 0; $failed = 0; $disconnects = 0
    foreach ($line in $lines) {
        try { $e = $line | ConvertFrom-Json } catch { continue }
        if ($e.type -ne 'supervisor') { continue }
        switch ($e.kind) {
            'iteration' { if ($e.pass) { $passes = [Math]::Max($passes, [int]$e.pass) } }
            'iterationFailed' { $failed++ }
            'disconnected' { $disconnects++ }
        }
    }
    [pscustomobject]@{ Passes = $passes; Failed = $failed; Disconnects = $disconnects }
}

$deadline = $started.AddMinutes($Minutes)
$diedEarly = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $SampleSeconds
    $proc.Refresh()
    if ($proc.HasExited) { $diedEarly = $true; break }
    $events = Read-Events
    $logKb = if (Test-Path $log) { [math]::Round((Get-Item $log).Length / 1KB) } else { 0 }
    $row = [pscustomobject]@{
        Seconds = [int]((Get-Date) - $started).TotalSeconds
        PrivateMb = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        WorkingSetMb = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        Handles = $proc.HandleCount
        Threads = $proc.Threads.Count
        Cpu = [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1)
        LogKb = $logKb
        Passes = $events.Passes
        Failed = $events.Failed
        Disconnects = $events.Disconnects
    }
    $samples.Add($row)
    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}" -f $row.Seconds, $row.PrivateMb, $row.WorkingSetMb, $row.Handles, $row.Threads, $row.Cpu, $row.LogKb, $row.Passes, $row.Failed, $row.Disconnects | Add-Content $csv
    if (($samples.Count % 8) -eq 1) { "  {0,5} s  private {1,6} MB  handles {2,4}  threads {3,3}  passes {4,8}  failed {5}  disconnects {6}" -f $row.Seconds, $row.PrivateMb, $row.Handles, $row.Threads, $row.Passes, $row.Failed, $row.Disconnects }
}

# Does Ctrl+C stop it, in the middle of a pass or a wait, as the documentation says?
$stoppedByCtrlC = $false
$stopSeconds = $null
if (-not $diedEarly) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Send-CtrlC $proc.Id
    if ($proc.WaitForExit(10000)) { $stoppedByCtrlC = $true; $stopSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
    else { Stop-Process -Id $proc.Id -Force }
}
$final = Read-Events
$stopEvent = $false
try { $stopEvent = (Get-Content $console -ErrorAction SilentlyContinue | Select-String '"kind":"stopped"|"state":"stopped"' | Measure-Object).Count -gt 0 } catch { }

# the line through the memory after the warm-up
function Slope($points) {
    $n = $points.Count
    if ($n -lt 3) { return 0.0 }
    $mx = ($points | ForEach-Object { $_.x } | Measure-Object -Average).Average
    $my = ($points | ForEach-Object { $_.y } | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    foreach ($p in $points) { $num += ($p.x - $mx) * ($p.y - $my); $den += ($p.x - $mx) * ($p.x - $mx) }
    if ($den -eq 0) { return 0.0 }
    return $num / $den
}

$warm = [Math]::Max(120, ($Minutes * 60) * 0.15)
$after = @($samples | Where-Object { $_.Seconds -ge $warm })
$privatePoints = @($after | ForEach-Object { [pscustomobject]@{ x = [double]$_.Seconds; y = [double]$_.PrivateMb } })
$growthMbPerHour = [math]::Round((Slope $privatePoints) * 3600, 2)
$handleGrowth = if ($after.Count -ge 2) { $after[-1].Handles - $after[0].Handles } else { 0 }
$threadGrowth = if ($after.Count -ge 2) { $after[-1].Threads - $after[0].Threads } else { 0 }
$peak = if ($samples.Count) { ($samples | Measure-Object -Property PrivateMb -Maximum).Maximum } else { 0 }
$cpuPercent = if ($samples.Count -gt 1) { [math]::Round(100 * ($samples[-1].Cpu - $samples[0].Cpu) / ($samples[-1].Seconds - $samples[0].Seconds) / [Environment]::ProcessorCount, 2) } else { 0 }

# did the passes keep coming until the end? (the last three minutes of samples against the three before them)
$keptRunning = $true
if ($samples.Count -ge 20) {
    $tail = $samples[-1].Passes - $samples[-12].Passes
    $keptRunning = $tail -gt 0
}
$logKbMax = if ($samples.Count) { ($samples | Measure-Object -Property LogKb -Maximum).Maximum } else { 0 }
$rotated = (Test-Path "$log.old")

$problems = New-Object System.Collections.Generic.List[string]
if ($diedEarly) { $problems.Add("FlowHost exited after $([int]((Get-Date) - $started).TotalSeconds) s with code $($proc.ExitCode)") }
if ($final.Passes -le 0) { $problems.Add('no pass was reported') }
if (-not $keptRunning) { $problems.Add('the passes stopped coming before the end') }
if ($growthMbPerHour -gt $MaxMemoryGrowthMbPerHour) { $problems.Add("memory grows $growthMbPerHour MB/hour (limit $MaxMemoryGrowthMbPerHour)") }
if ($handleGrowth -gt $MaxHandleGrowth) { $problems.Add("handles grew by $handleGrowth (limit $MaxHandleGrowth)") }
if ($logKbMax -gt 6144) { $problems.Add("the log reached $logKbMax KB (limit 5 MB plus one line)") }
if ($final.Disconnects -gt $AllowedDisconnects) { $problems.Add("$($final.Disconnects) disconnection(s) (allowed $AllowedDisconnects)") }
if (-not $diedEarly -and -not $stoppedByCtrlC) { $problems.Add('Ctrl+C did not stop it within 10 s') }

''
"Result for $([IO.Path]::GetFileName($Flow)) on $Port, $([int]((Get-Date) - $started).TotalMinutes) min, every $IntervalMs ms"
"  passes reported      : $($final.Passes) ($($final.Failed) failed, $($final.Disconnects) disconnections)"
"  private memory       : peak $peak MB, trend after warm-up $growthMbPerHour MB/hour"
"  handles / threads    : change after warm-up $handleGrowth / $threadGrowth"
"  cpu (all cores)      : $cpuPercent %"
"  log                  : up to $logKbMax KB, rotated: $rotated"
"  Ctrl+C               : " + $(if ($stoppedByCtrlC) { "stopped it in $stopSeconds s" } else { 'did not stop it' })
"  samples              : $csv"
if ($problems.Count -gt 0) {
    ''
    $problems | ForEach-Object { Write-Host "  PROBLEM: $_" -ForegroundColor Red }
    exit 1
}
Write-Host '  Healthy.' -ForegroundColor Green
exit 0
