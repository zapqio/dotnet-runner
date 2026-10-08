# Moduły współdzielone: runner + podział module-nexo na osobne repo

Plan z 14.09.2026 (`~/.claude/plans/shimmering-roaming-pony.md`), odświeżony 15.09 rano po ponownym
przejrzeniu kodu i przepisany 15.09 po południu pod osobne repo per moduł (Twoja decyzja: z modułów będą
korzystać różni klienci, część dostaną gotową). Do wdrożenia po Twoim wyraźnym "implementuj". Nic z tego
nie jest jeszcze w kodzie.

## Co się zmieniło względem wersji monorepo z 15.09 rano

- **Trzy repo zamiast trzech projektów w `module-nexo`:** `module-nexo-connection` (moduł współdzielony,
  gotowy dla klientów), `module-nexo-testconnect` (gotowy test instalacji), `module-nexo-invoices`
  (dzisiejsze `module-nexo` po zmianie nazwy). Nazwy to moja propozycja, patrz "Pytania otwarte".
- **Spoiwem jest paczka NuGet `Zapqio.Nexo.Connection`, nie submoduł.** Konsument ma
  `PackageReference ... ExcludeAssets="runtime"`, czyli ten sam mechanizm, którym `Nexo.csproj` bierze dziś
  `Zapqio.Runner.Module.Core` (linia 64): API do kompilacji z paczki, DLL w runtime z jednej paczki `##Shared`
  w `Modules\`. Submoduł odpada: przypina źródła, a nie DLL, z którym konsument wiąże się w runtime,
  dubluje build SDK w każdym repo konsumenta i wymaga bumpu wskaźnika przy każdej zmianie API.
- **Referencje SDK jadą w paczce** (`build/Zapqio.Nexo.Connection.props`, NuGet importuje go do każdego
  konsumenta). Csproj konsumenta to dwie linie `PackageReference` i target zip; klient piszący własny
  moduł nie przepisuje ośmiu `<Reference>` do InsERT. Ścieżkę SDK nadpisuje jedną właściwością.
- **`Nexo.Connection` dostaje wersję.** `Version` paczki (od 1.0.0) i przypięte `AssemblyVersion 1.0.0.0`
  jak w `Module.Core`: konsument skompilowany przeciw 1.0.0 wiąże się z każdym 1.x w runtime. W wersji
  monorepo było `GenerateAssemblyInfo=false` (0.0.0.0); przy paczce wersja musi istnieć.
- **Kanał dystrybucji: na razie lokalny folder jako źródło NuGet**, tak jak dziś `Module.Core` (źródło
  "Runner module core" w `%APPDATA%\NuGet\NuGet.Config` wskazuje `Runner.Module.Core\bin\Release`).
  Żadnej z paczek nie ma na nuget.org, a klient piszący moduł potrzebuje obu. To pytanie otwarte; plan
  działa z każdą odpowiedzią, bo zmienia się tylko adres źródła.
- **Buildu modułów Nexo nie da się przenieść do GitHub Actions** (SDK leży w `C:\nexoSDK_<wersja>\Bin\`,
  nie ma go na hostowanych runnerach). Publish, pack i zip zostają lokalne i ręczne, jak dziś.
- **Krok 1 (runner) bez zmian w kodzie.** W docs dochodzi podsekcja "Jak zbudować paczkę modułu"
  (csproj, `ExcludeAssets="runtime"`, target zip z `##Dll`, `##Shared` dla modułu współdzielonego) oraz
  zdanie, że konsument bierze API modułu współdzielonego z paczki NuGet z `ExcludeAssets="runtime"`.

## Co się zmieniło względem wersji z 14.09

- **Wersja release'u: 0.1.9.** `0.1.8` wyszło dziś rano (commit `14c5612`, reconnect i `ActiveAttemptIds`),
  więc krok 1 kończy się commitem `Release v0.1.9`.
- **Jawna kolejność paczek.** `EnumerateFiles("*.zip")` nie gwarantuje kolejności (NTFS zwraca alfabetycznie,
  ext4 nie), a od niej zależy, który z dwóch modułów współdzielonych z tym samym zestawem wygra. Dochodzi
  `OrderBy(Name, OrdinalIgnoreCase)` w `CheckCacheOrCreate`.
- **Piąty test** odzwierciedlający test negatywny z weryfikacji (pkt 7): konsument bez modułu współdzielonego,
  metoda pominięta z błędem w logu, `GetMethods()` nie rzuca.
- **Logger zbierający wpisy** w testach zamiast `NullLogger` - inaczej nie da się sprawdzić, że kopia DLL
  została pominięta ani że metoda nie powstała z właściwym powodem.
