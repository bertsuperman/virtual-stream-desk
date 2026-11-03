# Virtual Stream Deck

Jednoduchý virtuálny streamdeck pre Windows 10/11. Malé okno s mriežkou tlačidiel, ktoré nekradne focus, a globálne klávesové skratky, ktoré fungujú aj vtedy, keď je okno skryté.

## Ovládanie
| Čo | Ako |
|---|---|
| Pridať tlačidlo | pretiahni program, odkaz, súbor, priečinok alebo URL z prehliadača na mriežku, alebo klikni na prázdne políčko |
| Upraviť / vymazať | pravý klik na tlačidlo |
| Presunúť | ťahaj tlačidlo na iné políčko (obsadené políčka sa vymenia) |
| Presunúť okno | ťahaj za horný pruh |
| Nastavenia | ozubené koliesko v hornom pruhu alebo ikona v lište (tray) |
| Skryť | `–` v hornom pruhu; okno vrátiš klikom na ikonu v lište |

Akcie: **Otvoriť** (program / súbor / priečinok / web), **Klávesová skratka** (pošle sa do aktívneho okna), **Médiá** (play/pauza, ďalšia, predošlá, stop, hlasitosť, mute).

## Zostavenie
Treba [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Potom v PowerShelli:

```powershell
./build.ps1
```

Výsledok je `publish\VirtualStreamDeck.exe`: jeden súbor, netreba nič inštalovať. Pri každom pushnutí ho zostaví aj GitHub Actions (`.github/workflows/build.yml`, artefakt *VirtualStreamDeck-win-x64*).

Na vývoj stačí otvoriť `VirtualStreamDeck.sln` vo Visual Studiu alebo Rideri.

## Konfigurácia
`%AppData%\VirtualStreamDeck\config.json` a priečinok `icons\`. Ak leží `config.json` vedľa .exe, appka beží v portable režime a použije ten. Formát je popísaný v project knowledge (`03-konfiguracia-a-akcie.md`). Pokazený config sa zálohuje ako `config.broken-*.json` a appka začne nanovo. Neočakávané chyby sa zapisujú do `error.log`.

## Štruktúra
```
src/VirtualStreamDeck.Core/    model, config, skratky, vykonávanie akcií (bez WPF; neskôr ho použije aj server pre mobil)
src/VirtualStreamDeck.App/     WPF: hlavné okno, editor tlačidla, nastavenia, tray, globálne hotkeys, ikony
tests/VirtualStreamDeck.Core.Tests/   testy Core (dotnet run, bez NuGet závislostí)
```

## Známe obmedzenia
- Do aplikácií spustených ako správca nevie poslať klávesy (ochrana Windows / UIPI). Riešenie je spustiť ako správca aj Stream Deck.
- Systémové skratky (Win+L, Ctrl+Alt+Del) sa zaregistrovať nedajú.
- Samostatný kláves bez Ctrl/Alt/Shift (napr. F13) prestane fungovať normálne, kým appka beží.
