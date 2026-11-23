<#
  Automatické testy hry Kočky vs. vysavače (Windows 10/11, Windows PowerShell 5.1).

  Spuštění z kořene repozitáře:
      powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1

  Parametry:
      -Games 30    kolik her na každou obtížnost odehraje bot v kroku --sim
      -UiGames 2   kolik her se odehraje přes skutečné okno v kroku --uitest
      -SkipUi      vynechá test přes okno (počítač bez přihlášené plochy)

  Skript nic nezapisuje do repozitáře. Zdrojáky zkopíruje do dočasné složky v %TEMP%, tam je
  přeloží (jako by je překládal cizí počítač), spustí přepínače hry --sim, --shots a --uitest
  a dočasnou složku na konci smaže. U každého kroku vypíše PASS nebo FAIL.
  Návratový kód: 0 = vše PASS, 1 = aspoň jeden FAIL.
#>
[CmdletBinding()]
param(
    [int]$Games = 30,
    [int]$UiGames = 2,
    [switch]$SkipUi
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$sources = "Kit.cs", "Defs.cs", "Core.cs", "Bot.cs", "Art.cs", "Audio.cs", "Ui.cs", "Program.cs"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$failed = 0
$passed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    $line = "[{0}] {1}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name
    if ($detail) { $line += "  (" + $detail + ")" }
    Write-Host $line -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    if ($ok) { $script:passed++ } else { $script:failed++ }
}

# Spustí hru s přepínačem a počká na konec. Okno, které nedoběhne, se ukončí jen podle PID.
function Invoke-Game([string]$exe, [string]$arguments, [int]$timeoutSec) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    $null = $p.Handle   # bez toho PowerShell 5.1 někdy po skončení vrátí prázdný ExitCode
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        & taskkill /PID $p.Id /T /F | Out-Null
        return [pscustomobject]@{ Code = -1; TimedOut = $true }
    }
    $p.WaitForExit()
    return [pscustomobject]@{ Code = $p.ExitCode; TimedOut = $false }
}

# Desetinná čárka i tečka (hra píše čísla podle jazyka Windows).
function ToNumber([string]$s) { [double]::Parse($s.Replace(",", "."), [Globalization.CultureInfo]::InvariantCulture) }

function Get-RepoState {
    $git = Join-Path $repo ".git"
    Get-ChildItem -LiteralPath $repo -Recurse -File -Force |
        Where-Object { -not $_.FullName.StartsWith($git) } |
        ForEach-Object { "{0}|{1}|{2}" -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.Ticks } |
        Sort-Object
}

function Read-Report([string]$file) {
    if (Test-Path -LiteralPath $file) { return Get-Content -LiteralPath $file -Raw -Encoding UTF8 }
    return ""
}

Write-Host ("Kočky vs. vysavače - testy, {0}, PowerShell {1}" -f (Get-Date -Format "d. M. yyyy HH:mm"), $PSVersionTable.PSVersion)
$before = (Get-RepoState) -join "`n"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("kocky-tests-" + [guid]::NewGuid().ToString("N").Substring(0, 8))