- **`PrivateAssets="all"`** na referencjach między modułami testowymi: bez tego SDK przepuszcza je tranzytywnie
  do katalogu binarki testów, host rozwiązuje je sam po nazwie i test niczego nie dowodzi.
- **Moduł współdzielony bez wstrzyknięć** (samo SDK) dostaje wpis Information zamiast dzisiejszego
  ostrzeżenia "nie dostarczył żadnej metody ani wstrzyknięcia".
- **Pytanie otwarte:** `Dispose()` ma tylko odpinać handler; kontener modułów zostaje nie zwalniany jak dziś
  (`NexoClient.Dispose`, czyli `Uchwyt.Dispose`, nigdy nie jest wołane przy zatrzymaniu usługi). Zmiana tego
  to osobna decyzja - w planie zostawiam zachowanie bez zmian.

## Kontekst

Moduł `Nexo` to jedna DLL kodu i 539 MB SDK InsERT (zip 182 MB). Chcemy jedno połączenie do
Nexo (`NexoClient`, jeden `Uchwyt`, wiele wątków) w module współdzielonym `Nexo.Connection`, a metody
w osobnych, chudych modułach: `Nexo.TestConnect` (pierwszy konsument, "Who am I") i `Nexo.Invoices`.
Każdy moduł ma własne repo, bo z modułów będą korzystać różni klienci, a część dostaną gotową; klient
może też napisać własny moduł na `Nexo.Connection`, mając tylko dwie paczki NuGet i zainstalowane SDK.

Runner ma zostać niezależny od modułów. Współdzielenie usług przez DI już działa (`IRunnerInjection`
rejestruje singleton, konsument wstrzykuje typ konkretny; wszystkie skany kończą się przed pierwszą
konstrukcją). Brakuje trzech rzeczy w loaderze, wszystkie generyczne:

1. Konsument odwołuje się do zestawów, których moduł współdzielony sam nigdy nie ładuje
   (np. `InsERT.Moria.ModelDanych`). Dziś `LoadFrom` probuje tylko katalog zestawu, który prosi.
2. Kopia DLL modułu współdzielonego w paczce konsumenta rejestruje te same typy drugi raz.
3. Jeden rzucający konstruktor metody wywraca `GetServices<IRunnerMethod>()` w `SendInfo`
   (`WSClient.cs:307`), wyjątek łapie pętla w `RequestBindBackground.cs` ("Main loop") i runner łączy się
   w kółko, nie ogłaszając żadnej metody.

Liczba połączeń do Nexo nie zależy od układu repo ani od tego, czy konsument bierze API z paczki, czy
z `ProjectReference`. Zależy od tego, ile paczek w `Modules\` niesie `NexoClient`: `ExcludeAssets="runtime"`
nie kopiuje DLL do zipa konsumenta, a gdyby ktoś o tym zapomniał, runtime i tak nie załaduje dwóch zestawów
o tej samej nazwie do domyślnego kontekstu (identyczna binarka jest pomijana, inna rzuca `FileLoadException`).

`Module.Core` bez zmian (zostaje 1.1.0). Fakty o SDK (AssemblyVersion zawsze 1.0.0.0 itd.) są w
pamięci projektu; tu nie są potrzebne.

## Kolejność

1. Runner (`runner-dotnet`): `MethodsProvider` + testy + docs. Osobny commit, potem `Release v0.1.9`
   (push po Twojej stronie - push na `main` wydaje release).
2. Nowe repo `module-nexo-connection` i `module-nexo-testconnect` (pliki skopiowane z `module-nexo`,
   które w tym kroku zostaje nietknięte i nadal buduje stary `Nexo.zip`). Lokalne źródło NuGet.
3. Zmiana nazwy `module-nexo` na `module-nexo-invoices`; `Nexo.Invoices` przechodzi na paczkę
   `Zapqio.Nexo.Connection` (podział `Settings`).

Kroki 2 i 3 rozdzielone, żeby mechanizm sprawdzić na `TestConnect`, zanim ruszą faktury
(weryfikacja pkt 5-7 wymaga panelu Web, czyli Ciebie).

---

## Krok 1: runner

### `Zapqio.Runner/MethodsProvider.cs`

**Konstruktor testowalny.** Obecny konstruktor `MethodsProvider(ILogger<MethodsProvider>)` zostaje (używa go
`RunnerReconnectTests`) i deleguje do nowego `MethodsProvider(ILogger<MethodsProvider>, DirectoryInfo modules,
DirectoryInfo cache)`; statyczne `DirModules`/`DirModulesCache` zostają jako domyślne. Klasa implementuje
`IDisposable` (odpięcie handlera, patrz niżej); host trzyma ją jako singleton do końca procesu.

**Marker `##Shared` i dwa przebiegi w `AddModules`.**
- `CheckCacheOrCreate()` sortuje zipy po nazwie (`OrdinalIgnoreCase`) - kolejność jawna, nie z systemu plików.
- Przebieg 1: dla każdego katalogu sprawdź `File.Exists("##Shared")`; trafienia dodaj do
  `List<DirectoryInfo> _sharedDirs` (kolejność = kolejność zipów). Log Information:
  `Moduł współdzielony: {Module} - jego katalog służy do rozwiązywania zestawów innych modułów`.
