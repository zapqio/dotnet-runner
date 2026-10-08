# Moduły współdzielone: plan krótki

Wersja minimalna z 15.09.2026. Szczegóły (treść logów, testy, pełne csproj, weryfikacja) są w
`PLAN-moduly-wspoldzielone-szczegoly.md`. Start po Twoim "implementuj".

## Cel

`Nexo.Connection.zip` (NexoClient + SDK, jedno połączenie) leży w `Modules\`, a metody są w osobnych,
chudych zipach z osobnych repo. Konsument kompiluje się przeciw paczce NuGet `Zapqio.Nexo.Connection`
z `ExcludeAssets="runtime"`, a w runtime bierze DLL z zipa Connection.

## Co dziś nie działa

Wstrzyknięcia między zipami działają już teraz. Nie działa ładowanie zestawów: DLL konsumenta odwołuje
się do `Nexo.Connection` i SDK z katalogu innego zipa, a `LoadFrom` probuje tylko własny katalog.
Do tego jeden padający konstruktor metody (np. brak zipa Connection) wywraca `SendInfo` i runner kręci
się w pętli reconnectów bez metod.

## Krok 1: runner (ZROBIONE 15.09: commit `07e02ee` + `Release v0.1.9` `4bd6c42`, bez pusha)

`Zapqio.Runner/MethodsProvider.cs`:

1. Zipy sortowane po nazwie. Przebieg 1: katalogi z plikiem `##Shared` na listę. Przebieg 2: skan jak dziś.
2. `AppDomain.CurrentDomain.AssemblyResolve`: brakujący zestaw szukaj w katalogu zestawu proszącego,
   potem po kolei w katalogach `##Shared`; trafienie ładuj przez `Assembly.LoadFrom`.
3. Metody rejestrowane po typie (`AddSingleton(type)`) i tworzone pojedynczo w try/catch przy pierwszym
   `GetMethods()`. Padający konstruktor = wpis Error z powodem i metoda pominięta, reszta działa.
4. Konstruktor z katalogami modułów i cache jako parametrami, żeby dało się testować.

Testy: `Zapqio.Runner.Tests/MethodsProviderTests.cs` plus dwa moduły testowe (współdzielony i konsument)
pakowane do zipów w katalogu tymczasowym. Trzy przypadki: kolejność zipów bez znaczenia; brak modułu
współdzielonego daje Error bez wyjątku; padający konstruktor pomija tylko tę metodę.

Docs `docs/szczegoly.md` §2: podsekcja "Jak zbudować paczkę modułu" (csproj z `ExcludeAssets="runtime"`,
target zip z `##Dll`) i "Moduły współdzielone" (`##Shared`, konsument nie pakuje DLL Connection ani SDK).

Commit, potem osobny commit `Release v0.1.9` w `release.yml`.

## Krok 2: `module-nexo-connection` i `module-nexo-testconnect` (ZROBIONE 15.09, repo lokalne bez remote)

Stan: oba repo z jednym commitem, zbudowane i sprawdzone lokalnie uprzężą na `MethodsProvider` z prawdziwymi
zipami (konsument sortowany przed Connection): `Who am I (test)` zwróciło sygnaturę operatora,
`InsERT.Moria.ModelDanych.dll` rozwiązany z paczki współdzielonej, bez Connection wpis Error i brak wyjątku.
Zostało po Twojej stronie: założyć repo na GitHubie i wypchnąć; sprawdzenie z panelu Web opcjonalne.
Przy okazji: źródło NuGet "Runner module core" wskazywało nieistniejący katalog (restore padał z NU1301),
przepięte na `runner-dotnet\Zapqio.Runner.Module.Core\bin\Release`; dodane źródło "Nexo connection".

`Nexo.Connection.csproj` = dzisiejszy `Nexo.csproj` plus:

- `PackageId=Zapqio.Nexo.Connection`, `Version=1.0.0`, `AssemblyVersion=1.0.0.0`, `GeneratePackageOnBuild`,
- referencje SDK przeniesione do `build/Zapqio.Nexo.Connection.props` (`Private="$(NexoSdkCopyLocal)"`);
  plik importowany przez csproj i pakowany do paczki, więc konsument dostaje je automatycznie,
- w targecie zip dodatkowo `##Shared`.

Kod: `NexoClient.cs` bez zmian, `ConnectionSettings.cs` = `Settings` okrojona do sekcji `Connect`.

`Nexo.TestConnect.csproj`: `net8.0-windows`, `x64`, `UseWPF`, dwie linie `PackageReference`
(`Zapqio.Runner.Module.Core` i `Zapqio.Nexo.Connection`, obie z `ExcludeAssets="runtime"`), target zip
z `##Dll`. Kod: `TestConnect.cs` bez zmian.

Lokalne źródło NuGet: `dotnet nuget add source <repo>\module-nexo-connection\bin\Release`.

