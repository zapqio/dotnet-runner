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
    bo runner zapisuje pliki obok binarki.

.PARAMETER ServiceName
    Nazwa usługi Windows. Domyślnie ZapqioRunner.

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

.EXAMPLE
    .\install.ps1 -Instance test -Token <token>

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
    [switch]$Net10
)

$ErrorActionPreference = 'Stop'
$repo = 'zapqio/dotnet-runner'
$baseUrl = 'wss://app.zapq.io'

if ($Instance) {
    if ($Url) { throw 'Podaj -Instance albo -Url — nie oba naraz.' }
    if ($Instance -match '[:/\s]') { throw '-Instance to sama nazwa instancji (np. test); pełny adres podaje się przez -Url.' }
    $Url = "$baseUrl/$Instance"
}

# #Requires -RunAsAdministrator nie działa, gdy skrypt idzie przez irm | scriptblock
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Uruchom ten skrypt w PowerShellu jako administrator.'
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
    throw @"
Brak .NET Desktop Runtime $runtimeMajor (x64), którego wymaga runner. Zainstaluj go i uruchom skrypt ponownie:
  winget install Microsoft.DotNet.DesktopRuntime.$runtimeMajor
albo instalatorem ze strony https://dotnet.microsoft.com/download/dotnet/$runtimeMajor.0 (sekcja ".NET Desktop Runtime", Windows x64).
"@
}

