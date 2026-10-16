#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Instaluje lub aktualizuje Zapqio Runner jako usługę Windows.

.DESCRIPTION
    Pobiera paczkę zapqio-runner-<wersja>-win-x64-net8.zip z GitHub Releases
    (domyślnie najnowszą; przy -Net10 wariant -win-x64.zip), rozpakowuje do
    katalogu instalacji i rejestruje usługę Windows:
    start automatyczny, polityka restartu po awarii procesu, konto wirtualne
    NT SERVICE\<usługa> z prawem zapisu ograniczonym do katalogu instalacji
    (chyba że podano -LocalSystem).

    Na początku pozwala wybrać język instalacji: polski, angielski lub niemiecki.
    Parametr -Language pomija wybór interaktywny.
    Przed pobraniem paczki pyta o katalog instalacji i nazwę usługi Windows.
    Enter zatwierdza wartości domyślne; -InstallDir i -ServiceName pomijają
    odpowiednie pytania. Bez odczytu z konsoli pozostają wartości domyślne.
    O brakującą nazwę instancji i token pyta interaktywnie; przy nowej instalacji
    pyta też o liczbę równoczesnych zadań. Zapisuje konfigurację w appsettings.json,
    a po starcie usługi czeka, aż w logu pojawi się
    potwierdzenie nawiązania połączenia WebSocket z instancją Web.

    Uruchomiony na istniejącej instalacji działa jak aktualizacja: zatrzymuje
    usługę, podmienia pliki i zachowuje appsettings.json, ##Name, Modules\
    oraz Logs\, Config\, Deployments\ i Build\ (kasuje .modulesCache — odtworzy się przy starcie).

.PARAMETER Version
    Wersja release'u, np. 0.1.1. Pusta = najnowszy release z GitHuba.

.PARAMETER InstallDir
    Katalog instalacji. Domyślnie C:\zapqio\runner — celowo poza Program Files,
    bo runner zapisuje pliki obok binarki. Bez parametru skrypt pyta o katalog;
    Enter zatwierdza domyślny. Jawne podanie parametru pomija pytanie.

.PARAMETER ServiceName
    Nazwa usługi Windows. Domyślnie ZapqioRunner.
    Bez parametru skrypt pyta o nazwę; Enter zatwierdza domyślną.
    Jawne podanie parametru pomija pytanie.

.PARAMETER Instance
    Nazwa instancji Web, np. test. Skrypt buduje z niej adres
    wss://app.zapq.io/<nazwa> i zapisuje go do appsettings.json.

.PARAMETER Url
    Pełny adres instancji Web bez sufiksu /ws-runner — dla adresów niestandardowych
    (np. lokalnie ws://localhost:5208/moja-instancja). Zamiast -Instance, nie razem
    z nim. Zapisywany do appsettings.json.

.PARAMETER Token
    Token runnera wydany przez panel Web. Zapisywany do appsettings.json, którego
    ACL jest przy tym zawężany do SYSTEM, administratorów i konta usługi.

.PARAMETER RunnerName
    Stabilna nazwa runnera (klucz Name). Pusta = runner wygeneruje UUID przy
    pierwszym starcie i zapisze go w pliku ##Name.

.PARAMETER MaxConcurrency
    Maksymalna liczba zadań wykonywanych równocześnie (co najmniej 1).
    Nowa instalacja domyślnie używa 1; instalacja interaktywna pyta o wartość.
    Aktualizacja bez tego parametru zachowuje dotychczasowe ustawienie.
    Parametr ustawia również wartość dla usługi, jeśli nadpisuje ją środowisko.

.PARAMETER LogLevel
    Poziom logowania (klucz Logger:LogLevel): Verbose, Debug, Information,
    Warning, Error albo Fatal. Domyślnie runner używa Information.

.PARAMETER LogDirectory
    Katalog logów (klucz Logger:PathDirectory), względny wobec katalogu
    instalacji albo bezwzględny. Jawnie pusty ("") wyłącza logi plikowe —
    w trybie usługi niezalecane, bo to jedyny lokalny ślad działania.

.PARAMETER LocalSystem
    Zostawia usługę na koncie LocalSystem zamiast przełączać na konto wirtualne
    NT SERVICE\<usługa>.

.PARAMETER Net10
    Instaluje wariant runnera zbudowany na .NET 10 zamiast domyślnego .NET 8;
    wymaga wtedy .NET Desktop Runtime 10 (x64). Domyślny jest .NET 8, bo część
    modułów nie ładuje się na nowszym runtime — moduł nexo korzysta z obfuskowanych
    bibliotek InsERT-a (InsERT.Moria.Sfera i pokrewne), które loader odrzuca
    od .NET 9. Bez takich modułów wybierz -Net10.

.PARAMETER Language
    Język komunikatów instalatora: pl (polski), en (angielski), de (niemiecki).
    Bez parametru skrypt pyta o język; Enter wybiera polski. Gdy host nie pozwala
    na odczyt z konsoli (np. -NonInteractive), również używa polskiego.
    W automatyzacji podaj język jawnie, aby pominąć pytanie.

.EXAMPLE
    .\install.ps1 -Instance test -Token <token>

.EXAMPLE
    .\install.ps1 -Language en -Instance test -Token <token>

.EXAMPLE
    .\install.ps1 -Instance test -Token <token> -MaxConcurrency 15

.EXAMPLE
    .\install.ps1 -Version 0.1.1 -InstallDir D:\zapqio\runner

.EXAMPLE
    # Sama zmiana konfiguracji też przechodzi przez skrypt:
    .\install.ps1 -LogLevel Debug

.EXAMPLE
    # Aktualizacja do najnowszej wersji, konfiguracja zostaje bez zmian:
    .\install.ps1

.EXAMPLE
    # Maszyna bez modułów wymagających .NET 8 — nowszy runtime:
    .\install.ps1 -Instance test -Token <token> -Net10

.NOTES
    Uruchomienie bez klonowania repozytorium (PowerShell jako administrator).
    TrimStart zdejmuje BOM UTF-8, którego irm nie usuwa, a na którym wykłada się
    parser — z tego samego powodu proste `irm ... | iex` NIE zadziała.

    Interaktywnie — skrypt dopyta o nazwę instancji i token:

      $s = irm https://raw.githubusercontent.com/zapqio/dotnet-runner/main/install.ps1
      & ([scriptblock]::Create($s.TrimStart([char]0xFEFF)))

    Z parametrami:

      $s = irm https://raw.githubusercontent.com/zapqio/dotnet-runner/main/install.ps1
      & ([scriptblock]::Create($s.TrimStart([char]0xFEFF))) -Instance test -Token <token>
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$InstallDir = 'C:\zapqio\runner',
    [string]$ServiceName = 'ZapqioRunner',
    [string]$Instance,
    [string]$Url,
    [string]$Token,
    [string]$RunnerName,
    [ValidateRange(1, 2147483647)]
    [int]$MaxConcurrency,
    [ValidateSet('Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal')]
    [string]$LogLevel,
    [string]$LogDirectory,
    [switch]$LocalSystem,
    [switch]$Net10,
    [ValidateSet('pl', 'en', 'de')]
    [string]$Language
)

