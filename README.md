# iControl

Utilitar de tavă pentru **iMac 27" 2017 (iMac18,3)** cu **Windows 10 în Boot Camp**. Un singur proces, `iControl.exe`, face trei lucruri:

| Modul | Ce face |
|---|---|
| **Ventilator** (`src/Fan`) | Controlează ventilatorul prin Apple SMC cu o curbă silențioasă (~950 rpm în idle, sub limita de 1200 rpm impusă de SMC) și revine automat pe modul din fabrică la ieșire, sleep, shutdown sau eroare. |
| **Afișaj taskbar** (`src/Fan/TaskbarLabel.cs`) | Afișează în taskbar, lângă ceas: `955rpm \| 61°CPU \| 53°GPU`. Temperaturile devin galbene de la 75° și roșii de la 85°. |
| **Taste** (`src/Keys`) | F1 = mut, F3 / F4 = volum −/+ (F2 rămâne liberă), F11 / F12 = luminozitate −/+, fiecare cu OSD propriu în stil macOS. Cu Shift, tastele F funcționează normal. |

Proiectul a fost creat pe 2026-11-03 prin unificarea a două aplicații separate: `iMacFanControl` și `iMacBrightnessKeys`. Aceasta din urmă conținea deja și tastele de volum.

## Instalare, actualizare, dezinstalare

Nu e nevoie de drepturi de administrator, de SDK sau de NuGet. Se folosește compilatorul C# inclus în .NET Framework 4.8.

```powershell
# compilează, oprește curat instanța veche, (re)înregistrează task-ul de logon și pornește aplicația
H:\__Proiecte\iControl\deploy.ps1

# doar compilare (eșuează dacă iControl rulează, pentru că exe-ul e blocat)
H:\__Proiecte\iControl\build.cmd

# dezinstalare: oprește aplicația (ventilatorul revine pe Automat SMC) și șterge task-ul
H:\__Proiecte\iControl\deploy.ps1 -Uninstall
```

- Pornirea automată se face prin task-ul **`iControl`** din Task Scheduler, la logon, pentru utilizatorul curent, fără elevare.
- Executabilul rulează din `bin\iControl.exe`, folder ignorat de git.
- Argumentele `iControl.exe`:
  - `--exit` închide curat instanța pornită;
  - `--auto` pune SMC pe modul automat;
  - `--status` scrie o citire SMC în `status.txt`.

## Fișiere la rulare (`%LOCALAPPDATA%\iControl\`)

| Fișier | Conținut |
|---|---|
| `log.txt` | Jurnal comun, cu prefixele `[app]`, `[fan]` și `[keys]`. La 1 MB se rotește în `log.old.txt`. |
| `status.txt` | Ultima stare a ventilatorului (rpm, țintă, temperaturi, mod). |
| `level.txt` | Nivelul de luminozitate ales, reaplicat la pornire. |

## Structura codului

```
src/
  Program.cs            Main, iconița și meniul din tavă, --exit/--auto/--status, AppLog
  iControl.manifest     asInvoker, supportedOS Win8+/Win10, DPI PerMonitorV2
  Fan/
    Smc.cs              SmcDevice: IOCTL pe \\.\MacHALDriver (driverul Boot Camp)
    FanModule.cs        curba, media mobilă, fail-safe, tooltip/status, textul din taskbar
    TaskbarLabel.cs     fereastră copil layered în Shell_TrayWnd
  Keys/
    KeysModule.cs       hotkey-uri, worker, luminozitate WMI, oprire Bootcamp.exe
    KeysNative.cs       P/Invoke pentru RegisterHotKey și hook-ul de tastatură
    HotkeyWindow.cs     fereastră message-only care primește WM_HOTKEY
    Audio.cs            Core Audio (IAudioEndpointVolume), fără flyout-ul Windows
    Osd.cs              OSD-ul pentru volum și luminozitate
```

## Detalii tehnice

