# Obhajoba: Kočky vs. vysavače

Podklady na jednu stranu. Odpovědi vycházejí z kódu a ze záznamů o vývoji, uprav je do svých slov. Podrobnosti: [DESIGN.md](DESIGN.md), [TESTY.md](TESTY.md), [DENIK-SELHANI-AI.md](DENIK-SELHANI-AI.md).

## Dvouminutové shrnutí

Kočky vs. vysavače je tower defense pro Windows. Robotické vysavače jedou po koberci z nabíječky k pelíšku a každý, který tam dojede, mi vezme životy. Po obýváku rozmísťuji kočky (šest druhů) a za pamlsky z rozbitých vrstev je vylepšuji. Každá má tři cesty po pěti stupních, vylepšovat jdou nejvýš dvě a jen jedna nad 2. stupeň; legendu (5. stupeň) smí mít na cestě jen jedna kočka stejného druhu. Hra má 40 kol, tři obtížnosti, tiché a pancéřované vysavače, bossy a po 40. kole volnou hru.

Je to jedna aplikace v C# 5 (Windows Forms, GDI+), přeložená překladačem, který je ve Windows, bez instalací a stahování. Obrázky se kreslí a hudba se syntetizuje v kódu. Má osm souborů a asi 9 600 řádků. Kód psali agenti Claude podle mého zadání a smlouvy `SPEC.md`, já jsem zadával, zkoušel a vracel připomínky. Testuje se skriptem `tests\run-tests.ps1`: čistý překlad, bot hrající celé hry, vykreslení obrazovek a okno, které se samo ovládá. Neověřené je ruční hraní poslední verze a hudba.

## Otázky a odpovědi

1. **Proč C# 5, Windows Forms a GDI+?** `SPEC.md` povoluje jen překladač z Windows (`csc.exe`), bez NuGetu a stahování, aby se hra dala sestavit i spustit na cizím počítači. Cena: C# 5 nemá interpolaci řetězců ani `?.`.
2. **Jaká je architektura?** `Defs` a `Kit` sdílená data a kreslení, `Core` simulace, `Bot` automatický hráč, `Art` kreslení, `Audio` zvuk, `Ui` okno a vstup. `Core` nezná okno, `Art` nezná `Game`, `Ui` je spojuje.
3. **Jak funguje praskání vrstev?** Zásah odloupne vrstvu a vysavač se změní v děti (Raketa → Sprint → Turbo → Plus → Robík). Vrstva dá 1 pamlsek. Když dojede, stojí 1 plus součet dětí (Dvojče 11 životů, Obr 757; `Game.LeakCost`).
4. **Jak fungují vylepšení?** 90 záznamů v `Defs.cs` (6 koček × 3 cesty × 5 stupňů), každý mění čísla nebo dává schopnost. `Defs.CanRaise` povolí 5-2-0, zakáže 3-3-0; `Game.UpgradeBlock` hlídá i legendu.
5. **Proč pevný krok 1/60 s?** Rychlost 2× a 3× je jen víc kroků na snímek, takže hra dopadne stejně na rychlém i pomalém počítači. Náhoda je jen z `Random(seed)` v `Game`, proto jde hra zopakovat a změřit botem.
6. **Jak jste hru testovali?** `run-tests.ps1`: čistý překlad, `--sim` (bot, 3 × 30 her a volná hra), `--shots` (21 snímků), `--uitest` (okno se ovládá samo). Běh 23. 11. 2026: 17 PASS, 0 FAIL. K tomu ruční scénář v TESTY.md.
7. **Nepodvádí bot?** Bot volá stejné veřejné API jako okno, včetně pravidla o legendách. Z 30 her vyhrál na Lehké 30, na Střední 26, na Těžké 11, v mezích cílů ze `SPEC.md`. Je to ale bot, ne člověk.
8. **Kde AI chybovala?** Vylepšení byla „naslepo“ (přibylo okno všech vylepšení, popis na tlačítku, štítky), volná hra byla příliš lehká (já jsem kolem 70. kola přestal vylepšovat a obrana držela), čísla výdrže a pruh bosse (nahradily je výztuhy), v poslední úpravě 3 drobnosti a prohrané kolo se počítalo jako přežité (opraveno 23. 11., hlídá to simulace).
9. **Co nebylo ověřeno?** Ruční hraní poslední verze, hudba a nové zvuky, běh na jiném počítači a přes SmartScreen. Kontroly agentů (až 453 testů) nejsou v repozitáři.
10. **Co by se dalo zlepšit?** *(návrh, potvrď nebo uprav)* Ukládání rozehrané hry, testy s jinými hráči, kontroly agentů přenést do `tests\`.
