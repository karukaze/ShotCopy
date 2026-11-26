# Build ShotCopy-Setup.exe (the installer). First builds ShotCopy.exe, then packs it inside the installer.
# Uses only the .NET Framework compiler that ships with Windows (no install needed).
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "build.ps1")
$exe = Join-Path $PSScriptRoot "ShotCopy.exe"
if (-not (Test-Path $exe)) { Write-Output "Build FAILED (ShotCopy.exe)"; exit 1 }
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot "installer\Setup.cs"
$out = Join-Path $PSScriptRoot "ShotCopy-Setup.exe"
$ico = Join-Path $PSScriptRoot "ShotCopy.ico"
# The installer's version follows ShotCopy.exe
$ver = (Get-Item $exe).VersionInfo.FileVersion
$verCs = Join-Path $env:TEMP "ShotCopySetupVersion.cs"
[IO.File]::WriteAllText($verCs, "[assembly: System.Reflection.AssemblyVersion(`"$ver`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$ver`")]`n")
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/win32icon:$ico" "/resource:$exe,ShotCopy.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll $src $verCs
if ($LASTEXITCODE -eq 0) { Write-Output "Installer OK: $out ($ver)" } else { Write-Output "Installer build FAILED" }
