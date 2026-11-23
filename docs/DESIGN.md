Vylosovaná kombinace: (doplní Michal)

# Design dokument: Kočky vs. vysavače

Autor: Michal Skřipský. Čísla pravidel jsou ze `zdrojaky\Defs.cs`, `zdrojaky\Core.cs` a `zdrojaky\SPEC.md`, výsledky měření z běhu testů 23. 11. 2026 (podrobnosti v [TESTY.md](TESTY.md)).

## 1. Cíl a cílová skupina

**Cíl.** Dokončená hra pro jednoho hráče na Windows, která se dá spustit jedním dvojklikem a sestavit ze zdrojáků jedním příkazem, bez instalace a bez stahování. Má mít vlastní vzhled, jména, hudbu a zvuky (zadání v `SPEC.md`: „do not copy any existing game“). Mechanika je ve stylu klasických tower defense her typu „praskání vrstev“: zásah odloupne jednu vrstvu nepřítele a ten se změní ve slabšího.

**Cílová skupina.** Příležitostní hráči strategických her. Pravidla se dají naučit z obrazovky „Jak hrát“ přímo ve hře, hra tyká a má veselý kreslený vzhled. Hráči, kteří chtějí plánovat, mají k dispozici 3 cesty vylepšení po 5 stupních u každé kočky (90 vylepšení) a volnou hru po 40. kole.

## 2. Hlavní herní smyčka

1. Na začátku hráč vybere obtížnost a dostane počáteční životy a pamlsky (měna hry).
2. Mezi koly staví kočky na podlahu obýváku a vylepšuje je. Pravý panel ukazuje náhled dalšího kola („Kolo 27: 15× Disko, 8× Pancéřák, 40× Raketa“).
3. Hráč pustí kolo (mezerník). Z nabíječky vyjíždějí vysavače po koberci k pelíšku, kočky na ně útočí. Každá rozbitá vrstva dá 1 pamlsek.
4. Vysavač, který dojede k pelíšku, ubere životy (kolik, to určují jeho vrstvy, viz níže). Při 0 životech hra končí.
5. Po konci kola hráč dostane 100 pamlsků plus číslo kola a případné příjmy Pamlskáren. Pokračuje bodem 2.
6. Po 40. kole se zvládnutou hrou hráč vyhraje a může pokračovat ve volné hře, která je čím dál těžší, až do prohry.

## 3. Pravidla a mechaniky

### Mapa

Herní plocha je obývák 960 × 800 px (virtuální plátno hry je 1280 × 800, vpravo je panel široký 320 px). Vysavače jedou po koberci (šířka 56 px) z nabíječky vlevo (`Defs.Path`, 9 bodů) k pelíšku vpravo nahoře. Překážky, kam se nesmí stavět: knihovna, květina, stolek, křeslo, pohovka a samotný pelíšek. Kočka (poloměr 22 px) se smí položit jen na podlahu uvnitř plochy, mimo koberec, mimo překážky a tak, aby se nepřekrývala s jinou kočkou (`Game.CanPlace`).

### Kočky

Cena je základní, na obtížnosti se násobí (viz níže) a zaokrouhluje na 5. Dosah je v pixelech mapy a vidí se jako kruh kolem kočky. Prodej vrátí 70 % utracených pamlsků.

| Kočka | Cena | Dosah | Interval útoku | Poškození | Průraz | Jak funguje |
|---|---|---|---|---|---|---|
| Drápka | 150 | 95 | 0,6 s | 1 (ostré) | 2 | okamžitě švihne tlapkou po cílech v dosahu |
| Chlupáč | 260 | 170 | 0,95 s | 1 (ostré) | 3 | plive chlupové kuličky (520 px/s) s předstihem |
| Klubíčkářka | 420 | 150 | 1,3 s | 1 (výbuch) | 18 | hodí klubko (380 px/s), které bouchne v okruhu 50 px; zasáhne i tiché a pancéřové |
| Laserovka | 640 | 190 | 0,14 s | 1 (energie) | 1 | paprsek, který vysavače rozpéká |
| Předoucí kocour | 320 | 105 | 0,5 s | – | – | nemá útok, jeho aura zpomaluje vysavače v dosahu |
| Pamlskárna | 800 | – | – | – | – | na konci kola vydá pamlsky navíc (základ 70) |