- Przebieg 2: dotychczasowy skan wszystkich modułów (współdzielonych też, po ich `##Dll`).
  Dzięki temu kolejność zipów nie ma znaczenia i żadne wiązanie nie zdąży zawieść przed
  zarejestrowaniem katalogów.
- Moduł współdzielony, w którym skan nic nie znalazł, dostaje Information
  `Moduł współdzielony {Module} nie dostarczył żadnej metody ani wstrzyknięcia - udostępnia tylko biblioteki`
  zamiast ostrzeżenia (paczka z samym SDK to poprawny przypadek).

**Handler rozwiązywania.** Rejestrowany w konstruktorze, przed pierwszym `LoadFrom`:
`AppDomain.CurrentDomain.AssemblyResolve += ResolveFromSharedModules`. Dlaczego `AssemblyResolve`,
a nie `AssemblyLoadContext.Default.Resolving`: tylko `ResolveEventArgs` niesie `RequestingAssembly`,
a `Assembly.LoadFrom` sam używa tego samego zdarzenia (jego wewnętrzny handler probuje katalog
zestawu proszącego). Nasz handler:
1. jeśli zestaw proszący nie jest dynamiczny (`IsDynamic` - dla dynamicznych `Location` rzuca) i ma `Location`,
   probuj jego katalog (`<Name>.dll`) - ta sama semantyka co `LoadFrom`, więc kolejność handlerów nie ma
   znaczenia; własny katalog modułu wygrywa,
2. potem `_sharedDirs` po kolei,
3. trafienie ładuj przez `Assembly.LoadFrom(path)` (nie `LoadFromAssemblyPath`), żeby zestaw z
   katalogu współdzielonego sam rozwiązywał swoje zależności z tego katalogu, także po odpięciu handlera;
   `FileNotFoundException`/`FileLoadException`/`BadImageFormatException` przy ładowaniu = log Debug i następny katalog,
4. Debug: `Zestaw {Name} dla {Requestor} rozwiązany z modułu współdzielonego {Module}`;
   brak trafienia zwraca `null` bez logu (zdarzenie pada też dla zasobów satelickich itp.).
`Dispose()` odpina handler. Kontener modułów nie jest zwalniany (jak dziś - patrz pytanie otwarte).

**Deduplikacja skanu.** `Dictionary<Assembly, string> scanned` (zestaw -> moduł) na jeden przebieg. Gdy
`Assembly.LoadFrom(dll)` zwróci zestaw już obecny w `scanned` (identyczna binarka w drugiej paczce -> runtime
zwraca załadowany), pomiń z logiem Information:
`Pomijam {Dll} z modułu {Module}: ten zestaw został już załadowany z modułu {Other}`.
Reguła jest "już zeskanowany w tym przebiegu", nie "Location != ścieżka" - inaczej testy (i każdy
kolejny `MethodsProvider` w procesie) pominęłyby zestawy załadowane wcześniej z innego katalogu.
Inna binarka pod tą samą nazwą rzuca dziś `FileLoadException` i trafia w istniejące ostrzeżenie
`Nie udało się załadować` (komentarz przy nim dopisuje ten powód).

**Tworzenie metod pojedynczo.** Zamiast `AddSingleton(typeof(IRunnerMethod), type)`:
- `AddSingleton(type)` + `List<(Type, string Module)> _methodTypes`; `HashSet<Type> registered` pilnuje, żeby typ
  będący naraz `IRunnerInjection` i `IRunnerMethod` był zarejestrowany raz,
- `Lazy<IReadOnlyList<IRunnerMethod>> _methods`: dla każdego typu `GetRequiredService(type)` w
  try/catch; wyjątek -> log Error
  `Metoda {Type} z modułu {Module} nie została utworzona i nie będzie ogłoszona: {Reason}`
  (`ex.Message`; dla DI to np. "Unable to resolve service for type 'Nexo.NexoClient'"), typ pominięty,
- `GetMethods()` zwraca listę z `Lazy`, `GetMethod(name)` szuka w niej. Sygnatury bez zmian, więc
  `WSClient.SendInfo` i `ExecuteJob` nie wymagają dotknięcia.
Metoda, której konstruktor padł, jest wyłączona do restartu (dziś: pętla reconnectów bez metod).

**Porządki przy okazji:** trzy komentarze w cp1250 (`okre�laj�cy`) przepisane w UTF-8; `HashFile`/`DllFile` jako `const`.

