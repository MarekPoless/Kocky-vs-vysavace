# Builds KockyVsVysavace.exe with the C# compiler that ships with Windows (.NET Framework 4.8), no downloads needed.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = "Kit.cs", "Defs.cs", "Core.cs", "Bot.cs", "Art.cs", "Audio.cs", "Ui.cs", "Program.cs"
$common = "/nologo", "/target:winexe", "/optimize+", "/main:Kocky.Program", "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll"

& $csc @common /out:KockyVsVysavace.exe @src
if ($LASTEXITCODE -ne 0) { throw "Kompilace selhala" }

# second pass embeds the icon drawn by the game itself
$p = Start-Process -FilePath .\KockyVsVysavace.exe -ArgumentList "--icon", "app.ico" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Ikona se nevytvorila" }
& $csc @common /win32icon:app.ico /out:KockyVsVysavace.exe @src
if ($LASTEXITCODE -ne 0) { throw "Kompilace s ikonou selhala" }
Get-Item .\KockyVsVysavace.exe | Select-Object Name, Length, LastWriteTime