function Invoke-Sc {
    param([Parameter(Mandatory)][string[]]$ScArgs)
    $out = & sc.exe @ScArgs
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($ScArgs -join ' ') zakończyło się kodem ${LASTEXITCODE}:`n$($out -join "`n")"
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

# --- 1. Ustalenie wersji ------------------------------------------------------

if ($Version) {
    $Version = $Version -replace '^v', ''
} else {
    Write-Host '==> Sprawdzam najnowszy release...'
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

    Write-Host "==> Pobieram $zipUrl"
    $zipPath = Join-Path $tempDir $zipName
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    } catch {
        throw "Nie udało się pobrać paczki ($($_.Exception.Message)). Sprawdź, czy wersja $Version istnieje: https://github.com/$repo/releases$(if (-not $Net10) { ' — paczki win-x64-net8 są wydawane od 0.1.4.' })"
    } finally {
        $ProgressPreference = $oldProgress
    }

    Write-Host '==> Rozpakowuję...'
    $extractDir = Join-Path $tempDir 'extract'
    Expand-Archive -Path $zipPath -DestinationPath $extractDir
    if (-not (Test-Path (Join-Path $extractDir 'Zapqio.Runner.exe'))) {
        throw 'W paczce nie ma Zapqio.Runner.exe — to nie wygląda na paczkę win-x64 runnera.'
    }

    # --- 3. Zatrzymanie usługi przy aktualizacji -----------------------------

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $isUpgrade = [bool]$service
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "==> Zatrzymuję usługę $ServiceName..."
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

    Write-Host "==> Kopiuję pliki do $InstallDir..."
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
                    $answer = (Read-Host 'Nazwa instancji Web, np. test').Trim().Trim('/')
                } while (-not $answer)
                # Wklejony pełny adres ws(s):// przyjmij bez sklejania z $baseUrl
                $overrides['Url'] = if ($answer -match '^wss?://') { $answer } else { "$baseUrl/$answer" }
                $haveUrl = $true
            }
            if (-not $haveToken) {
                do {
                    $answer = (Read-Host 'Token runnera z panelu Web').Trim()
                } while (-not $answer)
                $overrides['Token'] = $answer
                $haveToken = $true
            }
            # Nazwę wolno nadać tylko, dopóki runner jej nie ma — potem jest związana z tokenem
            if (-not $overrides['Name'] -and -not $fileName -and -not (Test-Path (Join-Path $InstallDir '##Name'))) {
                $answer = Read-Host 'Nazwa runnera (Enter = wygeneruje się UUID; nazwy nie da się potem zmienić)'
                if ($answer) { $overrides['Name'] = $answer }
            }
            if (-not $hadConfig -and -not $setMaxConcurrency) {
                do {
                    $answer = (Read-Host 'Maksymalna liczba równoczesnych zadań (Enter = 1)').Trim()
                    if (-not $answer) { $answer = '1' }
                    $parsedConcurrency = 0
                    $validConcurrency = [int]::TryParse($answer, [ref]$parsedConcurrency) -and $parsedConcurrency -ge 1
                    if (-not $validConcurrency) { Write-Warning 'Podaj liczbę całkowitą większą od zera.' }
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
            Write-Host "==> Zapisano w appsettings.json: $($savedKeys -join ', ')."
        } catch {
            if ($setMaxConcurrency) {
                throw "Nie udało się zapisać MaxConcurrency do ${appsettingsPath}: $($_.Exception.Message)"
            }
            $wanted = @($overrides.Keys) + @($loggerOverrides.Keys | ForEach-Object { "Logger:$_" })
            Write-Warning "Nie udało się sparsować $appsettingsPath (plik z komentarzami?). Ustaw ręcznie: $($wanted -join ', ')."
        }
    }

    # --- 6. Rejestracja usługi -----------------------------------------------

    if (-not $isUpgrade) {
        Write-Host "==> Rejestruję usługę $ServiceName..."
        $displayName = if ($ServiceName -eq 'ZapqioRunner') { 'Zapqio Runner' } else { $ServiceName }
        Invoke-Sc @('create', $ServiceName, 'binPath=', "`"$exePath`"", 'start=', 'auto', 'DisplayName=', $displayName)
        Invoke-Sc @('description', $ServiceName, 'Zapqio Runner - wykonuje zadania zlecane przez serwer Web Zapqio.')
        Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/60000')
    } else {
        # Gdy ktoś zmienił -InstallDir między instalacjami, dociągnij binPath do nowego exe
        $currentPath = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName
        if ($currentPath -and $currentPath.Trim('"') -ne $exePath) {
            Write-Host '==> Aktualizuję ścieżkę binarki w usłudze...'
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
        Write-Host '==> Nadaję kontu usługi prawo zapisu do katalogu instalacji...'
        & icacls $InstallDir /grant "NT SERVICE\${ServiceName}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls na $InstallDir zakończyło się kodem $LASTEXITCODE." }

        # Moduł może zrestartować usługę po podmianie swoich bibliotek (tak robi moduł Nexo po aktualizacji
        # Subiekta): konto usługi dostaje prawo start/stop na własnej usłudze. Bez tego prawa skrypt modułu
        # kończy proces runnera i usługa wstaje z opcji odzyskiwania, więc błąd tutaj nie przerywa instalacji.
        Write-Host '==> Nadaję kontu usługi prawo restartu własnej usługi...'
        try {
            $serviceSid = (New-Object System.Security.Principal.NTAccount("NT SERVICE\$ServiceName")).Translate([System.Security.Principal.SecurityIdentifier]).Value
            $sddl = (& sc.exe sdshow $ServiceName | Where-Object { $_ -match '^D:' }) -join ''
            if (-not $sddl) { throw 'sc.exe sdshow nie zwróciło deskryptora zabezpieczeń' }
            if ($sddl -notlike "*$serviceSid*") {
                $ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$serviceSid)"
                $newSddl = if ($sddl -match '^(D:.*?)(S:.*)$') { $Matches[1] + $ace + $Matches[2] } else { $sddl + $ace }
                Invoke-Sc @('sdset', $ServiceName, $newSddl)
            }
        }
        catch {
            Write-Warning "Nie udało się nadać kontu usługi prawa restartu ($($_.Exception.Message)). Moduły podmieniające biblioteki użyją opcji odzyskiwania usługi."
        }
    }

    if ($overrides['Token'] -or $fileToken) {
        # Token leży w pliku — zawęź ACL (SID-y zamiast nazw, bo grupy są zlokalizowane)
        $grants = @('*S-1-5-18:F', '*S-1-5-32-544:F')  # SYSTEM, Administratorzy
        if (-not $LocalSystem) { $grants += "NT SERVICE\${ServiceName}:R" }
        & icacls $appsettingsPath /inheritance:r /grant:r $grants | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls na $appsettingsPath zakończyło się kodem $LASTEXITCODE." }
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
        throw "icacls na $deploymentDir zakończyło się kodem $LASTEXITCODE."
    }

    $configDir = Join-Path $InstallDir 'Config'
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }
    $configGrants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    if (-not $LocalSystem) { $configGrants += "NT SERVICE\${ServiceName}:(OI)(CI)M" }
    & icacls $configDir /inheritance:r /grant:r $configGrants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls na $configDir zakończyło się kodem $LASTEXITCODE." }

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

        Write-Host "==> Uruchamiam usługę $ServiceName..."
        $started = $false
        try {
            Start-Service -Name $ServiceName
            $started = $true
        } catch {
            Write-Warning "Nie udało się uruchomić usługi: $($_.Exception.Message)"
            Write-Warning "Zajrzyj do $(if ($logsDir) { $logsDir } else { 'Dziennika zdarzeń Windows (Application)' }), popraw konfigurację i spróbuj: sc.exe start $ServiceName"
        }

        if ($started -and $logsDir) {
            Write-Host '==> Czekam na ślad połączenia WebSocket w logu (do 30 s)...'
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
                Write-Host '==> Runner nawiązał połączenie WebSocket z instancją Web.'
            } elseif ($rejection) {
                Write-Warning "Serwer odrzucił połączenie: $rejection — sprawdź adres instancji i token (docs/szczegoly.md > Rozwiązywanie problemów)."
            } elseif ($retrying) {
                Write-Warning "Runner nie może nawiązać połączenia i ponawia próby — sprawdź adres instancji, token i dostęp sieciowy. Log: $logsDir"
            } else {
                Write-Warning "W ciągu 30 s log nie potwierdził połączenia — zajrzyj do $logsDir i do panelu Web."
            }
        } elseif ($started) {
            Write-Host '==> Logi plikowe wyłączone — status połączenia sprawdź w panelu Web.'
        }
    } else {
        Write-Warning "Usługa nie została uruchomiona — brak tokenu lub adresu instancji. Uzupełnij Token i Url w $appsettingsPath (albo uruchom skrypt z -Token i -Instance), potem: sc.exe start $ServiceName"
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
    Write-Host "Zapqio Runner $installedVersion (.NET $runtimeMajor) — usługa $ServiceName ($status)."
    Write-Host "  Katalog: $InstallDir"
    Write-Host "  Konfiguracja: $appsettingsPath"
    Write-Host "  Równoczesne zadania: $effectiveConcurrency"
    if ($environmentConcurrency) {
        Write-Host '  Limit ustala ZAPQIO_MAX_CONCURRENCY; zmienisz go instalatorem z -MaxConcurrency.'
    }
    Write-Host "  Logi: $(if ($logsDir) { $logsDir } else { 'wyłączone (Logger:PathDirectory puste)' })"
    Write-Host "  Moduły (.zip): $(Join-Path $InstallDir 'Modules') — po zmianach zrestartuj usługę."
    Write-Host ''
    Write-Host "Zawartość $appsettingsPath (token zamaskowany):"
    Write-Host (($rawConfig -replace '("Token"\s*:\s*")([^"]{4})[^"]{4,}(")', '$1$2...$3').TrimEnd())
    Write-Host ''
    Write-Host "W razie problemów lub zmiany ustawień edytuj ten plik, potem: Restart-Service $ServiceName"
} finally {
    # Remove-Item wykrzacza się na ścieżkach ze skróconą nazwą 8.3 (np. C:\Users\UKASZ~1),
    # a taką potrafi mieć %TEMP% — .NET usuwa je bez problemu
    try { [IO.Directory]::Delete($tempDir, $true) } catch {}
}