$ErrorActionPreference = 'Stop'
$repo = 'zapqio/dotnet-runner'
$baseUrl = 'wss://app.zapq.io'

# Tłumaczenia pozostają w skrypcie, aby działało także uruchomienie przez irm.
# Nazwy parametrów, klucze konfiguracji i wzorce odczytywane z logów są niezmienne.
$installerMessages = @{
    pl = @{
        LanguageSelected = '==> Język instalacji: polski.'
        InstallDirPrompt = 'Katalog instalacji (Enter = {0}; wpisz inną ścieżkę, aby zmienić)'
        ServiceNamePrompt = 'Nazwa usługi Windows (Enter = {0}; wpisz inną nazwę, aby zmienić)'
        InstanceOrUrl = 'Podaj -Instance albo -Url — nie oba naraz.'
        InstanceNameOnly = '-Instance to sama nazwa instancji (np. test); pełny adres podaje się przez -Url.'
        AdministratorRequired = 'Uruchom ten skrypt w PowerShellu jako administrator.'
        DesktopRuntimeRequired = @'
Brak .NET Desktop Runtime {0} (x64), którego wymaga runner. Zainstaluj go i uruchom skrypt ponownie:
  winget install Microsoft.DotNet.DesktopRuntime.{0}
albo instalatorem ze strony https://dotnet.microsoft.com/download/dotnet/{0}.0 (sekcja ".NET Desktop Runtime", Windows x64).
'@
        ServiceCommandFailed = "sc.exe {0} zakończyło się kodem {1}:`n{2}"
        CheckingLatestRelease = '==> Sprawdzam najnowszy release...'
        Downloading = '==> Pobieram {0}'
        DownloadFailed = 'Nie udało się pobrać paczki ({0}). Sprawdź, czy wersja {1} istnieje: https://github.com/{2}/releases{3}'
        Net8PackageHint = ' — paczki win-x64-net8 są wydawane od 0.1.4.'
        Extracting = '==> Rozpakowuję...'
        InvalidPackage = 'W paczce nie ma Zapqio.Runner.exe — to nie wygląda na paczkę win-x64 runnera.'
        StoppingService = '==> Zatrzymuję usługę {0}...'
        CopyingFiles = '==> Kopiuję pliki do {0}...'
        InstancePrompt = 'Nazwa instancji Web, np. test'
        TokenPrompt = 'Token runnera z panelu Web'
        RunnerNamePrompt = 'Nazwa runnera (Enter = wygeneruje się UUID; nazwy nie da się potem zmienić)'
        ConcurrencyPrompt = 'Maksymalna liczba równoczesnych zadań (Enter = 1)'
        PositiveIntegerRequired = 'Podaj liczbę całkowitą większą od zera.'
        ConfigurationSaved = '==> Zapisano w appsettings.json: {0}.'
        ConcurrencySaveFailed = 'Nie udało się zapisać MaxConcurrency do {0}: {1}'
        ConfigurationParseFailed = 'Nie udało się sparsować {0} (plik z komentarzami?). Ustaw ręcznie: {1}.'
        RegisteringService = '==> Rejestruję usługę {0}...'
        ServiceDescription = 'Zapqio Runner - wykonuje zadania zlecane przez serwer Web Zapqio.'
        UpdatingServicePath = '==> Aktualizuję ścieżkę binarki w usłudze...'
        GrantingDirectoryAccess = '==> Nadaję kontu usługi prawo zapisu do katalogu instalacji...'
        AccessControlFailed = 'icacls na {0} zakończyło się kodem {1}.'
        GrantingServiceRestart = '==> Nadaję kontu usługi prawo restartu własnej usługi...'
        MissingSecurityDescriptor = 'sc.exe sdshow nie zwróciło deskryptora zabezpieczeń'
        ServiceRestartPermissionFailed = 'Nie udało się nadać kontu usługi prawa restartu ({0}). Moduły podmieniające biblioteki użyją opcji odzyskiwania usługi.'
        StartingService = '==> Uruchamiam usługę {0}...'
        ServiceStartFailed = 'Nie udało się uruchomić usługi: {0}'
        ServiceStartAdvice = 'Zajrzyj do {0}, popraw konfigurację i spróbuj: sc.exe start {1}'
        EventLogLocation = 'Dziennika zdarzeń Windows (Application)'
        WaitingForConnection = '==> Czekam na ślad połączenia WebSocket w logu (do 30 s)...'
        Connected = '==> Runner nawiązał połączenie WebSocket z instancją Web.'
        ConnectionRejected = 'Serwer odrzucił połączenie: {0} — sprawdź adres instancji i token (docs/szczegoly.md > Rozwiązywanie problemów).'
        ConnectionRetrying = 'Runner nie może nawiązać połączenia i ponawia próby — sprawdź adres instancji, token i dostęp sieciowy. Log: {0}'
        ConnectionUnconfirmed = 'W ciągu 30 s log nie potwierdził połączenia — zajrzyj do {0} i do panelu Web.'
        FileLogsDisabled = '==> Logi plikowe wyłączone — status połączenia sprawdź w panelu Web.'
        MissingConfiguration = 'Usługa nie została uruchomiona — brak tokenu lub adresu instancji. Uzupełnij Token i Url w {0} (albo uruchom skrypt z -Token i -Instance), potem: sc.exe start {1}'
        ServiceSummary = 'Zapqio Runner {0} (.NET {1}) — usługa {2} ({3}).'
        DirectorySummary = '  Katalog: {0}'
        ConfigurationSummary = '  Konfiguracja: {0}'
        ConcurrencySummary = '  Równoczesne zadania: {0}'
        EnvironmentConcurrencySummary = '  Limit ustala ZAPQIO_MAX_CONCURRENCY; zmienisz go instalatorem z -MaxConcurrency.'
        LogsSummary = '  Logi: {0}'
        LogsDisabled = 'wyłączone (Logger:PathDirectory puste)'
        ModulesSummary = '  Moduły (.zip): {0} — po zmianach zrestartuj usługę.'
        ConfigurationContents = 'Zawartość {0} (token zamaskowany):'
        RestartAdvice = 'W razie problemów lub zmiany ustawień edytuj ten plik, potem: Restart-Service {0}'
    }
    en = @{
        LanguageSelected = '==> Installation language: English.'
        InstallDirPrompt = 'Installation directory (Enter = {0}; type another path to change it)'
        ServiceNamePrompt = 'Windows service name (Enter = {0}; type another name to change it)'
        InstanceOrUrl = 'Specify either -Instance or -Url, not both.'
        InstanceNameOnly = '-Instance accepts only the instance name (e.g. test); use -Url for a full address.'
        AdministratorRequired = 'Run this script in PowerShell as an administrator.'
        DesktopRuntimeRequired = @'
The runner requires .NET Desktop Runtime {0} (x64), which is missing. Install it and run the script again:
  winget install Microsoft.DotNet.DesktopRuntime.{0}
or use the installer at https://dotnet.microsoft.com/download/dotnet/{0}.0 (".NET Desktop Runtime" section, Windows x64).
'@
        ServiceCommandFailed = "sc.exe {0} exited with code {1}:`n{2}"
        CheckingLatestRelease = '==> Checking the latest release...'
        Downloading = '==> Downloading {0}'
        DownloadFailed = 'Could not download the package ({0}). Check that version {1} exists: https://github.com/{2}/releases{3}'
        Net8PackageHint = ' — win-x64-net8 packages are available starting with version 0.1.4.'
        Extracting = '==> Extracting...'
        InvalidPackage = 'The package does not contain Zapqio.Runner.exe — it does not appear to be a win-x64 runner package.'
        StoppingService = '==> Stopping service {0}...'
        CopyingFiles = '==> Copying files to {0}...'
        InstancePrompt = 'Web instance name, e.g. test'
        TokenPrompt = 'Runner token from the Web panel'
        RunnerNamePrompt = 'Runner name (Enter = generate a UUID; the name cannot be changed later)'
        ConcurrencyPrompt = 'Maximum number of concurrent jobs (Enter = 1)'
        PositiveIntegerRequired = 'Enter an integer greater than zero.'
        ConfigurationSaved = '==> Saved to appsettings.json: {0}.'
        ConcurrencySaveFailed = 'Could not save MaxConcurrency to {0}: {1}'
        ConfigurationParseFailed = 'Could not parse {0} (does the file contain comments?). Set these values manually: {1}.'
        RegisteringService = '==> Registering service {0}...'
        ServiceDescription = 'Zapqio Runner - runs jobs assigned by the Zapqio Web server.'
        UpdatingServicePath = '==> Updating the executable path in the service...'
        GrantingDirectoryAccess = '==> Granting the service account write access to the installation directory...'
        AccessControlFailed = 'icacls on {0} exited with code {1}.'
        GrantingServiceRestart = '==> Granting the service account permission to restart its own service...'
        MissingSecurityDescriptor = 'sc.exe sdshow did not return a security descriptor'
        ServiceRestartPermissionFailed = 'Could not grant the service account permission to restart the service ({0}). Modules that replace libraries will use the service recovery options.'
        StartingService = '==> Starting service {0}...'
        ServiceStartFailed = 'Could not start the service: {0}'
        ServiceStartAdvice = 'Check {0}, correct the configuration and try: sc.exe start {1}'
        EventLogLocation = 'Windows Event Viewer (Application)'
        WaitingForConnection = '==> Waiting for a WebSocket connection entry in the log (up to 30 s)...'
        Connected = '==> The runner established a WebSocket connection to the Web instance.'
        ConnectionRejected = 'The server rejected the connection: {0} — check the instance address and token (see docs/szczegoly.md).'
        ConnectionRetrying = 'The runner cannot connect and is retrying — check the instance address, token and network access. Log: {0}'
        ConnectionUnconfirmed = 'The log did not confirm a connection within 30 s — check {0} and the Web panel.'
        FileLogsDisabled = '==> File logging is disabled — check the connection status in the Web panel.'
        MissingConfiguration = 'The service was not started — the token or instance address is missing. Set Token and Url in {0} (or run the script with -Token and -Instance), then: sc.exe start {1}'
        ServiceSummary = 'Zapqio Runner {0} (.NET {1}) — service {2} ({3}).'
        DirectorySummary = '  Directory: {0}'
        ConfigurationSummary = '  Configuration: {0}'
        ConcurrencySummary = '  Concurrent jobs: {0}'
        EnvironmentConcurrencySummary = '  The limit is set by ZAPQIO_MAX_CONCURRENCY; change it using the installer with -MaxConcurrency.'
        LogsSummary = '  Logs: {0}'
        LogsDisabled = 'disabled (Logger:PathDirectory is empty)'
        ModulesSummary = '  Modules (.zip): {0} — restart the service after making changes.'
        ConfigurationContents = 'Contents of {0} (token masked):'
        RestartAdvice = 'If you encounter problems or need to change settings, edit this file, then: Restart-Service {0}'
    }
    de = @{
        LanguageSelected = '==> Installationssprache: Deutsch.'
        InstallDirPrompt = 'Installationsverzeichnis (Enter = {0}; zum Ändern einen anderen Pfad eingeben)'
        ServiceNamePrompt = 'Name des Windows-Dienstes (Enter = {0}; zum Ändern einen anderen Namen eingeben)'
        InstanceOrUrl = 'Geben Sie entweder -Instance oder -Url an, nicht beide gleichzeitig.'
        InstanceNameOnly = '-Instance erwartet nur den Instanznamen (z. B. test); verwenden Sie -Url für eine vollständige Adresse.'
        AdministratorRequired = 'Führen Sie dieses Skript in PowerShell als Administrator aus.'
        DesktopRuntimeRequired = @'
Der Runner benötigt .NET Desktop Runtime {0} (x64), die nicht installiert ist. Installieren Sie diese und führen Sie das Skript erneut aus:
  winget install Microsoft.DotNet.DesktopRuntime.{0}
oder verwenden Sie das Installationsprogramm unter https://dotnet.microsoft.com/download/dotnet/{0}.0 (Abschnitt ".NET Desktop Runtime", Windows x64).
'@
        ServiceCommandFailed = "sc.exe {0} wurde mit Code {1} beendet:`n{2}"
        CheckingLatestRelease = '==> Neueste Version wird geprüft...'
        Downloading = '==> {0} wird heruntergeladen'
        DownloadFailed = 'Das Paket konnte nicht heruntergeladen werden ({0}). Prüfen Sie, ob Version {1} existiert: https://github.com/{2}/releases{3}'
        Net8PackageHint = ' — win-x64-net8-Pakete sind ab Version 0.1.4 verfügbar.'
        Extracting = '==> Dateien werden entpackt...'
        InvalidPackage = 'Das Paket enthält keine Zapqio.Runner.exe — es scheint kein win-x64-Paket des Runners zu sein.'
        StoppingService = '==> Dienst {0} wird beendet...'
        CopyingFiles = '==> Dateien werden nach {0} kopiert...'
        InstancePrompt = 'Name der Web-Instanz, z. B. test'
        TokenPrompt = 'Runner-Token aus dem Web-Panel'
        RunnerNamePrompt = 'Name des Runners (Enter = UUID erzeugen; der Name kann später nicht geändert werden)'
        ConcurrencyPrompt = 'Maximale Anzahl gleichzeitiger Aufgaben (Enter = 1)'
        PositiveIntegerRequired = 'Geben Sie eine ganze Zahl größer als null ein.'
        ConfigurationSaved = '==> In appsettings.json gespeichert: {0}.'
        ConcurrencySaveFailed = 'MaxConcurrency konnte nicht in {0} gespeichert werden: {1}'
        ConfigurationParseFailed = '{0} konnte nicht gelesen werden (enthält die Datei Kommentare?). Setzen Sie diese Werte manuell: {1}.'
        RegisteringService = '==> Dienst {0} wird registriert...'
        ServiceDescription = 'Zapqio Runner - führt vom Zapqio-Webserver zugewiesene Aufgaben aus.'
        UpdatingServicePath = '==> Pfad der ausführbaren Datei im Dienst wird aktualisiert...'
        GrantingDirectoryAccess = '==> Dem Dienstkonto wird Schreibzugriff auf das Installationsverzeichnis gewährt...'
        AccessControlFailed = 'icacls für {0} wurde mit Code {1} beendet.'
        GrantingServiceRestart = '==> Dem Dienstkonto wird die Berechtigung zum Neustart des eigenen Dienstes gewährt...'
        MissingSecurityDescriptor = 'sc.exe sdshow hat keinen Sicherheitsdeskriptor zurückgegeben'
        ServiceRestartPermissionFailed = 'Dem Dienstkonto konnte die Berechtigung zum Neustart des Dienstes nicht gewährt werden ({0}). Module, die Bibliotheken ersetzen, verwenden die Wiederherstellungsoptionen des Dienstes.'
        StartingService = '==> Dienst {0} wird gestartet...'
        ServiceStartFailed = 'Der Dienst konnte nicht gestartet werden: {0}'
        ServiceStartAdvice = 'Prüfen Sie {0}, korrigieren Sie die Konfiguration und versuchen Sie: sc.exe start {1}'
        EventLogLocation = 'die Windows-Ereignisanzeige (Application)'
        WaitingForConnection = '==> Auf einen WebSocket-Verbindungseintrag im Protokoll wird gewartet (bis zu 30 s)...'
        Connected = '==> Der Runner hat eine WebSocket-Verbindung zur Web-Instanz hergestellt.'
        ConnectionRejected = 'Der Server hat die Verbindung abgelehnt: {0} — prüfen Sie die Instanzadresse und das Token (siehe docs/szczegoly.md).'
        ConnectionRetrying = 'Der Runner kann keine Verbindung herstellen und versucht es erneut — prüfen Sie die Instanzadresse, das Token und den Netzwerkzugriff. Protokoll: {0}'
        ConnectionUnconfirmed = 'Das Protokoll hat innerhalb von 30 s keine Verbindung bestätigt — prüfen Sie {0} und das Web-Panel.'
        FileLogsDisabled = '==> Dateiprotokollierung ist deaktiviert — prüfen Sie den Verbindungsstatus im Web-Panel.'
        MissingConfiguration = 'Der Dienst wurde nicht gestartet — das Token oder die Instanzadresse fehlt. Setzen Sie Token und Url in {0} (oder führen Sie das Skript mit -Token und -Instance aus), danach: sc.exe start {1}'
        ServiceSummary = 'Zapqio Runner {0} (.NET {1}) — Dienst {2} ({3}).'
        DirectorySummary = '  Verzeichnis: {0}'
        ConfigurationSummary = '  Konfiguration: {0}'
        ConcurrencySummary = '  Gleichzeitige Aufgaben: {0}'
        EnvironmentConcurrencySummary = '  Das Limit wird durch ZAPQIO_MAX_CONCURRENCY festgelegt; ändern Sie es mit dem Installationsskript und -MaxConcurrency.'
        LogsSummary = '  Protokolle: {0}'
        LogsDisabled = 'deaktiviert (Logger:PathDirectory ist leer)'
        ModulesSummary = '  Module (.zip): {0} — starten Sie den Dienst nach Änderungen neu.'
        ConfigurationContents = 'Inhalt von {0} (Token maskiert):'
        RestartAdvice = 'Bei Problemen oder zum Ändern der Einstellungen bearbeiten Sie diese Datei, danach: Restart-Service {0}'
    }
}

