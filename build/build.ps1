#Requires -Version 5.1
# Builds MonitorLux.exe from src/ into dist/.
# Requires .NET Framework 4.x (ships with Windows 10/11) - no SDK needed.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

$dist = Join-Path $root "dist"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }

$src      = Join-Path $root "src\MonitorLux.cs"
$manifest = Join-Path $root "src\app.manifest"
$icon     = Join-Path $root "assets\app.ico"
$exe      = Join-Path $dist "MonitorLux.exe"

foreach ($f in @($src, $manifest, $icon)) {
    if (-not (Test-Path $f)) { throw "missing input: $f" }
}

# the running app locks the exe - close it first
Get-Process -Name "MonitorLux" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$params = New-Object System.CodeDom.Compiler.CompilerParameters
$params.GenerateExecutable = $true
$params.GenerateInMemory = $false
$params.OutputAssembly = $exe
$params.CompilerOptions = "/target:winexe /platform:anycpu /optimize+ /win32icon:`"$icon`" /win32manifest:`"$manifest`""
$params.EmbeddedResources.Add($icon) | Out-Null
$params.ReferencedAssemblies.Add("System.dll") | Out-Null
$params.ReferencedAssemblies.Add("System.Drawing.dll") | Out-Null
$params.ReferencedAssemblies.Add("System.Windows.Forms.dll") | Out-Null

$results = $provider.CompileAssemblyFromFile($params, $src)
$provider.Dispose()

if ($results.Errors.Count -gt 0) {
    foreach ($e in $results.Errors) { Write-Host ("ERROR line " + $e.Line + ": " + $e.ErrorText) }
    Write-Host "BUILD FAILED"
    exit 1
}

Write-Host ("BUILD OK -> " + $exe)
Write-Host ("size: " + (Get-Item $exe).Length + " bytes")
