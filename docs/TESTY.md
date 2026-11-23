# Testování: Kočky vs. vysavače

Dokument popisuje, co se testuje automaticky, jak se hra zkouší ručně a co ukázal běh 23. 11. 2026. Co zůstává neověřené, je v [DESIGN.md](DESIGN.md), kapitola 9.

## 1. Přehled

| Úroveň | Co | Jak se spouští |
|---|---|---|
| Automatické testy | čistý překlad, bot hrající celé hry, vykreslení všech obrazovek, hra ovládaná sama v otevřeném okně | `powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1` |
| Přepínače hry | stejné nástroje, které test používá, jdou pustit i samostatně | `KockyVsVysavace.exe --sim N [soubor]`, `--uitest N [soubor]`, `--shots složka`, `--audio složka`, `--icon soubor` |
| Ruční scénář | 18 kroků s očekávaným výsledkem | kapitola 3 |

Kontroly pravidel, které při vývoji psali AI agenti (podle záznamů 30, potom 228 a 453 testů a další), nejsou součástí repozitáře, takže v tomto dokumentu nejsou a nejdou znovu spustit.

## 2. Automatické testy

### Spuštění

```
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 [-Games 30] [-UiGames 2] [-SkipUi]
```

- `-Games` (výchozí 30): počet her na obtížnost v kroku `--sim`.
- `-UiGames` (výchozí 2): počet her, které se odehrají přes okno v kroku `--uitest`. Ovladač střídá cesty (po konci hry „Zkusit znovu“ a „Do menu“, do okna vylepšení vstup z obchodu a z vybrané kočky), proto jsou k pokrytí všech cest potřeba aspoň dvě hry.
- `-SkipUi`: vynechá test s oknem (počítač bez přihlášené plochy).

Skript nic nezapisuje do repozitáře. Zdrojáky zkopíruje do dočasné složky v `%TEMP%`, tam je přeloží, spustí přepínače hry a složku nakonec smaže (jen tu, kterou sám vytvořil). U každého kroku vypíše `PASS` nebo `FAIL`, na konci souhrn a vrátí kód 0 (vše v pořádku) nebo 1 (aspoň jedno FAIL). Okno, které by nedoběhlo v časovém limitu, ukončí jen podle čísla procesu (PID).

### Co se kontroluje (17 kroků)