Sprawdzenie: oba zipy w `Modules\`; w logu `Moduł współdzielony: Nexo.Connection` i
`Add method: Nexo.TestConnect`; "Who am I (test)" z panelu zwraca operatora. Zip TestConnect nie zawiera
`InsERT.*` ani `Nexo.Connection.dll`. Bez zipa Connection: Error o metodzie, runner połączony, bez pętli.

## Krok 3: `module-nexo` -> `module-nexo-invoices` (ZROBIONE 15.09 lokalnie, katalog i remote bez zmian)

Stan: commit w `module-nexo` z projektem `Nexo.Invoices`; zip 4 pliki bez SDK. Uprząż z trzema paczkami:
`Add invoice and pdf` i `Who am I (test)` ogłoszone, `InsERT.Moria.API.dll` dla faktur rozwiązany z paczki
współdzielonej, "Who am I" zwraca operatora. Po Twojej stronie: zmiana nazwy repo na GitHubie i push, na
runnerach trzy zipy zamiast `Nexo.zip`, faktura testowa na bazie demo z panelu.

Zmiana nazwy repo. Usunąć `NexoClient.cs` i `TestConnect.cs`, z `Settings` wyciąć sekcję `Connect`.
Csproj -> `Nexo.Invoices.csproj`: wyciąć referencje SDK, dodać `PackageReference Zapqio.Nexo.Connection`
z `ExcludeAssets="runtime"`. Na runnerach zastąpić `Nexo.zip` trzema zipami.

## Krok 4: SDK Subiekta jako osobna paczka, aktualizacja bez builda (ZROBIONE 16.09, commity lokalne)

Wyniki weryfikacji 16.09 (uprząż na `MethodsProvider`, baza demo 61.1.0.9431):
- Zip Connection: 6 plików, bez SDK. Skrypt: pobranie 61.1.1 z FTP i wariant `-SdkDir`, oba OK.
- Reguła pakowania SDK: z poziomu `Bin` tylko `*.dll` i `*.pak`, bez podkatalogów, `.exe`, `.chm`, `.XML`.
  Nadzbiór dotychczasowego `Nexo.zip` (549 DLL + 2 pak). Cały `Bin` to 1,1 GB (Subiekt.exe, libcef, Pomoc.chm).
- Niezgodność: Sfera odrzuca nawet różnicę w patchu. Typ `System.InvalidOperationException`, treść:
  "Podana baza danych jest w innej wersji niż użyte biblioteki sferyczne. Wersja bazy danych to
  61.1.0.9431, a wersja Sfery to 61.1.1.9471. Zaktualizuj bazę albo użyj innej wersji bibliotek
  sferycznych." `NexoClient` opakowuje to w `NexoConnectionException` z wersją SDK, nazwą paczki i
  nazwą skryptu. Ogólny `catch` zostaje: ten sam wyjątek niesie też brak serwera SQL.
- Bez `Nexo.Sdk.zip`: obie metody Nexo wyłączone przy starcie z `InsERT.Moria.Sfera` w powodzie.
- Zgodne wersje, cztery paczki (Sdk 61.1.0 spakowane regułą dll+pak: 613 plików, 320 MB): oba wpisy
  `Moduł współdzielony`, dla `Nexo.Sdk` "udostępnia tylko biblioteki", Sfera/API/ModelDanych rozwiązane
  z `Nexo.Sdk`, obie metody ogłoszone, "Who am I" zwraca operatora, w wyjściu zadania
  `Connecting Nexo (SDK 61.1.0.9431 z paczki Nexo.Sdk)`.
- Skrypt `.ps1` musi być zapisany jako UTF-8 z BOM, inaczej Windows PowerShell 5.1 czyta polskie znaki
  jako ANSI i nie parsuje pliku.
- Odstępstwo od planu: nieudane logowanie operatora nie dostaje wskazówki o skrypcie (to nie problem
  SDK), tylko wersję SDK w treści; przy okazji naprawione: nieudane logowanie nie zostawia już uchwytu
  w polu, więc kolejne wywołanie nie dostaje połączenia bez operatora.

Cel: zmiana wersji Subiekta u klienta = jeden skrypt na maszynie runnera, bez .NET SDK, bez przebudowy
modułów. Runner stoi na maszynie z Subiektem. Ustalenia z 16.09, wszystkie sprawdzone:

- InsERT publikuje SDK na publicznym FTP bez logowania:
  `https://ftp.insertcdn.pl/pub/aktualizacje/InsERT_nexo/nexoSDK_<major>_<minor>_<patch>.exe`
  (od 33.0.0 do bieżącego 61.1.1; numer w nazwie = numer wersji Subiekta z "O programie").
- Plik to 7-Zip SFX: `nexoSDK_61_1_1.exe -y -o"C:\nexoSDK"` rozpakowuje bez okna do
  `C:\nexoSDK\nexoSDK_61.1.1.9471\Bin` (657 plików, w tym Sfera, `ijwhost.dll`, SNI).
- Zestawy SDK mają zawsze `AssemblyVersion 1.0.0.0`, więc moduły skompilowane przeciw jednej wersji
  wiążą się z każdą inną bez przebudowy. Przebudowa tylko wtedy, gdy InsERT zmienił używane API.
