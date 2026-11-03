# CLAUDE.md: hot start pentru iControl

Lucrezi pe proiectul **iControl** (`H:\__Proiecte\iControl`, repo `https://github.com/karen20dec4/iControl`). Citește întâi `README.md`: acolo e documentația completă (arhitectură, chei SMC, taskbar, taste).

Răspunde în **română**, cu diacritice.

## Contextul mașinii

- **IMAC-7700**: iMac 27" 2017 (iMac18,3), i7-7700, Radeon Pro 575, fără cip T2.
- Windows 10 Pro 22H2 (19045), instalat prin Boot Camp.
- Ecran 1920×1080 la 125% (DPI 120). Taskbar jos, tema dark, transparență activă.
- Rulează și **7+ Taskbar Tweaker** (modifică taskbar-ul, posibile interacțiuni cu eticheta noastră).
- Utilizatorul nu rulează de obicei ca administrator, iar iControl nu are nevoie de admin.
- Unelte disponibile: `csc` din .NET Framework 4.8, `dotnet` 10, Python 3.14, `git`, `gh` (autentificat ca `karen20dec4`).

## Reguli de cod

- **Doar C# 5**: compilatorul este `csc.exe` 4.8 din `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`.
  - Interzise: `$"..."`, `?.`, `nameof`, membri `=>`, `out var`, tupluri, `using static`, pattern matching.
- Fără NuGet și fără SDK. Un singur exe, `bin\iControl.exe`, compilat cu `build.cmd`.
- Sursele sunt UTF-8 fără BOM și se compilează cu `/codepage:65001`. Caracterul `°` din cod depinde de asta.
- Stilul codului:
  - Textele din UI și din jurnal sunt în română, **fără diacritice**.
  - Comentariile din `Fan/` sunt în română fără diacritice, iar cele din `Keys/` în engleză. Păstrează stilul fișierului pe care îl editezi.
- Jurnalul se scrie numai prin `AppLog.Write("<zona>", ...)`.
- Un fișier nou din `src/` intră automat în build (`/recurse:src\*.cs`).

## Flux de lucru

1. Editezi în `src/`.
2. Rulezi `deploy.ps1`. Scriptul oprește curat instanța curentă (`--exit`, cu fail-safe pe ventilator), compilează, reînregistrează task-ul `iControl` și pornește aplicația.
3. Verifici:
   - `%LOCALAPPDATA%\iControl\log.txt`;
   - o captură a taskbar-ului (de exemplu `CopyFromScreen` pe zona `0,1030 → 1920×50` în PowerShell, salvată în scratchpad, apoi `Read`);
   - tastele, prin `keybd_event` (F4 urmat de F3, ca să lași volumul cum era).
4. Faci commit pe o ramură sau pe `main`, după ce ai cerut acordul utilizatorului. Faci push doar când ți se cere.

## Siguranță

- **Nu opri iControl cu `Stop-Process`/kill** când ventilatorul e în modul manual. Folosește `bin\iControl.exe --exit`, sau `--auto` după un kill.
- Nu coborî `F0Mn` sub 800 și nu elimina niciuna dintre căile de fail-safe (ieșire, sleep, shutdown, erori de senzori).
- Nu instala drivere terțe (InpOut32, WinRing0, PawnIO, LibreHardwareMonitor). SMC-ul se citește deja prin `\\.\MacHALDriver`.
- Cere confirmare înainte de modificări în registry, servicii, drivere sau de ștergerea de fișiere în afara proiectului.

## Fapte stabilite (nu le mai redescoperi)

- `\\.\MacHALDriver` se deschide fără admin. IOCTL-urile sunt `0x9C402454` (read) și `0x9C402458` (write). Cheile SMC sunt în README.
- LibreHardwareMonitor și TrafficMonitor **nu** pot citi ventilatorul Apple SMC. Nu le propune.
- F12 nu poate fi înregistrată cu `RegisterHotKey`, fiind rezervată pentru debugger, așa că folosește hook-ul de rezervă. E un comportament normal.
- `Bootcamp.exe` e oprit intenționat, pentru că suprascrie luminozitatea manuală.
- Aplicațiile vechi (`iMacFanControl`, `iMacBrightnessKeys`), cu folderele și task-urile lor, au fost șterse pe 2026-11-03. Blocul de migrare din `deploy.ps1` nu mai are efect.
- **F2 este lăsată liberă intenționat**, la cererea utilizatorului. Volumul este pe F3 (−) și F4 (+).

## Idei posibile (doar la cerere)

- Fișier de configurare (JSON) pentru praguri, curbă și formatul textului.
- Tooltip la hover pe eticheta din taskbar, cu min/max din sesiune.
- Suport pentru taskbar vertical.

## Prompt de pornire

> Ești în proiectul iControl. Citește `README.md` și acest fișier, rulează `git status` și `git log --oneline -5`, verifică dacă procesul `iControl` rulează și uită-te la ultimele 10 linii din `%LOCALAPPDATA%\iControl\log.txt`. Apoi spune-mi pe scurt starea și întreabă-mă ce facem azi.
