KONCEPT: sestaveno ze záznamů sezení, student ho má projít a přepsat do svých slov.

# Retrospektiva: Kočky vs. vysavače

Michal Skřipský. Všechna fakta jsou ze záznamů o vývoji, ze `zdrojaky\SPEC.md` a z běhu testů 23. 11. 2026. Návrhy na konci jsou odvozené z těchto faktů a čekají na tvé potvrzení.

## Kde mi AI pomohla

- **Rychlost a rozsah.** Celá hra vznikla 12. 11. 2026: přibližně 9 600 řádků C#, šest koček s 90 vylepšeními, 11 druhů vysavačů, 40 kol, vlastní kreslení a hudba. Šlo to hlavně proto, že po napsání smlouvy `SPEC.md` pracovali čtyři agenti souběžně, každý na svých souborech.
- **Kontroly psala také AI.** Kontroly pravidel (30, potom 228 a 453 testů), simulace bota, která měří vyváženost, snímky obrazovek a test, při kterém se okno hry samo ovládá. Bot ukázal například, že po zpřísnění volné hry se obrana, která přestane růst v 50. kole, zhroutí do 62. kola.
- **Rychlé zapracování připomínek.** Michalovy věty („nevidím, co to dopředu dá za bonus“, „udělej to ještě promakanější“) se do hry dostaly ještě téhož dne.

## Kde mi AI překážela

- **Vylepšení „naslepo“.** První verze nedala vidět, co vylepšení udělá. Oprava vyžadovala tři další úpravy: okno všech vylepšení, popis na tlačítku a štítky s ikonkami.
- **Chyby, které našel až člověk.** Příliš lehkou volnou hru poznal Michal, když ji hrál do 70. kola. Čísla výdrže a pruh zdraví bosse také nechtěl on.
- **„Ověřeno“ znamenalo automaticky.** U každé změny záznam říká „Ručně to nikdo nehrál“ a hudbu ani nové zvuky nikdo neposlouchal.
- **Výstup agenta potřeboval kontrolu.** U poslední úpravy musel hlavní Claude opravit 3 drobnosti.
- **Kontroly agentů nezůstaly.** Programy, které počítaly 453 testů a 474 stavů panelu, nejsou součástí odevzdané složky, takže je nejde znovu spustit.

## Co bych udělal jinak

1. *(návrh, potvrď nebo uprav)* Po každé větší změně zahrát aspoň jedno kolo ručně a výsledek si zapsat. Chyby, které záznamy uvádějí (vylepšení naslepo, příliš lehká volná hra), objevil člověk, ne automatická kontrola, a u poslední úpravy stojí „Ručně to nikdo nehrál“.
2. *(návrh, potvrď nebo uprav)* Hned od začátku ukládat kontrolní programy do repozitáře (`tests\`) a dělat commity po každé iteraci. Složka hry nebyla git repozitář, dokud se nepřipravovalo odevzdání, a kontrolní programy agentů nejsou v odevzdané složce.
3. *(návrh, potvrď nebo uprav)* Do smlouvy `SPEC.md` hned napsat, co musí hráč vidět bez najetí myší (u vylepšení cena, popis a čísla), aby se nemusel UI třikrát předělávat.