- Runner niczego nie potrzebuje: paczka z samymi bibliotekami (`##Shared` + pusty `##Dll`) już działa,
  loguje "udostępnia tylko biblioteki" i nie skanuje jej.
- Wariant "runner czyta katalog instalacji Subiekta" odrzucony: niepotrzebny przy publicznym SDK,
  a do tego blokowałby pliki instalacji i nie wiadomo, czy są x64.

### `module-nexo-connection`

- **Csproj:** `NexoSdkCopyLocal=false`. Zip Connection kurczy się do `Nexo.Connection.dll`, `.deps.json`,
  `##Dll`, `##Shared` (marker zostaje, bo konsumenci nadal biorą stąd `Nexo.Connection.dll`).
  `Version` 1.0.1 (nowa DLL, API bez zmian, `AssemblyVersion` zostaje 1.0.0.0). Domyślny `NexoSdkVersion`
  w props to wersja do kompilacji u Ciebie, niezależna od tego, co klient ma w `Nexo.Sdk.zip`.
- **`NexoClient`:** w konstruktorze odczyt wersji SDK z `typeof(Uchwyt).Assembly` (`ProductVersion`, np.
  `61.1.1.9471+sha`, obcięte do numeru) i nazwy katalogu paczki. To celowo wymusza załadowanie Sfery
  przy tworzeniu metod: bez `Nexo.Sdk.zip` metody Nexo padają przy starcie z czytelnym Error
  `nie została utworzona ... 'InsERT.Moria.Sfera'`, zamiast dopiero w zadaniu. Przy łączeniu
  `Console.WriteLine("Connecting Nexo (SDK <wersja> z paczki <nazwa>)")` - leci do logu zadania w panelu.
  Wyjątek z `Polacz`/`ZalogujOperatora` opakowany: `SDK <wersja> (paczka <nazwa>) nie połączył się
  z Subiektem: <treść Sfery>. Jeśli Subiekt ma inną wersję, uruchom update-nexo-sdk.ps1 -Version
  <wersja z "O programie">` z oryginałem jako `InnerException`. Dokładny typ i treść wyjątku Sfery przy
  niezgodności do sprawdzenia w weryfikacji, na razie łapany ogólny.
- **`update-nexo-sdk.ps1`** w korzeniu repo (kopiowany do katalogu runnera). Parametry: `-Version`
  (wymagany, np. `61.1.1`), `-RunnerDir` (domyślnie `C:\zapqio\runner`), `-ServiceName` (`ZapqioRunner`),
  `-SdkDir` (gotowy katalog `Bin`, pomija pobieranie), `-NoRestart` (do testów bez usługi). Kroki:
  pobranie z FTP do `%TEMP%`, `-y -o`, kontrola `InsERT.Moria.Sfera.dll` i odczyt `ProductVersion`,
  katalog roboczy z kopią `Bin` + pusty `##Shared` + pusty `##Dll`, `ZipFile.CreateFromDirectory` do
  `Modules\Nexo.Sdk.zip` (stary usunięty, stała nazwa = jedna paczka SDK na runnerze),
  `Restart-Service`, na końcu ostatnie wpisy logu z `Nexo.Sdk`. Sprzątanie `%TEMP%`.
- **README:** instalacja to teraz `Nexo.Sdk.zip` + `Nexo.Connection.zip` + paczki z metodami; procedura
  aktualizacji Subiekta = skrypt; kiedy jednak potrzebny programista.

### Pozostałe repo

`module-nexo-testconnect` i `module-nexo` bez zmian. `runner-dotnet`: jedno zdanie w
`docs/szczegoly.md` §2 "Moduły współdzielone", że paczka z samymi bibliotekami to `##Shared` plus pusty
`##Dll` (commit bez podbicia `VERSION`, push nic nie wydaje).

### Weryfikacja

1. `dotnet publish -c Release` Connection: zip = 4 pliki, bez `InsERT.*`; nupkg jak dotąd.
2. `update-nexo-sdk.ps1 -Version 61.1.1 -RunnerDir <katalog testowy> -NoRestart` na Twojej maszynie:
   `Modules\Nexo.Sdk.zip` z 657 plikami + 2 markery; drugi przebieg z `-SdkDir C:\nexoSDK_61.1.0.9431\Bin`.
3. Uprząż z czterema zipami (Sdk, Connection, TestConnect, Invoices): w logu dwa wpisy
   `Moduł współdzielony`, dla `Nexo.Sdk` "udostępnia tylko biblioteki"; obie metody ogłoszone;
   "Who am I" z SDK 61.1.0 na bazie demo zwraca operatora, w logu zadania linia z wersją SDK.
4. Niezgodność: to samo z SDK 61.1.1 przeciw bazie demo. Jeśli Sfera odrzuci, komunikat ma obie
   informacje (wersja SDK, treść Sfery) i nazwę skryptu; zapisać dokładną treść wyjątku Sfery.
   Jeśli nie odrzuci (różnica tylko w patchu), powtórzyć ze starszym SDK, np. 60.1.0.