### Testy: `Zapqio.Runner.Tests/MethodsProviderTests.cs` (nowy) + moduły testowe

Loader trzeba sprawdzić na prawdziwych zestawach. Trzy małe projekty w nowym katalogu
`Zapqio.Runner.Tests.Modules/` (siostrzany do projektu testów, żeby globy `*.cs` się nie mieszały),
`net10.0`, bez `Nullable` (jak prawdziwe moduły), `ProjectReference` do `Module.Core` z `Private="false"`
(kontrakt bierze się z hosta):

| Projekt | Zawartość |
|---|---|
| `TestModule.Shared` | `SharedClient : IRunnerInjection` z `string Hello()` |
| `TestModule.Shared.Extra` | `abstract class SharedBaseMethod : IRunnerMethod`, `static class ExtraHelper` |
| `TestModule.Consumer` (referencje do obu z `Private="false" PrivateAssets="all"`) | `UsesClientMethod(SharedClient)` "uses-client", `DerivedMethod : SharedBaseMethod` "derived" (używa `ExtraHelper` w `Run`), `ThrowingMethod` "throwing" (ctor rzuca), `NeedsUnregisteredMethod(NotRegistered dep)` "needs-unregistered" |

`PrivateAssets="all"` jest konieczne: bez tego SDK dokłada referencje tranzytywne z pliku assets i
`TestModule.Shared.dll` lądowałby obok binarki testów - host rozwiązałby go sam, z pominięciem handlera.

W `Zapqio.Runner.Tests.csproj`: `ProjectReference` do trzech projektów z
`ReferenceOutputAssembly="false" OutputItemType="TestModuleDll"` i target `CopyTestModules`
(`AfterTargets="Build"`) kopiujący `@(TestModuleDll)` do `$(OutDir)TestModules\` (poza korzeniem bin,
żeby host testów nie widział ich przez własne probowanie). Wpisy do `Zapqio.Runner.slnx`
(folder `/Zapqio.Runner.Tests.Modules/`).

Testy budują zipy w katalogu tymczasowym per test (`%TEMP%\zapqio-runner-tests\modules\<guid>\`, helper
`Sandbox` z `Zip(name, dlls, scan, shared)` - `##Dll` z listą do skanowania, pusty `##Shared`), tworzą
`MethodsProvider(logger, modulesDir, cacheDir)` i sprzątają handler przez `Dispose()`. Logger to prosty
`ILogger<MethodsProvider>` zbierający `(poziom, tekst)`. Katalogów tymczasowych test nie kasuje (załadowane DLL
są zablokowane na Windows); stare kasuje następne uruchomienie testów. Stan `AssemblyLoadContext.Default`
jest globalny w procesie, więc każdy test musi przechodzić zarówno jako pierwszy (zestawy jeszcze nie
załadowane - dopiero on uruchamia rozwiązywanie), jak i kolejny (zestawy już w procesie, żadne zdarzenie nie
pada). Asercje są pisane pod oba przypadki; weryfikacja uruchamia każdy test także osobno.

1. `SharedModule_ResolvesAssembliesForConsumer_RegardlessOfZipOrder`: `A-Consumer.zip`
   (Consumer.dll, `##Dll`) + `Z-Shared.zip` (Shared.dll, Shared.Extra.dll, `##Dll`=Shared.dll,
   `##Shared`). Konsument jest alfabetycznie pierwszy. Asercje: `GetMethods()` zawiera
   "uses-client" i "derived" (ten drugi wymaga `Shared.Extra` już przy `GetTypes()`),
   `Run` obu zwraca wartości z `SharedClient`/`ExtraHelper`, w logu `Moduł współdzielony: Z-Shared`.
2. `DuplicateAssemblyInConsumer_IsScannedOnce`: konsument zawiera też kopię Shared.dll;
   nazwy metod bez duplikatów, dokładnie jeden wpis `Pomijam TestModule.Shared.dll z modułu ...`
   (Information) i jeden `Add injection: TestModule.Shared.SharedClient`.
3. `FailingConstructor_SkipsOnlyThatMethod`: lista to dokładnie {"derived", "uses-client"},
   `GetMethod("throwing")` i `GetMethod("needs-unregistered")` dają `null`, wywołanie nie rzuca,
   dwa wpisy Error z nazwą typu i powodem ("constructor failed on purpose" / "NotRegistered").
4. `MissingSharedModule_SkipsMethodsThatNeedIt_WithoutThrowing`: sam `A-Consumer.zip`; "uses-client"
   nieobecna, wpis Error dla `UsesClientMethod`, `SharedDirectories` puste, brak wyjątku.
   (Asercja o "derived" celowo pominięta: jako pierwszy test typ nie załaduje się w ogóle, jako kolejny -
   powstanie, bo `Shared.Extra` jest już w procesie.)
