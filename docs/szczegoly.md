# Zapqio Runner — szczegóły

Uzupełnienie głównego [README](../README.md): wymagania, instalacja i odinstalowanie są tam,
tutaj tematy eksploatacyjne — konfiguracja, moduły, zarządzanie usługą, logi, aktualizacja
i rozwiązywanie problemów.

### 1. Konfiguracja

Konfiguracja w pliku `appsettings.json` obok binarki lub przez zmienne środowiskowe. Zmienne
`ZAPQIO_*` mają pierwszeństwo przed wartościami z pliku.

| Klucz (`appsettings.json`) | Zmienna środowiskowa | Opis |
| --- | --- | --- |
| `Token` | `ZAPQIO_TOKEN` | Sekretny token runnera, wydany przez Twoją instancję Web. Web przechowuje wyłącznie skrót Argon2, więc jawną wartość widać tylko w chwili wygenerowania — zgubionego tokenu nie da się odczytać, trzeba wydać nowy. |
| `Name` | `ZAPQIO_NAME` | Stabilna nazwa runnera. Puste = runner generuje UUID i zapisuje go trwale w pliku `##Name` obok binarki. **Nazwa zostaje związana z tokenem przy pierwszym połączeniu i musi już pozostać niezmienna** — inna nazwa przy kolejnym połączeniu to odmowa `401`. |
| `Url` | `ZAPQIO_URL` | Adres instancji Web **bez** sufiksu `/ws-runner` — runner dokleja go sam. |
| `Logger:LogLevel` | `Logger__LogLevel` | Poziom logowania: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal` (domyślnie `Information`). |
| `Logger:PathDirectory` | `Logger__PathDirectory` | Katalog logów (domyślnie `Logs` obok binarki); pusty = brak zapisu do pliku. |
| `MaxConcurrency` | `ZAPQIO_MAX_CONCURRENCY` | Ile zadań runner wykonuje naraz (domyślnie `1`, czyli jedno po drugim). Wyższa wartość ma sens wyłącznie dla modułów gotowych na równoległe wywołania `Run`, także tej samej metody na tej samej instancji — runner nie dodaje żadnej synchronizacji, o tym decyduje twórca modułu. To jedyne miejsce, w którym pojemność się ustawia: runner ogłasza ją platformie przy połączeniu, a panel **Runnery** pokazuje ją tylko do odczytu. Platforma przycina wartości powyżej 32. |
| `StopTimeoutSeconds` | `StopTimeoutSeconds` | Ile sekund przy zatrzymaniu usługi runner czeka na zadania w toku i wysyłkę ich wyników, zanim zamknie połączenie (domyślnie `30`). Po tym czasie zadania są porzucane, a platforma zamyka je jako „wynik nieznany". Menedżer usług Windows ma własny limit na zatrzymanie usługi, zwykle krótszy — dłuższe zadania mogą go przekroczyć. |
| `MaxQueuedLogLines` | `MaxQueuedLogLines` | Ile linii logu zadań może czekać na wysyłkę, gdy platforma jest niedostępna (domyślnie `10000`). Ponad limit kolejne linie są pomijane, a w logu zadania pojawia się jedna linia ostrzegawcza. |
| `MinRemoteLogLevel` | `ZAPQIO_MIN_LOG_LEVEL` | Od jakiej wagi wpisy **modułu** idą do panelu Web: `Debug`, `Info` (domyślnie), `Warning`, `Error`, `Critical`. Obejmuje wszystkie trzy kanały modułu, więc próg `Warning` ucisza też zwykłe `Console.WriteLine`. Nie obejmuje wpisów, które runner generuje sam (wiersz startowy zadania, wyjątek, ostrzeżenie o pełnej kolejce) — te wychodzą zawsze. Wartość spoza listy jest ignorowana i zostaje `Info`. Czytane raz, przy starcie — włączenie `Debug` wymaga restartu usługi. |

**Adres instancji.** Nazwa instancji jest częścią adresu, a nie hosta — instancje stoją pod wspólnym
hostem i rozróżnia je segment ścieżki wybrany przy zakładaniu instancji:

```
wss://app.zapq.io/{instancja}      ← produkcja
ws://localhost:5208/hdwr-test      ← lokalnie
```

Pomyłka w tym segmencie nie daje odmowy protokołu, tylko `404` albo stronę HTML — patrz
[Rozwiązywanie problemów](#6-rozwiązywanie-problemów).

Przykładowy `appsettings.json`:

```json
{
  "Logger": {
    "LogLevel": "Information",
    "PathDirectory": "Logs"
  },
  "Token": "<TOKEN-Z-PANELU-WEB>",
  "Name": "moj-runner-01",
  "Url": "wss://app.zapq.io/moja-instancja",
  "MaxConcurrency": 1
}
```

**Kilka zadań naraz.** Domyślnie runner wykonuje zadania jedno po drugim. Żeby wykonywał kilka
naraz, ustaw `MaxConcurrency` i zrestartuj runnera — nowa pojemność idzie do platformy przy
połączeniu i od tej chwili platforma wysyła najwyżej tyle zadań naraz. Nic nie trzeba zmieniać w
panelu. Zanim to zrobisz, upewnij się, że moduły na tym runnerze są gotowe na równoległe wywołania:
metoda jest jedną instancją na proces i będzie wołana z kilku wątków naraz.

**Test w konsoli przed założeniem usługi.** Zanim runner trafi pod menedżera usług, uruchom go
ręcznie **z katalogu instalacji**:

```powershell
cd C:\zapqio\runner
.\Zapqio.Runner.exe
```

Katalog roboczy ma tu znaczenie: uruchomiony z innego miejsca runner szuka `appsettings.json`
w katalogu roboczym, a nie obok binarki, i tam też zapisze `##Name`. Jako usługa działa poprawnie —
katalogiem roboczym jest wtedy `System32`, a host sam wraca do katalogu binarki. Do szybkiego testu
konfigurację można też podać z linii poleceń:

```powershell
.\Zapqio.Runner.exe --Url=wss://app.zapq.io/moja-instancja --Token=<token>
```

### 2. Moduły — czym runner wykonuje zadania

Runner sam z siebie nie ma żadnych metod. Dostarczają je **moduły**: paczki `.zip` wrzucane do
katalogu `Modules` obok binarki.

Przy starcie runner rozpakowuje każdy `.zip` do `.modulesCache\<nazwa-zip>\`, zapisuje sumę SHA-256
paczki w pliku `##Hash` i przy kolejnych startach rozpakowuje ją ponownie tylko wtedy, gdy zip się
zmienił. Moduły ładują się **wyłącznie przy starcie** — po dorzuceniu albo podmianie paczki
zrestartuj usługę.

Moduł to zwykłe biblioteki .NET. Runner skanuje DLL-e z rozpakowanego katalogu i rejestruje publiczne,
nieabstrakcyjne klasy implementujące jeden z interfejsów z projektu
[`Zapqio.Runner.Module.Core`](../Zapqio.Runner.Module.Core/):

- **`IRunnerMethod`** — metoda widoczna w panelu Web:
  - `NameMethod()` — nazwa, po której Web kieruje do niej zadania,
  - `InData()` / `OutData()` — typy wejścia i wyjścia, z których generowany jest JSON Schema
    ogłaszany serwerowi; mogą zwrócić `null`,
  - `Run(string data)` — wykonanie; wejście i wyjście to JSON w postaci ciągu znaków.
- **`IRunnerInjection`** — sam znacznik: klasa trafia do kontenera DI jako singleton i można ją
  wstrzykiwać w pozostałych modułach.

Paczka powinna przynieść ze sobą plik `##Dll` z listą bibliotek do skanowania (jedna nazwa pliku na
linię) — bez niego runner ładuje i sprawdza wszystkie DLL-e z paczki, a po pierwszym udanym skanie
zapisuje w katalogu modułu listę tych, w których coś znalazł, i przy kolejnych startach czyta już tylko je.

#### Konfiguracja modułów

