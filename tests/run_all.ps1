<#
.SYNOPSIS
  Integration test of every CodeBridge module, with a summary table at the end.

.DESCRIPTION
  Stages: solution build, all test projects, firmware builds for every board, packages + VSIX + smoke test, the helper host on the
  simulator (flows, monitor, code mode, discovery), the samples, and optionally a real board.
  A stage whose prerequisite is missing (PlatformIO, MAUI workload, a board) is reported as SKIPPED, never as passed.

.PARAMETER Port
  Serial port of a connected ESP32 (for example COM3). Enables the hardware stages: flashing, connection, flows, monitor, Wi-Fi status.
  The board's program is replaced by the CodeBridge firmware. Its saved Wi-Fi network is kept.

.PARAMETER Fast
  Skips the slow stages (firmware builds, the long Wi-Fi transport tests, MAUI).

.EXAMPLE
  ./tests/run_all.ps1                 # everything that needs no hardware
  ./tests/run_all.ps1 -Port COM3      # plus the real-board checks
#>
param(
    [string]$Port,
    [switch]$Fast
)

$ErrorActionPreference = 'Continue'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

$results = New-Object System.Collections.Generic.List[object]
$hostExe = Join-Path $root 'src/CodeBridge.FlowHost/bin/Release/net8.0-windows/CodeBridge.FlowHost.exe'

function Step([string]$Group, [string]$Name, [scriptblock]$Body) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'PASS'; $detail = ''
    try {
        $out = & $Body 2>&1 | Out-String
        if ($out -match '^SKIP:\s*(?<why>.+)$' -or $out -match '(?m)^SKIP:\s*(?<why>.+)$') { $status = 'SKIP'; $detail = $Matches['why'].Trim() }
        elseif ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { $status = 'FAIL'; $detail = ($out -split "`n" | Select-Object -Last 12) -join "`n" }
        elseif ($out -match '(?m)^FAIL:\s*(?<why>.+)$') { $status = 'FAIL'; $detail = $Matches['why'].Trim() }
    } catch {
        $status = 'FAIL'; $detail = $_.Exception.Message
    }
    $global:LASTEXITCODE = 0
    $watch.Stop()
    $results.Add([pscustomobject]@{ Group = $Group; Step = $Name; Status = $status; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1); Detail = $detail })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow' }[$status]
    Write-Host ("[{0}] {1} / {2} ({3:0.#}s) {4}" -f $status, $Group, $Name, $watch.Elapsed.TotalSeconds, $(if ($status -ne 'PASS') { '- ' + ($detail -split "`n")[0] } else { '' })) -ForegroundColor $color
}

function Skip([string]$Why) { "SKIP: $Why" }

function Host-Json([string[]]$Arguments, [string]$StdinScript) {
    # runs the helper host and returns the JSON lines it printed
    if ($StdinScript) { $lines = & { Invoke-Expression $StdinScript } | & $hostExe @Arguments }
    else { $lines = & $hostExe @Arguments }
    $lines | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
}

$pio = $null
try { $pio = (python -m platformio --version 2>$null) } catch { }
if (-not $pio) { try { $pio = (pio --version 2>$null) } catch { } }

# ------------------------------------------------------------------ 1. build
Step 'Build' 'Solution (Release)' { dotnet build CodeBridge.slnx -c Release --nologo -v q 2>&1 | Select-Object -Last 5 }

# ------------------------------------------------------------------ 2. tests
foreach ($project in 'CodeBridge.VisualStudio.Tests', 'CodeBridge.Blazor.Tests', 'CodeBridge.HomeAssistant.Tests', 'CodeBridge.Mcp.Tests') {
    Step 'Tests' $project { dotnet test "tests/$project" -c Release --nologo 2>&1 | Select-Object -Last 6 }
}
Step 'Tests' 'CodeBridge.Core.Tests' {
    $filter = if ($Fast) { '--filter', 'FullyQualifiedName!~WifiTransportTests' } else { @() }
    dotnet test tests/CodeBridge.Core.Tests -c Release --nologo @filter 2>&1 | Select-Object -Last 6
}

