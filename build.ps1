# Builds MachineGauges.exe with the compiler that ships with Windows - no SDK required.
$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$src   = Join-Path $PSScriptRoot 'src'
$build = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path (Join-Path $build 'tools') | Out-Null

# 1. Render the gauge icon into a multi-size .ico
$iconGen = Join-Path $build 'tools\IconGen.exe'
$ico     = Join-Path $build 'app.ico'
& $csc /nologo /target:exe /platform:x64 /optimize+ /langversion:5 "/out:$iconGen" `
       /r:System.dll /r:System.Drawing.dll (Join-Path $src 'GaugeArt.cs') (Join-Path $src 'IconGen.cs')
if ($LASTEXITCODE -ne 0) { throw "Icon generator build failed (exit $LASTEXITCODE)." }
& $iconGen $ico (Join-Path $build 'icon-preview.png') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

# 2. Build the app (every source file except the icon tool) with the icon embedded
$out = Join-Path $build 'MachineGauges.exe'
$appSources = Get-ChildItem $src -Filter *.cs | Where-Object Name -ne 'IconGen.cs' | ForEach-Object FullName
& $csc /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 "/out:$out" "/win32icon:$ico" `
       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
       /r:System.Management.dll /r:System.Web.Extensions.dll `
       $appSources
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

"Built: $out ({0:N0} bytes)" -f (Get-Item $out).Length
