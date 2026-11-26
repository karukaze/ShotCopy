# Build ShotCopy.exe with the .NET Framework compiler that ships with Windows (no install needed)
# The source is split by role into src\*.cs (see the comment at the top of each file)
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot "src\*.cs"
$out = Join-Path $PSScriptRoot "ShotCopy.exe"
$ico = Join-Path $PSScriptRoot "ShotCopy.ico"
$help = Join-Path $PSScriptRoot "help.html"
# Stop a running copy first, otherwise the exe is locked
Get-Process ShotCopy -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/win32icon:$ico" "/resource:$ico,ShotCopy.ico" "/resource:$help,help.html" /r:System.Windows.Forms.dll /r:System.Drawing.dll $src
if ($LASTEXITCODE -eq 0) { Write-Output "Build OK: $out" } else { Write-Output "Build FAILED" }