function Select-InstallerLanguage {
    param([string]$RequestedLanguage)

    if ($RequestedLanguage) {
        return $RequestedLanguage.ToLowerInvariant()
    }

    Write-Host ''
    Write-Host 'Wybierz język instalacji / Select installation language / Installationssprache wählen:'
    Write-Host '  1. Polski (pl)'
    Write-Host '  2. English (en)'
    Write-Host '  3. Deutsch (de)'

    while ($true) {
        try {
            $answer = Read-Host 'Wybór / Choice / Auswahl (Enter = Polski)'
        } catch {
            # PowerShell -NonInteractive nie pozwala na Read-Host. Zachowaj dotychczasowy język.
            return 'pl'
        }

        switch (([string]$answer).Trim().ToLowerInvariant()) {
            { $_ -in @('', '1', 'pl') } { return 'pl' }
            { $_ -in @('2', 'en') } { return 'en' }
            { $_ -in @('3', 'de') } { return 'de' }
            default {
                Write-Warning 'Wpisz 1/pl, 2/en lub 3/de. / Enter 1/pl, 2/en or 3/de. / Geben Sie 1/pl, 2/en oder 3/de ein.'
            }
        }
    }
}

function Get-InstallerMessage {
    param(
        [Parameter(Mandatory)][string]$Key,
        [object[]]$Values = @()
    )

    $message = $installerMessages[$Language][$Key]
    if ($Values.Count -gt 0) {
        return $message -f $Values
    }

    return $message
}