Ustawienia modułu (adresy, bazy, hasła) nie należą do zipa, bo zip jest podmieniany przy każdej
aktualizacji. Ich miejsce to katalog `Config\` obok `Modules\` (runner zakłada go przy starcie), plik per
moduł, np. `Config\nexoModule.json`. `install.ps1` nadaje temu katalogowi takie uprawnienia jak
`appsettings.json`: SYSTEM i Administratorzy pełne, konto usługi zapis (moduł zakłada plik przy pierwszym
starcie), nikt inny nie czyta. W kodzie modułu ścieżka to `Path.Combine(AppContext.BaseDirectory, "Config")`.

#### Jak zbudować paczkę modułu

Biblioteka klas z jedną paczką NuGet i targetem, który po `dotnet publish -c Release` pakuje katalog
publish do zipa (zip ląduje obok katalogu `publish`):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- net8.0 działa z każdą paczką runnera; net10.0 tylko z paczką .NET 10 -->
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <!-- Kontrakt tylko do kompilacji: w runtime dostarcza go runner, kopia DLL w zipie jest zbędna -->
    <PackageReference Include="Zapqio.Runner.Module.Core" Version="1.0.0" ExcludeAssets="runtime" />
  </ItemGroup>

  <Target Name="ZipAfterPublish" AfterTargets="Publish">
    <PropertyGroup>
      <ZipFilePath>$(PublishDir)..\$(MSBuildProjectName).zip</ZipFilePath>
    </PropertyGroup>
    <!-- Lista bibliotek, które runner ma skanować; kilka bibliotek z metodami = kilka linii -->
    <WriteLinesToFile File="$(PublishDir)##Dll" Lines="$(TargetFileName)" Overwrite="true" />
    <Delete Files="$(ZipFilePath)" Condition="Exists('$(ZipFilePath)')" />
    <ZipDirectory SourceDirectory="$(PublishDir)" DestinationFile="$(ZipFilePath)" />
  </Target>

</Project>
```

Zip wrzuć do `Modules\` i zrestartuj usługę. Zależności modułu (paczki NuGet, natywne DLL) jadą
w zipie razem z nim — runner rozwiązuje je z katalogu paczki.

#### Moduły współdzielone

Moduł może udostępniać innym modułom usługi i biblioteki:

- **Usługi** — klasa `IRunnerInjection` z jednej paczki jest singletonem wstrzykiwanym do metod
  z każdej innej paczki. To działa zawsze, bez dodatkowych oznaczeń.
- **Biblioteki** — żeby konsument mógł wiązać się z zestawami leżącymi w innej paczce (z DLL modułu
  współdzielonego albo z SDK, które ten moduł wozi), paczka dostawcy musi zawierać pusty plik
  `##Shared`. W targecie z przykładu wyżej dochodzi jedna linia:
  `<WriteLinesToFile File="$(PublishDir)##Shared" Lines="shared" Overwrite="true" />`.

Paczka może też nie mieć żadnego kodu i tylko udostępniać biblioteki, np. SDK zewnętrznego systemu:
wtedy oprócz `##Shared` ma **pusty** `##Dll`, żeby runner niczego w niej nie skanował. W logu jest
wówczas `Moduł współdzielony <paczka> nie dostarczył żadnej metody ani wstrzyknięcia - udostępnia
tylko biblioteki`, i to jest oczekiwany wpis, nie błąd.

Runner przy starcie najpierw zbiera katalogi wszystkich paczek z `##Shared` (wpis
`Moduł współdzielony: <paczka>`), a dopiero potem skanuje, więc kolejność paczek nie ma znaczenia.
Gdy jakiegoś zestawu nie ma w katalogu modułu, który o niego prosi, runner probuje po kolei katalogi
paczek współdzielonych w kolejności ich nazw; na poziomie `Debug` loguje, skąd zestaw został wzięty.