# ------------------------------------------------------------------ 3. firmware
foreach ($env in @(@('esp32-bridge', 'esp32'), @('esp32-bridge', 'esp32s3'), @('esp32-bridge', 'esp32c3'), @('esp32-bridge', 'xiao_esp32s3'), @('esp32-bridge', 'esp32s2'), @('arduino-uno-bridge', 'uno'), @('arduino-uno-bridge', 'nano'), @('arduino-uno-bridge', 'mega'))) {
    $project, $name = $env
    $body = {
        if ($Fast) { return Skip 'fast mode' }
        if (-not $pio) { return Skip 'PlatformIO is not installed' }
        Push-Location "firmware/$project"
        try { python -m platformio run -e $name 2>&1 | Select-Object -Last 4 } finally { Pop-Location }
    }.GetNewClosure()
    Step 'Firmware' "$project ($name)" $body
}

# ------------------------------------------------------------------ 4. packages and extension
Step 'Packaging' 'NuGet packages and VSIX' { pwsh -NoProfile -File build/pack.ps1 -Vsix 2>&1 | Select-Object -Last 3 }
Step 'Packaging' 'VSIX smoke test' { Push-Location tests; try { pwsh -NoProfile -File smoke_test.ps1 2>&1 | Select-Object -Last 3 } finally { Pop-Location } }
Step 'Packaging' 'Every package has the same version' {
    $version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
    $bad = Get-ChildItem artifacts/packages -Filter *.nupkg | Where-Object { $_.Name -notmatch [regex]::Escape($version) -and $_.Name -notmatch 'CodeBridge\.\w+(\.\w+)*\.\d+\.\d+\.\d+\.nupkg' }
    $expected = 'Core', 'Transport', 'ESP32', 'Flow', 'Designer.WinForms', 'Hosting', 'Blazor', 'HomeAssistant', 'PlatformIO', 'Mcp'
    $missing = $expected | Where-Object { -not (Test-Path "artifacts/packages/CodeBridge.$_.$version.nupkg") }
    if ($missing) { "FAIL: missing packages: $($missing -join ', ')" }
}

# ------------------------------------------------------------------ 5. helper host on the simulator
Step 'Host (simulator)' 'boards, ports and catalog' {
    $boards = Host-Json @('boards')
    if (@($boards.boards).Count -lt 7) { return 'FAIL: fewer than 7 boards' }
    $ports = Host-Json @('ports')
    if (-not ($ports.ports.name -contains 'Simulator')) { return 'FAIL: the simulator is not listed' }
}
foreach ($board in 'esp32-devkit', 'esp32-s3-devkit', 'esp32-c3-devkit', 'xiao-esp32-s3', 'esp32-s2-mini', 'arduino-uno', 'arduino-nano', 'arduino-mega') {
    $body = {
        $r = Host-Json @('test', '--board', $board, '--port', 'simulator')
        if (-not ($r | Where-Object { $_.type -eq 'connected' })) { 'FAIL: did not connect' }
    }.GetNewClosure()
    Step 'Host (simulator)' "connect $board" $body
}
Step 'Host (simulator)' 'run the 5 example flows' {
    foreach ($flow in Get-ChildItem src/CodeBridge.VisualStudio/ItemTemplates -Recurse -Filter 'Example*.cbflow') {
        $r = & { Start-Sleep -Milliseconds 300; 'stop' } | & $hostExe run --flow $flow.FullName --board esp32-devkit --port simulator 2>&1 | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
        $result = $r | Where-Object { $_.type -eq 'result' } | Select-Object -Last 1
        if (-not $result -or -not $result.success) { return "FAIL: $($flow.Name): $($result.message)" }
    }
}
Step 'Host (simulator)' 'serial monitor streams and answers' {
    $lines = & { Start-Sleep -Milliseconds 800; '>ping'; Start-Sleep -Milliseconds 700; 'stop' } | & $hostExe monitor --port simulator 2>&1 | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
    $text = ($lines | Where-Object { $_.type -eq 'lines' }).lines
    if (-not ($text -match '^light:\d+')) { return 'FAIL: no data lines' }
    if (-not ($text -contains 'pong')) { return 'FAIL: the simulator did not answer ping' }
}
Step 'Host (simulator)' 'export a flow to C#' {
    $flow = Get-ChildItem src/CodeBridge.VisualStudio/ItemTemplates -Recurse -Filter 'Example3*.cbflow' | Select-Object -First 1
    $r = Host-Json @('export', '--flow', $flow.FullName, '--board', 'esp32-devkit')
    if (-not ($r.code -match 'AnalogReadAsync')) { 'FAIL: unexpected C# output' }
}
Step 'Host (simulator)' 'find boards and Home Assistant on the network' {
    $r = Host-Json @('wifi-boards')
    if ($r.type -ne 'wifi-boards') { 'FAIL: discovery did not answer' }
}
Step 'Code mode' 'create a project and build it' {
    if (-not $pio) { return Skip 'PlatformIO is not installed' }
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('cb-fw-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        $created = Host-Json @('pio-new', '--board', 'arduino-uno', '--dir', $dir, '--template', 'serial')
        if ($created.type -ne 'pio-created') { return 'FAIL: project not created' }
        $built = Host-Json @('pio-build', '--dir', $dir) | Where-Object { $_.type -eq 'result' }
        if (-not $built.success) { "FAIL: $($built.message)" }
    } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}