5. `OnlyMarkedModules_AreShared`: property `IReadOnlyList<DirectoryInfo> SharedDirectories`
   zawiera wyłącznie katalog z `##Shared`.

### Dokumentacja

- `docs/szczegoly.md` §2: nowa podsekcja "Jak zbudować paczkę modułu" (Twoja uwaga z 15.09: dziś §2 mówi
  o `##Dll` tylko od strony runnera, a autor modułu nie ma skąd wziąć csproj). Po liście interfejsów:
  szkielet csproj z `PackageReference Zapqio.Runner.Module.Core ExcludeAssets="runtime"` (kontrakt w runtime
  dostarcza runner, kopia DLL w zipie jest zbędna), target `ZipAfterPublish` z `WriteLinesToFile` piszącym
  `##Dll` z `$(TargetFileName)` (kilka bibliotek z metodami = kilka linii; bez pliku runner skanuje wszystkie
  DLL z paczki), `dotnet publish -c Release` daje zip do wrzucenia do `Modules\`. Zdanie o `##Dll`
  przepisane: paczka powinna przynieść go sama, a gdy go nie ma, runner zapisuje listę po pierwszym udanym
  skanie. Wzór to `Nexo.csproj`; `module-simple` i `module-testbench` działają bez `##Dll`, bo mają jedną DLL.
- `docs/szczegoly.md` §2: podsekcja "Moduły współdzielone": marker `##Shared` (zapisuje go target
  publish modułu), co konsument NIE pakuje (DLL modułu współdzielonego, jego zależności; `Private="false"`
  przy `ProjectReference`/`Reference`, `ExcludeAssets="runtime"` przy `PackageReference` - tak jak przy
  `Module.Core`), kolejność probowania (własny katalog, potem współdzielone po nazwie paczki), kolejność
  zipów bez znaczenia, nowe wpisy w logu, zachowanie przy braku modułu współdzielonego. Wzmianka przy
  `IRunnerInjection`. Tabela w §4: wiersz dla `Metoda ... nie została utworzona`. Wiersz "Runner widoczny
  w panelu, ale bez metod" w §6: dopisek o tym wpisie.
- `README.md` linia o modułach: jedno zdanie, że moduły mogą współdzielić usługi i biblioteki.
- `.github/workflows/release.yml`: `VERSION: "0.1.9"` w osobnym commicie "Release v0.1.9"
  zgodnie z instrukcją w nagłówku pliku (push wydaje release; decyzja o pushu po Twojej stronie).

---

## Krok 2: repo `module-nexo-connection` i `module-nexo-testconnect`

### Repozytoria

| Repo | Projekt | Wynik buildu | Zawartość |
|---|---|---|---|
| `module-nexo-connection` (nowe) | `Nexo.Connection` | `Nexo.Connection.zip` (z `##Shared`), `Zapqio.Nexo.Connection.<wersja>.nupkg` | `NexoClient.cs`, `ConnectionSettings.cs`, `build/Zapqio.Nexo.Connection.props` |
| `module-nexo-testconnect` (nowe) | `Nexo.TestConnect` | `Nexo.TestConnect.zip` | `TestConnect.cs` |
| `module-nexo-invoices` (dzisiejsze `module-nexo`, zmiana nazwy w kroku 3) | `Nexo.Invoices` | `Nexo.Invoices.zip` | reszta: `AddInvoice`, `Invoice/`, `NexoExtensions`, `OwnField`, `SlackClient`, `Settings` |

Pliki do nowych repo są kopiowane (historia `NexoClient.cs` i `TestConnect.cs` zostaje w
`module-nexo-invoices`). Namespace wszędzie `Nexo`, jak dziś; wszystkie klasy są już `public`.
Każde repo: `.gitignore` i `Properties/PublishProfiles/FolderProfile.pubxml` skopiowane z `module-nexo`
(`PublishDir=bin\Release\publish\`), `<Projekt>.slnx` z jednym projektem, `README.md`.
Repo `module-nexo` zostaje w tym kroku nietknięte.

### `module-nexo-connection`

**`Nexo.Connection.csproj`.** Z dzisiejszego `Nexo.csproj`: `net8.0-windows`, `UseWPF`, `x64`,
`Append*ToOutputPath=false`, `DeterministicSourcePaths`. Bez `GenerateAssemblyInfo=false` - zamiast tego:

```xml
<PackageId>Zapqio.Nexo.Connection</PackageId>
<Version>1.0.0</Version>
<!-- Tożsamość zestawu stała jak w Module.Core: konsument skompilowany przeciw 1.0.0 wiąże się
     z każdym 1.x. Podbić dopiero przy zmianie łamiącej API (wtedy 2.0.0.0 i przebudowa konsumentów). -->
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<GeneratePackageOnBuild>true</GeneratePackageOnBuild>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);CS1591</NoWarn>
<NexoSdkCopyLocal>true</NexoSdkCopyLocal>
```

Po `PropertyGroup`: `<Import Project="build\Zapqio.Nexo.Connection.props" />` (ten sam plik, który jedzie
w paczce - jedna lista referencji SDK w repo), `<None Include="build\Zapqio.Nexo.Connection.props"
Pack="true" PackagePath="build\" />`, `PackageReference Zapqio.Runner.Module.Core 1.0.0 ExcludeAssets="runtime"`.
`FileVersion`/`InformationalVersion` idą za `Version`, więc runtime będzie miał z czego odczytać wersję,
gdy dojdzie zgłaszanie wersji modułów (poza zakresem).