Konsument kompiluje się przeciw modułowi współdzielonemu, ale **nie pakuje** jego DLL ani jego
zależności: `ExcludeAssets="runtime"` przy `PackageReference`, `Private="false"` przy
`ProjectReference` albo `Reference` — tak samo jak przy `Module.Core` wyżej. Jeśli paczki
współdzielonej nie ma w `Modules\`, metody, których nie da się utworzyć, są pomijane z wpisem `Error`
`Metoda <typ> z modułu <paczka> nie została utworzona ...` z powodem, a pozostałe metody działają.

Co warto wiedzieć, pisząc moduł:

- `Console.WriteLine` i `Console.Error.WriteLine` z wnętrza `Run` trafiają na żywo do logów zadania
  w panelu Web (strumień błędów jako poziom `Error`). Nic nie trzeba w module zmieniać, żeby to
  działało — i tak zostaje dla modułów zbudowanych pod starsze wersje `Module.Core`.
- **Wagę wpisu można nazwać wprost** (od `Module.Core` 1.2): `RunnerLog.Debug/Info/Warning/Error/Critical`.
  Klasa jest statyczna, więc działa też w metodach pomocniczych i wątkach, które metoda uruchomi —
  wpis sam trafia do właściwego zadania. Przed kosztownym budowaniem treści warto zapytać
  `RunnerLog.IsEnabled(RunnerLogLevel.Debug)`, bo poziom `Debug` domyślnie **nie** idzie do Web
  (patrz `MinRemoteLogLevel` w [Konfiguracji](#1-konfiguracja)).

  ```csharp
  public async Task<string> Run(string data)
  {
      RunnerLog.Debug($"wejście: {data}");
      if (budget.Exceeded) RunnerLog.Warning($"źródło odpowiedziało po {elapsed:0.0}s");
      return output;
  }
  ```

- Kto woli wstrzykiwanie, może wziąć w konstruktorze `ILogger<T>` — runner rejestruje go w kontenerze
  modułów i kieruje w to samo miejsce, z tym samym progiem. `Trace` schodzi do `Debug`, bo protokół
  nie ma osobnego poziomu poniżej `Debug`.
- Wyjątek z `Run` kończy zadanie statusem `ERROR`, a jego treść ląduje w logach zadania.
- `JobContext.Current` (od `Module.Core` 1.1) daje w `Run` identyfikator operacji (`JobId`, ten sam
  przy każdej wysyłce i każdym ponowieniu tego zadania), identyfikator próby (`AttemptId`) i nazwę
  metody. Metoda z nieodwracalnym skutkiem powinna zapisać `JobId` razem ze skutkiem i przed
  wykonaniem sprawdzić, czy taki już istnieje — platforma wysyła zadanie ponownie, gdy straciła
  runnera po starcie metody i nie wie, jak się skończyła. Poza `Run` kontekst jest pusty.
- Wynik, którego nie udało się odesłać przed zerwaniem połączenia, runner trzyma w pamięci i wysyła
  zaraz po ponownym połączeniu z tym samym identyfikatorem próby; restart usługi go gubi.
- Wynik idzie jedną wiadomością WebSocket, a serwer przyjmuje najwyżej 32 MiB na wiadomość — rozmiar
  wyniku trzeba ograniczyć po stronie modułu, z zapasem na kodowanie.

### 3. Zarządzanie usługą

```powershell
sc.exe query ZapqioRunner      # status
sc.exe stop ZapqioRunner       # zatrzymanie
sc.exe start ZapqioRunner      # uruchomienie
Restart-Service ZapqioRunner   # restart - po zmianie konfiguracji albo modułów
```

Konto usługi (`NT SERVICE\ZapqioRunner`) dostaje od `install.ps1` prawo start/stop na własnej usłudze,
żeby moduł mógł ją zrestartować po podmianie swoich bibliotek (tak robi moduł Nexo po aktualizacji
Subiekta). Przy `-LocalSystem` nie jest to potrzebne.

### 4. Logi i diagnostyka

- **Logi plikowe** — katalog wskazany w `Logger:PathDirectory` (domyślnie `Logs` obok binarki), pliki
  rolowane dziennie i po przekroczeniu 200 MB.
- **Konsola** — tylko przy ręcznym uruchomieniu; usługa konsoli nie ma.
- **Windows Event Log** — runner do niego **nie pisze**. W trybie usługi logi plikowe są jedynym
  lokalnym śladem, więc nie zostawiaj `Logger:PathDirectory` pustego. W Podglądzie zdarzeń
  (dziennik `System`, źródło *Service Control Manager*) znajdziesz wyłącznie zdarzenia startu
  i zatrzymania samej usługi.
- **Logi zadań** — wszystko, co metoda zaloguje (konsola, `RunnerLog`, wstrzyknięty `ILogger`), idzie
  do panelu Web, a równolegle do logu plikowego pod źródłem `Method(<nazwa-metody>)`, z
  identyfikatorem zadania w nawiasie kwadratowym na początku wiersza. Wpisy poniżej
  `MinRemoteLogLevel` nie opuszczają runnera, ale w logu plikowym są — o ile przepuści je
  `Logger:LogLevel`.

Czy runner poprawnie połączył się z Web — szukaj w logu:

| Wpis w logu | Znaczenie |
| --- | --- |
| `Successfully connected to WebSocket at wss://…/ws-runner` | Uzgodnienie się powiodło (HTTP 101). |
| `Add method: <typ> in module: <moduł>` | Metoda z modułu została zarejestrowana i pójdzie do Web w wiadomości `Info`. |
| `Moduł współdzielony: <paczka> - jego katalog służy do rozwiązywania zestawów innych modułów` | Paczka z plikiem `##Shared`; inne moduły mogą brać z niej biblioteki (patrz §2). |
| `Metoda <typ> z modułu <paczka> nie została utworzona i nie będzie ogłoszona: <powód>` | Konstruktor metody rzucił albo brakuje wstrzyknięcia — najczęściej nie ma paczki modułu współdzielonego. Metoda jest wyłączona do restartu, pozostałe idą do Web normalnie. |
| `Kolejna próba połączenia za <n>s (nieudanych z rzędu: <k>)` | Web nieosiągalny albo uzgodnienie odrzucone; runner ponawia sam. |
| `Serwer ogranicza tempo uzgodnień (429)` | Limit po stronie Web — patrz niżej. |
| `Serwer odrzucił uzgadnianie ze statusem <kod>` | Odmowa protokołu; kod rozstrzyga przyczynę. |

