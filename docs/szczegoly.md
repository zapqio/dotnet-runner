# Obsługa runnera

Instalacja jest w [README](../README.md), pisanie modułów w [moduly.md](./moduly.md),
wdrożenia modułów z platformy w [wdrozenia.md](./wdrozenia.md).

## Konfiguracja

Plik `appsettings.json` w katalogu instalacji. Zmienne środowiskowe `ZAPQIO_*` mają pierwszeństwo.
Po zmianie zrestartuj usługę.

```json
{
  "Logger": { "LogLevel": "Information", "PathDirectory": "Logs" },
  "Token": "<TOKEN-Z-PANELU-WEB>",
  "Name": "moj-runner-01",
  "Url": "wss://app.zapq.io/moja-instancja",
  "MaxConcurrency": 1
}
```

| Klucz | Zmienna | Opis |
| --- | --- | --- |
| `Url` | `ZAPQIO_URL` | Adres instancji, np. `wss://app.zapq.io/moja-instancja`, **bez** `/ws-runner`. |
| `Token` | `ZAPQIO_TOKEN` | Token z panelu Web. |
| `Name` | `ZAPQIO_NAME` | Nazwa runnera. Pusta = UUID zapisany w pliku `##Name`. Przy pierwszym połączeniu wiąże się z tokenem i **nie może się już zmienić** (inaczej `401`). |
| `MaxConcurrency` | `ZAPQIO_MAX_CONCURRENCY` | Ile zadań naraz (domyślnie `1`, maks. 32). Zwiększ tylko, jeśli moduły to obsługują. |
| `Logger:LogLevel` | `Logger__LogLevel` | `Verbose`, `Debug`, `Information` (domyślnie), `Warning`, `Error`, `Fatal`. |
| `Logger:PathDirectory` | `Logger__PathDirectory` | Katalog logów (domyślnie `Logs`); pusty wyłącza logi plikowe. |
| `MinRemoteLogLevel` | `ZAPQIO_MIN_LOG_LEVEL` | Od jakiego poziomu logi modułów idą do Web: `Debug`, `Info` (domyślnie), `Warning`, `Error`, `Critical`. |
| `StopTimeoutSeconds` | — | Ile sekund przy zatrzymaniu czekać na trwające zadania (domyślnie `30`). |
| `MaxQueuedLogLines` | — | Ile linii logu buforować, gdy Web jest niedostępny (domyślnie `10000`). |

**Test w konsoli.** Uruchamiaj runnera ręcznie zawsze z katalogu instalacji — inaczej nie znajdzie
`appsettings.json`:

```powershell
cd C:\zapqio\runner
.\Zapqio.Runner.exe
```

## Zarządzanie usługą

```powershell
sc.exe query ZapqioRunner      # status
sc.exe stop ZapqioRunner
sc.exe start ZapqioRunner
Restart-Service ZapqioRunner   # po zmianie konfiguracji lub modułów
```

## Logi

- **Pliki** w `Logs\` (dzienne, max 200 MB). W usłudze to jedyny lokalny log — runner nie pisze
  do Dziennika zdarzeń Windows.
- **Logi zadań** widać w panelu Web i w pliku (źródło `Method(<nazwa>)`, ID zadania na początku linii).

Na co patrzeć w logu:

| Wpis | Znaczenie |
| --- | --- |
| `Successfully connected to WebSocket at …` | Połączono z Web. |
| `Add method: <typ> in module: <moduł>` | Załadowano metodę. |
| `Wysyłam Info: N metod: …` | Lista metod wysłana do Web. |
| `Kolejna próba połączenia za <n>s …` | Brak połączenia; runner ponawia sam. |
| `Serwer odrzucił uzgadnianie ze statusem <kod>` | Odmowa — patrz kod w tabeli niżej. |

## Aktualizacja

Uruchom ponownie [`install.ps1`](../install.ps1) — pobierze najnowszą wersję i zachowa konfigurację,
moduły i tożsamość runnera.

Przy ręcznej podmianie plików **nie usuwaj** `appsettings.json`, `##Name` (bez niego Web odrzuci
runnera kodem `401`), `Modules\`, `Config\`, `Deployments\`, `Build\` ani `Logs\`.
`.modulesCache\` można skasować.

Zainstalowana wersja:

```powershell
(Get-Item C:\zapqio\runner\Zapqio.Runner.exe).VersionInfo.ProductVersion
```

## Rozwiązywanie problemów

| Objaw | Co zrobić |
| --- | --- |
| Usługa nie startuje, w Dzienniku zdarzeń `You must install .NET…` lub `Microsoft.WindowsDesktop.App … not found` | Zainstaluj .NET **Desktop** Runtime x64 w wersji paczki (8, a przy `-Net10` — 10). Sprawdź: `dotnet --list-runtimes`. |
| Usługa nie startuje z innego powodu | Uruchom `Zapqio.Runner.exe` ręcznie z katalogu instalacji i przeczytaj błąd. |
| Usługa zatrzymała się i nie wróciła | Po nieobsłużonym błędzie runner kończy się kodem 0, którego Windows nie traktuje jako awarii. Sprawdź log i uruchom usługę. |
| `401` | Zły token, nazwa albo instancja w `Url`. Sprawdź, czy nie zniknął plik `##Name`. |
| `404` lub strona HTML | Zły segment instancji w `Url` albo `Url` kończy się na `/ws-runner`. |
| `426 Upgrade Required` | Runner jest za stary dla tej instancji — zaktualizuj go. |
| `429` | Web ogranicza liczbę połączeń; runner odczeka sam. Jeśli się powtarza, sprawdź, czy z tego adresu nie łączy się wiele runnerów. |
| Runner w panelu bez metod | Sprawdź w logu `Katalog modułów:` i listę paczek. `Brak dostępu do katalogu modułów` → `icacls C:\zapqio\runner\Modules /reset /T` (zdarza się po **przeniesieniu** katalogu z profilu użytkownika). `Nie udało się załadować <dll>` → zła wersja .NET modułu albo blokada WDAC / Smart App Control. `Metoda … nie została utworzona` → zwykle brak modułu współdzielonego. |
| `Not found method: <nazwa>` | Moduł się nie załadował (restart po dodaniu paczki?) albo nazwa metody nie zgadza się z pipeline. |
| `Failed to send JobReturn … lost` | Połączenie padło przed odesłaniem wyniku. Przy dużych wynikach sprawdź limit 32 MiB. |
