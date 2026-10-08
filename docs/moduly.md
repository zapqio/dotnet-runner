# Pisanie modułów

Runner sam nie ma żadnych metod — dostarczają je moduły. Moduł to biblioteka .NET spakowana do
`.zip` i wrzucona do katalogu `Modules\` runnera. Moduły ładują się **tylko przy starcie**, więc
po dodaniu lub podmianie paczki zrestartuj usługę (`Restart-Service ZapqioRunner`).

## Kontrakt

Referencja: paczka NuGet [`Zapqio.Runner.Module.Core`](../Zapqio.Runner.Module.Core/) (1.2.0).
Runner rejestruje publiczne, nieabstrakcyjne klasy implementujące:

- **`IRunnerMethod`** — metoda widoczna w panelu Web:
  - `NameMethod()` — nazwa, po której Web kieruje zadania,
  - `InData()` / `OutData()` — typy wejścia i wyjścia (z nich powstaje JSON Schema); mogą zwrócić `null`,
  - `Run(string data)` — wykonanie; wejście i wyjście to JSON jako tekst.
- **`IRunnerInjection`** — usługa rejestrowana w DI jako singleton, do wstrzykiwania w metody
  (także z innych paczek).

## Budowanie paczki

Po `dotnet publish -c Release` poniższy projekt sam tworzy zip obok katalogu `publish`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- net8.0 działa z każdą paczką runnera; net10.0 tylko z paczką .NET 10 -->
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <!-- Kontrakt dostarcza runner, więc nie pakujemy go do zipa -->
    <PackageReference Include="Zapqio.Runner.Module.Core" Version="1.2.0" ExcludeAssets="runtime" />
  </ItemGroup>

  <Target Name="ZipAfterPublish" AfterTargets="Publish">
    <PropertyGroup>
      <ZipFilePath>$(PublishDir)..\$(MSBuildProjectName).zip</ZipFilePath>
    </PropertyGroup>
    <!-- Biblioteki, które runner ma skanować (jedna na linię) -->
    <WriteLinesToFile File="$(PublishDir)##Dll" Lines="$(TargetFileName)" Overwrite="true" />
    <Delete Files="$(ZipFilePath)" Condition="Exists('$(ZipFilePath)')" />
    <ZipDirectory SourceDirectory="$(PublishDir)" DestinationFile="$(ZipFilePath)" />
  </Target>

</Project>
```

- Zależności modułu (NuGet, natywne DLL) jadą w zipie razem z nim.
- `##Dll` wskazuje biblioteki do skanowania. Bez niego runner sprawdza wszystkie DLL-e z paczki
  i zapamiętuje te, w których coś znalazł.
- Runner rozpakowuje paczkę do `.modulesCache\` i robi to ponownie tylko po zmianie zipa.

## Konfiguracja modułu

Ustawień (adresy, hasła) nie trzymaj w zipie — jest podmieniany przy aktualizacji. Ich miejsce to
plik w katalogu `Config\` obok `Modules\`, np. `Config\nexoModule.json`. W kodzie:
`Path.Combine(AppContext.BaseDirectory, "Config")`. Konto usługi może tam zapisywać, inni nie
mają dostępu.

## Logowanie

Wszystkie trzy sposoby trafiają na żywo do logów zadania w panelu Web i do logu plikowego:

- `Console.WriteLine` / `Console.Error.WriteLine` (ten drugi jako `Error`),
- `RunnerLog.Debug/Info/Warning/Error/Critical` — działa też w wątkach pomocniczych,
- `ILogger<T>` wstrzyknięty w konstruktorze.

`Debug` domyślnie nie idzie do Web (próg `MinRemoteLogLevel`). Przed kosztownym budowaniem treści
sprawdź `RunnerLog.IsEnabled(RunnerLogLevel.Debug)`.

```csharp
public async Task<string> Run(string data)
{
    RunnerLog.Debug($"wejście: {data}");
    return output;
}
```

## Zasady

- **Wyjątek** z `Run` kończy zadanie statusem `ERROR`, a jego treść trafia do logów zadania.
- **Powtórzenia.** Platforma może wysłać zadanie ponownie, jeśli straciła runnera w trakcie.
  Metoda z nieodwracalnym skutkiem powinna zapisywać `JobContext.Current.JobId` razem ze skutkiem
  i przed wykonaniem sprawdzać, czy go już nie ma. `JobContext.Current` daje też `AttemptId`
  i nazwę metody; poza `Run` jest pusty.
- **Rozmiar wyniku** — najwyżej 32 MiB (jedna wiadomość WebSocket), z zapasem na kodowanie.
- **Równoległość.** Metoda i usługi to jedna instancja na proces. Przy `MaxConcurrency` > 1
  `Run` może być wołany z kilku wątków naraz — synchronizacja należy do modułu.

## Moduły współdzielone

- **Usługi** (`IRunnerInjection`) są wstrzykiwane między paczkami zawsze, bez dodatkowych kroków.
- **Biblioteki** — jeśli inny moduł ma korzystać z DLL-i tej paczki, dodaj do niej pusty plik
  `##Shared`:
  `<WriteLinesToFile File="$(PublishDir)##Shared" Lines="shared" Overwrite="true" />`.
- Paczka z samymi bibliotekami (np. SDK zewnętrznego systemu) ma `##Shared` i **pusty** `##Dll`.
  Wpis w logu `... udostępnia tylko biblioteki` jest wtedy oczekiwany.
- Konsument kompiluje się przeciw modułowi współdzielonemu, ale go **nie pakuje**:
  `ExcludeAssets="runtime"` przy `PackageReference`, `Private="false"` przy `ProjectReference`.
- Kolejność paczek nie ma znaczenia. Gdy paczki współdzielonej brakuje, zależne od niej metody są
  pomijane (wpis `Error` w logu), a reszta działa.