### Apple SMC
- Accesul se face prin `\\.\MacHALDriver`, driverul instalat de Boot Camp, **fără drivere terțe** (nu e nevoie de InpOut32, WinRing0 sau PawnIO). Dispozitivul se poate deschide și fără drepturi de admin.
- `IOCTL_SMC_READ = 0x9C402454`, `IOCTL_SMC_WRITE = 0x9C402458`. Intrarea are 4 octeți pentru numele cheii, un octet 0 și apoi datele.
- Chei folosite:

  | Cheie | Semnificație |
  |---|---|
  | `F0Ac` | Turația reală (fpe2 = valoare/4) |
  | `F0Tg` | Ținta |
  | `F0Mn` / `F0Mx` | Turația minimă / maximă |
  | `FS! ` | Modul manual, cu spațiu la final (ui16; 1 = manual) |
  | `TC0c`, `TC1c`, `TC2c`, `TC3c`, `TCXc`, `TC0P` | Temperaturi CPU (sp78 = valoare/256) |
  | `TG0D`, `TG0E`, `TG0F`, `TG0P` | Temperaturi GPU |

- **Curba:**
  - În idle (CPU ≤ 60 °C și GPU ≤ 65 °C): `F0Mn = 800`, `FS! = 1`, `F0Tg = 900`, adică ~950 rpm reali.
  - Peste aceste praguri, turația crește liniar de la 940 la 2700 rpm (CPU între 60 și 80 °C, GPU între 65 și 82 °C).
  - Decizia se ia pe o medie mobilă de 12 s.
- **Fail-safe** (`FS! = 0`, `F0Mn = 1200`): se aplică la ieșire, `ProcessExit`, `SessionEnding`, `WM_ENDSESSION`, sleep și după 3 citiri de senzori eșuate la rând.

### Afișajul din taskbar
- Este o fereastră `WS_CHILD | WS_EX_LAYERED` cu părintele `Shell_TrayWnd`, desenată prin `UpdateLayeredWindow` (alpha per pixel, text antialiased).
- Ferestrele copil layered necesită `supportedOS` Win8+ în manifest.
- Ca să facă loc etichetei, lista de butoane `MSTaskSwWClass` (din `ReBarWindow32`) este îngustată.
- Un `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` pe procesul explorer refixează layout-ul imediat după ce explorer îl modifică.
- Dacă explorer repornește, eticheta se reatașează la următorul tick (1 s). La ieșire, lista de butoane primește înapoi lățimea completă.
- Fontul e Segoe UI 9pt, scalat după DPI-ul taskbar-ului. Textul e alb pe tema dark și negru pe light. Click dreapta deschide meniul aplicației.
- Funcționează doar pe taskbar orizontal; pe vertical, eticheta se ascunde.
- Afișajul arată valorile instantanee, nu media folosită de curbă.

### Taste
- Tastele sunt preluate cu `RegisterHotKey` (WM_HOTKEY e pus în coadă de sistem, deci nu se pierd apăsări).
- **F12** e rezervată de kernel pentru debugger, deci folosește un `WH_KEYBOARD_LL` minimal, al cărui callback doar setează un contor și un eveniment.
- Volumul se schimbă prin Core Audio, ca să nu apară flyout-ul Windows. Pasul e de 4%.
- Luminozitatea se schimbă prin WMI (`WmiMonitorBrightness` / `WmiSetBrightness`), cu pas de 5 și frână anti-rafală.
- `Bootcamp.exe` (luminozitatea automată de la senzorul de lumină) este oprit la pornire și verificat la fiecare 20 s, iar nivelul salvat e reaplicat.

## Depanare

| Simptom | Ce verifici |
|---|---|
| Lipsește textul din taskbar | `log.txt` pentru `Eroare afisaj taskbar`, apoi repornește cu `deploy.ps1`. |
| Butoanele aplicațiilor se suprapun peste text | Posibil conflict cu 7+ Taskbar Tweaker; textul se corectează la următorul eveniment sau tick. |
| Ventilatorul rămâne pe manual după un crash | Rulează `bin\iControl.exe --auto`. |
| O tastă nu răspunde | `log.txt` pentru `RegisterHotKey refuzat` sau `hook de rezerva`. |

## Licență

MIT, vezi `LICENSE`.
