#Requires -Version 5.1
# Builds Setup.exe: the app exe is embedded as a resource, so the installer is a single file.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

$dist = Join-Path $root "dist"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }

$src      = Join-Path $root "src\Setup.cs"
$manifest = Join-Path $root "src\app.manifest"
$icon     = Join-Path $root "assets\app.ico"
$appExe   = Join-Path $dist "MonitorLux.exe"
$setupExe = Join-Path $dist "MonitorLux-Setup.exe"

if (-not (Test-Path $appExe)) {
    Write-Host "MonitorLux.exe not found in dist/ - running build.ps1 first..."
    & (Join-Path $PSScriptRoot "build.ps1")
    if (-not (Test-Path $appExe)) { throw "app build failed" }
}

Get-Process -Name "MonitorLux-Setup" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

$payloadDir = Join-Path $dist "_payload"
if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Copy-Item $appExe (Join-Path $payloadDir "payload.exe") -Force

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$params = New-Object System.CodeDom.Compiler.CompilerParameters
$params.GenerateExecutable = $true
$params.GenerateInMemory = $false
$params.OutputAssembly = $setupExe
$params.CompilerOptions = "/target:winexe /platform:anycpu /optimize+ /win32icon:`"$icon`" /win32manifest:`"$manifest`""
$params.EmbeddedResources.Add((Join-Path $payloadDir "payload.exe")) | Out-Null
$params.EmbeddedResources.Add($icon) | Out-Null
$params.ReferencedAssemblies.Add("System.dll") | Out-Null
$params.ReferencedAssemblies.Add("System.Drawing.dll") | Out-Null
$params.ReferencedAssemblies.Add("System.Windows.Forms.dll") | Out-Null

$results = $provider.CompileAssemblyFromFile($params, $src)
$provider.Dispose()

if ($results.Errors.Count -gt 0) {
    foreach ($e in $results.Errors) { Write-Host ("ERROR line " + $e.Line + ": " + $e.ErrorText) }
    Write-Host "SETUP BUILD FAILED"
    exit 1
}

Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ("SETUP BUILD OK -> " + $setupExe)
Write-Host ("size: " + (Get-Item $setupExe).Length + " bytes")