5. Bez `Nexo.Sdk.zip`: Error przy starcie dla obu metod Nexo z `InsERT.Moria.Sfera` w powodzie,
   runner bez wyjątku.

## Krok 5: build modułów dla każdej nowej wersji SDK na VM z Subiektem (ZROBIONE 16.09, commity lokalne)

Wyniki weryfikacji 16.09 na Twojej maszynie (`-ReleasesDir` i `-SdkRoot` w katalogu tymczasowym):
- `-Version 61.1.1`: pobranie z FTP (5 min), cztery buildy zielone przeciw 61.1.1 (bramka kompilacji
  przeszła), test na żywo rozpoznał niezgodność z bazy `Nexo_Bizhouse` 61.1.0.9431, komplet `sdk-61.1.1`
  opublikowany z `testedLive=false`. Druga wersja z `-SdkDir` i `-SkipLiveTest`: komplet `sdk-61.1.0`.
  Tryb listingu bez `-Version`: "Brak nowych wersji SDK na FTP", kod 0.
- Skrypt kliencki z `-ModulesPath`: komplet 61.1.1 podmienił Connection i TestConnect z kontrolą SHA-256,
  Invoices pominięty, bo runner go nie miał; dla 60.1.0 ostrzeżenie o braku kompletu, moduły zostały.
- Testów na żywo z zielonym wynikiem nie uruchamiałem: `nexoModule.json` testów wskazuje `Nexo_Bizhouse`,
  a testy faktur zakładają dokumenty. To punkt 2 weryfikacji, na VM, po Twojej stronie.
- Zmiany poza skryptem: `ZapqioModules.Test` zwraca kod wyjścia (0 OK, 1 błędy, 2 nic nie wybrano),
  `update-nexo-sdk.ps1` dostał `-ModulesUrl`/`-ModulesPath`. Commit w `zapqio-modules` obejmuje tylko
  nowe pliki (`build-modules.ps1`, `README.md`); `Program.cs`, csproj, `NexoTestSuite.cs` i `slnx` czekają
  w drzewie roboczym razem z Twoimi zmianami.
- Pułapki PowerShell 5.1 obsłużone w skrypcie: `$PSCommandPath` puste w wartościach domyślnych `param()`,
  `Start-Process -PassThru` bez `-Wait` gubi `ExitCode` (trzeba dotknąć `$p.Handle`), pusta
  `-ArgumentList` jest odrzucana, stderr programów zamieniany na ErrorRecord (stąd `Start-Process`
  z przekierowaniem do plików).
- Do ustalenia zostało: gdzie publikować komplety dla klientów (dziś katalog lokalny, `-GitHubRepo` gotowe,
  ale nietestowane, bo `gh` nie jest tu zalogowany) i `Module.Core` na VM (kopia nupkg w lokalnym źródle).
  Odczyt wersji Subiekta rozwiązany bez rejestru: z odpowiedzi Sfery przy pierwszym połączeniu testów.

Cel: każda nowa wersja SDK na FTP InsERT jest budowana i testowana u Ciebie automatycznie, zanim klient
zaktualizuje Subiekta, a klient dostaje gotowy komplet zipów per wersja. Bez GitHub Actions: skrypt
PowerShell i Harmonogram zadań Windows na VM z Subiektem (zawsze włączona, widzi bazę testową).
Wiązanie po nazwie zostaje fundamentem, komplety per wersja są warstwą wygody nad nim. Budowanie
u klienta odrzucone: wymagałoby .NET SDK, źródeł i paczek NuGet na każdej maszynie klienta.

### Punkt 0: naprawa `ZapqioModules.Test` po kroku 3 (ZROBIONE 16.09, build zielony, nieuruchamiane)

Projekt odwoływał się do usuniętego `Nexo.csproj` i do `Settings.Connect`. Poprawka: `ProjectReference`
do `Nexo.Invoices.csproj`, `PackageReference Zapqio.Nexo.Connection` bez `ExcludeAssets` (test chodzi poza
runnerem, więc DLL i SDK muszą być w wyjściu), `NexoSdkCopyLocal=true`, siedem powtórzonych referencji
SDK i `nexoSdkBinPath` usunięte (przychodzą z props paczki), `new NexoClient(new ConnectionSettings())`,
`NexoTestSuite` bierze dane bazy z `ConnectionSettings`, `ZapqioModules.slnx` na `Nexo.Invoices.csproj`.
Bez commita: repo ma Twoje niezacommitowane zmiany w tych samych plikach.

### `build-modules.ps1` (repo `zapqio-modules`, Harmonogram zadań co godzinę)

1. Listing FTP `https://ftp.insertcdn.pl/pub/aktualizacje/InsERT_nexo/`, regex
   `nexoSDK_(\d+)_(\d+)_(\d+)\.exe`, porównanie z plikiem stanu `built-versions.json` (wersja, wynik, data).
   Nowe wersje po kolei od najstarszej; parametr `-Version` wymusza jedną wersję (do testów i na żądanie).
