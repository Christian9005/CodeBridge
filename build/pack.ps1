<#
.SYNOPSIS
  Builds and packs every CodeBridge NuGet package into artifacts/packages, then (optionally) builds the VSIX.
.EXAMPLE
  ./build/pack.ps1            # packages only
  ./build/pack.ps1 -Vsix      # packages + VSIX (artifacts/vsix/CodeBridge.VisualStudio.vsix)
#>
param(
    [string]$Configuration = 'Release',
    [switch]$Vsix
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'artifacts/packages'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$projects = @(
    'src/CodeBridge.Core/CodeBridge.Core.csproj',
    'src/CodeBridge.Transport/CodeBridge.Transport.csproj',
    'src/CodeBridge.ESP32/CodeBridge.ESP32.csproj',
    'src/CodeBridge.Flow/CodeBridge.Flow.csproj',
    'src/CodeBridge.Designer.WinForms/CodeBridge.Designer.WinForms.csproj',
    'src/CodeBridge.Hosting/CodeBridge.Hosting.csproj',
    'src/CodeBridge.Blazor/CodeBridge.Blazor.csproj',
    'src/CodeBridge.HomeAssistant/CodeBridge.HomeAssistant.csproj',
    'src/CodeBridge.PlatformIO/CodeBridge.PlatformIO.csproj',
    'src/CodeBridge.Mcp/CodeBridge.Mcp.csproj'
)

foreach ($project in $projects) {
    Write-Host "Packing $project"
    dotnet pack (Join-Path $root $project) -c $Configuration -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $project" }
}

if ($Vsix) {
    # The FlowHost is the net8 process the extension uses to reach the board.
    Write-Host 'Publishing FlowHost'
    $hostOut = Join-Path $root 'artifacts/flowhost'
    if (Test-Path $hostOut) { Remove-Item $hostOut -Recurse -Force }
    dotnet publish (Join-Path $root 'src/CodeBridge.FlowHost/CodeBridge.FlowHost.csproj') -c $Configuration -o $hostOut
    if ($LASTEXITCODE -ne 0) { throw 'FlowHost publish failed' }

    # Shipped as one archive so Visual Studio does not treat the net8 assemblies as extension assemblies.
    $hostZip = Join-Path $root 'artifacts/FlowHost.zip'
    if (Test-Path $hostZip) { Remove-Item $hostZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($hostOut, $hostZip)

    # The editor block catalog is generated from the real runtime catalog so pins/blocks cannot drift.
    Write-Host 'Generating block catalogs'
    & (Join-Path $hostOut 'CodeBridge.FlowHost.exe') catalog --out (Join-Path $root 'src/CodeBridge.VisualStudio/Assets')
    if ($LASTEXITCODE -ne 0) { throw 'Catalog generation failed' }

    Write-Host 'Building VSIX'
    dotnet build (Join-Path $root 'src/CodeBridge.VisualStudio/CodeBridge.VisualStudio.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'VSIX build failed' }

    $vsixDir = Join-Path $root 'artifacts/vsix'
    New-Item -ItemType Directory -Force -Path $vsixDir | Out-Null
    Copy-Item (Join-Path $root "src/CodeBridge.VisualStudio/bin/$Configuration/net472/CodeBridge.VisualStudio.vsix") $vsixDir -Force
}