Po stronie Web potwierdzeniem jest pojawienie się runnera wraz z listą jego metod w panelu.

### 5. Aktualizacja

Ponowne uruchomienie [`install.ps1`](../install.ps1) (bez parametrów = najnowsza wersja)
zatrzymuje usługę, podmienia pliki i uruchamia ją z powrotem — konfiguracja zostaje.
Ręcznie:

```powershell
sc.exe stop ZapqioRunner
# podmiana plików z nowej paczki - patrz lista poniżej
sc.exe start ZapqioRunner
```

Z katalogu instalacji **zachowaj** (skrypt robi to sam):

- `appsettings.json` — konfiguracja,
- `##Name` — tożsamość runnera. Skasowany, wygeneruje się od nowa jako inny UUID, a Web odrzuci
  połączenie kodem `401`, bo nazwa jest trwale związana z tokenem,
- `Modules\` — paczki modułów,
- `Logs\` — jeśli zależy Ci na historii.

`.modulesCache\` można skasować — odtworzy się przy starcie z paczek w `Modules\`.

Wersję zainstalowanej binarki sprawdzisz przez:

```powershell
(Get-Item C:\zapqio\runner\Zapqio.Runner.exe).VersionInfo.ProductVersion
```

### 6. Rozwiązywanie problemów

| Objaw | Możliwa przyczyna / rozwiązanie |
| --- | --- |
| Usługa nie startuje, w Dzienniku zdarzeń `You must install .NET to run this application` albo `The framework 'Microsoft.WindowsDesktop.App', version '8.0.0' was not found` | Paczka jest framework-dependent i wymaga **.NET Desktop Runtime (x64)** w wersji zgodnej z paczką: domyślnej `win-x64-net8` odpowiada runtime 8, paczce `win-x64` (instalacja z `-Net10`) — runtime 10. Sam .NET Runtime bez *Desktop* nie wystarczy. `winget install Microsoft.DotNet.DesktopRuntime.8` (albo `.10`), instalatory na [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet); sprawdzisz przez `dotnet --list-runtimes`. |
| Usługa nie startuje | Najczęściej zła ścieżka w `binPath=` albo brak prawa zapisu w katalogu instalacji — przy pierwszym starcie runner zapisuje tam `##Name` i bez tego nie ruszy. Uruchom binarkę ręcznie z katalogu instalacji, żeby zobaczyć błąd na konsoli. |
| W logu `401` | Zły token, zła nazwa albo zły segment instancji w `Url`. Nazwa musi być identyczna z tą powiązaną przy pierwszym połączeniu — sprawdź, czy `##Name` nie zniknął przy aktualizacji. Token istnieje tylko w bazie swojej instancji, więc poprawny token pod adresem innej instancji też kończy się `401`. |
| `404` albo strona HTML zamiast `101` | Żądanie nie trafiło w punkt końcowy tej instancji. Sprawdź segment instancji w `Url` i to, że `Url` **nie** kończy się na `/ws-runner`. |
| W logu `426 Upgrade Required` | Web mówi inną główną wersją protokołu niż runner. Zaktualizuj runnera do wersji zgodnej z instancją (odpowiedź niesie wersję serwera w nagłówku `X-Zapqio-Protocol-Version`). |
| `429` / `Serwer ogranicza tempo uzgodnień` | Limit uzgodnień po stronie Web; odmowa zapada przed sprawdzeniem tokenu, więc nie mówi o nim nic. Runner odczekuje sam: honoruje `Retry-After` (do 300 s), a bez tego nagłówka wycofuje się narastająco od 3 s do 60 s z losowym rozrzutem. Jeśli wraca uporczywie, sprawdź, czy spod tego samego adresu nie łączy się naraz wiele runnerów. |
| Runner widoczny w panelu, ale bez metod | Przy starcie runner loguje `Katalog modułów: <ścieżka> (konto: <użytkownik>)`, listę znalezionych paczek, każdy `Add method:` i na koniec `Wysyłam Info: N metod: ...` (albo ostrzeżenie, że metod nie ma). Jeśli paczek nie widać albo jest `Brak dostępu do katalogu modułów`, to konto usługi `NT SERVICE\ZapqioRunner` nie ma prawa odczytu — zdarza się, gdy katalog `Modules\` został **przeniesiony** (nie skopiowany) z profilu użytkownika, bo zachowuje wtedy stare uprawnienia. Sprawdź `icacls C:\zapqio\runner\Modules /T`, napraw przez `icacls C:\zapqio\runner\Modules /reset /T` (przywraca dziedziczenie z katalogu instalacji) albo uruchom ponownie `install.ps1`. Jeśli paczki są, ale DLL się nie ładują, w logu jest `Nie udało się załadować <dll>` z powodem — np. blokada polityki kontroli aplikacji (WDAC / Smart App Control) albo moduł zbudowany pod inną wersję .NET. Jeśli `Add method:` jest, ale metody nie ma w `Wysyłam Info`, szukaj wpisu `Metoda ... nie została utworzona` z powodem — zwykle brak paczki modułu współdzielonego, z którego metoda bierze wstrzyknięcie. Poziom `Debug` dokłada `Unppack module:` i `Check dll:`. |
| W logu zadania `Not found method: <nazwa>` | Web kieruje zadanie po nazwie z `NameMethod()`. Moduł nie został załadowany (restart po dorzuceniu paczki) albo nazwa metody rozjechała się z tą użytą w pipeline. |
| `Failed to send JobReturn ... the result of this job was lost` | Połączenie padło między odebraniem zadania a odesłaniem wyniku; Web zamknie takie zadanie jako osierocone. Przy dużych wynikach sprawdź limit 32 MiB na wiadomość — jego przekroczenie zamyka połączenie kodem WS `1009`. |
| Konfiguracja ignorowana przy ręcznym uruchomieniu | `appsettings.json` jest czytany z katalogu roboczego — uruchamiaj binarkę po `cd C:\zapqio\runner`. Trybu usługi to nie dotyczy. |

## Wdrożenia SR3 na Windows

Paczki z platformy wymagają zgody administratora maszyny. Zaktualizuj runner oraz
`install.ps1`: instalator zachowuje `Config`, `Modules`, `Deployments`, `Logs`,
`appsettings.json` i tożsamość maszyny. Zapisuje `##ServiceName` z rzeczywistą nazwą
usługi oraz ogranicza dostęp do poczekalni do administratorów, SYSTEM i konta usługi.
Nie zmieniaj nazwy usługi ręcznie w tym pliku. Istniejące moduły przenosi administrator;
SR3 nie importuje ich automatycznie do repozytoriów platformy.