try {
    # 1. předpoklady
    Check "Překladač csc.exe z Windows (.NET Framework 4.x) existuje" (Test-Path $csc) $csc
    $missing = @($sources + "build.ps1" | Where-Object { -not (Test-Path (Join-Path $repo "zdrojaky\$_")) })
    Check "Všechny zdrojové soubory a build.ps1 jsou v zdrojaky\" ($missing.Count -eq 0) $(if ($missing.Count) { "chybí: " + ($missing -join ", ") } else { "8 souborů .cs + build.ps1" })
    if ($failed -gt 0) { throw "Chybí předpoklady, dál nemá smysl pokračovat." }

    # 2. překlad na čisté kopii
    $work = Join-Path $tmp "zdrojaky"
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    foreach ($f in $sources + "build.ps1") { Copy-Item (Join-Path $repo "zdrojaky\$f") $work }
    $exe = Join-Path $work "KockyVsVysavace.exe"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $b = Start-Process -FilePath "powershell.exe" -PassThru -Wait -NoNewWindow `
        -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f (Join-Path $work "build.ps1")) `
        -RedirectStandardOutput (Join-Path $tmp "build.log") -RedirectStandardError (Join-Path $tmp "build-err.log")
    $size = if (Test-Path $exe) { (Get-Item $exe).Length } else { 0 }
    Check "build.ps1 na čisté kopii skončí kódem 0 a vznikne exe" ($b.ExitCode -eq 0 -and $size -gt 100000) ("kód {0}, exe {1} B, {2:N1} s" -f $b.ExitCode, $size, $watch.Elapsed.TotalSeconds)
    $ico = Join-Path $work "app.ico"
    Check "Druhý průchod překladu vložil ikonu nakreslenou hrou (app.ico)" ((Test-Path $ico) -and (Get-Item $ico).Length -gt 1000) $(if (Test-Path $ico) { "{0} B" -f (Get-Item $ico).Length } else { "app.ico chybí" })
    if ($b.ExitCode -ne 0) {
        # dočasná složka se na konci smaže, proto výpis překladače ukážeme hned
        Write-Host "Výpis překladu (prvních 15 řádků):" -ForegroundColor Yellow
        foreach ($name in "build.log", "build-err.log") {
            $logFile = Join-Path $tmp $name
            # překladač píše v kódování konzole, ve které běžel
            if (Test-Path $logFile) { [IO.File]::ReadAllLines($logFile, [Console]::OutputEncoding) | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" } }
        }
    }
    if (-not (Test-Path $exe)) { throw "Bez exe se nedá testovat dál." }

    # 3. hotové exe z dist\ (to, co README nabízí ke spuštění dvojklikem)
    $dist = Join-Path $repo "dist\KockyVsVysavace.exe"
    $distOk = $false
    if (Test-Path $dist) {
        $r = Invoke-Game $dist ('--sim 1 "{0}"' -f (Join-Path $tmp "sim-dist.txt")) 120
        $distOk = (-not $r.TimedOut) -and $r.Code -eq 0 -and ((Read-Report (Join-Path $tmp "sim-dist.txt")) -match "total exceptions: 0")
    }
    Check "dist\KockyVsVysavace.exe existuje a zvládne --sim 1 bez výjimky" $distOk

    # 4. --sim: bot hraje celé hry bez okna
    $simFile = Join-Path $tmp "sim.txt"
    $r = Invoke-Game $exe ('--sim {0} "{1}"' -f $Games, $simFile) 600
    $sim = Read-Report $simFile
    $wall = [regex]::Match($sim, "wall time ([\d.,]+) s")
    Check "--sim $Games skončí kódem 0 a zapíše report" ((-not $r.TimedOut) -and $r.Code -eq 0 -and $sim.Length -gt 0) $(if ($r.TimedOut) { "vypršel čas" } else { "kód {0}, hlavní hry {1} s" -f $r.Code, $(if ($wall.Success) { $wall.Groups[1].Value } else { "?" }) })
    Check "Report má hlavičku a správný počet her na obtížnost" ($sim -match "(?m)^Kocky vs\. vysavace - balance report" -and $sim -match "games per difficulty: $Games,")
    $total = [regex]::Match($sim, "total exceptions: (\d+)")
    Check "Simulace proběhla bez jediné výjimky (total exceptions: 0)" ($total.Success -and $total.Groups[1].Value -eq "0") $(if ($total.Success) { "total exceptions: " + $total.Groups[1].Value } else { "řádek v reportu chybí" })

    $rate = @{}
    foreach ($d in "Easy", "Medium", "Hard") {
        $m = [regex]::Match($sim, "(?m)^${d}: wins (\d+)/(\d+) .*exceptions (\d+)")
        if ($m.Success -and [int]$m.Groups[2].Value -gt 0) { $rate[$d] = 100.0 * [int]$m.Groups[1].Value / [int]$m.Groups[2].Value }
    }
    Check "V reportu jsou všechny tři obtížnosti" ($rate.Count -eq 3) ((@("Easy", "Medium", "Hard") | Where-Object { $rate.ContainsKey($_) } | ForEach-Object { "{0} {1:N1} %" -f $_, $rate[$_] }) -join ", ")
    if ($Games -ge 20 -and $rate.Count -eq 3) {
        # Cíle ze SPEC.md (Lehká skoro vždy, Střední většinou, Těžká třetina až polovina), tady s tolerancí.
        $okBal = $rate["Easy"] -ge 80 -and $rate["Medium"] -ge 50 -and $rate["Medium"] -le 98 -and $rate["Hard"] -ge 15 -and $rate["Hard"] -le 65
        Check "Úspěšnost bota je v rozumných mezích (Lehká >= 80 %, Střední 50-98 %, Těžká 15-65 %)" $okBal
        # Volná hra: bot, který přestane vylepšovat, musí do deseti kol prohrát. Aspoň jedna hra musí do daného kola dojít.
        foreach ($stop in 50, 70) {
            $m = [regex]::Match($sim, "bot stops spending at round ${stop}: games reaching it (\d+), lost by round (\d+): (\d+)")
            $reached = if ($m.Success) { [int]$m.Groups[1].Value } else { 0 }
            $lost = if ($m.Success) { [int]$m.Groups[3].Value } else { 0 }
            Check "Volná hra: bot, který přestane vylepšovat v ${stop}. kole, do $($m.Groups[2].Value). kola prohraje (cíl >= 90 %)" ($reached -ge 1 -and $lost -ge 0.9 * $reached) $(if ($m.Success) { "{0} z {1} her, které do kola ${stop} došly" -f $lost, $reached } else { "řádek v reportu chybí" })
        }
    }

    # 5. --shots: vykreslení všech obrazovek do PNG (bez okna)
    $shotDir = Join-Path $tmp "shots"
    $r = Invoke-Game $exe ('--shots "{0}"' -f $shotDir) 300
    $png = @(if (Test-Path $shotDir) { Get-ChildItem $shotDir -Filter *.png })
    $badPng = @($png | Where-Object {
        $head = [IO.File]::ReadAllBytes($_.FullName)[0..7] -join ","
        $head -ne "137,80,78,71,13,10,26,10" -or $_.Length -lt 20000
    })
    Check "--shots skončí kódem 0 a vznikne aspoň 20 platných snímků (PNG nad 20 kB)" ((-not $r.TimedOut) -and $r.Code -eq 0 -and $png.Count -ge 20 -and $badPng.Count -eq 0) ("{0} snímků, vadných {1}" -f $png.Count, $badPng.Count)

    # 6. --uitest: skutečné okno se samo ovládá klikáním a klávesami
    if ($SkipUi) {
        Write-Host "[SKIP] --uitest vynechán (-SkipUi)" -ForegroundColor Yellow
    } else {
        $uiFile = Join-Path $tmp "uitest.txt"
        $r = Invoke-Game $exe ('--uitest {0} "{1}"' -f $UiGames, $uiFile) (900 * $UiGames)
        $ui = Read-Report $uiFile
        Check "--uitest $UiGames doběhl bez chyby (kód 0, první řádek OK)" ((-not $r.TimedOut) -and $r.Code -eq 0 -and $ui -match "^OK") $(if ($r.TimedOut) { "vypršel čas" } elseif ($ui) { ($ui -split "`r?`n")[0] } else { "report chybí" })
        $m = [regex]::Match($ui, "her dohrano: (\d+), kol odehrano: (\d+), kocek polozeno: (\d+), vylepseni: (\d+) \(cesty 1/2/3: (\d+)/(\d+)/(\d+)\), strom vylepseni otevren a zavren: (\d+)x")
        if ($m.Success) {
            $v = 1..8 | ForEach-Object { [int]$m.Groups[$_].Value }
            Check "Okno odehrálo všechny hry, položilo kočky, vylepšilo všechny tři cesty a otevřelo strom vylepšení" ($v[0] -ge $UiGames -and $v[2] -gt 0 -and $v[4] -gt 0 -and $v[5] -gt 0 -and $v[6] -gt 0 -and $v[7] -gt 0) ("her {0}, kol {1}, koček {2}, vylepšení {3} (cesty {4}/{5}/{6}), strom {7}x" -f $v)
        } else {
            Check "Okno odehrálo všechny hry, položilo kočky, vylepšilo všechny tři cesty a otevřelo strom vylepšení" $false "řádek se statistikou v reportu chybí"
        }
        $t = [regex]::Match($ui, "prumer ([\d.,]+) ms, max ([\d.,]+) ms")
        $fr = [regex]::Match($ui, "snimku: (\d+), cas behu: ([\d.,]+) s, fps: (\d+)")
        Check "Vykreslení snímku trvá v průměru pod 33 ms (plynulých 30 snímků/s)" ($t.Success -and (ToNumber $t.Groups[1].Value) -lt 33) $(if ($t.Success -and $fr.Success) { "průměr {0} ms, max {1} ms; {2} snímků za {3} s, {4} snímků/s" -f $t.Groups[1].Value, $t.Groups[2].Value, $fr.Groups[1].Value, $fr.Groups[2].Value, $fr.Groups[3].Value } else { "řádek v reportu chybí" })
    }
}
catch {
    Check "Test se dokončil" $false $_.Exception.Message
}
finally {
    # smaže jen složku, kterou si skript sám vytvořil
    if ((Test-Path $tmp) -and $tmp -like "*kocky-tests-*") { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

$after = (Get-RepoState) -join "`n"
Check "Repozitář je po testech beze změny a dočasná složka je smazaná" ($before -eq $after -and -not (Test-Path $tmp))

Write-Host ("Souhrn: {0} PASS, {1} FAIL" -f $passed, $failed) -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
exit $(if ($failed -eq 0) { 0 } else { 1 })
