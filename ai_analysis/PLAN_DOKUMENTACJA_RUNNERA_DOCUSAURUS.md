# Dokumentacja runnera: audyt i układ do Docusaurusa

Stan analizy: 2026-11-02. Kod runnera: commit `d9fe672` (`readme eng`).

Dokument przygotowuje późniejszą migrację. Zawiera proponowany układ stron, potwierdzone rozbieżności, brakujące informacje oraz kolejność przenoszenia treści. Na tym etapie nie przenosimy stron, nie usuwamy obecnych dokumentów i nie zmieniamy działania programu.

## 1. Zakres i podstawa ustaleń

Przejrzano:

- `Program.cs`, `AppSettings.cs`, `appsettings.json` i `install.ps1`;
- obsługę awarii, logowania, wykonywania zadań, połączeń i ponawiania wysyłki wyników;
- ładowanie modułów, kontrakty modułów i walidację paczek wdrożeniowych;
- CLI wdrożeń, dziennik, koordynator, budowanie, instalację i przywracanie poprzednich paczek;
- [obsługę runnera](../docs/szczegoly.md), [wdrożenia](../docs/wdrozenia.md), [pisanie modułów](../docs/moduly.md) i oba README;
- lokalne polskie strony Docusaurusa: [instalacja runnera](../../../docs/i18n/pl/docusaurus-plugin-content-docs/current/runners.md) oraz [moduły runnera zewnętrznego](../../../docs/i18n/pl/docusaurus-plugin-content-docs/current/external-runner-modules.md);
- konfigurację języków i nawigacji w lokalnym projekcie `zapq/docs` oraz strukturę istniejącego szablonu modułu .NET.

Podstawą opisów zachowania jest wykonywany kod. Komentarze i protokół pomagają ustalić kontekst, ale same też mogą zawierać nieaktualne uproszczenia. Rozbieżności w interfejsie Web, polityce ponawiania po stronie platformy i konfiguracji konkretnego wdrożenia Web wymagają osobnego sprawdzenia przed publikacją. Nie wykonano audytu zrzutów ekranu, wszystkich tłumaczeń ani działającej instalacji Windows Service.

## 2. Proponowany układ

Sześć stron wystarczy, aby oddzielić pierwszą instalację, codzienną obsługę i tworzenie modułów. Nie trzeba przenosić każdego obecnego pliku jako osobnej strony.

```text
Runner zewnętrzny
  Instalacja runnera                     [istniejąca]
  Konfiguracja i utrzymanie              [nowa]
  Wgrywanie i aktualizowanie modułów     [istniejąca]
  Polecenia i przebieg wdrożeń            [nowa]
  Tworzenie modułów .NET                 [nowa]
  Logi i rozwiązywanie problemów         [nowa]
```

Adresy nowych stron poniżej są propozycją. Dwa obecne adresy pozostają bez zmian.

| Strona / proponowany adres | Odbiorca i wynik | Materiał źródłowy |
| --- | --- | --- |
| Instalacja runnera, `/guides/runners` | Administrator doprowadza do działającej usługi i połączenia z Zapq. | Istniejący `runners.md`, `install.ps1`. |
| Konfiguracja i utrzymanie, `/guides/external-runner-configuration` | Administrator rozumie ustawienia, tożsamość, uprawnienia, aktualizację i zatrzymywanie. | `docs/szczegoly.md`, `AppSettings.cs`, `Program.cs`, instalator. |
| Wgrywanie i aktualizowanie modułów, `/guides/external-runner-modules` | Operator przechodzi od repozytorium do zatwierdzonego modułu i sprawdzonej metody. | Istniejący `external-runner-modules.md`. |
| Polecenia i przebieg wdrożeń, `/reference/external-runner-deployments` | Administrator interpretuje CLI, stany, opóźnione zatwierdzenia i przywracanie paczek. | `docs/wdrozenia.md`, `Deployments/`, `DeploymentBundle.cs`. |
| Tworzenie modułów .NET, `/guides/dotnet-modules` | Programista tworzy poprawny moduł, pakuje go i rozumie jego wykonanie. | `docs/moduly.md`, `Module.Core`, `MethodsProvider.cs`, szablon .NET. |
| Logi i rozwiązywanie problemów, `/guides/external-runner-diagnostics` | Administrator znajduje przyczynę problemu i zbiera właściwe logi. | Diagnostyka z obu dokumentów lokalnych, `RunnerLogging.cs`, `JobLogWriter.cs`, obsługa połączenia i wdrożeń. |

### 2.1. Instalacja runnera

Zakres:

1. Kiedy potrzebny jest runner zewnętrzny i skąd musi mieć dostęp do systemów lokalnych.
2. Windows x64, wariant .NET 8 lub 10, Desktop Runtime, dodatkowy SDK tylko do budowania źródeł.
3. Dodanie runnera w Web, token i poprawny adres instancji.
4. Instalator: język, katalog, nazwa usługi, tożsamość i równoczesność.
5. Kontrola `Running`, połączenia i pierwszych logów.
6. Przejście do wgrywania modułu i odnośnik do pełnej konfiguracji.

Zachować obecną procedurę i zrzuty po sprawdzeniu ich aktualności. Pełną tabelę ustawień oraz rozbudowane aktualizowanie i odinstalowanie przenieść na stronę utrzymania. Listę wspieranych systemów nadal odsyłać do producenta .NET, zamiast utrwalać ją w kilku miejscach.

### 2.2. Konfiguracja i utrzymanie

Zakres:

1. `appsettings.json`, ustawienia środowiskowe, dedykowane aliasy `ZAPQIO_*`, wymagany restart.
2. Pełna tabela ustawień z sekcji 4 tej analizy.
3. Nazwa widoczna w panelu, `Name` / `##Name` i nazwa usługi / `##ServiceName` jako odrębne informacje.
4. Znaczenie katalogów `Modules`, `.modulesCache`, `Config`, `Deployments`, `Logs` i zachowywanego przez instalator `Build`.
5. Konto usługi, dostęp do zależności, konfiguracji i zasobów sieciowych.
6. Start, stop, restart i uruchomienie diagnostyczne w konsoli.
7. Zwykłe zatrzymanie z oczekiwaniem na zadania oraz restart po wdrożeniu bez tego oczekiwania.
8. Aktualizacja: ten sam katalog i nazwa usługi, zachowywane pliki, wyjątki dotyczące konta i katalogu logów.
9. Odinstalowanie i unieważnienie tokenu.