„Průraz“ je počet vysavačů, které jeden útok zasáhne. Každá útočící kočka může mířit na první, poslední, nejsilnější nebo nejbližší vysavač (klávesa Tab).

### Vysavače

Vysavač se skládá z vrstev. Zásah odloupne jednu vrstvu a vysavač se změní v „děti“, slabší vysavače. Do životů, které hráč ztratí, se počítá celý zbytek stromu: cena průjezdu je 1 plus součet cen dětí (`Game.LeakCost`).

| Vysavač | Rychlost (px/s) | Výdrž vrstvy | Po rozbití | Cena průjezdu | Zvláštnost |
|---|---|---|---|---|---|
| Robík | 55 | 1 | – | 1 | |
| Robík Plus | 70 | 1 | Robík | 2 | |
| Turbo | 90 | 1 | Plus | 3 | |
| Sprint | 125 | 1 | Turbo | 4 | |
| Raketa | 160 | 1 | Sprint | 5 | |
| Dvojče | 95 | 1 | 2× Raketa | 11 | |
| Pancéřák | 50 | 1 | 2× Dvojče | 23 | pancéřový: ostré poškození mu nic nedělá |
| Disko | 140 | 1 | 2× Dvojče | 23 | |
| Keramik | 160 | 22 | 2× Disko | 47 | |
| Průmyslák | 32 | 300 | 4× Keramik | 189 | boss: zpomalení a omráčení na něj působí méně |
| Obr | 22 | 2500 | 4× Průmyslák | 757 | boss, objevuje se jako závěrečný ve 40. kole |

Vysavače mohou být **tiché** (průsvitné, čárkovaný obrys): cílit na ně smí jen kočka, která je umí vidět (vylepšení „Noční vidění“ a podobná), nebo kočka v dosahu Předoucího kocoura s odhalením. Výbuchy a aura zasáhnou tiché vysavače vždy. Pancéřový vysavač zraní jen výbuch (Klubíčkářka), energie (Laserovka) a vylepšení, která pancíř prorážejí.

### Cesty vylepšení

Každá kočka má **3 cesty × 5 stupňů** (6 koček × 3 cesty × 5 stupňů = 90 vylepšení). Pravidlo o cestách (`Defs.CanRaise`): vylepšovat jdou nejvýš dvě cesty a jen jedna z nich může jít nad 2. stupeň. Dovolené jsou třeba 5-2-0, 2-0-5 nebo 0-3-2, zakázané 3-3-0 nebo 2-2-2. Pravidlo **legendy**: 5. stupeň na dané cestě smí mít v jednu chvíli jen jedna kočka stejného druhu, takže druh má nejvýš tři legendy (`Game.UpgradeBlock` vrací `Maxed`, `Crosspath` nebo `LegendTaken`).

| Kočka | Cesta 1 | Cesta 2 | Cesta 3 |
|---|---|---|---|
| Drápka | Drápy | Rychlost | Zuby |
| Chlupáč | Kuličky | Mířidla | Ostřelování |
| Klubíčkářka | Výbuch | Hod | Pletení |
| Laserovka | Paprsek | Optika | Žár |
| Předoucí kocour | Spánek | Pelíšek | Pohoda |
| Pamlskárna | Zásoby | Péče | Rozvoz |

Každý stupeň mění číselné vlastnosti (dosah, interval, poškození, průraz, výbuch, vidění tichých, pancíř, násobek na bossy; pole `UpgradeDef`) a u vyšších stupňů dává novou schopnost (třeba kousnutí Drápky, ostřelovač Chlupáče, pavučiny Klubíčkářky, výbuchy žáru Laserovky). Příklady z `Defs.cs`: Drápka, cesta Drápy, stupně stojí 90, 210, 680, 1550 a 4550 pamlsků (základní ceny). Pamlskárna s vylepšením „Zásoby“ vydává za kolo 70, 110, 200, 450, 900 a 1800 pamlsků podle stupně.

### Kola

