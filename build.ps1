# Builds MachineGauges.exe with the compiler that ships with Windows - no SDK required.
$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$build = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path (Join-Path $build 'tools') | Out-Null
function Src($names) { $names | ForEach-Object { Join-Path $PSScriptRoot "src\$_" } }

# 1. Render the gauge icon into a multi-size .ico
$iconGen = Join-Path $build 'tools\IconGen.exe'
$ico     = Join-Path $build 'app.ico'
& $csc /nologo /target:exe /platform:x64 /optimize+ /langversion:5 "/out:$iconGen" `
       /r:System.dll /r:System.Drawing.dll (Src 'GaugeArt.cs','IconGen.cs')
if ($LASTEXITCODE -ne 0) { throw "Icon generator build failed (exit $LASTEXITCODE)." }
& $iconGen $ico (Join-Path $build 'icon-preview.png') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

# 2. Build the app with the icon embedded
$out = Join-Path $build 'MachineGauges.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 "/out:$out" "/win32icon:$ico" `
       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
       (Src 'AssemblyInfo.cs','Native.cs','GaugeArt.cs','Sampler.cs','Config.cs','Installer.cs','OverlayForm.cs','Program.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

"Built: $out ({0:N0} bytes)" -f (Get-Item $out).Length