W podniesionym terminalu, w katalogu instalacji:

```powershell
.\Zapqio.Runner.exe deploy list
.\Zapqio.Runner.exe deploy show <deployment-id>
.\Zapqio.Runner.exe deploy approve <deployment-id>
.\Zapqio.Runner.exe deploy approve --all
.\Zapqio.Runner.exe deploy reject <deployment-id>
```

Alias podkomendy: `deployments`. `show` pokazuje manifest, commit, autora, wiadomość,
sumy kontrolne i zmienione pliki względem aktualnie zainstalowanego manifestu.
`list`, `show`, pobranie i `reject` nie tworzą hosta ani nie uruchamiają nowego kodu.
CLI korzysta z trwałego dziennika `Deployments/state.json`; paczki leżą w `bundles/`.

`approve` wybiera konkretne ID i hashe, a działająca usługa przesyła tę listę do Weba.
Web sprawdza aktualność całego zestawu i zapisuje zgodę. Dopiero jego potwierdzenie
pozwala zrestartować usługę. Restart przerywa trwające zadania bez oczekiwania na
ich zakończenie. Brak ACK nie pozwala na instalację; po 60 sekundach CLI kończy
oczekiwanie, ale zapisane żądanie pozostaje i jest ponawiane po odzyskaniu łączności.
Oznacza to, że zaakceptowane później żądanie może zrestartować usługę po wyjściu z CLI.
Stan żądania sprawdzisz przez `list`. Odmowa Weba nie zatwierdza automatycznie nowszej paczki.

