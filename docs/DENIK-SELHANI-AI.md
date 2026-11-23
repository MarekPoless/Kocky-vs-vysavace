KONCEPT: sestaveno ze záznamů sezení, student ho má projít a přepsat do svých slov.

# Deník selhání AI: Kočky vs. vysavače

Autor: Michal Skřipský. Šest případů: pět z vývoje hry 12. 11. 2026 a šestý z přípravy odevzdání 23. 11. 2026. Všechno je převzaté ze záznamů o vývoji (soubor vaultu `CLAUDE slozka na start\logs\2026-11-11-kontrola-skillu-graphify-pamet.md`, oddíly o Kočkách) a ze `zdrojaky\SPEC.md`. Kde záznam neříká, jak se chyba poznala nebo opravila, stojí „v logu není“ a nic není domyšleno. Než to odevzdáš, doplň z paměti, co víš ty (hlavně u „Jak jsem to poznal“).

## Případ 1: Vylepšení byla „naslepo“

- **Co jsem chtěl:** Vylepšení koček a vlastní dosah útoku „jak v Bloons TD 6“. U druhé úpravy navíc vidět dopředu, co z kočky bude.
- **Co AI udělala špatně:** Po první verzi se třemi cestami po pěti stupních byla vylepšení „naslepo“: hráč neviděl, co kočka dostane a co přijde dál na cestě. Podle `SPEC.md` (oddíl Round 3) přibyl až v této úpravě obrázek kočky před a po vylepšení a celá cesta všech 5 stupňů.
- **Jak jsem to poznal:** Michal hru zkoušel a řekl, že vylepšení jsou naslepo (záznam: „Michal: vylepšení byla „naslepo“, chtěl vidět dopředu, co z kočky bude“; `SPEC.md`: „now it is blind“). Jak přesně hru zkoušel, v logu není.
- **Jak jsem to opravil:** Nové okno se všemi vylepšeními (3 cesty × 5 stupňů, klávesa I nebo „i“ v obchodě) a bublina s porovnáním „teď → potom“ a s celou cestou. Záznam uvádí kontrolu: 453 testů pravidel, strom vylepšení 90 z 90, 19 snímků, test přes okno 2 hry / 40 kol.
- **Zdroj:** vault, `logs\2026-11-11-kontrola-skillu-graphify-pamet.md`, oddíl „Kočky: celá cesta vylepšení, těžší volná hra, výdrž na vysavačích (2026-11-12)“; `zdrojaky\SPEC.md`, oddíl „Round 3 (2026-11-12)“, část „Upgrade visibility“.

## Případ 2: Co vylepšení dá, nebylo vidět na tlačítku

- **Co jsem chtěl:** Při nákupu vylepšení hned vidět, jaký bonus dá. Michal: „nevidím, co to dopředu dá za bonus“.
- **Co AI udělala špatně:** Popis a čísla byly jen v bublině po najetí myší. Kdo kupuje klávesami Z, X, C nebo rovnou kliknutím, je neviděl.
- **Jak jsem to poznal:** Michal to napsal při zkoušení hry (citát výše). Jak se k tomu dostal, v logu není.
- **Jak jsem to opravil:** Tlačítko vylepšení teď ukazuje název, cenu a klávesu, pod tím popis dalšího stupně (nejvýš 2 řádky) a u krátkých popisů první změnu čísla; tečky stupňů jsou malé pod ikonou. Záznam uvádí kontrolu: všech 90 popisů se vejde celých, jména se vejdou ve všech stavech, 19 snímků, test přes okno 2 hry / 40 kol. Na další žádost „udělej to ještě promakanější“ přibyly štítky s ikonkami pro všechny změny čísel a efekt po koupi nad kočkou (viz případ 5).
- **Zdroj:** vault, `logs\2026-11-11-kontrola-skillu-graphify-pamet.md`, oddíly „Kočky: co vylepšení dá, je vidět přímo na tlačítku (2026-11-12)“ a „Kočky: štítky s ikonkami u vylepšení a efekt po koupi (2026-11-12)“.

## Případ 3: Volná hra byla příliš lehká

- **Co jsem chtěl:** Po 40. kole volnou hru, která se stále zesiluje (`SPEC.md`: „rounds after 40 are generated and keep getting harder“).
- **Co AI udělala špatně:** Volná hra nerostla dost rychle. Michal přestal vylepšovat kolem 70. vlny a kočky pořád vydržely.
- **Jak jsem to poznal:** Michal hrál až do volné hry a viděl, že bez dalšího vylepšování obrana drží (záznam i `SPEC.md`: „Michal played into free play“). Kolikrát a jak dlouho, v logu není.
- **Jak jsem to opravil:** Výdrž Keramiků roste ×1,08 za kolo, výdrž bossů ×1,10, rychlost až ×1,6, odměny klesají a přibývá Obrů. Nové cíle pro bota: kdo přestane vylepšovat v 50. kole, prohraje do 62. kola, kdo přestane v 70., do 80. Při běhu `tests\run-tests.ps1` 23. 11. 2026 to platilo ve 26 z 26 her (kolo 50) a v 7 ze 7 her (kolo 70).
- **Zdroj:** vault, `logs\2026-11-11-kontrola-skillu-graphify-pamet.md`, oddíl „Kočky: celá cesta vylepšení, těžší volná hra, výdrž na vysavačích (2026-11-12)“; `zdrojaky\SPEC.md`, oddíl „Round 3“, část „Free play scaling“.