**`build/Zapqio.Nexo.Connection.props`.** Nazwa pliku musi być równa `PackageId`, wtedy NuGet importuje go
automatycznie do każdego projektu z `PackageReference` (`ExcludeAssets="runtime"` nie wyklucza zasobów `build`).
Właściwości z warunkiem "jeśli puste", żeby konsument mógł je nadpisać w csproj albo `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <NexoSdkVersion Condition="'$(NexoSdkVersion)' == ''">61.1.0.9431</NexoSdkVersion>
    <nexoSdkBinPath Condition="'$(nexoSdkBinPath)' == ''">C:\nexoSDK_$(NexoSdkVersion)\Bin\</nexoSdkBinPath>
    <NexoSdkCopyLocal Condition="'$(NexoSdkCopyLocal)' == ''">false</NexoSdkCopyLocal>
  </PropertyGroup>
  <ItemGroup>
    <!-- osiem <Reference> z dzisiejszego Nexo.csproj, każda z Private="$(NexoSdkCopyLocal)" -->
  </ItemGroup>
  <ItemGroup Condition="'$(NexoSdkCopyLocal)' == 'true'">
    <!-- None: Xml.pak, Mrt.pak, ijwhost.dll, Microsoft.Data.SqlClient.SNI.dll (CopyToOutputDirectory) -->
  </ItemGroup>
</Project>
```

Konsument dostaje więc referencje SDK z `Private="false"`: kompiluje się przeciw SDK, a RAR nie kopiuje
ani ich, ani ich zależności do publish (sprawdzić w weryfikacji). Sam `Nexo.Connection` ustawia
`NexoSdkCopyLocal=true` przed importem, więc jego publish niesie całe SDK.

**Target `ZipAfterPublish`.** Dzisiejszy z `Nexo.csproj` (`##Dll` = `$(TargetFileName)`) plus
`WriteLinesToFile File="$(PublishDir)##Shared" Lines="shared"` bez warunku - to repo jest modułem
współdzielonym z definicji.

**Kod.** `NexoClient.cs` bez zmian logiki (zostaje `lock` w `Connection()`, jedno połączenie dla wszystkich
modułów i wątków, zgodnie z ustaleniem). `ConnectionSettings : IRunnerInjection` = dzisiejsza `Settings`
okrojona do `Connect` (`NexoConnect` przeniesiony razem), czyta `nexoModule.json` z `AppDomain.BaseDirectory`
tak jak dziś, sekcja `Connect`; plik zakłada z domyślnymi tylko, gdy nie istnieje. Nazwa pliku bez zmian,
więc istniejące instalacje nie wymagają migracji.

**`README.md`** (dla klienta): co robi moduł i że w `Modules\` ma być jedyną paczką z `NexoClient`;
szkielet csproj konsumenta (poniżej, z `Nexo.TestConnect`); wymagania: `net8.0-windows`, `x64`, SDK
w `C:\nexoSDK_<wersja>\Bin\` albo `nexoSdkBinPath` w csproj; `nexoModule.json` z sekcją `Connect`;
zasada wersji (paczka `Version` rośnie z każdą zmianą, `AssemblyVersion` tylko przy zmianie łamiącej).

### `module-nexo-testconnect`

`Nexo.TestConnect.csproj` jest zarazem wzorcem dla klienta piszącego własny moduł:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <PlatformTarget>x64</PlatformTarget>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
    <DeterministicSourcePaths>true</DeterministicSourcePaths>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Zapqio.Runner.Module.Core" Version="1.0.0" ExcludeAssets="runtime" />
    <PackageReference Include="Zapqio.Nexo.Connection" Version="1.0.0" ExcludeAssets="runtime" />
  </ItemGroup>
  <!-- ZipAfterPublish jak w Nexo.csproj (##Dll = $(TargetFileName)), bez ##Shared -->
</Project>
```