Prvních 40 kol je ruční tabulka skupin (druh, počet, rozestup, zpoždění, tiché) v `Core.cs`. Nové druhy přicházejí postupně: Robík Plus ve 3. kole, Turbo v 6., Sprint v 10., první tiché vysavače ve 12., Raketa ve 14., Dvojče v 18., Pancéřák v 21., Disko ve 24., Keramik ve 28., první Průmyslák ve 30., dva Průmysláci v 35. a Obr ve 40. kole.

### Obtížnosti

| Obtížnost | Životy | Počáteční pamlsky | Násobek cen koček a vylepšení |
|---|---|---|---|
| Lehká | 150 | 700 | × 0,85 |
| Střední | 100 | 650 | × 1,1 |
| Těžká | 50 | 600 | × 1,2 |

### Volná hra (po 40. kole)

Kola 41 a dál se generují a každé je těžší. Všechny křivky jsou na jednom místě v `Core.cs` (n = číslo kola − 40): výdrž Keramiků roste jako 1,08^n a bossů jako 1,10^n, rychlost všech vysavačů o 2 % za kolo nejvýš na × 1,6, odměna za rozbitou vrstvu klesá na 1 / (1 + 0,1 n), skupiny jsou hustší a Obr přichází stále častěji. Cíl (`SPEC.md`): bot, který v 50. kole přestane utrácet, prohraje do 62. kola, a bot, který přestane v 70. kole, do 80. kola, takže obrana, která přestane růst, se brzy zhroutí. Výdrž se hráči ukazuje na vysavači samém (ocelové pásy, šrouby, pláty a žhavé švy podle `Vac.Tough`), ne číslem.

## 4. Obrazovky a ovládání

| Obrazovka | Co obsahuje |
|---|---|
| Úvod | název a ilustrace, tlačítka Hrát, Jak hrát, Zvuk, Hudba, Konec, nejlepší kola na každé obtížnosti a počet výher |
| Obtížnost | tři karty (Lehká, Střední, Těžká) s počtem životů, pamlsků, násobkem cen a nejlepším kolem; klávesy 1 až 3 |
| Hra | vlevo mapa, vpravo panel: životy, pamlsky, „Kolo N / 40“, tlačítka pauzy, zvuku a hudby, obchod se šesti kočkami (cena, popis a dosah po najetí, šedě když nejsou peníze), náhled dalšího kola, zaškrtávátko „Další kolo pustit samo“ a velké tlačítko „Pustit vysavače“ (za běhu kola ukazuje rychlost 1× až 3×) |
| Vybraná kočka | panel kočky nahradí obchod: počet rozbitých vrstev, aktuální čísla, tři řádky vylepšení se stavy (koupitelné, chybí pamlsky, zamčeno, obsazená legenda, hotovo), tlačítka Cíl, Vylepšení, Prodat a Do obchodu; po najetí na vylepšení bublina se změnou čísel a celou cestou |
| Okno všech vylepšení | všech 90 vylepšení po druzích koček (klávesa I), hra při něm stojí |
| Pauza | Pokračovat, Zvuk, Hudba, Jak hrát, Do menu (se zeptá „Opravdu? Hra se neuloží“) |
| Konec hry | prohra („Vysavače vysály pelíšek!“) nebo výhra („Kočky ubránily pelíšek!“ s tlačítkem „Hrát dál (volná hra)“), statistiky a nejlepší výsledek |
| Jak hrát | cíl, vrstvy, zvláštní vysavače, vylepšení, šest koček a tabulka ovládání |

Úplná tabulka ovládání je v [README.md](../README.md). Pokládání koček řídí myš (duch kočky a kruh dosahu pod kurzorem, červeně, když tam kočka nesmí), vylepšování myš nebo klávesy Z, X, C, výběr z obchodu klávesy 1 až 6.

## 5. Vzhled a zvuk

**Všechno je kreslené nebo syntetizované v kódu, žádné obrázky ani zvuky se nenačítají ze souborů.**