Step 'Code mode' 'compiler errors reach the error list' {
    if (-not $pio) { return Skip 'PlatformIO is not installed' }
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('cb-fw-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Host-Json @('pio-new', '--board', 'arduino-uno', '--dir', $dir, '--template', 'empty') | Out-Null
        Set-Content (Join-Path $dir 'src/main.cpp') "#include <Arduino.h>`nvoid setup(){ nope(); }`nvoid loop(){}`n"
        $messages = Host-Json @('pio-build', '--dir', $dir)
        $diag = $messages | Where-Object { $_.type -eq 'diagnostic' -and $_.severity -eq 'error' }
        $global:LASTEXITCODE = 0 # the build is supposed to fail: that exit code is the expected outcome, not a failure of this step
        if (-not $diag -or $diag[0].line -ne 2) { 'FAIL: the error was not reported with its line' }
    } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

# ------------------------------------------------------------------ 6. MCP server as a real process
Step 'MCP' 'server answers a raw JSON-RPC session' {
    $dll = Get-ChildItem src/CodeBridge.Mcp/bin/Release -Recurse -Filter codebridge-mcp.dll | Select-Object -First 1
    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"run_all","version":"1"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_board_manifest","arguments":{"boardId":"esp32-devkit"}}}')
    $out = & { $requests; Start-Sleep -Seconds 4 } | dotnet $dll.FullName 2>$null
    if (-not ($out -match 'logicLevelVolts')) { 'FAIL: no manifest in the answer' }
}

# ------------------------------------------------------------------ 7. samples
foreach ($sample in 'Integration.Console', 'Integration.Api', 'Integration.WindowsService', 'Integration.Blazor', 'Integration.HomeAssistant') {
    $body = { dotnet build "samples/$sample" -c Release --nologo -v q 2>&1 | Select-Object -Last 3 }.GetNewClosure()
    Step 'Samples' "build $sample" $body
}
Step 'Samples' 'Blazor dashboard serves the page and the stylesheet' {
    $env:CodeBridge__Port = 'simulator'; $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $p = Start-Process dotnet -ArgumentList 'run', '--no-build', '-c', 'Release', '--project', 'samples/Integration.Blazor', '--urls', 'http://localhost:5099' -PassThru -WindowStyle Hidden
    try {
        $page = $null
        for ($i = 0; $i -lt 30 -and -not $page; $i++) { Start-Sleep -Milliseconds 500; try { $page = Invoke-WebRequest http://localhost:5099/ -UseBasicParsing -TimeoutSec 2 } catch { } }
        if (-not $page -or $page.Content -notmatch 'My board') { return 'FAIL: the dashboard did not render' }
        $css = Invoke-WebRequest http://localhost:5099/_content/CodeBridge.Blazor/codebridge.css -UseBasicParsing
        if ($css.StatusCode -ne 200) { 'FAIL: stylesheet missing' }
    } finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Get-Process -Name 'CodeBridge.Samples.IntegrationBlazor' -ErrorAction SilentlyContinue | Stop-Process -Force }
}
Step 'Samples' 'MAUI app builds (Windows)' {
    if ($Fast) { return Skip 'fast mode' }
    if (-not (dotnet workload list 2>$null | Select-String 'maui')) { return Skip 'the MAUI workload is not installed' }
    dotnet build samples/Integration.Maui -f net10.0-windows10.0.19041.0 -c Release --nologo -v q 2>&1 | Select-Object -Last 3
}

# ------------------------------------------------------------------ 8. real board
$hw = 'Hardware'
if (-not $Port) {
    $results.Add([pscustomobject]@{ Group = $hw; Step = 'all hardware checks'; Status = 'SKIP'; Seconds = 0; Detail = 'pass -Port COM3 to test a real board' })
    Write-Host "[SKIP] $hw / all hardware checks - pass -Port COM3" -ForegroundColor Yellow
} else {
    Step $hw "port $Port exists" { if ([System.IO.Ports.SerialPort]::GetPortNames() -notcontains $Port) { "FAIL: $Port is not present" } }
    Step $hw 'flash the CodeBridge firmware' {
        $r = Host-Json @('upload', '--board', 'esp32-devkit', '--port', $Port) | Where-Object { $_.type -eq 'result' }
        if (-not $r.success) { "FAIL: $($r.message)" }
    }
    Start-Sleep -Seconds 2
    Step $hw 'connect and read board info' {
        $r = Host-Json @('test', '--board', 'esp32-devkit', '--port', $Port) | Where-Object { $_.type -eq 'connected' }
        if (-not $r) { return 'FAIL: no connection' }
        if ($r.firmware -notmatch '^0\.9') { "FAIL: unexpected firmware $($r.firmware)" }
    }
    Step $hw 'run the night-light flow on the board' {
        $flow = Get-ChildItem src/CodeBridge.VisualStudio/ItemTemplates -Recurse -Filter 'Example3*.cbflow' | Select-Object -First 1
        $r = & { Start-Sleep -Seconds 5; 'stop' } | & $hostExe run --flow $flow.FullName --board esp32-devkit --port $Port 2>&1 | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
        $result = $r | Where-Object { $_.type -eq 'result' } | Select-Object -Last 1
        if (-not $result.success) { "FAIL: $($result.message)" }
        if (-not ($r | Where-Object { $_.type -eq 'pin' })) { 'FAIL: no pin activity was reported' }
    }
    Step $hw 'Analog -> Map -> PWM flow with 8-sample averaging' {
        $flowPath = Join-Path ([IO.Path]::GetTempPath()) 'cb-dimmer.cbflow'
        @'
{"Name":"Dimmer","BoardId":"esp32-devkit","Version":1,"Nodes":[
{"Id":"start","Type":"flow.manual-trigger","Position":{"X":60,"Y":120},"Parameters":{}},
{"Id":"light","Type":"gpio.analog-read","Position":{"X":300,"Y":120},"Parameters":{"pin":34,"samples":8}},
{"Id":"map","Type":"math.map","Position":{"X":540,"Y":120},"Parameters":{"inMin":0,"inMax":4095,"outMin":0,"outMax":255}},
{"Id":"led","Type":"gpio.pwm-write","Position":{"X":780,"Y":120},"Parameters":{"pin":2}}],
"Connections":[
{"Id":"c1","FromNodeId":"start","FromPort":"trigger","ToNodeId":"light","ToPort":"trigger"},
{"Id":"c2","FromNodeId":"light","FromPort":"value","ToNodeId":"map","ToPort":"value"},
{"Id":"c3","FromNodeId":"map","FromPort":"result","ToNodeId":"led","ToPort":"duty"}]}
'@ | Set-Content $flowPath
        $r = & { Start-Sleep -Seconds 5; 'stop' } | & $hostExe run --flow $flowPath --board esp32-devkit --port $Port 2>&1 | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
        if (-not (($r | Where-Object { $_.type -eq 'result' } | Select-Object -Last 1).success)) { 'FAIL: the flow failed' }
        if (-not ($r | Where-Object { $_.type -eq 'pin' -and $_.kind -eq 'pwm' -and $_.pin -eq 2 })) { 'FAIL: pin 2 PWM was not driven' }
    }
    Step $hw 'serial monitor reads the board and the board answers PING' {
        $lines = & { Start-Sleep -Milliseconds 700; '>PING'; Start-Sleep -Milliseconds 900; 'stop' } | & $hostExe monitor --port $Port 2>&1 | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
        $text = ($lines | Where-Object { $_.type -eq 'lines' }).lines
        if (-not ($text -contains 'OK:PONG')) { "FAIL: no PONG; saw: $($text -join ' | ')" }
    }
    Step $hw 'Wi-Fi status and scan' {
        $status = Host-Json @('wifi-status', '--port', $Port) | Select-Object -First 1
        if ($status.type -ne 'wifi-status') { return 'FAIL: no status' }
        $scan = Host-Json @('wifi-scan', '--port', $Port) | Where-Object { $_.type -eq 'wifi-networks' }
        if (-not $scan) { 'FAIL: scan did not answer' }
    }
    Step $hw 'code mode: build and upload a Blink project' {
        if (-not $pio) { return Skip 'PlatformIO is not installed' }
        $dir = Join-Path ([IO.Path]::GetTempPath()) ('cb-fw-hw-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        try {
            Host-Json @('pio-new', '--board', 'esp32-devkit', '--dir', $dir, '--template', 'blink') | Out-Null
            $r = Host-Json @('pio-upload', '--dir', $dir, '--port', $Port) | Where-Object { $_.type -eq 'result' }
            if (-not $r.success) { return "FAIL: $($r.message)" }
            Start-Sleep -Seconds 2
            $lines = & { Start-Sleep -Milliseconds 1800; 'stop' } | & $hostExe monitor --port $Port 2>&1 | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json }
            $text = ($lines | Where-Object { $_.type -eq 'lines' }).lines
            if (-not ($text -match 'LED (on|off)')) { "FAIL: the blink program did not run; saw: $($text -join ' | ')" }
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Step $hw 'restore the CodeBridge firmware' {
        $r = Host-Json @('upload', '--board', 'esp32-devkit', '--port', $Port) | Where-Object { $_.type -eq 'result' }
        if (-not $r.success) { "FAIL: $($r.message)" }
    }
}

# ------------------------------------------------------------------ summary
Write-Host ''
$results | Format-Table Group, Step, Status, Seconds -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object Status -eq 'FAIL')
$skipped = @($results | Where-Object Status -eq 'SKIP')
Write-Host ("{0} passed, {1} failed, {2} skipped" -f @($results | Where-Object Status -eq 'PASS').Count, $failed.Count, $skipped.Count) -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
foreach ($f in $failed) { Write-Host "`nFAILED: $($f.Group) / $($f.Step)`n$($f.Detail)" -ForegroundColor Red }
$results | ConvertTo-Json | Set-Content (Join-Path $root 'artifacts/integration-results.json')
exit $(if ($failed.Count) { 1 } else { 0 })