Żadnych `<Reference>` do SDK - przychodzą z props paczki. `TestConnect.cs` skopiowany bez zmian. Używa
`InsERT.Moria.Uzytkownicy`, więc w `Run` rozwiązuje zestaw SDK przez katalog współdzielony - realny test
mechanizmu z kroku 1.

### Lokalne źródło NuGet

`dotnet nuget add source "<repos>\module-nexo-connection\bin\Release" -n "Nexo connection"` - trafia do
`%APPDATA%\NuGet\NuGet.Config` obok dzisiejszego "Runner module core". `GeneratePackageOnBuild` kładzie
nupkg w `bin\Release\` (bo `AppendTargetFrameworkToOutputPath=false`), więc `dotnet publish` Connection
daje naraz zip i paczkę. Kolejność: najpierw Connection, potem `dotnet restore` konsumenta.

Pułapka przy pracy lokalnej: NuGet trzyma rozpakowaną paczkę w `%USERPROFILE%\.nuget\packages\zapqio.nexo.connection\<wersja>\`
i nie zauważa, że nupkg pod tą samą wersją został przebudowany. Przy zmianie API bez podbicia `Version`
trzeba skasować ten katalog przed restore konsumenta. Docelowo każda zmiana API = nowa `Version`.

---

## Krok 3: `module-nexo-invoices` na paczce `Zapqio.Nexo.Connection`

- **Zmiana nazwy repo** na GitHubie (`module-nexo` -> `module-nexo-invoices`; stary adres przekierowuje),
  `git remote set-url origin`, katalog lokalny wedle uznania.
- `git rm NexoClient.cs TestConnect.cs`; `git mv Nexo.csproj Nexo.Invoices.csproj`,
  `git mv Nexo.slnx Nexo.Invoices.slnx` (ścieżka w slnx poprawiona).
- **Csproj:** usunąć osiem `<Reference>` SDK i cztery `None` z `.pak`/`.dll` (przychodzą z props paczki,
  z `Private="false"`), usunąć martwe `Compile/EmbeddedResource/None/Page Remove="NexoModule.Test\**"`,
  dodać `PackageReference Zapqio.Nexo.Connection Version="1.0.0" ExcludeAssets="runtime"`.
  `GenerateAssemblyInfo=false` zostaje. Target bez zmian (`##Dll` = `Nexo.Invoices.dll`).
- `Settings` traci `Connect`/`NexoConnect` (reszta kluczy bez zmian, ten sam `nexoModule.json`;
  klasa czyta swoje klucze, nieznane pola są ignorowane; domyślny plik zakłada tylko, gdy nie istnieje,
  czyli w praktyce zrobi to `Connection`, bo jego singleton powstaje pierwszy jako zależność).
- `AddInvoice`, `SlackClient`, `NexoExtensions` bez zmian poza `using` (klient nadal wstrzykiwany
  jako `NexoClient`). Statyczne `NexoExtensions.Client` zostaje: przy jednym singletonie jest
  nieszkodliwe (ustalenie z rozmowy); porządkowanie to osobny temat.
- Helpery generyczne (`Error`, `SetFlag`, `SetOwnFields`) zostają w `Invoices`, dopóki nie ma
  drugiego konsumenta, który ich potrzebuje - przenosiny do `Connection` wymagałyby zdjęcia statyka.
- Zip zmienia nazwę z `Nexo.zip` na `Nexo.Invoices.zip`; na runnerach stary `Nexo.zip` trzeba usunąć
  (niesie własny `NexoClient`, czyli drugie połączenie).

---

## Weryfikacja

**Runner**
1. `dotnet test -c Release` w `runner-dotnet` (nowe testy + istniejące), a każdy test z
   `MethodsProviderTests` dodatkowo osobno (`--filter`), bo jako pierwszy w procesie idzie inną ścieżką.
   Po buildzie: w `bin\...\Zapqio.Runner.Tests\` nie ma żadnego `TestModule.*.dll` poza `TestModules\`.
2. `dotnet publish Zapqio.Runner -c Release -f net8.0-windows -r win-x64 --no-self-contained`
   (wariant dla Nexo, jak w release.yml).

**Moduły** (po kroku 2)
3. `dotnet publish -c Release` w `module-nexo-connection`: `bin\Release\Nexo.Connection.zip` = SDK +
   `Nexo.Connection.dll` + `##Dll` + `##Shared`; `bin\Release\Zapqio.Nexo.Connection.1.0.0.nupkg` (to zip)
   zawiera `lib/net8.0-windows7.0/Nexo.Connection.dll` i `.xml`, `build/Zapqio.Nexo.Connection.props`,
   w nuspec jedyna zależność to `Zapqio.Runner.Module.Core`, żadnego `InsERT.*`.