Po restarcie gotowe ZIP-y są walidowane, źródła budowane, a następnie podmieniany jest
cały wybrany zestaw. `Applied` jest wysyłane po załadowaniu modułów i walidacji ich metod.
Przy błędzie dowolnego modułu przywracany jest cały poprzedni zestaw; po próbie ładowania
DLL następuje dodatkowy restart do świeżego procesu. Dziennik odtwarza również instalację
przerwaną awarią procesu. Kopie wycofania są tymczasowe; nie są historią wersji do uruchamiania.
Cofnięcie plików nie cofa skutków ubocznych konstruktora ani targetów budowania.

Nowsza wysyłka zastępuje wcześniejszą niezatwierdzoną pozycję tego samego repozytorium.
Po zgodzie jej ID/hash pozostają niezmienne. Powrót do starszego commita i usunięcie
modułu (`Withdraw`) są kolejnymi operacjami wymagającymi zgody. `Config` pozostaje.
Statusy są ponawiane do potwierdzenia przez Web. Problemy helpera restartu trafiają do
`Deployments/restart.log`; pozostałe błędy do zwykłego logu runnera i historii wdrożeń.

### Źródła i artefakty CI

- `Runner`: dokładny commit jest dostarczany jako źródła. Dopiero po ACK uruchamiane
  jest lokalne `dotnet publish <projekt> -c Release -o <katalog roboczy>/output/publish`.
  Potrzebny jest SDK zgodny z `global.json`/projektem i lokalna konfiguracja NuGet.
  Limit budowania wynosi 10 minut; po przekroczeniu kończone jest drzewo procesu.
  Projekt może utworzyć jeden ZIP obok `PublishDir` przez `ZipAfterPublish` z przykładu
  powyżej. Alternatywnie zawartość publish musi mieć poprawne `##Dll`/`##Shared`.
- `CiZip`: SDK na runnerze nie jest potrzebny. Platforma pobiera gotowy artefakt dla
  dokładnego commita. Na GitHub artefakt nazywa się `zapqio-module` i pochodzi z udanego
  workflow. Na GitLab udany job `zapqio-module` należy do udanego pipeline tego commita.
  Archiwum artefaktu zawiera dokładnie plik `module.zip` w korzeniu; to właściwa paczka
  modułu z `##Dll` i opcjonalnym `##Shared`. Wygasły lub brakujący artefakt daje błąd,
  bez automatycznego przełączania na kompilację źródeł. Wyszukiwanie jest ograniczone
  do 2000 artefaktów GitHub lub 2000 pipeline GitLab (do 2000 jobów na pipeline).

Paczka transportowa, payload i pliki mają niezależne sumy SHA-256 oraz limity.
Token repozytorium pozostaje na platformie. Runner pobiera tylko z adresu instancji,
z zachowaniem prefiksu URL, używając swojej istniejącej nazwy i tokenu.

Wersja repozytorium/migawki/commita jest przypisana do faktycznie wybranej metody
przy rozpoczęciu próby i jedzie w logu startowym oraz wyniku, także po reconnect.
Paczka B czekająca na akceptację nie zmienia wersji A już załadowanej w procesie.
Moduły instalowane ręcznie nie otrzymują zgadywanej wersji repozytorium.