- **Vzhled.** Pohled shora na útulný obývák, plochá kreslená ilustrace s tmavým obrysem, měkké stíny, teplá paleta. Každý druh kočky má vlastní siluetu a barvy srsti a s vyššími stupni se kočka viditelně mění: každá cesta má svůj motiv, od 3. stupně je to jasná nová součást a 5. stupeň (legenda) má záři a výraznou siluetu. Vysavače jsou kulaté roboty seshora s nárazníkem, tiché jsou průsvitné, zpomalené namodralé, zmražené mají led. Vše kreslí `Art.cs` přes GDI+ z prostých hodnot (druh, stupně, poloha, úhel, čas), takže kreslení nezávisí na simulaci. Statická mapa se nakreslí jednou do bitmapy a znovu se používá. Písma jsou Bahnschrift, jinak Segoe UI nebo Arial.
- **Plátno.** Hra kreslí do pevného plátna 1280 × 800 a to se zvětší do okna se zachováním poměru stran (černé okraje). Okno jde zvětšovat, nejmenší velikost je 820 × 560.
- **Zvuk.** Zvuky a hudbu počítá `Audio.cs` za běhu: vzorkování 22 050 Hz, mono, 16 bit. Jeden směšovač na vlastním vlákně sčítá hudbu a až 16 zvukových hlasů a výsledek jde přes měkký limiter do `waveOut` (`winmm.dll`). Zvuky se omezují, aby se neslévaly (třeba prasknutí vrstvy nejdřív po 35 ms). Hra běží i bez zvukové karty. Zvuky mají jména (`pop`, `clank`, `swipe`, `spit`, `throw`, `blast`, `laser`, `freeze`, `zzz`, `place`, `upgrade`, `sell`, `round`, `coins`, `leak`, `boss`, `win`, `lose`, `meow`, `click`, `bite`, `snipe`, `web`, `legend`), která používá simulace i okno.
- **Hudba.** Jedna původní plynule se opakující smyčka „Kočičí kavárna“: swing ve F dur, 104 úderů za minutu, 24 taktů, asi 55 s.

## 6. Architektura

Jedna aplikace, osm souborů, jeden jmenný prostor `Kocky`. Každý soubor má jednoho vlastníka (viz kapitola 7) a závislosti míří jedním směrem: okno zná simulaci a kreslení, simulace nezná nic z okna.

| Soubor | Odpovědnost |
|---|---|
| `Kit.cs` | paleta, písma, štětce, text a zaoblené tvary pro všechno kreslení |
| `Defs.cs` | statická data (mapa, trasa, kočky, vylepšení, vysavače, obtížnosti) a pravidlo `CanRaise` |
| `Core.cs` | třída `Game`: kola, pohyb vysavačů, cílení, útoky, odměny, vylepšení, volná hra. Nezná okno, jen `Defs` a standardní knihovnu |
| `Bot.cs` | `Bot` (automatický hráč přes veřejné API `Game`) a `Sim` (hry bez okna, `Parallel.For`) |
| `Art.cs` | statická třída `Art`, funkce `Map`, `Cat`, `Vacuum`, `Shot`, `Effect`, ikony. Nezná třídu `Game` |
| `Audio.cs` | `Audio.Play(jméno)`, směšovač, hudba, export do WAV |
| `Ui.cs` | obrazovky, panel, okno vylepšení, vstup, nastavení; za běhu hry místo, které spojuje simulaci, kreslení a zvuk |
| `Program.cs` | `Main`, hlavní okno `MainForm`, testovací ovladač `UiDriver`, přepínače |

**Datový tok:**

```
časovač okna (15 ms) -> MainForm.OnTick -> Ui.Tick(dt)
                                              |  pevný krok 1/60 s, rychlost 1x/2x/3x
                                              v
vstup (myš, klávesy) -> Ui.MouseDown/Key -> Game: Place, Upgrade, Sell, StartRound, Step
                                              |                |
                                              |                +-- Game.Sound(jméno) -> Audio.Play
                                              v
                      Ui.Render(Graphics) -> Art.* kreslí aktuální stav hry
Bot.Act(game)  -- stejné veřejné API jako Ui -->  Game      (Sim.Run: hry bez okna)
```

Důležitá rozhodnutí:

- **Pevný časový krok.** Simulace jde po 1/60 s. Rychlost 2× a 3× znamená víc kroků na snímek, ne delší krok, takže výsledek hry nezávisí na rychlosti ani na výkonu počítače. Když počítač nestíhá, zahodí se čas místo spirály (nejvýš 60 kroků na snímek).
- **Opakovatelnost.** Veškerá náhoda je v `Game` z `new Random(seed)`, takže se stejným semínkem a stejným hráčem proběhne hra stejně. Na tom stojí měření bota.
- **Bot nepodvádí.** `Bot` používá stejné veřejné API jako okno (`Place`, `Upgrade`, `UpgradePrice`, `UpgradeBlock`), včetně pravidla o legendách, takže měření vyváženosti platí i pro člověka, který tytéž tahy zadává myší. Bot má 20 sestav (10 dvojic cest k 5. stupni v obou pořadích) a jednu z nich si na začátku hry vylosuje.
- **Testovatelnost je součástí programu.** `Ui` má příznaky `Silent` (bez zvuku) a `Persist` (bez ukládání nastavení), aby testy nepřepisovaly hráčovo nastavení a nejlepší kola. Přepínače `--sim`, `--uitest`, `--shots` a `--audio` jsou součástí exe.
- **Nastavení** je v `%APPDATA%\KockyVsVysavace\nastaveni.txt` (zvuk, hudba, nejlepší kola, výhry); při chybě čtení nebo zápisu se hra rozběhne s výchozími hodnotami.

## 7. Jak vznikla s AI

Podle záznamů o vývoji vznikla hra celá 12. 11. 2026 (časy změn zdrojových souborů sedí: od 10:44 do 20:26). Kód psali agenti Claude od Anthropic, Michal zadával, zkoušel hru a vracel připomínky.