## Případ 4: Čísla výdrže a pruh zdraví bosse

- **Co jsem chtěl:** Aby síla vysavačů v pozdní hře byla vidět na nich samých, ne v číslech. Michal: „nedávej tam, kolik má HP, ať to jde vidět na něm“ a „nechci, aby byl nahoře HP bar“.
- **Co AI udělala špatně:** Hra ukazovala čísla výdrže v okně „Další kolo“ a nahoře pruh se zdravím bosse. Záznam uvádí, že po úpravě zmizela. Zda to Michal zadal výslovně, nebo to AI přidala sama, v logu není.
- **Jak jsem to poznal:** Michal to při zkoušení odmítl (citáty výše). Jak přesně, v logu není.
- **Jak jsem to opravil:** Výdrž je vidět přímo na vysavači: čtyři stupně výztuh (ocelový pás s nýty, silnější pás se šrouby, tmavé pláty, pláty se žhavými švy) a praskliny navrchu. Čísla z okna „Další kolo“ i horní pruh bosse jsou pryč. Záznam uvádí kontrolu: 453 testů pravidel, 19 snímků, test přes okno 2 hry / 40 kol.
- **Zdroj:** vault, `logs\2026-11-11-kontrola-skillu-graphify-pamet.md`, oddíl „Kočky: celá cesta vylepšení, těžší volná hra, výdrž na vysavačích (2026-11-12)“; `zdrojaky\SPEC.md`, oddíl „UI and Program“ (věta o pruhu zdraví bosse).

## Případ 5: Tři drobnosti v kódu agenta

- **Co jsem chtěl:** Štítky s ikonkami u všech změn čísel u vylepšení a efekt po koupi (Michal: „udělej to ještě promakanější“).
- **Co AI udělala špatně:** Agent na modelu Opus napsal úpravu, ale v jeho kódu byly tři drobnosti, které bylo potřeba opravit. Které to byly, v logu není.
- **Jak jsem to poznal:** Hlavní Claude prošel celý rozdíl v kódu (záznam: „hlavní Claude prošel celý rozdíl v kódu a opravil 3 drobnosti“).
- **Jak jsem to opravil:** Hlavní Claude těch 3 drobností opravil. Záznam uvádí kontrolu: rozložení a čísla ve všech 474 stavech koček (nic se neusekne, štítky sedí s čísly v bublině), 21 snímků, test přes okno 2 hry / 70 kol. Co přesně se opravilo, v logu není.
- **Zdroj:** vault, `logs\2026-11-11-kontrola-skillu-graphify-pamet.md`, oddíl „Kočky: štítky s ikonkami u vylepšení a efekt po koupi (2026-11-12)“.

## Případ 6: Prohrané kolo se počítalo jako přežité

- **Co jsem chtěl:** Aby závěrečné okno po prohře ukázalo, kolik kol jsem přežil, tedy dokončil. Kód to tak zamýšlel: rozehrané kolo se nepočítá (`Round - 1`).
- **Co AI udělala špatně:** Okno (`Ui.Survived`) odečítalo 1 jen tehdy, když kolo běží (`RoundActive`), jenže při prohře hra kolo vypne. Po prohře ve 2. kole proto okno ukázalo „Přežitá kola 2“ a stejné číslo se uložilo jako nejlepší výsledek, ačkoli se dokončilo jen jedno kolo.
- **Jak jsem to poznal:** Při hraní jsem si toho nevšiml. Našel to pomocný program, který Claude (Sonnet) napsal při přípravě odevzdání: zahrál prohru přes veřejné rozhraní `Ui` a `Game` a číslo se nezhodovalo s tím, co mělo okno ukázat. Žádný z dosavadních testů to neodhalil (snímek `08-lost` z `--shots` prohru nastavuje uměle, bez vypnutí kola).
- **Jak jsem to opravil:** Počítání je teď ve `Game.RoundsSurvived` (běžící ani prohrané kolo se nepočítá) a okno ho jen zobrazuje. Hlídá to simulace: před opravou hlásila u každé prohrané hry „rounds survived is 29 after round 29 (lost)“, po opravě `total exceptions: 0`, a skript `tests\run-tests.ps1` na kopii s vrácenou chybou skončil s FAIL (113 výjimek). Okno po prohře ve 2. kole na Těžké ukazuje 1 (před opravou 2).
- **Zdroj:** příprava odevzdání 23. 11. 2026; commity „fix: …“ v tomto repozitáři; `docs\TESTY.md`, část „Opravená odchylka“.

## Poznámky

- **Co jsem nepoužil.** Omezení jazyka C# 5 je ve `SPEC.md` pravidlo (oddíl Technology), ale záznam nezaznamenává, že by kvůli němu AI chybovala, takže to není případ. Stejně tak v záznamech není doložená chyba, kterou by odhalil test nebo snímek obrazovky.
- **Omezení ověřování.** Záznam u každé změny uvádí „Ručně to nikdo nehrál“ a u zvuků, že je nikdo neposlouchal. To není selhání AI, ale hranice toho, co je ověřeno (viz [DESIGN.md](DESIGN.md), kapitola 9).