2. Pobranie do `%TEMP%`, `-y -o C:\nexoSDK\` daje `C:\nexoSDK\nexoSDK_<pełna wersja>\Bin`; katalogi SDK
   starsze niż ostatnie N kasowane.
3. `git pull` w repo: `module-nexo-connection`, `module-nexo-testconnect`, `module-nexo`, `zapqio-modules`.
4. Build w kolejności: Connection `dotnet publish -c Release -p:nexoSdkBinPath=<Bin>` (zip + nupkg do
   lokalnego źródła; przy niezmienionym `Version` skasować `%USERPROFILE%\.nuget\packages\zapqio.nexo.connection`
   przed restore konsumentów), potem TestConnect i Invoices z tym samym `-p:nexoSdkBinPath` (props go
   honoruje), na końcu build `ZapqioModules.Test`.
5. Wersja Subiekta na VM (odczyt do ustalenia: rejestr "InsERT nexo" `DisplayVersion` albo wersja pliku
   w katalogu instalacji) równa wersji SDK: uruchomienie `ZapqioModules.Test` (flagi w `Program.cs`, dziś
   `InvoiceTests`, każdy przebieg tworzy dokumenty w bazie testowej; `SlackTests` wyłączone). Inna wersja:
   wynik "build OK, nieprzetestowane na żywo".
6. Publikacja: `Nexo.Connection.zip`, `Nexo.TestConnect.zip`, `Nexo.Invoices.zip` do katalogu wydań
   `sdk-<wersja>` (udział albo HTTP na platformie, albo `gh release create sdk-<wersja>` w `zapqio-modules`;
   przy prywatnym repo klient potrzebuje tokenu), wpis w pliku stanu, log do pliku, wiadomość na Slacku
   (istniejący `SlackClient`) z wynikiem per moduł.
7. Czerwony build: brak publikacji, Slack z pierwszym błędem kompilacji, czyli nazwą typu albo metody
   SDK, której zabrakło. To jest bramka, która odpowiada na "skąd będę wiedział, że InsERT zmienił API".

### Skrypt kliencki z kroku 4

`update-nexo-sdk.ps1` dostaje krok "pobierz komplet modułów dla wersji": jeśli wydanie `sdk-<wersja>`
istnieje, podmienia też zipy modułów; jeśli nie, podmienia sam SDK, zostawia obecne moduły i wypisuje
ostrzeżenie z prośbą o ponowne uruchomienie później.

### Weryfikacja

1. Na Twojej maszynie `build-modules.ps1 -Version 61.1.1`: SDK pobrane i rozpakowane, trzy zipy
   zbudowane, test pominięty z komunikatem o braku Subiekta.
2. Na VM z Subiektem: pełny przebieg dla wersji zgodnej z Subiektem, `ZapqioModules.Test` zielone,
   komplet w katalogu wydań, wiadomość na Slacku.
3. Harmonogram: zadanie co godzinę, konto z dostępem do bazy testowej, opcja uruchomienia zaległego.

### Do ustalenia przy implementacji

- gdzie leżą komplety per wersja (udział, platforma, GitHub Releases),
- sposób odczytu wersji Subiekta na VM,
- `Zapqio.Runner.Module.Core` na VM: kopia nupkg w lokalnym źródle do czasu nuget.org.

## Krok 6: automatyczna podmiana SDK na runnerze (ZROBIONE 16.09, commity lokalne)

Wyniki weryfikacji 16.09 (uprząż, SDK 61.0.2 przeciw `Nexo_Bizhouse` 61.1.0, `Restart=false`):
- "Who am I": `Connecting Nexo (SDK 61.0.2.9383 z paczki Nexo.Sdk)`, potem `uruchomiono automatyczną
  podmianę SDK na 61.1.0`, wyjątek z tym samym zdaniem; skrypt skopiowany z paczki do katalogu runnera.
- Skrypt w tle przeżył zakończenie procesu uprzęży: pobranie 61.1.0 z FTP (5 min), nowy `Nexo.Sdk.zip`,
  stan `ok`, `nexo-sdk.json`; kolejne wywołanie: `Connecting Nexo (SDK 61.1.0.9431 ...)` i operator.
- Blokada: stan `running` daje "już trwa (od ...)", stan `failed` daje "kolejna próba po godzinie",
  bez uruchomienia skryptu (zero nowych linii w logu).
- SDDL z `install.ps1` zbudowany na sucho na usłudze Spooler: parsuje się, 4 ACE, bez `sdset`.
- Nieprzetestowane u mnie: `Restart-Service` z konta usługi po nadaniu prawa i zapas `Stop-Process`
  z odzyskiwaniem SCM. To VM z Subiektem i instalacja przez nowy `install.ps1`, po Twojej stronie.
- Uwaga testowa: proces skryptu dziedziczy uchwyty konsoli runnera, więc narzędzie czekające na koniec
  wyjścia runnera czeka też na skrypt. W usłudze bez znaczenia.

Cel: po aktualizacji Subiekta u klienta nikt nie uruchamia skryptu ręcznie. Decyzja z rozmowy: `NexoClient`
uruchamia `update-nexo-sdk.ps1` bezpośrednio w chwili błędu, bez zadania w harmonogramie. Warunki, które
to umożliwiają (z `install.ps1` runnera): usługa działa jako `NT SERVICE\ZapqioRunner` z prawem zapisu
w katalogu instalacji, ma opcje odzyskiwania (restart po 5 s po nieoczekiwanym zakończeniu). Brakuje
prawa do restartu własnej usługi, które dokłada `install.ps1` (`sc sdset`, ACE start/stop dla SID konta
usługi); skrypt ma zapas dla starszych instalacji: zamiast `Restart-Service` kończy proces runnera,
a SCM podnosi go z opcji odzyskiwania.

### Moduł

- `ConnectionSettings.SdkUpdate` w `nexoModule.json`: `Enabled` (domyślnie true), `ModulesUrl`,
  `ServiceName` (`ZapqioRunner`), `Restart` (true; false w konsoli i testach), `RunnerDir`
  (puste = `AppDomain.BaseDirectory`).
- `update-nexo-sdk.ps1` jedzie w `Nexo.Connection.zip`; `NexoClient` przy tworzeniu kopiuje go do
  katalogu runnera, gdy go tam nie ma albo jest starszy. Klient nie instaluje nic poza zipem.
- `NexoClient` przy niezgodności wersji (regex na treści Sfery) uruchamia w tle
  `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File update-nexo-sdk.ps1 -Version <X>
  -RunnerDir ... -ServiceName ... [-ModulesUrl ...] [-NoRestart] -Log Logs\nexo-sdk-update.log -State
  nexo-sdk-update.json`, pisze do logu zadania "uruchomiono automatyczną podmianę SDK, usługa zostanie
  zrestartowana" i rzuca wyjątek jak dziś (z dopiskiem o podmianie zamiast wskazówki ręcznej).
- Blokada w `nexo-sdk-update.json` obok binarki: ta sama wersja nie jest ponawiana przez godzinę,
  a trwająca podmiana (stan `running` młodszy niż 30 min) nie jest dublowana przez kolejne padające zadania.
- Skrypt: `-Log` (dopisywanie do pliku, bo z usługi nie ma konsoli), `-State` (wynik na końcu), katalog
  roboczy `<RunnerDir>\.sdk-tmp` zamiast `%TEMP%` (konto usługi), `Restart-Service` z zapasem
  `Stop-Process` na procesie runnera, po podmianie `nexo-sdk.json` z aktualną wersją SDK.

### Runner (`install.ps1`, bez zmiany wersji, 0.1.9 jeszcze nie wyszło)

Po ustawieniu konta usługi: SID konta z `NTAccount("NT SERVICE\<usługa>")`, `sc sdshow`, dopisanie
`(A;;CCLCSWRPWPDTLOCRRC;;;<SID>)` do części `D:`, `sc sdset`. Pomijane przy `-LocalSystem` (ma prawa)
i gdy ACE już jest. Błąd nadania to ostrzeżenie, nie przerwanie instalacji. Docs §3: jedno zdanie.

### Weryfikacja

1. Uprząż: SDK 61.1.1 + Connection (nowy zip ze skryptem) przeciw bazie 61.1.0, `SdkUpdate.Restart=false`,
   `RunnerDir` = katalog uprzęży. "Who am I" pada z komunikatem o uruchomionej podmianie, skrypt skopiowany
   do katalogu runnera, w tle pobranie 61.1.0 z FTP i nowy `Nexo.Sdk.zip`, `nexo-sdk-update.json` ze stanem.
   Po zakończeniu drugie uruchomienie uprzęży: "Who am I" zwraca operatora.
2. Blokada: drugie wywołanie w trakcie podmiany nie uruchamia drugiego skryptu (wpis w logu zadania).
3. Budowa SDDL na sucho na istniejącej usłudze (bez `sdset`), żeby funkcja z `install.ps1` była sprawdzona.
4. Pełna pętla z restartem usługi i nadaniem prawa przez `install.ps1`: VM z Subiektem, po Twojej stronie.

### Poprzedni wariant (odrzucony na rzecz powyższego)

Podział ról, który omija ograniczenia z dyskusji (konto usługi nie restartuje usług, podmiana w środku zadania
ubija inne zadania): **moduł sygnalizuje, zadanie systemowe działa.** Zostawiony dla porównania.

### Sygnał z modułu

`NexoClient` przy `NexoConnectionException`, gdy treść Sfery pasuje do
`Wersja bazy danych to (X), a wersja Sfery to (Y)`, zapisuje obok binarki runnera
(`AppDomain.BaseDirectory`, tam gdzie `nexoModule.json`) plik `nexo-sdk-mismatch.json`:
`{ "subiekt": "61.1.1.9471", "sdk": "61.1.0.9431", "at": "<czas>" }`. Wyjątek leci dalej jak dziś, więc
zadanie kończy się błędem z tą samą treścią. Zapis do pliku jest w try/catch, brak prawa zapisu nie
zmienia zachowania metody.

### `watch-nexo-sdk.ps1` (zadanie systemowe na runnerze)

Uruchamiane z Harmonogramu zadań co 2 minuty jako SYSTEM (ma prawo restartować usługę). Parametry:
`-RunnerDir`, `-ServiceName`, `-ModulesUrl`/`-ModulesPath` (przekazywane dalej), `-SubiektDir`
(opcjonalnie, tryb proaktywny), `-Register` / `-Unregister` (zakłada albo usuwa zadanie; `-Register`
zapisuje parametry w argumentach zadania).

Jeden przebieg:
1. **Reaktywnie:** jest `nexo-sdk-mismatch.json` -> wersja Subiekta X z pliku -> `update-nexo-sdk.ps1
   -Version <X w trzech członach>` (pobranie z FTP InsERT, `Nexo.Sdk.zip`, komplet modułów, restart) ->
   po sukcesie plik sygnału kasowany. Nieudana próba (np. brak wersji na FTP, brak sieci) zapisuje się
   w `nexo-sdk-watch.json` i ta sama wersja nie jest ponawiana przez godzinę, żeby nie kręcić się
   w kółko przy pobieraniu 466 MB.
2. **Proaktywnie, gdy podano `-SubiektDir`:** wersja `Subiekt.exe` z katalogu instalacji (ProductVersion)
   porównana z wersją w `nexo-sdk.json`, który `update-nexo-sdk.ps1` zapisuje po każdej podmianie.
   Różnica -> ta sama podmiana, zanim jakiekolwiek zadanie zdąży paść. Ścieżka i to, czy `Subiekt.exe`
   niesie właściwą wersję, do sprawdzenia na VM z Subiektem; bez `-SubiektDir` działa tylko tryb 1.
3. Log do `Logs\nexo-sdk-watch.log` w katalogu runnera, po jednej linii na przebieg z akcją.

`update-nexo-sdk.ps1` dostaje dodatkowo zapis `nexo-sdk.json` (`{ "version": "61.1.1.9471", "at": ... }`)
po podmianie, żeby tryb proaktywny miał z czym porównywać.

### Co widzi klient

Aktualizuje Subiekta. Bez `-SubiektDir`: pierwsze zadania Nexo padają z komunikatem o wersji, w ciągu
2 minut zadanie systemowe pobiera SDK (1-5 min zależnie od łącza), podmienia, restartuje usługę, kolejne
zadania działają. Z `-SubiektDir`: podmiana zwykle zdąży przed pierwszym zadaniem. Zadania, które padły,
platforma pokazuje jako ERROR do ręcznego ponowienia, tak jak każdy inny błąd metody.

### Instalacja u klienta

Raz, jako administrator, w katalogu runnera:
`.\watch-nexo-sdk.ps1 -Register -RunnerDir C:\zapqio\runner -ModulesUrl <adres kompletów> [-SubiektDir "C:\Program Files\InsERT\..."]`.
README Connection: sekcja "Aktualizacja Subiekta" przepisana na wariant automatyczny, ręczny zostaje
jako zapasowy.

### Weryfikacja

1. Sygnał: uprząż z `Nexo.Sdk.zip` 61.1.1 przeciw bazie 61.1.0 -> obok binarki powstaje
   `nexo-sdk-mismatch.json` z obiema wersjami; przy zgodnych wersjach plik nie powstaje.
2. Watcher bez rejestracji, jednorazowo (`-Once`): z plikiem sygnału i `-NoRestart` przekazanym dalej ->
   pobranie 61.1.0 (`-SdkDir` w testach zamiast FTP), nowy `Nexo.Sdk.zip`, plik sygnału skasowany,
   wpis w logu; drugi przebieg bez sygnału -> "nic do zrobienia"; nieudana próba -> wpis o odroczeniu.
3. `-Register` na tej maszynie z katalogiem Desktop\Runner (bez usługi, więc `-NoRestart`): zadanie
   w Harmonogramie widoczne, przebieg co 2 minuty w logu; `-Unregister` usuwa.
4. Pełna pętla z restartem usługi: na VM z Subiektem, po Twojej stronie.

## Następny temat (16.09, jeszcze nie omawiany): instalacja dla klienta w jednym miejscu

Krok 6 sprawdzony na maszynie z testowym Subiektem (celowo zła wersja 58.0.1, automatyczna podmiana
i restart zadziałały). Opis instalacji jest dziś rozrzucony po README czterech repo i docs runnera;
klient potrzebuje jednej ścieżki od zera: runner (`install.ps1`), cztery zipy, skrypt SDK,
`nexoModule.json`, sprawdzenie w panelu. Do ustalenia, gdzie to ma żyć i w jakiej formie.

## Krok 7: katalog `Config\` na konfigurację modułów (ZROBIONE 16.09, commity lokalne)

Wyniki weryfikacji 16.09 (uprząż, SDK 61.1.0, baza `Nexo_Bizhouse`):
- Świeży katalog: `Config\nexoModule.json` z pustym `Connect`, domyślnym `SdkUpdate` i kluczami faktur
  (dopisane przez `Settings` z Invoices); "Who am I" pada natychmiast z listą brakujących pól
  (`DatabaseServer, DatabaseName, DatabaseUser, UserName`), bez próby łączenia.
- Plik uzupełniony danymi bazy: operator zwrócony; do skopiowanego pliku bez `SdkUpdate` sekcja dopisała
  się sama (merge rekurencyjny).
- Stary `nexoModule.json` obok binarki, bez `Config\`: przeniesiony, połączenie działa.
- Runner: testy 47 + 90 zielone, `install.ps1` parsuje się; ACL na `Config\` nietestowane tu (wymaga
  instalacji), na maszynie z testowym Subiektem po Twojej stronie: nowy `install.ps1` z release'u po
  pushu albo ręcznie `icacls Config /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F "NT SERVICE\ZapqioRunner:(OI)(CI)M"`.
- Uwaga: w `module-nexo` leży nieśledzony `Nexo.slnx` z IDE (wskazuje usunięty `Nexo.csproj`); przez niego
  `dotnet publish` bez nazwy projektu nie wie, co budować. Do skasowania.
- `ZapqioModules.Test.csproj` (paczka 1.0.3, kopia `nexoModule.json` do `Config\`) nadal niezacommitowany
  razem z Twoimi zmianami w tym repo.

Decyzja z rozmowy: konfiguracja modułów zostaje w plikach obok runnera, ale w osobnym katalogu `Config\`
z ochroną jak `appsettings.json`. Moduł czyta plik, jeśli jest; jeśli nie ma, tworzy go z pustymi
danymi, a przy pierwszym połączeniu kończy zadanie czytelnym błędem o braku konfiguracji.

### Runner (`runner-dotnet`, bez zmiany wersji)

- `MethodsProvider` tworzy przy starcie `Config\` obok `Modules\`, tak jak dziś tworzy `Modules\`.
- `install.ps1`: `Config\` z ACL jak `appsettings.json` z tokenem: SYSTEM i Administratorzy pełne, konto
  usługi Modify (musi założyć plik), bez dziedziczenia, więc inni użytkownicy maszyny nie czytają haseł.
- Docs §2: podsekcja "Konfiguracja modułów": `Config\<nazwa>.json`, przeżywa podmianę zipów, ACL.
  Żadnej zmiany w `Module.Core`: konwencja to `Path.Combine(AppContext.BaseDirectory, "Config")`.

### `module-nexo-connection` (paczka 1.0.3)

- Nowa publiczna klasa `NexoConfig`: ścieżka `Config\nexoModule.json`, `Populate<T>(T instance)` dla klas
  ustawień. Wczytuje plik, dopisuje do niego brakujące klucze z wartości domyślnych `T` (JsonObject,
  zapis tylko gdy coś doszło) i wypełnia instancję. Dzięki temu po starcie wszystkich modułów Nexo plik
  ma komplet kluczy do uzupełnienia, choć każdy moduł zna tylko swoje. Guard rekurencji (deserializacja
  woła konstruktor) w jednym miejscu zamiast w każdej klasie.
- Migracja: gdy `Config\nexoModule.json` nie istnieje, a stary `nexoModule.json` obok binarki jest,
  plik jest przenoszony. Istniejące instalacje działają bez ręcznych kroków.
- `ConnectionSettings.Connect`: wartości domyślne puste (serwer, baza, użytkownik SQL, hasło, operator),
  `WindowsLogin=false`. `SdkUpdate` bez zmian.
- `NexoClient.Connection()`: przed Sferą sprawdzenie `DatabaseServer` i `DatabaseName`; puste = od razu
  `NexoConnectionException` "Brak konfiguracji połączenia z Nexo: uzupełnij sekcję Connect w
  `<pełna ścieżka pliku>` i zrestartuj usługę". Metoda pozostaje ogłoszona (błąd przy wywołaniu, jak
  ustalono), a nie wyłączona przy starcie.
- README: instalacja z krokiem "uzupełnij `Config\nexoModule.json`".

### `module-nexo` (Invoices)

`Settings` przechodzi na `NexoConfig.Populate(this)` (paczka Connection 1.0.3), usuwa własny odczyt pliku
i guard. Klucze faktur dopisują się do wspólnego pliku przy pierwszym starcie modułu.

### Weryfikacja

1. Uprząż w świeżym katalogu bez `Config\`: po starcie jest `Config\nexoModule.json` z pustym `Connect`,
   `SdkUpdate` z domyślnymi i kluczami faktur; "Who am I" kończy się błędem o braku konfiguracji w czasie
   poniżej sekundy, bez próby łączenia.
2. Ten sam katalog po wpisaniu danych bazy: "Who am I" zwraca operatora.
3. Stary układ (plik obok binarki, bez `Config\`): plik przeniesiony, połączenie działa, stary znika.
4. `install.ps1` z `Config\`: ACL na katalogu (`icacls Config`) bez wpisu dla Users, usługa Modify.
5. Na maszynie z testowym Subiektem po Twojej stronie: aktualizacja zipów, restart, plik przeniesiony.

## Do potwierdzenia

- nazwy repo jak wyżej,
- paczki dla klientów: na razie lokalny folder na Twojej maszynie, nuget.org później.
