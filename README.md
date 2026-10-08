# Zapqio Runner (.NET)

Runner łączy się z instancją **Web** Zapqio przez WebSocket i wykonuje zlecane przez nią zadania.
Działa jako usługa Windows. Paczki: [GitHub Releases](https://github.com/zapqio/dotnet-runner/releases).

Dalsza dokumentacja:

- [Obsługa runnera](./docs/szczegoly.md) — konfiguracja, usługa, logi, aktualizacja, problemy,
- [Wdrożenia z platformy](./docs/wdrozenia.md) — zatwierdzanie wdrożeń modułów (`deploy`),
- [Pisanie modułów](./docs/moduly.md) — jak dostarczyć runnerowi metody.

## Jak to działa

- Runner łączy się z `{Url}/ws-runner`, loguje tokenem i nazwą, ogłasza swoje metody, a potem
  odbiera zadania i odsyła wyniki. Niczego nie nasłuchuje — wystarczy ruch wychodzący.
- **Metody dostarczają moduły** — paczki `.zip` w katalogu `Modules`. Bez modułów runner się
  połączy, ale nie będzie miał nic do zrobienia.
- Domyślnie wykonuje jedno zadanie naraz (zmienisz to kluczem `MaxConcurrency`). To, co metoda
  wypisze, trafia na żywo do logów zadania w panelu Web.
- Zerwane połączenie odnawia sam (co 3 s → 60 s). Wynik, którego nie zdążył odesłać, wysyła po
  ponownym połączeniu.

## Wymagania

- **Windows x64** — Windows 10 (1607+), Windows 11 lub Windows Server 2012 R2+.
- **.NET Desktop Runtime 8 (x64)** — `winget install Microsoft.DotNet.DesktopRuntime.8`.
  Wersja *Desktop* jest konieczna; sam .NET Runtime nie wystarczy.
- Uprawnienia administratora.
- Dostęp wychodzący do instancji Web (`wss://`, zwykle TCP 443).

> **.NET 10:** `install.ps1 -Net10` instaluje wariant pod .NET Desktop Runtime 10
> (`winget install Microsoft.DotNet.DesktopRuntime.10`). Domyślny jest .NET 8, bo moduł nexo
> korzysta z bibliotek InsERT-a, które od .NET 9 się nie ładują. Bez takich modułów możesz
> wybrać `-Net10`.

## Instalacja

### 1. Przygotuj nazwę instancji i token

**Nazwa instancji** to ostatni segment adresu Twojej instancji Web: dla
`https://app.zapq.io/testowa-instancja` jest to `testowa-instancja`.

![Nazwa instancji wybierana przy zakładaniu instancji](docs/screenshots/1.nazwa-instancji.png)

**Token** wygenerujesz w panelu Web: **Runnery** → **Dodaj**.

![Sekcja Runnery i przycisk Dodaj w panelu Web](docs/screenshots/2.dodawnie-runnera.png)

Nadaj runnerowi nazwę i **skopiuj token przed kliknięciem Zapisz** — później nie da się go już
odczytać, można tylko wydać nowy.

![Formularz dodawania runnera — skopiuj token przed zapisaniem](docs/screenshots/3.konfig-runnera.png)

Runner pojawi się na liście z białą nazwą — jeszcze nic się z nim nie połączyło.

![Lista runnerów po dodaniu — runner jeszcze niepołączony](docs/screenshots/4.po-dodaniu.png)

### 2. Uruchom instalator

W PowerShellu **jako administrator**:

```powershell
$s = irm https://raw.githubusercontent.com/zapqio/dotnet-runner/main/install.ps1
& ([scriptblock]::Create($s.TrimStart([char]0xFEFF)))
```

Skrypt zapyta o nazwę instancji, token i nazwę runnera (Enter = wygenerowany UUID). Możesz je też
podać od razu: dopisz `-Instance moja-instancja -Token <token>` na końcu drugiej linii.
Z klonu repozytorium: `.\install.ps1 -Instance moja-instancja -Token <token>`.

![Przebieg instalacji w konsoli](docs/screenshots/5.instalacja.png)

Instalator pobiera paczkę, zapisuje `appsettings.json`, zakłada usługę `ZapqioRunner` i czeka, aż
runner połączy się z Web. Po udanej instalacji nazwa runnera w panelu robi się zielona.

![Runner połączony — nazwa w panelu na zielono](docs/screenshots/6.po-instalacji.png)

### Parametry instalatora

Wszystkie są opcjonalne.

| Parametr | Opis |
| --- | --- |
| `-Instance` | Nazwa instancji; adres `wss://app.zapq.io/<nazwa>` trafia do `Url`. |
| `-Url` | Pełny adres zamiast `-Instance`, np. lokalne `ws://…`. |
| `-Token`, `-RunnerName` | Token i nazwa runnera (`Token`, `Name`). |
| `-MaxConcurrency` | Ile zadań naraz (`MaxConcurrency`). |
| `-LogLevel`, `-LogDirectory` | Poziom i katalog logów. Nie wyłączaj logów plikowych (`""`) — w usłudze to jedyny lokalny ślad. |
| `-Version` | Konkretna wersja, np. `0.1.1`; domyślnie najnowsza. |
| `-InstallDir` | Katalog instalacji, domyślnie `C:\zapqio\runner`. |
| `-ServiceName` | Nazwa usługi, domyślnie `ZapqioRunner`. |
| `-Net10` | Wariant pod .NET 10. |
| `-LocalSystem` | Usługa na koncie LocalSystem zamiast konta wirtualnego. |

Klucze konfiguracji opisuje [Obsługa runnera](./docs/szczegoly.md#konfiguracja).

### Co jeszcze ustawia instalator

- **Konto usługi** `NT SERVICE\ZapqioRunner` (konto wirtualne, bez hasła) z prawem zapisu tylko
  do katalogu instalacji. Jeśli moduły potrzebują zasobów sieciowych lub logowania Windows do bazy,
  zmień je na konto domenowe:
  `sc.exe config ZapqioRunner obj= "DOMENA\konto" password= "..."`.
- **Restart po awarii** procesu: po 5 s, 30 s i 60 s. Zerwanego połączenia to nie dotyczy —
  runner odnawia je sam.
- **Ograniczony dostęp** do `appsettings.json` i `Config\` (zawierają sekrety): tylko SYSTEM,
  administratorzy i konto usługi.

Ponowne uruchomienie instalatora aktualizuje runnera i zachowuje konfigurację.

## Odinstalowanie

```powershell
sc.exe stop ZapqioRunner
sc.exe delete ZapqioRunner
Remove-Item -Recurse -Force C:\zapqio\runner   # opcjonalnie; katalog zawiera token
```

W panelu Web usuń runnera albo unieważnij jego token — dopóki token jest ważny, można go użyć na
innej maszynie.