function Read-InstallerValue {
    param(
        [Parameter(Mandatory)][string]$PromptKey,
        [Parameter(Mandatory)][string]$DefaultValue
    )

    $prompt = Get-InstallerMessage $PromptKey @($DefaultValue)
    try {
        $answer = Read-Host $prompt
    } catch {
        # PowerShell -NonInteractive nie pozwala na Read-Host. Zachowaj wartość domyślną.
        return $DefaultValue
    }

    $answer = ([string]$answer).Trim()
    if ($answer) {
        return $answer
    }

    return $DefaultValue
}

$Language = Select-InstallerLanguage $Language
Write-Host (Get-InstallerMessage 'LanguageSelected')

if ($Instance) {
    if ($Url) {
        throw (Get-InstallerMessage 'InstanceOrUrl')
    }

    if ($Instance -match '[:/\s]') {
        throw (Get-InstallerMessage 'InstanceNameOnly')
    }

    $Url = "$baseUrl/$Instance"
}

# #Requires -RunAsAdministrator nie działa, gdy skrypt idzie przez irm | scriptblock
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw (Get-InstallerMessage 'AdministratorRequired')
}

# Paczka jest framework-dependent: runtime musi być na maszynie. Potrzebny jest .NET Desktop
# Runtime x64 (zawiera zwykły runtime), bo runner odwołuje się do Microsoft.WindowsDesktop.App —
# domyślnie w wersji 8, a dla wariantu -Net10 w wersji 10.
# Sprawdzamy instalację x64 pod Program Files (tam szuka apphost usługi), a dopiero potem PATH —
# dotnet.exe z PATH bywa 32-bitowy albo z katalogu użytkownika, którego usługa nie zobaczy.
function Test-DotnetDesktopRuntime {
    param([Parameter(Mandatory)][int]$Major)
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    $fromPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates += $fromPath.Source }
    foreach ($dotnet in $candidates | Select-Object -Unique) {
        if (-not (Test-Path $dotnet)) { continue }
        $runtimes = & $dotnet --list-runtimes
        if ($runtimes | Where-Object { $_ -match "^Microsoft\.WindowsDesktop\.App $Major\." }) { return $true }
    }
    return $false
}