1. **Zadání a kontrakt.** Michal chtěl hru typu tower defense s vlastním vzhledem a hudbou a „víc agentů, všechny na Opusu (i hudbu)“. Hlavní Claude napsal návrh, sdílená data a rozhraní: `Defs.cs`, `Kit.cs`, `build.ps1` a hlavně `SPEC.md`, technickou smlouvu s přesným API tříd, pravidly simulace, pravidly pro jazyk (C# 5 bez interpolace řetězců, `?.` a dalších novinek) a tabulkou souborů a jejich vlastníků.
2. **Paralelní práce.** Pak souběžně pracovali čtyři agenti na modelu Opus, každý jen na svých souborech: simulace a bot (`Core.cs`, `Bot.cs`), grafika (`Art.cs`), zvuk a hudba (`Audio.cs`) a okno s ovládáním (`Ui.cs`, `Program.cs`). Soubory sdílené všemi (`Kit.cs`, `Defs.cs`) smí měnit jen vlastník, čísla v `Defs.cs` jen autor jádra kvůli vyvážení.
3. **Iterace podle Michalových připomínek** (všechny 12. 11.):
   - nejdřív 2 cesty po 3 stupních, pak na přání (vylepšení a vlastní dosah útoku „jak v Bloons TD 6“, tedy podle vzoru existující hry; vzhled, jména a hudba jsou vlastní) rozšíření na 3 cesty × 5 stupňů, 90 vylepšení;
   - vylepšení byla „naslepo“: přibylo okno všech vylepšení, bublina „teď → potom“ s celou cestou a pravidlo legend;
   - volná hra byla příliš lehká (Michal přestal vylepšovat kolem 70. kola a kočky pořád vydržely), proto se zpřísnila a výdrž vysavačů je vidět na nich samých (Michal: „nedávej tam, kolik má HP“);
   - „nevidím, co to dopředu dá za bonus“: popis stupně přímo na tlačítku; „udělej to ještě promakanější“: štítky s ikonkami u každé změny čísla.
4. **Kontrola.** Po každé změně se podle záznamů dělalo toto: překlad, kontroly pravidel napsané agenty (30, potom 228 a 453 testů; kontrola, že všech 90 vylepšení má účinek; kontrola legend; kontrola rozložení a čísel ve 474 stavech kočky), simulace botem, snímky obrazovek a test přes okno. U poslední úpravy hlavní Claude prošel celý rozdíl v kódu a opravil 3 drobnosti. Podrobněji [deník selhání AI](DENIK-SELHANI-AI.md) a [retrospektiva](RETROSPEKTIVA.md).

## 8. Testování

V repozitáři jsou:

- **`tests\run-tests.ps1`**: čistý překlad ze zdrojáků, hra dojede `--sim`, vykreslí `--shots` a v otevřeném okně se sama odehraje `--uitest`; 17 kontrol, každá PASS nebo FAIL. Běh 23. 11. 2026: viz [TESTY.md](TESTY.md).
- **Ruční testovací scénář** (aspoň 12 kroků s očekávaným výsledkem) v [TESTY.md](TESTY.md).
- **Přepínače hry** `--sim`, `--uitest`, `--shots`, `--audio`, které zároveň slouží jako nástroje pro vývoj.

Měření vyváženosti botem (běh 23. 11. 2026, 30 her na obtížnost): Lehká 30 z 30 vyhraných, Střední 26 z 30, Těžká 11 z 30. Cíl ze `SPEC.md`: Lehká skoro vždy, Střední 70 až 90 %, Těžká třetina až polovina; měření v těchto mezích je. Ve volné hře bot, který přestane vylepšovat v 50. kole, prohrál do 62. kola ve všech 26 hrách, které do 50. kola došly, a bot, který přestal v 70. kole, do 80. kola ve všech 7 hrách, které do 70. kola došly (cíl ze `SPEC.md`: aspoň 90 %).

## 9. Omezení a co nebylo ověřeno

- **Ruční hraní.** Michal hru hrál, došel až do volné hry (z toho vzešlo její zpřísnění), ale poslední verzi (štítky u vylepšení a efekt po koupi) podle záznamů nikdo ručně nehrál. Ověřila ji jen kontrola rozložení, snímky obrazovek a test přes okno. Ruční testovací scénář v [TESTY.md](TESTY.md) zatím nikdo neprošel, je to připravený postup k odehrání.
- **Hudba a zvuky.** Podle záznamů hudbu ani nové zvuky nikdo neposlouchal, jen se změřily. Při automatických testech jsou zvuky vypnuté.
- **Vyváženost** je změřená jen botem. Bot je rozumný, ale není člověk, takže skutečná obtížnost pro lidi je odhad.
- **Kontroly agentů nejsou v repozitáři.** Kontroly pravidel (30, 228, 453 testů), kontrola legend, kontrola rozložení a podobné programy, o kterých záznamy mluví, nebyly odevzdány spolu se zdrojáky, takže je nejde znovu spustit. V repozitáři je jen `run-tests.ps1` a přepínače hry. Čísla z těchto kontrol jsou proto citována ze záznamů o vývoji, ne z běhu popsaného v [TESTY.md](TESTY.md).
- **Cizí počítač.** Že je hra spustitelná podle README, ověřuje `run-tests.ps1` čistým překladem do dočasné složky a spuštěním přepínačů. Na jiném fyzickém počítači, na jiné verzi Windows ani dvojklikem přes SmartScreen se hra neověřovala. Testy běžely na Windows 11 (sestavení 26100) s 12 logickými procesory.
- **Platforma.** Jen Windows (Windows Forms, `winmm.dll`) a jen česky. Překlad ze zdrojů potřebuje 64bitové Windows, protože `build.ps1` volá `Framework64\v4.0.30319\csc.exe`.
- **Výkon.** Test přes okno měří dobu vykreslení snímku (v běhu 23. 11. 2026 v průměru 6,01 ms, nejvíc 49,75 ms), ale ne na pomalém počítači.
- **Uložení hry** není. Rozehranou hru nejde uložit ani načíst, ukládají se jen nastavení a nejlepší kola.
- **Uložené rekordy z doby před opravou.** Do 23. 11. 2026 se po prohře počítalo prohrané kolo jako přežité (opraveno, viz [TESTY.md](TESTY.md), část „Opravená odchylka“), takže nejlepší kola uložená dřív v `%APPDATA%\KockyVsVysavace\nastaveni.txt` mohou být o 1 vyšší.