Źródła: [konfiguracja](../Zapqio.Runner/AppSettings.cs), [wiązanie ustawień i tożsamość](../Zapqio.Runner/Program.cs#L19), [zatrzymywanie](../Zapqio.Runner/Background/RequestBindBackground.cs#L77), [aktualizacja plików](../install.ps1#L539), [konto usługi](../install.ps1#L712).

### 2.3. Wgrywanie i aktualizowanie modułów

Zachować istniejący poradnik użytkowy. Uzupełnić go o odnośniki do wymagań ZIP-a i źródeł oraz pełnego opisu CLI.

Sekwencja:

1. Wybrać gotowy ZIP z CI albo budowanie na runnerze.
2. Wybrać repozytorium i commit, wysłać paczkę.
3. Sprawdzić `deploy list` i `deploy show`, następnie zatwierdzić lokalnie.
4. Poczekać na potwierdzenie z Web, restart, budowanie lub instalację i walidację metod.
5. Sprawdzić wynik konkretnego wdrożenia, zainstalowany commit, listę metod i próbne wykonanie.
6. Aktualizować, wrócić do starszego commita albo wycofać moduł.
7. Wyjaśnić wpływ restartu na wszystkie zadania tego runnera.

Zachować obecne rozróżnienie między przyjęciem zatwierdzenia a pomyślną instalacją oraz informację, że timeout CLI nie anuluje zatwierdzenia.

### 2.4. Polecenia i przebieg wdrożeń

Zawartość referencyjna:

- Polecenia `deploy` / `deployments`: `list` / `pending`, `show`, `approve`, `approve --all`, `reject`.
- `pending` jest aliasem `list`, a nie filtrem pokazującym wyłącznie oczekujące paczki.
- Windows, podwyższone uprawnienia CLI, tożsamość usługi w `##ServiceName`.
- Identyfikator wdrożenia, commit i identyfikator zatwierdzanego zestawu to różne wartości.
- Kody CLI: `0` oznacza poprawne zakończenie polecenia, `1` błąd, `2` m.in. brak potwierdzenia w ciągu 60 sekund lub niepoprawne użycie. `approve` z kodem `0` nie potwierdza pomyślnego załadowania modułu.
- Trwałe zatwierdzenie zestawu, ponawianie komunikacji i ograniczenia odrzucania paczki objętej zatwierdzeniem.
- Zastępowanie starszych niezatwierdzonych zgłoszeń, konflikt nazw paczek.
- Budowanie po zatwierdzeniu, limit 10 minut, jeden główny `.csproj`, możliwość przygotowania ZIP-a przez projekt.
- Przygotowanie wszystkich paczek, kopie poprzednich plików, podmiana i walidacja całego zestawu.
- Przywracanie poprzednich paczek po błędzie; kolejny restart, jeśli nowe zestawy DLL zdążyły wejść do procesu.
- Przywracanie plików modułów nie cofa zmian wykonanych w bazach ani innych systemach.

Stanów technicznych `Requested`, `Approved`, `Preparing`, `Switching`, `Loading` nie mieszać ze statusami pojedynczej paczki i etykietami interfejsu Web. Nie dokumentować wewnętrznego `__restart` jako polecenia administratora.

Źródła: [CLI](../Zapqio.Runner/Deployments/WindowsDeploymentCli.cs#L11), [zatwierdzenie i odrzucenie](../Zapqio.Runner/Deployments/DeploymentStore.cs#L211), [instalacja i przywracanie](../Zapqio.Runner/Deployments/DeploymentInstaller.cs#L29), [budowanie](../Zapqio.Runner/Deployments/DeploymentInstaller.cs#L237).

### 2.5. Tworzenie modułów .NET

Zakres:

1. Rozpoczęcie od istniejącego szablonu, test lokalny, publikacja i plik ZIP.
2. Kontrakt `IRunnerMethod`: publiczna konkretna klasa, unikalna niepusta nazwa, `InData()`, `OutData()`, `Task<string> Run(string data)`.
3. JSON wejścia i wyjścia, schematy na podstawie typów, odpowiedzialność modułu za serializację.
4. `Zapqio.Runner.Module.Core` i `ExcludeAssets="runtime"`; zgodność frameworka oraz zależności z używaną paczką runnera.
5. Układ źródeł: dokładnie jeden `.csproj` w katalogu głównym paczki źródłowej; projekty narzędziowe i testy w podkatalogach, z poprawnym wyłączeniem ich kodu z projektu modułu.
6. Układ ZIP-a: biblioteka, zależności, `##Dll`, opcjonalny `##Shared`, zakaz pakowania zarezerwowanego `##Hash`.
7. Różnice między ręcznym kopiowaniem ZIP-a a walidacją wdrożenia z platformy.
8. `IRunnerInjection`: rejestracja konkretnego typu jako singleton, przykład wstrzyknięcia przez konstruktor, dostępność zestawów między paczkami.
9. Konfiguracja w `Config`, odczytywana przez moduł, zachowywana przy wymianie ZIP-a.
10. `Console`, `RunnerLog` i `ILogger<T>`; kontekst zadania i dwa etapy filtrowania logów.
11. Jedna instancja metody i usług na proces, równoczesne wywołania `Run`, synchronizacja stanu.
12. `JobContext.Current`, stabilny `JobId`, osobny `AttemptId`, obsługa powtórzenia bez powielania skutków.
13. Wyjątki metody, granice rozmiaru wiadomości i przerwanie pracy przez restart procesu.
14. Moduły współdzielone, `##Shared`, puste `##Dll` dla paczek bibliotecznych i konflikty zestawów.

Przykład metody powinien być kompletny i kompilowalny. Obecny fragment `Run` w `docs/moduly.md` odwołuje się do niezdefiniowanego `output`, więc nadaje się wyłącznie jako ilustracja logowania. Do docelowego poradnika wykorzystać i sprawdzić przykład z istniejącego szablonu.

Źródła: [IRunnerMethod](../Zapqio.Runner.Module.Core/IRunnerMethod.cs), [JobContext](../Zapqio.Runner.Module.Core/JobContext.cs), [tworzenie metod](../Zapqio.Runner/MethodsProvider.cs#L137), [DI](../Zapqio.Runner/MethodsProvider.cs#L481), [walidacja paczki](../protocol/dotnet/DeploymentBundle.cs#L144), [wykonanie](../Zapqio.Runner/ExecuteJob.cs#L34).

### 2.6. Logi i rozwiązywanie problemów

Zakres:

- Trzy miejsca diagnostyki: log procesu, `deployments-cli-YYYYMMDD.log`, `Deployments/restart.log`.
- Poziom lokalny `Logger:LogLevel` i filtr wpisów modułu `MinRemoteLogLevel`.
- Jak włączyć `Debug` lokalnie i w panelu oraz jak znaleźć PID, zadanie i wdrożenie.
- Brak runtime, brak startu usługi, awaria zadania w tle i akcje odzyskiwania Windows.
- Problemy z adresem, tokenem i tożsamością; statusy uzgodnienia połączenia.
- Brak metod, brak zależności, błędne markery, konstruktor i konflikt nazw lub DLL.
- Błąd źródeł, budowania, zatwierdzenia, restartu, przywracania i blokady `state.lock`.
- Zerwane połączenie a utrata procesu: ponowna wysyłka wyniku, pamięciowy bufor wyników, ograniczona kolejka logów.
- Zestaw informacji do diagnozy: wersja, czas zdarzenia, nazwa usługi, PID, identyfikatory zadania i wdrożenia oraz logi z tego samego okresu. Nie publikować tokenów ani konfiguracji z hasłami.

Logowanie połączenia może działać bez metod. Wpis o załadowaniu DLL nie zastępuje sprawdzenia, że metoda została utworzona i ogłoszona.

## 3. Rozbieżności i korekty przed migracją

Priorytet wysoki oznacza opis mogący prowadzić do błędnej reakcji na awarię, odrzucenia paczki albo utraty ustawień lub logów. Priorytet średni obejmuje niepełne zasady i brakujące rozróżnienia. Poniższe punkty opisują obecny kod; nie są deklaracją, że każde z tych zachowań powinno pozostać bez zmian.

### R01. Nieaktualny kod zakończenia po awarii, wysoki

**Obecny opis:** `docs/szczegoly.md:88` stwierdza, że nieobsłużony błąd kończy runnera kodem `0`, dlatego Windows nie uruchamia go ponownie.

**Kod:** [RunnerFailureMonitor.cs:21](../Zapqio.Runner/RunnerFailureMonitor.cs#L21) zwraca `1` dla awarii zadania w tle. W usłudze Windows [Program.cs:130](../Zapqio.Runner/Program.cs#L130) przekazuje `Environment.Exit`, aby zakończyć proces przed zgłoszeniem prawidłowego zatrzymania. Zwykłe zatrzymanie pozostawia `0`. `docs/wdrozenia.md` już opisuje tę zmianę prawidłowo.

**Korekta:** opisać rozróżnienie awarii i normalnego zatrzymania. Przy braku restartu sprawdzać log i ustawienia odzyskiwania usługi. Instalator ustawia restarty po 5, 30 i 60 sekundach podczas tworzenia nowej usługi ([install.ps1:677](../install.ps1#L677)); nie zapewnia ponownego ustawienia tych akcji przy każdej aktualizacji.

### R02. Nieaktualny komunikat o utraconym wyniku, wysoki

**Obecny opis:** `docs/szczegoly.md:95` każe szukać `Failed to send JobReturn ... lost`.

**Kod:** [ExecuteJob.cs:123](../Zapqio.Runner/ExecuteJob.cs#L123) zapisuje wynik do `PendingJobReturns` i informuje, że zostanie ponowiony po odzyskaniu połączenia. [RequestBindBackground.cs:309](../Zapqio.Runner/Background/RequestBindBackground.cs#L309) ponawia wysyłkę z tym samym identyfikatorem próby. [PendingJobReturns.cs:22](../Zapqio.Runner/PendingJobReturns.cs#L22) trzyma dane w pamięci procesu.

**Korekta:** użyć aktualnego komunikatu i opisać granicę gwarancji: utrata połączenia może zakończyć się ponownym wysłaniem wyniku, ale restart procesu usuwa bufor. Nie utożsamiać ponownej wysyłki wyniku z ponownym wykonaniem metody ani nie obiecywać trwałego przechowywania wyników na dysku.

### R03. Opcjonalność `##Dll` zależy od sposobu instalacji, wysoki

**Obecny opis:** `docs/moduly.md:50` mówi ogólnie, że bez `##Dll` runner skanuje DLL-e.

**Kod:** jest to prawdą dla lokalnego ładowania w [MethodsProvider.cs:305](../Zapqio.Runner/MethodsProvider.cs#L305). Natomiast [DeploymentBundle.cs:144](../protocol/dotnet/DeploymentBundle.cs#L144) odrzuca paczkę wdrożeniową bez `##Dll`. Pusty marker dopuszcza tylko razem z `##Shared`. Wskazane biblioteki muszą istnieć w katalogu głównym ZIP-a.

**Korekta:** w poradniku wdrażania wymagać `##Dll` zawsze. Automatyczny skan bez markera opisać wyłącznie jako zachowanie ręcznie zainstalowanych paczek. Dodać również wymaganie jednego głównego `.csproj` dla źródeł ([DeploymentBundle.cs:253](../protocol/dotnet/DeploymentBundle.cs#L253)), którego brakuje w lokalnym poradniku.

### R04. Filtr logów dotyczy także lokalnej kopii wpisów modułu, średni

**Obecny opis:** `docs/szczegoly.md:30` opisuje `MinRemoteLogLevel` jako próg wysyłki do Web. `docs/moduly.md:63` ogólnie obiecuje logowanie do panelu i pliku, a dalej wspomina jedynie, że `Debug` nie idzie do Web.

**Kod:** [JobLogWriter.cs:69](../Zapqio.Runner/JobLogWriter.cs#L69) sprawdza próg przed `EnqueueLog` i `Mirror`. Odrzucony wpis modułu nie trafia również do lokalnego loggera. Następnie [RunnerLogging.cs:13](../Zapqio.Runner/RunnerLogging.cs#L13) stosuje lokalny `Logger:LogLevel`. Dla `RunnerLog` i `ILogger<T>` wpis bez `JobContext` może trafić tylko lokalnie, jeśli przejdzie filtry; nie pojawia się w logach zadania. `Console` poza wykonaniem metody nie jest automatycznie logiem zadania.

**Korekta:** opisać faktyczne dwa filtry. Aby zobaczyć `Debug` modułu także w pliku, ustawić `MinRemoteLogLevel = Debug` i `Logger:LogLevel = Debug`. Zaznaczyć, że wpisy kontrolne generowane przez runnera, np. log startowy, nie podlegają filtrowi modułu. Jeżeli zamiarem produktu jest niezależność progów lokalnego i zdalnego, potrzebna będzie osobna zmiana kodu.

### R05. Aktualizacja może nadpisać własne konto usługi, wysoki

**Obecny opis:** Docusaurus, `runners.md`, sekcje o koncie usługi i aktualizacji, zaleca ustawienie własnego konta w razie potrzeby, a potem ponowne uruchomienie instalatora. Nie opisuje wpływu aktualizacji na to konto.

**Kod:** [install.ps1:716](../install.ps1#L716) ustawia `NT SERVICE\<nazwa-usługi>` również podczas aktualizacji, jeśli nie podano `-LocalSystem`. Przełącznik `-LocalSystem` omija tę zmianę; nie wykonuje jawnego przełączenia istniejącej usługi na LocalSystem. To nie jest pełna procedura utrzymywania dowolnego konta domenowego, bo skrypt ustawia także ACL.

**Korekta:** uprzedzić o przywracaniu konta wirtualnego i opisać sprawdzenie konta oraz uprawnień po aktualizacji. Przed publikacją procedury dla własnego konta ustalić, czy instalator ma je zachowywać. Nie przedstawiać samego `-LocalSystem` jako rozwiązania obsługującego wszystkie niestandardowe konta.

### R06. Zachowanie logów przy aktualizacji ma wyjątek, wysoki

**Obecny opis:** Docusaurus, `runners.md`, sekcja aktualizacji, mówi bez zastrzeżenia o zachowaniu logów. Instalator pozwala wybrać `-LogDirectory`.

**Kod:** [install.ps1:545](../install.ps1#L545) zachowuje listę nazw, w której jest dosłownie `Logs`. Inny katalog bezpośrednio w katalogu instalacji, np. `CustomLogs`, zostaje objęty usuwaniem starych plików. Ustawienie `Logger:PathDirectory` nie rozszerza tej listy. Katalog poza instalacją nie podlega temu konkretnemu sprzątaniu.

**Korekta:** nie obiecywać zachowania dowolnego katalogu logów. Opisać aktualne ograniczenie, a osobno rozważyć poprawienie instalatora przed opublikowaniem ogólnej obietnicy. Ustalenie wynika ze ścieżki kodu; nie uruchamiano instalatora ani usuwania plików.

### R07. `##Name` jest warunkowe, a w utrzymaniu brakuje `##ServiceName`, średni

**Obecny opis:** `docs/szczegoly.md:72` sugeruje, że brak `##Name` zawsze powoduje `401`, i nie wymienia `##ServiceName` w liście plików do zachowania.

**Kod:** [Program.cs:40](../Zapqio.Runner/Program.cs#L40) sięga po `##Name` tylko przy pustym efektywnym `Name`. `Name` może pochodzić z pliku lub aliasu środowiskowego. [WindowsDeploymentCli.cs:160](../Zapqio.Runner/Deployments/WindowsDeploymentCli.cs#L160) wymaga `##ServiceName` do ustalenia celu zatwierdzenia i restartu. Instalator ten plik zapisuje i zachowuje podczas sprzątania.

**Korekta:** zachowywać efektywną tożsamość runnera i, jeśli jest używany, plik `##Name`. Nie opisywać tego pliku jako obowiązkowego w każdej konfiguracji. Do listy utrzymania dopisać `##ServiceName` i jego odrębną rolę.

### R08. Limit 32 nie jest walidacją `MaxConcurrency` w runnerze, średni

**Obecny opis:** `docs/szczegoly.md:27` określa `MaxConcurrency` jako maksymalnie 32.

**Kod:** [AppSettings.cs:74](../Zapqio.Runner/AppSettings.cs#L74) ogranicza jedynie minimum do 1. [JobScheduler.cs:51](../Zapqio.Runner/JobScheduler.cs#L51) nie przycina wartości do 32; instalator również dopuszcza więcej. Limit platformy opisuje [protokół, sekcja Info](../protocol/PROTOCOL.md#L226). Obecny poradnik Docusaurusa trafniej przypisuje ograniczenie platformie.

**Korekta:** oddzielić deklarowaną pojemność runnera od limitu egzekwowanego przez platformę. Nie sugerować, że ustawienie większej wartości odblokowuje większą równoległość w Web. Przed publikacją potwierdzić limit w obsługiwanej wersji platformy.

### R09. Brakuje ustawienia rozmiaru wiadomości i kierunku limitu, średni

**Obecny opis:** lokalna tabela ustawień nie zawiera `MaxWebSocketMessageBytes`, a `docs/moduly.md:87` opisuje 32 MiB jako rozmiar wyniku. W Docusaurusie również brakuje tego ustawienia.

**Kod:** [AppSettings.cs:14](../Zapqio.Runner/AppSettings.cs#L14) ustawia domyślnie `33554432` bajty. Wartość musi być dodatnia. [RequestBindBackground.cs:176](../Zapqio.Runner/Background/RequestBindBackground.cs#L176) ogranicza całą odbieraną wiadomość. [WSClient.cs:228](../Zapqio.Runner/WSClient.cs#L228) serializuje i wysyła wiadomość bez lokalnego sprawdzenia tego limitu dla wyjścia. Limit odbioru wyniku egzekwuje odbiorca po stronie platformy.

**Korekta:** podać domyślną wartość, pełną kopertę UTF-8 i konieczność zgodnej konfiguracji obu stron. 32 MiB nie oznacza 32 MiB samego JSON-a wyniku. Zwiększenie limitu odbiorczego runnera nie zwiększa automatycznie limitu odbiorczego Web.

### R10. `StopTimeoutSeconds` nie chroni zadań przy restarcie wdrożeniowym, średni

**Obecny opis:** `docs/szczegoly.md:31` opisuje oczekiwanie na zadania przy zatrzymaniu bez wyjątku. Dokumenty wdrożeniowe prawidłowo mówią o przerywaniu pracy, ale brak połączenia obu zasad.

**Kod:** [RequestBindBackground.cs:84](../Zapqio.Runner/Background/RequestBindBackground.cs#L84) wybiera czas oczekiwania `0`, gdy restart został zlecony przez koordynator wdrożeń. Przy zwykłym zatrzymaniu korzysta z `StopTimeoutSeconds`. To budżet używany w procedurze zatrzymywania, a nie limit czasu wykonywania pojedynczej metody.

**Korekta:** opisać wyjątek obok ustawienia i dodać odnośnik do wpływu wdrożenia na zadania. Nie sugerować, że zwiększenie tej wartości zapewnia bezprzerwowe wdrażanie.

### R11. Błąd modułu przy zwykłym starcie i podczas wdrożenia ma inne skutki, średni

**Obecny opis:** `docs/moduly.md`, sekcja modułów współdzielonych, mówi, że brak zależności wyłącza część metod, a reszta działa. Czytelnik może przenieść tę zasadę na zatwierdzane wdrożenie.

**Kod:** [MethodsProvider.cs:33](../Zapqio.Runner/MethodsProvider.cs#L33) zgłasza błędy ładowania, gdy `validate` jest włączone. [DeploymentInstaller.cs:46](../Zapqio.Runner/Deployments/DeploymentInstaller.cs#L46) ładuje istniejące moduły bez tej walidacji, a [DeploymentInstaller.cs:155](../Zapqio.Runner/Deployments/DeploymentInstaller.cs#L155) waliduje zestaw podczas wdrożenia i uruchamia przywracanie po błędzie.

**Korekta:** wyraźnie oddzielić zwykłe ładowanie dotychczasowych lub ręcznie wgranych paczek od walidacji wdrożenia. Wadliwy nowy zestaw nie powinien być przedstawiany jako udane wdrożenie z jedną pominiętą metodą.

### R12. Opis DI między modułami jest zbyt ogólny, średni

**Obecny opis:** `docs/moduly.md:93` mówi, że usługi są wstrzykiwane między paczkami zawsze, bez dodatkowych kroków.

**Kod:** [MethodsProvider.cs:481](../Zapqio.Runner/MethodsProvider.cs#L481) rejestruje konkretną klasę przez `AddSingleton(type)`. Nie dodaje automatycznie mapowania dowolnego interfejsu użytkownika na tę klasę. Konstruktor konsumenta i jego zależności muszą dać się utworzyć, a biblioteki muszą być dostępne dla mechanizmu ładowania.

**Korekta:** pokazać przykład wstrzykiwania konkretnej klasy implementującej `IRunnerInjection`. Oddzielić wspólny kontener DI od udostępniania DLL przez `##Shared`. Nie obiecywać działania dla każdego interfejsu i dowolnego układu paczek.

## 4. Pełna tabela konfiguracji do opracowania strony

Poniższa tabela opisuje ustawienia aplikacji i jawne aliasy z `Program.cs`. Wartości środowiskowe wiązane standardowo przez konfigurację .NET, np. `Logger__LogLevel`, należy odróżnić od pięciu dedykowanych aliasów `ZAPQIO_*`. Nie tworzyć nieistniejących aliasów przez mechaniczne dodanie prefiksu.

| Klucz | Wartość domyślna / warunek | Jawny alias `ZAPQIO_*` | Istotna zasada |
| --- | --- | --- | --- |
| `Url` | Wymaga konfiguracji. | `ZAPQIO_URL` | Adres instancji bez `/ws-runner`; klient dokleja tę końcówkę. |
| `Token` | Wymaga konfiguracji. | `ZAPQIO_TOKEN` | Zmiana tokenu w Web wymaga aktualizacji efektywnej konfiguracji. |
| `Name` | Przy pustej wartości odczyt `##Name` albo utworzenie UUID i zapis pliku. | `ZAPQIO_NAME` | Zachować tożsamość połączenia; nie mylić z etykietą w panelu. |
| `MaxConcurrency` | `1`; wartości poniżej 1 normalizowane do 1. | `ZAPQIO_MAX_CONCURRENCY` | Metody i usługi współdzielą instancje. Limit platformy jest odrębny. |
| `MaxWebSocketMessageBytes` | `33554432`; musi być większe od zera. | Brak dedykowanego aliasu. | Limit całej wiadomości odbieranej przez runnera. |
| `StopTimeoutSeconds` | `30`; wartości ujemne normalizowane do 0. | Brak dedykowanego aliasu. | Oczekiwanie przy zwykłym zatrzymaniu; restart wdrożeniowy używa 0. |
| `MaxQueuedLogLines` | `10000`; minimum po normalizacji 100. | Brak dedykowanego aliasu. | Kolejne linie ponad limit są pomijane; dotyczy też nadprodukcji logów przy działającym łączu. |
| `MinRemoteLogLevel` | `Info`; nierozpoznana wartość wraca do `Info`. | `ZAPQIO_MIN_LOG_LEVEL` | `Debug`, `Info`, `Warning`, `Error`, `Critical`; filtruje wpis modułu również przed lokalną kopią. |
| `Logger:LogLevel` | `Information`; także wartość zastępcza przy błędnym ustawieniu. | Brak dedykowanego aliasu; standardowo `Logger__LogLevel`. | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`; lokalny logger procesu. |
| `Logger:PathDirectory` | `Logs` w dostarczonym pliku i konfiguracji tworzonej przez instalator. | Brak dedykowanego aliasu; standardowo `Logger__PathDirectory`. | Pusta wartość wyłącza log plikowy; względna ścieżka liczona od katalogu programu. |

`Logger.PathDirectory` nie ma inicjalizatora `Logs` w samej klasie `AppSettings`. Nie opisywać tej wartości jako bezwarunkowego zachowania przy brakującym pliku konfiguracji. `restart.log` jest niezależny od ustawień loggera.

Źródła: [AppSettings.cs](../Zapqio.Runner/AppSettings.cs), [Program.cs](../Zapqio.Runner/Program.cs#L19), [appsettings.json](../Zapqio.Runner/appsettings.json), [RunnerLogging.cs](../Zapqio.Runner/RunnerLogging.cs), [ograniczanie kolejki](../Zapqio.Runner/Outbox.cs#L120).

## 5. Mapa przeniesienia obecnych treści

| Obecne miejsce | Docelowe miejsce | Sposób przeniesienia |
| --- | --- | --- |
| README PL i EN: opis, instalator, wydania | README PL i EN | Zachować krótko, z odnośnikami do odpowiedniej wersji językowej dokumentacji. |
| README PL i EN: dokumentacja techniczna | Strony Docusaurusa | Po publikacji usunąć sekcję odsyłającą do lokalnych poradników; potrzebne linki włączyć do krótkich sekcji instalacji i modułów. |
| `docs/szczegoly.md`: konfiguracja, usługa, aktualizacja | Konfiguracja i utrzymanie | Przepisać z uwzględnieniem R05-R10, bez kopiowania błędnych obietnic. |
| `docs/szczegoly.md`: logi, tabela problemów | Logi i rozwiązywanie problemów | Zaktualizować komunikaty i obsługę awarii, R01-R04. |
| `docs/wdrozenia.md`: polecenia i mechanizm | Polecenia i przebieg wdrożeń | Zachować pełny opis, powiązać z istniejącym poradnikiem użytkowym. |
| `docs/wdrozenia.md`: diagnostyka i blokady | Logi i rozwiązywanie problemów | Zachować aktualne komunikaty, PID i etapy; szczegóły wewnętrznych pętli podać dopiero przy diagnozie konkretnego problemu. |
| `docs/moduly.md` | Tworzenie modułów .NET | Uzupełnić kontrakt paczki, źródła, DI, filtry logowania i różnicę walidacji wdrożenia. |
| Docusaurus: `runners.md` | Instalacja oraz konfiguracja i utrzymanie | Podzielić treść, zachować obecny adres instalacji. |
| Docusaurus: `external-runner-modules.md` | Ten sam poradnik oraz odnośniki do referencji | Zachować adres i procedurę użytkownika. |
| `protocol/PROTOCOL.md`, schematy, fixtures | Repozytorium protokołu / obecne pliki | Zachować blisko kontraktów i testów. Poradniki mogą odsyłać do nich w razie potrzeby. |
| `Zapqio.Runner.Module.Core/NUGET.md` i instrukcje wydawania | Repozytorium kodu | To materiał dla opiekunów pakietu. Nie mieszać go z instrukcją tworzenia zwykłego modułu. |

## 6. Wykonanie późniejszej migracji

1. Rozstrzygnąć, czy R04-R06 opisujemy jako obecne ograniczenia, czy najpierw zmieniamy kod. Szczególnie nie utrwalać obietnicy zachowania konta i wszystkich logów bez pokrycia w instalatorze.
2. Opracować pełne strony według powyższego układu. W projekcie Docusaurusa językiem bazowym jest angielski: źródła w `docs/`, odpowiedniki w `i18n/pl/docusaurus-plugin-content-docs/current/` i `i18n/de/docusaurus-plugin-content-docs/current/`.
3. Zachować identyfikatory istniejących dokumentów i ich `slug`. Dodać nowe ID, tytuły, opisy, `sidebar_position` oraz spójne odnośniki poprzednia/następna. Nawigacja jest generowana automatycznie przez `sidebars.ts`.
4. Sprawdzić procedury interfejsu Web, aktualne limity platformy, przykłady modułu i wymagania wydań runnera. Nie deklarować wsparcia systemu tylko na podstawie obecności targetu w `.csproj`.
5. Zbudować całą dokumentację przez `npm run build` i wykonać `npm run typecheck`. Zgodnie z lokalnym komentarzem konfiguracji użyć wspólnego builda języków. Sprawdzić linki, markery `##Dll` w treści MDX, bloki XML/C# i nawigację językową. Ewentualne artefakty kontroli przeglądarkowej zapisywać poza repozytorium, w katalogu tymczasowym Windows.
6. Opublikować dokumentację, sprawdzić nowe adresy, a dopiero potem zmienić README PL i EN. W wersji EN kierować do angielskich poradników, również dla modułów.
7. Wyszukać wszystkie odwołania do trzech lokalnych dokumentów. Na czas przejścia można zastąpić ich zawartość krótkimi odnośnikami do nowych stron, aby starsze linki nadal prowadziły do właściwej treści. Usunięcie tych plików wykonać jako osobny końcowy krok po uporządkowaniu odwołań.

## 7. Weryfikacja wykonana podczas audytu

Uruchomiono istniejące testy dotyczące ustawień, awarii procesu, logów modułów, bufora wyników, modułów współdzielonych, instalacji i dziennika wdrożeń oraz ponawiania połączenia:

```powershell
dotnet test Zapqio.Runner.Tests/Zapqio.Runner.Tests.csproj --no-restore --filter 'FullyQualifiedName~AppSettingsTests|FullyQualifiedName~RunnerFailureMonitorTests|FullyQualifiedName~JobLogWriterTests|FullyQualifiedName~PendingJobReturnsTests|FullyQualifiedName~MethodsProviderTests|FullyQualifiedName~DeploymentInstallerTests|FullyQualifiedName~DeploymentStoreTests|FullyQualifiedName~RunnerReconnectTests' --verbosity minimal
```

Wynik: **63 zaliczone, 0 nieudanych, 0 pominiętych**, `net10.0`. Kompilator zgłosił ostrzeżenia dotyczące nullowalności istniejącego kodu.

Testy uzupełniają analizę, ale nie pokrywają każdej tezy powyżej. W szczególności ustalenia dotyczące sprzątania katalogów, konta usługi, markerów ZIP i lokalnego filtrowania logów sprawdzono w implementacji. Nie uruchamiano instalatora, nie zmieniano kont usług i nie wykonywano rzeczywistego wdrożenia. Publikacja dokumentacji pozostaje osobnym etapem.
