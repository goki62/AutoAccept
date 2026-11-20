# Builds dist\AutoAccept.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio needed.   Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
if (-not (Test-Path $csc)) { throw "csc.exe not found under $fw (.NET Framework 4.x is required)" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$icon = Join-Path $root 'build\AutoAccept.ico'
if (-not (Test-Path $icon)) { & (Join-Path $root 'build\make-icon.ps1') -Out $icon }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
$cscArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001', '/warn:4',
    "/out:$(Join-Path $dist 'AutoAccept.exe')",
    "/win32icon:$icon",
    "/r:$(Join-Path $wpf 'UIAutomationClient.dll')",
    "/r:$(Join-Path $wpf 'UIAutomationTypes.dll')",
    "/r:$(Join-Path $wpf 'WindowsBase.dll')",
    '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll'
) + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)" }

$exe = Get-Item (Join-Path $dist 'AutoAccept.exe')
"Built $($exe.FullName)  ($([math]::Round($exe.Length / 1KB)) KB)"