$runtimeMajor = if ($Net10) { 10 } else { 8 }
if (-not (Test-DotnetDesktopRuntime $runtimeMajor)) {
    throw (Get-InstallerMessage 'DesktopRuntimeRequired' @($runtimeMajor))
}

function Invoke-Sc {
    param([Parameter(Mandatory)][string[]]$ScArgs)
    $out = & sc.exe @ScArgs
    if ($LASTEXITCODE -ne 0) {
        throw (Get-InstallerMessage 'ServiceCommandFailed' @(($ScArgs -join ' '), $LASTEXITCODE, ($out -join "`n")))
    }
}

# ConvertTo-Json w Windows PowerShell formatuje brzydko (podwójne spacje po dwukropku,
# rozjechane wcięcia zagnieżdżonych obiektów) — znormalizuj do wcięć 2-spacjowych
function Format-Json {
    param([Parameter(Mandatory)][string]$Json)
    $indent = 0
    (($Json -split "`r?`n") | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^[}\]]') { $indent = [Math]::Max(0, $indent - 1) }
        $out = (' ' * (2 * $indent)) + ($line -replace '":\s+', '": ')
        if ($line -match '[{\[]$') { $indent++ }
        $out
    }) -join [Environment]::NewLine
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# Potwierdź ustawienia przed pobieraniem paczki i zmianami w istniejącej instalacji.
if (-not $PSBoundParameters.ContainsKey('InstallDir')) {
    $InstallDir = Read-InstallerValue 'InstallDirPrompt' $InstallDir
}

if (-not $PSBoundParameters.ContainsKey('ServiceName')) {
    $ServiceName = Read-InstallerValue 'ServiceNamePrompt' $ServiceName
}

# --- 1. Ustalenie wersji ------------------------------------------------------

if ($Version) {
    $Version = $Version -replace '^v', ''
} else {
    Write-Host (Get-InstallerMessage 'CheckingLatestRelease')
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" `
        -Headers @{ 'User-Agent' = 'zapqio-runner-install' }
    $Version = $release.tag_name -replace '^v', ''
}

$flavor = if ($Net10) { '' } else { '-net8' }
$zipName = "zapqio-runner-$Version-win-x64$flavor.zip"
$zipUrl = "https://github.com/$repo/releases/download/v$Version/$zipName"

