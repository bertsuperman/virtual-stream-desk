# Virtual Stream Deck

Jednoduchý virtuálny streamdeck pre **Windows 10/11 a macOS** (Apple Silicon aj Intel). Malé okno s mriežkou tlačidiel, globálne klávesové skratky, ktoré fungujú aj vtedy, keď je okno skryté, a ovládanie z mobilu cez QR kód.

## ⬇ Stiahnuť
| Systém | Súbor |
|---|---|
| Windows | [VirtualStreamDeck.exe](https://github.com/bertsuperman/virtual-stream-desk/releases/download/latest/VirtualStreamDeck.exe) |
| Mac s Apple Silicon (M1–M4) | [VirtualStreamDeck-mac-arm64.zip](https://github.com/bertsuperman/virtual-stream-desk/releases/download/latest/VirtualStreamDeck-mac-arm64.zip) |
| Mac s procesorom Intel | [VirtualStreamDeck-mac-x64.zip](https://github.com/bertsuperman/virtual-stream-desk/releases/download/latest/VirtualStreamDeck-mac-x64.zip) |

**Windows:** spusti .exe, nič sa neinštaluje. Pri prvom spustení SmartScreen: *Ďalšie informácie → Spustiť aj tak*.

**Mac:** rozbaľ zip a presuň *Virtual Stream Deck* do **Aplikácií** (z priečinka Stiahnuté by sa nevedel sám aktualizovať). Appka nie je podpísaná od Apple, takže pri prvom spustení ju Mac zablokuje: otvor *Nastavenia systému → Súkromie a bezpečnosť* a dole klikni na **Otvoriť aj tak**. Na posielanie kláves a ovládanie hlasitosti sa Mac raz opýta na povolenie **Prístupnosť**.

Ďalšie verzie si appka stiahne sama: zelená šípka v pruhu s ikonami alebo *Nastavenia → Všeobecné → Aktualizovať*. Takto stiahnutá verzia sa spustí bez varovania SmartScreen / Gatekeeper.

## Ovládanie
| Čo | Ako |
|---|---|
| Pridať tlačidlo | pretiahni program / appku, odkaz, súbor, priečinok alebo URL z prehliadača na mriežku, alebo klikni na prázdne políčko |
| Upraviť / vymazať | pravý klik na tlačidlo |
| Presunúť | ťahaj tlačidlo na iné políčko (obsadené políčka sa vymenia) |
| Nastavenia, profily, návod | pruh s ikonami otvorí bočný panel vľavo (Profily, Rozloženie, Všeobecné, Mobil, OBS, Návod); šípka ho zbalí |
| Schovať / ukázať | klik na ikonu v lište vedľa hodín (Windows) / v lište s menu (Mac) |
| Ukončiť | zatvorenie okna (s potvrdením) alebo ikona v lište → Ukončiť |

Akcie: **Otvoriť** (program / appka / súbor / priečinok / web), **Klávesová skratka** (pošle sa do appky, v ktorej pracuješ), **Médiá** (play/pauza, ďalšia, predošlá, stop, hlasitosť, mute), **OBS Studio**, **Prepnúť profil**.

Na Windowse okno decku nekradne focus. Mac to nedovolí, preto si appka pamätá, kde si naposledy pracoval, a pred odoslaním skratky sa tam vráti. Na Macu je modifikátor *Win* klávesa **⌘** a *Alt* je **⌥**.

Nový deck začína piatimi tlačidlami na zvuk (play/pauza, ďalšia, tichšie, hlasnejšie, stlmiť). Rovnakú sadu pridá *Profily → Profil so zvukom*. Appka je po slovensky aj anglicky (*Nastavenia → Všeobecné → Jazyk*), na mobile sa jazyk prepne tiež.

## Profily
Každý profil má vlastnú mriežku a tlačidlá; veľkosť tlačidiel, medzery a popisky sú spoločné. Prepínanie: bočný panel → Profily, výber v mobile alebo tlačidlo s akciou **Prepnúť profil** (ako priečinky na skutočnom Stream Decku). Globálne skratky patria k profilu.

## Ovládanie z mobilu
1. **Nastavenia → Mobil** → *Povoliť ovládanie z mobilu*.
2. Naskenuj QR kód fotoaparátom mobilu. Ťuknutie stlačí tlačidlo na počítači.
3. Pre celú obrazovku daj v prehliadači *Pridať na plochu* (iPhone: Zdieľať → Pridať na plochu, Android: ⋮ → Pridať na plochu). Ikona na ploche sa pripojí bez ďalšieho skenovania.

Mobil musí byť v rovnakej Wi-Fi. QR kód obsahuje tajný kľúč, *Nový kód* všetky mobily odpojí. Server beží na porte 8765 bez HTTPS, je určený do domácej siete.

## OBS Studio
1. V OBS: **Nástroje → Nastavenia WebSocket servera** → *Povoliť WebSocket server*, skopíruj heslo.
2. V Stream Decku: **Nastavenia → OBS** → heslo (adresa `localhost`, port `4455`) → *Otestovať pripojenie*.
3. V editore tlačidla vyber akciu **OBS Studio**: prepnúť scénu, nahrávanie, stream, stlmiť zdroj, zobraziť/skryť zdroj, uložiť replay.

## Zostavenie
Treba [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project tests/VirtualStreamDeck.Core.Tests        # testy jadra
dotnet run --project src/VirtualStreamDeck.App                 # spustí appku (Windows, Mac, Linux na vývoj)
dotnet run --project src/VirtualStreamDeck.App -- --screenshots shots   # vyrenderuje okná do PNG bez obrazovky
```

GitHub Actions (`.github/workflows/build.yml`) pri každom pushi zostaví Windows .exe, Mac .app pre arm64 aj x64 (ad-hoc podpis, zip) a screenshoty UI (vetva `ci-screens`). Push do `main` aktualizuje release **latest** (odtiaľ sa aktualizujú nainštalované appky), iné vetvy release **preview** na testovanie.

## Konfigurácia
Windows: `%AppData%\VirtualStreamDeck\`, Mac: `~/Library/Application Support/VirtualStreamDeck/` – `config.json` a priečinok `icons/`. Na Windowse platí portable režim: ak leží `config.json` vedľa .exe, použije sa ten. Pokazený config sa zálohuje ako `config.broken-*.json`, neočakávané chyby sa zapisujú do `error.log`.

## Štruktúra
```
src/VirtualStreamDeck.Core/     model, config, skratky, akcie (Windows/ a Mac/), OBS, server pre mobil, QR, aktualizácie
src/VirtualStreamDeck.App/      Avalonia UI v C# (UI/), systémové služby (Services/): skratky, focus, lišta, autostart, ikony
tests/VirtualStreamDeck.Core.Tests/   testy jadra (dotnet run, bez NuGet závislostí)
```