4. `dotnet publish -c Release` w `module-nexo-testconnect`: restore bierze paczkę z lokalnego źródła
   (`obj\project.assets.json`), `Nexo.TestConnect.zip` = `Nexo.TestConnect.dll`, `.deps.json`, `##Dll`,
   bez żadnego `InsERT.*` ani `Nexo.Connection.dll`; w `bin\Release\` też ich nie ma (RAR i `Private=false`).
5. Oba zipy do `Modules\` runnera z kroku 1 (lokalna instalacja albo `Zapqio.Runner.exe` z bin,
   `appsettings.json` z katalogu roboczego). Oczekiwane w logu:
   `Moduł współdzielony: Nexo.Connection`, `Add injection: Nexo.NexoClient ... Nexo.Connection`,
   `Add injection: Nexo.ConnectionSettings`, `Add method: Nexo.TestConnect ... Nexo.TestConnect`,
   `Wysyłam Info: 1 metod: Who am I (test)`. Na poziomie Debug wpis o rozwiązaniu zestawu SDK z
   modułu współdzielonego przy pierwszym uruchomieniu metody.
6. Z panelu Web uruchomić "Who am I (test)" - wynik z sygnaturą operatora.
7. Test negatywny: usunąć `Nexo.Connection.zip`, restart. Oczekiwane: Error
   `Metoda Nexo.TestConnect z modułu Nexo.TestConnect nie została utworzona ... Unable to resolve service for type 'Nexo.NexoClient'`,
   runner połączony, `Info` bez tej metody, bez pętli reconnectów.
8. Klient od zera (dowód na "dać gotowe"): katalog poza repo, `dotnet new classlib -n Demo -f net8.0-windows`,
   csproj jak wzorzec z `Nexo.TestConnect`, kopia `TestConnect.cs` z inną nazwą metody. `dotnet publish`
   przechodzi bez ręcznych `<Reference>`; zip w `Modules\` i metoda ogłoszona. Dodatkowo `nexoSdkBinPath`
   na nieistniejącą ścieżkę w csproj = build pada na braku SDK (nadpisanie działa).

**Faktury** (po kroku 3)
9. Trzy zipy w `Modules\`; `Add invoice and pdf` ogłoszona; wystawienie faktury testowej na bazie
   demo. Stary `Nexo.zip` usunięty z `Modules\` (dwa `NexoClient` w procesie to dwa połączenia).

## Pytania otwarte

1. **Nazwy repo.** Propozycja: `module-nexo-connection`, `module-nexo-testconnect`, `module-nexo-invoices`
   (zmiana nazwy dzisiejszego `module-nexo`, żeby lista repo czytała się jednoznacznie dla klienta).
   Jeśli wolisz zostawić `module-nexo` dla faktur albo dać je Connection, zmienia się tylko tabela wyżej.
2. **Źródło paczek dla klientów.** `Zapqio.Runner.Module.Core` i `Zapqio.Nexo.Connection` są tylko w lokalnym
   folderze na Twojej maszynie. Klient piszący moduł potrzebuje obu. Najprościej nuget.org (publiczne,
   `dotnet add package`); alternatywa to wręczanie plików nupkg do lokalnego źródła u klienta. GitHub
   Packages odpada, bo wymaga tokenu nawet do odczytu paczek publicznych. Publikacja `Module.Core` to
   decyzja po stronie `runner-dotnet`, osobna od tego planu.
3. **Widoczność źródeł `module-nexo-connection`** (publiczne jak `runner-dotnet`, czy prywatne) - nie wpływa
   na plan, wpływa na to, czy paczka może iść na nuget.org z linkiem do repo.
4. **`Dispose` kontenera modułów** (patrz sekcja zmian z 14.09) - bez zmian.

## Poza zakresem tego planu

- Wyniesienie SDK poza paczkę `Connection` i automatyzacja zmiany wersji Subiekta (krok następny;
  katalog współdzielony jest już katalogiem probowania, więc dojdzie tylko konfigurowalna ścieżka).
- Target `ZipAfterPublish` z `##Dll` w paczce `Module.Core` (`build/*.targets`), żeby moduły klientów
  nie kopiowały go między repo. Dziś kopiują go `module-simple`, `module-testbench` i `module-nexo`.
- `ExcludeAssets="runtime"` w `module-simple` i `module-testbench` (dziś bez niego, ich zipy niosą kopię
  `Module.Core`, którą runner i tak pomija - porządek, nie błąd).
- Publikacja `Module.Core` na nuget.org (pytanie otwarte 2).
- `IRunnerModule.ConfigureServices` w `Module.Core` (dopiero gdy konsumenci mają zależeć od interfejsu).
- Zgłaszanie wersji modułów do platformy (zmiana protokołu); `InformationalVersion` z `Version` paczki
  jest już gotowe do odczytu.
- Zwalnianie kontenera modułów przy zatrzymaniu usługi (pytanie otwarte 4).