$tempDir = Join-Path $env:TEMP ("zapqio-runner-install-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    # --- 2. Pobranie i rozpakowanie ------------------------------------------

    Write-Host (Get-InstallerMessage 'Downloading' @($zipUrl))
    $zipPath = Join-Path $tempDir $zipName
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    } catch {
        $packageHint = if ($Net10) { '' } else { Get-InstallerMessage 'Net8PackageHint' }

        throw (Get-InstallerMessage 'DownloadFailed' @($_.Exception.Message, $Version, $repo, $packageHint))
    } finally {
        $ProgressPreference = $oldProgress
    }

    Write-Host (Get-InstallerMessage 'Extracting')
    $extractDir = Join-Path $tempDir 'extract'
    Expand-Archive -Path $zipPath -DestinationPath $extractDir
    if (-not (Test-Path (Join-Path $extractDir 'Zapqio.Runner.exe'))) {
        throw (Get-InstallerMessage 'InvalidPackage')
    }

    # --- 3. Zatrzymanie usługi przy aktualizacji -----------------------------

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $isUpgrade = [bool]$service
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host (Get-InstallerMessage 'StoppingService' @($ServiceName))
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2  # chwila na zwolnienie uchwytów plików
    }

    # --- 4. Podmiana plików --------------------------------------------------

    $exePath = Join-Path $InstallDir 'Zapqio.Runner.exe'
    $appsettingsPath = Join-Path $InstallDir 'appsettings.json'
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    # Stare pliki sprzątamy tylko wtedy, gdy katalog na pewno jest instalacją runnera
    if (Test-Path $exePath) {
        $preserve = 'appsettings.json', '##Name', '##ServiceName', 'Modules', 'Logs', 'Config', 'Deployments', 'Build'
        Get-ChildItem -Path $InstallDir -Force |
            Where-Object { $_.Name -notin $preserve } |
            Remove-Item -Recurse -Force
    }

    Write-Host (Get-InstallerMessage 'CopyingFiles' @($InstallDir))
    Get-ChildItem -Path $extractDir -Force |
        Where-Object { $_.Name -ne 'appsettings.json' } |
        Copy-Item -Destination $InstallDir -Recurse -Force

    # --- 5. Konfiguracja -----------------------------------------------------

    # Paczkowy appsettings.json zawiera komentarze (JSONC), więc zamiast niego
    # utrzymujemy czysty JSON, który można potem programowo aktualizować.
    $hadConfig = Test-Path $appsettingsPath
    if (-not $hadConfig) {
        @'
{
  "Logger": {
    "LogLevel": "Information",
    "PathDirectory": "Logs"
  },
  "Token": "",
  "Name": "",
  "Url": "",
  "MaxConcurrency": 1
}
'@ | Set-Content -Path $appsettingsPath -Encoding UTF8
    }

    $overrides = @{}
    if ($Token)      { $overrides['Token'] = $Token }
    if ($Url)        { $overrides['Url'] = $Url }
    if ($RunnerName) { $overrides['Name'] = $RunnerName }
    $setMaxConcurrency = $PSBoundParameters.ContainsKey('MaxConcurrency')
    if ($setMaxConcurrency) { $overrides['MaxConcurrency'] = $MaxConcurrency }
    $loggerOverrides = @{}
    if ($LogLevel) { $loggerOverrides['LogLevel'] = $LogLevel }
    # -LogDirectory "" to jawne wyłączenie logów plikowych, więc licz się z pustą wartością
    if ($PSBoundParameters.ContainsKey('LogDirectory')) { $loggerOverrides['PathDirectory'] = $LogDirectory }

    # Czy jest z czym się łączyć: parametry > appsettings.json > rejestr usługi > zmienne maszynowe
    $rawConfig = Get-Content -Path $appsettingsPath -Raw
    $fileToken = if ($rawConfig -match '"Token"\s*:\s*"([^"]*)"') { $Matches[1] } else { '' }
    $fileUrl   = if ($rawConfig -match '"Url"\s*:\s*"([^"]*)"')   { $Matches[1] } else { '' }
    $fileName  = if ($rawConfig -match '"Name"\s*:\s*"([^"]*)"')  { $Matches[1] } else { '' }
    $serviceEnv = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
        -Name Environment -ErrorAction SilentlyContinue).Environment
    $machineMaxConcurrency = [Environment]::GetEnvironmentVariable('ZAPQIO_MAX_CONCURRENCY', 'Machine')
    $haveToken = $overrides['Token'] -or $fileToken -or ($serviceEnv -like 'ZAPQIO_TOKEN=?*') -or [Environment]::GetEnvironmentVariable('ZAPQIO_TOKEN', 'Machine')
    $haveUrl   = $overrides['Url']   -or $fileUrl   -or ($serviceEnv -like 'ZAPQIO_URL=?*')   -or [Environment]::GetEnvironmentVariable('ZAPQIO_URL', 'Machine')

    # O braki dopytaj i nie odpuszczaj — bez adresu i tokenu usługa nie ma się z czym połączyć
    if (-not $haveUrl -or -not $haveToken) {
        try {
            if (-not $haveUrl) {
                do {
                    $answer = (Read-Host (Get-InstallerMessage 'InstancePrompt')).Trim().Trim('/')
                } while (-not $answer)
                # Wklejony pełny adres ws(s):// przyjmij bez sklejania z $baseUrl
                $overrides['Url'] = if ($answer -match '^wss?://') { $answer } else { "$baseUrl/$answer" }
                $haveUrl = $true
            }

            if (-not $haveToken) {
                do {
                    $answer = (Read-Host (Get-InstallerMessage 'TokenPrompt')).Trim()
                } while (-not $answer)
                $overrides['Token'] = $answer
                $haveToken = $true
            }

            # Nazwę wolno nadać tylko, dopóki runner jej nie ma — potem jest związana z tokenem
            if (-not $overrides['Name'] -and -not $fileName -and -not (Test-Path (Join-Path $InstallDir '##Name'))) {
                $answer = Read-Host (Get-InstallerMessage 'RunnerNamePrompt')
                if ($answer) {
                    $overrides['Name'] = $answer
                }
            }

            if (-not $hadConfig -and -not $setMaxConcurrency) {
                do {
                    $answer = (Read-Host (Get-InstallerMessage 'ConcurrencyPrompt')).Trim()
                    if (-not $answer) {
                        $answer = '1'
                    }

                    $parsedConcurrency = 0
                    $validConcurrency = [int]::TryParse($answer, [ref]$parsedConcurrency) -and $parsedConcurrency -ge 1
                    if (-not $validConcurrency) {
                        Write-Warning (Get-InstallerMessage 'PositiveIntegerRequired')
                    }
                } while (-not $validConcurrency)
                $overrides['MaxConcurrency'] = $parsedConcurrency
                $setMaxConcurrency = $true
            }
        } catch {
            # sesja nieinteraktywna (brak konsoli) — niżej zostanie ostrzeżenie zamiast startu
        }
    }

    if ($overrides.Count -gt 0 -or $loggerOverrides.Count -gt 0) {
        try {
            $config = Get-Content -Path $appsettingsPath -Raw | ConvertFrom-Json
            foreach ($key in $overrides.Keys) {
                $config | Add-Member -NotePropertyName $key -NotePropertyValue $overrides[$key] -Force
            }
            if ($loggerOverrides.Count -gt 0) {
                $logger = $config.PSObject.Properties['Logger']
                if (-not $logger -or $logger.Value -isnot [System.Management.Automation.PSCustomObject]) {
                    $config | Add-Member -NotePropertyName Logger -NotePropertyValue ([pscustomobject]@{}) -Force
                }
                foreach ($key in $loggerOverrides.Keys) {
                    $config.Logger | Add-Member -NotePropertyName $key -NotePropertyValue $loggerOverrides[$key] -Force
                }
            }
            Format-Json ($config | ConvertTo-Json -Depth 10) | Set-Content -Path $appsettingsPath -Encoding UTF8
            $savedKeys = @($overrides.Keys) + @($loggerOverrides.Keys | ForEach-Object { "Logger:$_" })
            Write-Host (Get-InstallerMessage 'ConfigurationSaved' @(($savedKeys -join ', ')))
        } catch {
            if ($setMaxConcurrency) {
                throw (Get-InstallerMessage 'ConcurrencySaveFailed' @($appsettingsPath, $_.Exception.Message))
            }

            $wanted = @($overrides.Keys) + @($loggerOverrides.Keys | ForEach-Object { "Logger:$_" })
            Write-Warning (Get-InstallerMessage 'ConfigurationParseFailed' @($appsettingsPath, ($wanted -join ', ')))
        }
    }

    # --- 6. Rejestracja usługi -----------------------------------------------

    if (-not $isUpgrade) {
        Write-Host (Get-InstallerMessage 'RegisteringService' @($ServiceName))
        $displayName = if ($ServiceName -eq 'ZapqioRunner') { 'Zapqio Runner' } else { $ServiceName }
        Invoke-Sc @('create', $ServiceName, 'binPath=', "`"$exePath`"", 'start=', 'auto', 'DisplayName=', $displayName)
        Invoke-Sc @('description', $ServiceName, (Get-InstallerMessage 'ServiceDescription'))
        Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/60000')
    } else {
        # Gdy ktoś zmienił -InstallDir między instalacjami, dociągnij binPath do nowego exe
        $currentPath = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName
        if ($currentPath -and $currentPath.Trim('"') -ne $exePath) {
            Write-Host (Get-InstallerMessage 'UpdatingServicePath')
            Invoke-Sc @('config', $ServiceName, 'binPath=', "`"$exePath`"")
        }
    }

    # Program.cs nadaje ZAPQIO_MAX_CONCURRENCY pierwszeństwo przed plikiem. Jawny wybór
    # w instalatorze musi wygrać także z poprzednim ustawieniem środowiska usługi.
    if ($setMaxConcurrency) {
        $hadServiceConcurrency = @($serviceEnv | Where-Object { $_ -like 'ZAPQIO_MAX_CONCURRENCY=*' }).Count -gt 0
        if ($hadServiceConcurrency -or $machineMaxConcurrency) {
            $serviceEnv = @($serviceEnv | Where-Object { $_ -and $_ -notlike 'ZAPQIO_MAX_CONCURRENCY=*' })
            # Globalnego środowiska nie zmieniamy. Jeżeli narzuca wartość, nadpisz ją
            # tylko dla tej usługi; w pozostałych przypadkach źródłem pozostaje plik.
            if ($machineMaxConcurrency) {
                $serviceEnv += "ZAPQIO_MAX_CONCURRENCY=$($overrides['MaxConcurrency'])"
            }
            $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
            if ($serviceEnv.Count -gt 0) {
                New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value ([string[]]$serviceEnv) -Force | Out-Null
            } else {
                Remove-ItemProperty -Path $serviceKey -Name Environment -ErrorAction Stop
            }
        }
    }

    # Konto ustawiaj także przy aktualizacji — naprawia instalacje, które zostały na LocalSystem.
    # Bez password= — konto wirtualne go nie potrzebuje, a Windows PowerShell gubi pusty argument
    # '' przy wywołaniu natywnego programu, przez co sc.exe dostawało gołe "password=" i kończyło
    # z ERROR_INVALID_COMMAND_LINE (1639).
    if (-not $LocalSystem) {
        Invoke-Sc @('config', $ServiceName, 'obj=', "NT SERVICE\$ServiceName")
    }

    # --- 7. Uprawnienia ------------------------------------------------------

    if (-not $LocalSystem) {
        Write-Host (Get-InstallerMessage 'GrantingDirectoryAccess')
        & icacls $InstallDir /grant "NT SERVICE\${ServiceName}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw (Get-InstallerMessage 'AccessControlFailed' @($InstallDir, $LASTEXITCODE))
        }

        # Moduł może zrestartować usługę po podmianie swoich bibliotek (tak robi moduł Nexo po aktualizacji
        # Subiekta): konto usługi dostaje prawo start/stop na własnej usłudze. Bez tego prawa skrypt modułu
        # kończy proces runnera i usługa wstaje z opcji odzyskiwania, więc błąd tutaj nie przerywa instalacji.
        Write-Host (Get-InstallerMessage 'GrantingServiceRestart')
        try {
            $serviceSid = (New-Object System.Security.Principal.NTAccount("NT SERVICE\$ServiceName")).Translate([System.Security.Principal.SecurityIdentifier]).Value
            $sddl = (& sc.exe sdshow $ServiceName | Where-Object { $_ -match '^D:' }) -join ''
            if (-not $sddl) {
                throw (Get-InstallerMessage 'MissingSecurityDescriptor')
            }

            if ($sddl -notlike "*$serviceSid*") {
                $ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$serviceSid)"
                $newSddl = if ($sddl -match '^(D:.*?)(S:.*)$') { $Matches[1] + $ace + $Matches[2] } else { $sddl + $ace }
                Invoke-Sc @('sdset', $ServiceName, $newSddl)
            }
        }
        catch {
            Write-Warning (Get-InstallerMessage 'ServiceRestartPermissionFailed' @($_.Exception.Message))
        }
    }

    if ($overrides['Token'] -or $fileToken) {
        # Token leży w pliku — zawęź ACL (SID-y zamiast nazw, bo grupy są zlokalizowane)
        $grants = @('*S-1-5-18:F', '*S-1-5-32-544:F')  # SYSTEM, Administratorzy
        if (-not $LocalSystem) { $grants += "NT SERVICE\${ServiceName}:R" }
        & icacls $appsettingsPath /inheritance:r /grant:r $grants | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw (Get-InstallerMessage 'AccessControlFailed' @($appsettingsPath, $LASTEXITCODE))
        }
    }

    # Konfiguracja modułów (Config\<moduł>.json) - moduły trzymają tam hasła do swoich systemów, więc ACL
    # jak na appsettings.json z tokenem. Konto usługi dostaje Modify, bo moduł zakłada plik przy pierwszym
    # starcie; inni użytkownicy maszyny nie czytają. Runner zakłada katalog sam, tu tylko uprawnienia.
    [System.IO.File]::WriteAllText((Join-Path $InstallDir '##ServiceName'), $ServiceName)
    $deploymentDir = Join-Path $InstallDir 'Deployments'
    New-Item -ItemType Directory -Path $deploymentDir -Force | Out-Null
    $deploymentGrants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    if (-not $LocalSystem)
    {
        $deploymentGrants += "NT SERVICE\${ServiceName}:(OI)(CI)M"
    }

    & icacls $deploymentDir /inheritance:r /grant:r $deploymentGrants | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw (Get-InstallerMessage 'AccessControlFailed' @($deploymentDir, $LASTEXITCODE))
    }

    $configDir = Join-Path $InstallDir 'Config'
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }
    $configGrants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    if (-not $LocalSystem) { $configGrants += "NT SERVICE\${ServiceName}:(OI)(CI)M" }
    & icacls $configDir /inheritance:r /grant:r $configGrants | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw (Get-InstallerMessage 'AccessControlFailed' @($configDir, $LASTEXITCODE))
    }

    # --- 8. Start i weryfikacja połączenia -----------------------------------

    # Katalog logów wg zapisanej konfiguracji — potrzebny do weryfikacji i podsumowania
    $rawConfig = Get-Content -Path $appsettingsPath -Raw
    $logsDir = $null
    if ($rawConfig -match '"PathDirectory"\s*:\s*"([^"]*)"' -and $Matches[1]) {
        $logsPath = $Matches[1] -replace '\\\\', '\'
        $logsDir = if ([IO.Path]::IsPathRooted($logsPath)) { $logsPath } else { Join-Path $InstallDir $logsPath }
    }

    if ($haveToken -and $haveUrl) {
        # Zapamiętaj, dokąd log sięgał przed startem — weryfikacja czyta tylko nowe wpisy
        $preLogFile = $null
        $preLogOffset = 0
        if ($logsDir) {
            $preLogFile = Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($preLogFile) { $preLogOffset = $preLogFile.Length }
        }

        Write-Host (Get-InstallerMessage 'StartingService' @($ServiceName))
        $started = $false
        try {
            Start-Service -Name $ServiceName
            $started = $true
        } catch {
            Write-Warning (Get-InstallerMessage 'ServiceStartFailed' @($_.Exception.Message))
            $diagnosticsLocation = if ($logsDir) { $logsDir } else { Get-InstallerMessage 'EventLogLocation' }

            Write-Warning (Get-InstallerMessage 'ServiceStartAdvice' @($diagnosticsLocation, $ServiceName))
        }

        if ($started -and $logsDir) {
            Write-Host (Get-InstallerMessage 'WaitingForConnection')
            $deadline = (Get-Date).AddSeconds(30)
            $connected = $false
            $rejection = $null
            $retrying = $false
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Seconds 2
                $logFile = Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if (-not $logFile) { continue }
                $offset = if ($preLogFile -and $logFile.FullName -eq $preLogFile.FullName) { $preLogOffset } else { 0 }
                try {
                    $stream = [IO.File]::Open($logFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                    try {
                        if ($offset -le $stream.Length) { [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin) }
                        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
                        $newLog = $reader.ReadToEnd()
                    } finally {
                        $stream.Dispose()
                    }
                } catch {
                    continue
                }
                if ($newLog -match 'Successfully connected to WebSocket') { $connected = $true; break }
                if ($newLog -match '(Serwer odrzucił uzgadnianie[^\r\n]*|Serwer ogranicza tempo uzgodnień[^\r\n]*)') { $rejection = $Matches[1]; break }
                if ($newLog -match 'Kolejna próba połączenia') { $retrying = $true }
            }
            if ($connected) {
                Write-Host (Get-InstallerMessage 'Connected')
            } elseif ($rejection) {
                Write-Warning (Get-InstallerMessage 'ConnectionRejected' @($rejection))
            } elseif ($retrying) {
                Write-Warning (Get-InstallerMessage 'ConnectionRetrying' @($logsDir))
            } else {
                Write-Warning (Get-InstallerMessage 'ConnectionUnconfirmed' @($logsDir))
            }
        } elseif ($started) {
            Write-Host (Get-InstallerMessage 'FileLogsDisabled')
        }
    } else {
        Write-Warning (Get-InstallerMessage 'MissingConfiguration' @($appsettingsPath, $ServiceName))
    }

    # --- 9. Podsumowanie -----------------------------------------------------

    $effectiveConcurrency = 1
    $parsedConcurrency = 0
    if ($rawConfig -match '"MaxConcurrency"\s*:\s*(-?\d+)' -and [int]::TryParse($Matches[1], [ref]$parsedConcurrency)) {
        $effectiveConcurrency = [Math]::Max(1, $parsedConcurrency)
    }
    $serviceConcurrency = @($serviceEnv | Where-Object { $_ -like 'ZAPQIO_MAX_CONCURRENCY=*' } | Select-Object -Last 1)
    $environmentConcurrency = if ($serviceConcurrency.Count) { $serviceConcurrency[0].Substring('ZAPQIO_MAX_CONCURRENCY='.Length) } else { $machineMaxConcurrency }
    if ([int]::TryParse($environmentConcurrency, [ref]$parsedConcurrency)) {
        $effectiveConcurrency = [Math]::Max(1, $parsedConcurrency)
    }

    $installedVersion = (Get-Item $exePath).VersionInfo.ProductVersion
    $status = (Get-Service -Name $ServiceName).Status
    Write-Host ''
    Write-Host (Get-InstallerMessage 'ServiceSummary' @($installedVersion, $runtimeMajor, $ServiceName, $status))
    Write-Host (Get-InstallerMessage 'DirectorySummary' @($InstallDir))
    Write-Host (Get-InstallerMessage 'ConfigurationSummary' @($appsettingsPath))
    Write-Host (Get-InstallerMessage 'ConcurrencySummary' @($effectiveConcurrency))
    if ($environmentConcurrency) {
        Write-Host (Get-InstallerMessage 'EnvironmentConcurrencySummary')
    }

    $logSummary = if ($logsDir) { $logsDir } else { Get-InstallerMessage 'LogsDisabled' }

    Write-Host (Get-InstallerMessage 'LogsSummary' @($logSummary))
    Write-Host (Get-InstallerMessage 'ModulesSummary' @((Join-Path $InstallDir 'Modules')))
    Write-Host ''
    Write-Host (Get-InstallerMessage 'ConfigurationContents' @($appsettingsPath))
    Write-Host (($rawConfig -replace '("Token"\s*:\s*")([^"]{4})[^"]{4,}(")', '$1$2...$3').TrimEnd())
    Write-Host ''
    Write-Host (Get-InstallerMessage 'RestartAdvice' @($ServiceName))
} finally {
    # Remove-Item wykrzacza się na ścieżkach ze skróconą nazwą 8.3 (np. C:\Users\UKASZ~1),
    # a taką potrafi mieć %TEMP% — .NET usuwa je bez problemu
    try { [IO.Directory]::Delete($tempDir, $true) } catch {}
}