| # | Kontrola | Co ověřuje |
|---|---|---|
| 1 | `csc.exe` z Windows existuje | předpoklad, že se hra dá přeložit bez čehokoli staženého |
| 2 | všech 8 souborů `.cs` a `build.ps1` v `zdrojaky\` | úplnost zdrojáků |
| 3 | `build.ps1` na čisté kopii skončí kódem 0 a vznikne exe (nad 100 kB) | „spustitelné na cizím počítači podle README“, bod B |
| 4 | vznikl `app.ico` (druhý průchod překladu) | hra si sama nakreslí ikonu a build ji vloží |
| 5 | `dist\KockyVsVysavace.exe` existuje a zvládne `--sim 1` | hotové exe z README, bod A, aspoň jeho herní jádro |
| 6 | `--sim 30` skončí kódem 0 a zapíše report | bot dohraje 90 her + volnou hru bez pádu |
| 7 | report má hlavičku a správný počet her | formát výstupu `--sim` |
| 8 | `total exceptions: 0` | žádná výjimka v celé simulaci (`Sim` zároveň hlídá pravidlo o legendách) |
| 9 | v reportu jsou Easy, Medium i Hard | všechny obtížnosti se odehrály |
| 10 | úspěšnost bota: Lehká aspoň 80 %, Střední 50 až 98 %, Těžká 15 až 65 % | hrubá kontrola vyváženosti; cíle ze `SPEC.md` jsou Lehká skoro vždy, Střední 70 až 90 %, Těžká třetina až polovina, tady s tolerancí |
| 11 | bot, který přestane vylepšovat v 50. kole, do 62. kola prohraje (aspoň 90 % her, které do 50. kola došly) | cíl volné hry ze `SPEC.md` |
| 12 | totéž pro 70. kolo a 80. kolo | cíl volné hry ze `SPEC.md` |
| 13 | `--shots` skončí kódem 0 a vznikne aspoň 20 platných PNG nad 20 kB | všechny obrazovky se dají vykreslit bez výjimky |
| 14 | `--uitest 2` skončí kódem 0 a první řádek reportu je `OK` | hra v okně odehraje hry klikáním a klávesami a nic se nerozbije |
| 15 | statistika `--uitest`: hry dohrány, kočky položeny, vylepšeny všechny tři cesty, strom vylepšení otevřen | ovladač opravdu používal všechny části ovládání |
| 16 | průměrné vykreslení snímku pod 33 ms | plynulost (60 snímků/s znamená 16 ms, tady velká rezerva pro slabší počítač) |
| 17 | repozitář po testech beze změny, dočasná složka smazaná | test po sobě nic nenechá |

Kroky 10 až 12 se vyhodnocují jen při `-Games` aspoň 20 (u méně her je statistika příliš hrubá). Simulace je deterministická: stejné semínko dává stejnou hru, takže `--sim 30` dává pokaždé stejný report (ověřeno třemi běhy, výpisy se lišily jen časem běhu).

### Výstup přepínačů

`--sim N soubor` zapíše textový report: hlavičku („games per difficulty: 30, final round 40, wall time …“), pro každou obtížnost řádek `Easy: wins 30/30 (100,0 %), exceptions 0` s dalšími řádky (průměrné kolo, první stupeň 4 a 5, ztracené životy po kolech, sestavy bota, průměrné vylepšení koček), oddíl „Free play“ s režimy „bot keeps playing“ a „bot stops spending at round 50 / 70“ a na konci `total exceptions: 0`. Desetinná čárka podle jazyka Windows, proto test čte jen celá čísla.

`--uitest N soubor` zapíše: první řádek `OK` (nebo `CHYBA: …`), počet kliknutí a stisků kláves, her, kol, položených koček, vylepšení (po cestách) a návštěv okna vylepšení, počet snímků, čas běhu, snímky za sekundu, průměrnou a největší dobu vykreslení snímku a velikost okna. Návratový kód je 0 nebo 1.

## 3. Ruční testovací scénář

Určeno pro člověka, který hru zkouší na čerstvě rozbaleném repozitáři. Kroky 4 až 15 se hrají na obtížnosti **Střední**, kroky 16 a 17 na **Těžké**. Zatím ho nikdo neprošel (viz kapitola 5), sloupec „Splněno“ je prázdný.

| # | Akce | Očekávaný výsledek | Splněno |
|---|---|---|---|
| 1 | Spusť `dist\KockyVsVysavace.exe` dvojklikem (přes SmartScreen: Další informace, Přesto spustit). | Otevře se okno „Kočky vs. vysavače“ uprostřed obrazovky. Na úvodní obrazovce je nadpis, ilustrace kočky a vysavače, tlačítka Hrát, Jak hrát, Zvuk, Hudba, Konec a dole „Nejlepší kola“ a „Výhry“. Po chvíli začne hrát hudba. | ☐ |
| 2 | Klikni na „Hudba: zapnutá“ a potom na „Zvuk: zapnutý“. Pak obojí zapni zpět (klávesy `N` a `M` dělají totéž). | Nápisy se změní na „Hudba: vypnutá“ a „Zvuk: vypnutý“, hudba utichne. Po zapnutí zpět hraje znovu. | ☐ |
| 3 | Klikni na „Jak hrát“ a potom na „Zpět (Esc)“. | Otevře se přehled: Cíl hry, Vrstvy, Zvláštní vysavače, Vylepšení, šest koček s cenami a tabulka Ovládání. Zpět ho zavře. | ☐ |
| 4 | Klikni na „Hrát“ a vyber „Střední“ (nebo stiskni `2`). | Obrazovka obtížnosti ukáže tři karty: Lehká 150 životů, 700 pamlsků, ceny ×0,85; Střední 100, 650, ×1,1; Těžká 50, 600, ×1,2. Po výběru se otevře hra: vlevo obývák s kobercem, vpravo panel se 100 životy, 650 pamlsky, nápisem „Kolo 1 / 40“ a obchodem se šesti kočkami (Drápka 165, Chlupáč 285, Klubíčkářka 460, Laserovka 705, Předoucí kocour 350, Pamlskárna 880). Nahoře hláška „Polož první kočky a pusť vysavače!“. | ☐ |
| 5 | Stiskni `1` (Drápka) a jezdi myší po mapě: přes koberec, přes nábytek, přes volnou podlahu. Klikni na koberec. | Pod kurzorem je duch kočky s kruhem dosahu, na zakázaných místech (koberec, nábytek, jiná kočka) červeně. Klik na zakázané místo ukáže „Sem kočku položit nejde.“ a nic nepoloží. Pravé tlačítko nebo `Esc` pokládání zruší. | ☐ |
| 6 | Znovu `1` a klikni na volnou podlahu těsně vedle koberce, tak aby kruh dosahu zasahoval koberec. | Kočka se položí a ozve se zvuk. Pamlsky klesnou z 650 na 485. | ☐ |
| 7 | Stiskni mezerník (nebo „Pustit vysavače“). | Hláška „Kolo 1“. Z nabíječky vlevo nahoře vyjíždí 20 Robíků a jedou po koberci. Tlačítko ukazuje „Rychlost 1×“ a „Další kolo“ ukazuje „Kolo 2: 30× Robík“. Drápka švihá na vysavače v dosahu, rozbitý Robík zmizí a za každého přibude 1 pamlsek. | ☐ |
| 8 | Během kola stiskni mezerník víckrát za sebou a skonči na rychlosti 1×. | Tlačítko střídá „Rychlost 1×“, „2×“, „3×“ a znovu „1×“. Při vyšší rychlosti se vysavače zřetelně rychleji pohybují. | ☐ |
| 9 | Stiskni `P` a po pár sekundách `P` znovu. | Hra se zastaví, ukáže se okno „Pauza“ (Pokračovat, Zvuk, Hudba, Jak hrát, Do menu), vysavače stojí. Po druhém `P` (nebo `Esc`) pokračují z místa, kde stály. | ☐ |
| 10 | Během kola stiskni `I`, potom `→`, klikni na některou dlaždici a nakonec `Esc`. | Otevře se okno „Všechna vylepšení“ se šesti záložkami koček, třemi cestami po pěti stupních a detailem vpravo; hra stojí, vysavače se nehýbou. Šipka přepne druh kočky, klik na dlaždici ukáže detail. `Esc` okno zavře a hra pokračuje. | ☐ |
| 11 | Počkej na konec kola (kolo 1 trvá na rychlosti 1× zhruba minutu; kdyby skončilo dřív, než stihneš kroky 8 až 10, pokračuj dalším kolem a odměna je pak 100 plus číslo právě skončeného kola). | Hláška „Kolo 1 zvládnuto!“. Pamlsky vzrostou o 101 (100 plus číslo kola) a o pamlsky za rozbité vrstvy. Tlačítko se vrátí na „Pustit vysavače“ a „Kolo 2“. | ☐ |
| 12 | Klikni na položenou kočku. | Panel kočky nahradí obchod: název „Drápka“, „Rozbité vrstvy“, dosah, útoky za sekundu, poškození, průraz, tři řádky vylepšení s cenami (100, 120 a 105 pamlsků) a tlačítka Cíl, Vylepšení, Prodat a Do obchodu. Kolem kočky je kruh dosahu. Najetí na řádek vylepšení ukáže bublinu se změnou čísel a celou cestou. | ☐ |
| 13 | Stiskni `Z` a potom `X`. | Vylepší se cesta 1 (za 100) a cesta 2 (za 120), pokaždé zazní zvuk vylepšení, kolem kočky se objeví efekt a nad ní na chvíli název vylepšení a štítky se změnami. Kočka je 1-1-0. | ☐ |
| 14 | Stiskni `C` (cesta 3). | Cesta 3 je zamčená: řádek je šedý se zámkem a nápisem „Zamčeno“, objeví se hláška „Zamčeno: nejvýš dvě cesty, jen jedna nad 2. stupeň.“ a pamlsky se nezmění. | ☐ |
| 15 | Se stále vybranou kočkou stiskni `Delete`, potom do 3 sekund znovu `Delete`. | Při prvním stisku se tlačítko změní na „Prodat? Znovu Delete“ a kočka zůstane. Po druhém kočka zmizí a pamlsky vzrostou o 269 (70 % z 385 utracených). | ☐ |
| 16 | Stiskni `P`, klikni „Do menu“ a potvrď „Opravdu? Hra se neuloží“. Klikni „Hrát“, vyber „Těžká“ (`3`), nic nestav, pusť kolo 1 a přepni na rychlost 3×. | Hra začne s 50 životy a 600 pamlsky. Všech 20 Robíků dojede k pelíšku vpravo nahoře (efekt a zvuk u pelíšku). Po kole životy klesnou na 30 a pamlsky vzrostou na 701. | ☐ |
| 17 | Pusť kolo 2 (30 Robíků). | Když dojede poslední Robík, životy klesnou na 0 a objeví se okno „Vysavače vysály pelíšek!“ s textem „Obtížnost Těžká“ a statistikou „Přežitá kola“, „Rozbité vrstvy 0“, „Nejlepší výsledek (Těžká)“ a tlačítky „Zkusit znovu“ a „Do menu“. „Přežitá kola“ má ukazovat 1 (kolo 2 se prohrálo); hra ve skutečnosti ukáže 2, viz „Známé odchylky“. | ☐ |
| 18 | Klikni „Do menu“, potom „Konec“ (nebo zavři okno křížkem) a hru spusť znovu. | Úvodní obrazovka ukazuje u Těžké nejlepší kolo. Okno se po „Konec“ zavře a proces zmizí ze Správce úloh. Po novém spuštění jsou zvuk a hudba v tom stavu, v jakém jsi je nechal v kroku 2, protože hra je ukládá do `%APPDATA%\KockyVsVysavace\nastaveni.txt`. | ☐ |

Čím se scénář nepokrývá: výhra ve 40. kole a volná hra (zabere desítky minut). Vítězná obrazovka se vykresluje ve snímku `09-won` z `--shots`, volnou hru zkouší automaticky `--sim`.

### Známé odchylky

- **„Přežitá kola“ po prohře je o 1 víc.** Při prohře ve 2. kole ukáže závěrečné okno „Přežitá kola 2“ a „Nejlepší výsledek kolo 2“, ačkoli se dokončilo jen jedno kolo. Příčina: `Ui.Survived` odečítá 1, jen když kolo běží (`G.RoundActive`), ale `Game` (`Core.cs`, dojetí vysavače k pelíšku) při prohře `RoundActive` vypne. Snímek `08-lost` z `--shots` to neodhalí, protože prohru nastavuje uměle bez vypnutí kola. Nalezeno při přípravě tohoto scénáře pomocným programem mimo repozitář a ověřeno na vykresleném okně. Zdrojové soubory nebyly upraveny.

## 4. Testy umějí selhat

Aby bylo vidět, že 17 kroků nejsou jen samé PASS, byl skript 23. 11. 2026 spuštěn na dvou záměrně rozbitých kopiích repozitáře v dočasné složce (originál zůstal beze změny), obě s `-SkipUi`:

| Rozbití | Výsledek skriptu |
|---|---|
| v `Core.cs` přebývají dvě otevírací složené závorky za hlavičkou metody `StartRound` (syntaktická chyba) | překlad selže (`error CS1513`), 3 FAIL (překlad, ikona, „Test se dokončil“), návratový kód 1, ostatní kroky se přeskočí |
| v `Defs.cs` má Keramik 2200 výdrže místo 22 | překlad i všechny technické kroky projdou, ale bot nevyhraje žádnou hru (Lehká 0 %, Střední 0 %, Těžká 0 %): 3 FAIL (meze úspěšnosti a obě kontroly volné hry), 11 PASS, kód 1 |

## 5. Výsledky běhu 23. 11. 2026

Prostředí: Windows 11 Pro 10.0.26100, Windows PowerShell 5.1.26100.9711, 12 logických procesorů. Spuštěno v 18:14 příkazem `powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1` (výchozí parametry: 30 her, 2 hry přes okno). Celý běh trval 154,2 s, z toho test přes okno 138,7 s.

```
[PASS] Překladač csc.exe z Windows (.NET Framework 4.x) existuje  (C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe)
[PASS] Všechny zdrojové soubory a build.ps1 jsou v zdrojaky\  (8 souborů .cs + build.ps1)
[PASS] build.ps1 na čisté kopii skončí kódem 0 a vznikne exe  (kód 0, exe 327168 B, 3,0 s)
[PASS] Druhý průchod překladu vložil ikonu nakreslenou hrou (app.ico)  (32610 B)
[PASS] dist\KockyVsVysavace.exe existuje a zvládne --sim 1 bez výjimky
[PASS] --sim 30 skončí kódem 0 a zapíše report  (kód 0, hlavní hry 1,6 s)
[PASS] Report má hlavičku a správný počet her na obtížnost
[PASS] Simulace proběhla bez jediné výjimky (total exceptions: 0)  (total exceptions: 0)
[PASS] V reportu jsou všechny tři obtížnosti  (Easy 100,0 %, Medium 86,7 %, Hard 36,7 %)
[PASS] Úspěšnost bota je v rozumných mezích (Lehká >= 80 %, Střední 50-98 %, Těžká 15-65 %)
[PASS] Volná hra: bot, který přestane vylepšovat v 50. kole, do 62. kola prohraje (cíl >= 90 %)  (26 z 26 her, které do kola 50 došly)
[PASS] Volná hra: bot, který přestane vylepšovat v 70. kole, do 80. kola prohraje (cíl >= 90 %)  (7 z 7 her, které do kola 70 došly)
[PASS] --shots skončí kódem 0 a vznikne aspoň 20 platných snímků (PNG nad 20 kB)  (21 snímků, vadných 0)
[PASS] --uitest 2 doběhl bez chyby (kód 0, první řádek OK)  (OK)
[PASS] Okno odehrálo všechny hry, položilo kočky, vylepšilo všechny tři cesty a otevřelo strom vylepšení  (her 2, kol 40, koček 14, vylepšení 26 (cesty 8/9/9), strom 2x)
[PASS] Vykreslení snímku trvá v průměru pod 33 ms (plynulých 30 snímků/s)  (průměr 6,01 ms, max 49,75 ms; 8773 snímků za 138,7 s, 63 snímků/s)
[PASS] Repozitář je po testech beze změny a dočasná složka je smazaná
Souhrn: 17 PASS, 0 FAIL
```

Návratový kód skriptu byl 0.

Čísla z reportu `--sim 30` téhož dne (opakované běhy dávají stejný výsledek):

| Obtížnost | Vyhráno z 30 her | Podíl |
|---|---|---|
| Lehká | 30 | 100,0 % |
| Střední | 26 | 86,7 % |
| Těžká | 11 | 36,7 % |

Volná hra na Střední (30 her na režim, hra se zastavila nejpozději po 120. kole): bot, který hraje dál, dohrál 40 kol v 26 hrách, medián konce ve volné hře je 64. kolo a žádná hra nepřežila 120. kolo; bot, který přestal utrácet v 50. kole, prohrál 26 her z 26 do 62. kola (medián konce 56); bot, který přestal v 70. kole, prohrál 7 her ze 7 do 80. kola (medián konce 73). Výjimek 0.

Čísla a stavy očekávané v krocích 4 až 17 ručního scénáře (ceny koček, 485 pamlsků po položení, ceny vylepšení 100 / 120 / 105, zamčená třetí cesta, prodej za 269, rychlosti 1× až 3×, pauza a okno vylepšení zastavují hru, odměna za kolo, 30 životů a 701 pamlsků po kole 1 na Těžké, prohra ve 2. kole) byly ověřeny pomocným programem, který tytéž akce provedl přes veřejné rozhraní `Ui` a `Game`. Program je jen v dočasné složce, není v repozitáři. Texty hlášek a vzhled oken jsou převzaté z kódu (`Ui.cs`) a ze snímků z `--shots`, ne z ručního zkoušení, a samotný scénář v okně zatím nikdo nepřešel.

## 6. Co testy neověřují

- Jak hra vypadá a zní na jiném počítači, na jiné verzi Windows a při jiném rozlišení nebo DPI (testy běžely na jednom počítači).
- Hudbu a zvuky: zvuky jsou při testech vypnuté, hudbu nikdo neposlouchal.
- Spuštění dvojklikem přes SmartScreen.
- Hraní člověkem. Ručně nikdo neprošel scénář z kapitoly 3.
- Vyváženost pro lidi: měří ji jen bot.
