[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$name = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'gitleaks.exe' } else { 'gitleaks' }
$scanner = Join-Path $repository ".local/security/tools/gitleaks/8.30.1/$name"
$config = Join-Path $repository '.gitleaks.toml'
$root = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp' } else { [IO.Path]::GetTempPath() }
$root = [IO.Path]::GetFullPath($root)
$directory = Join-Path $root ('zapq-secret-rules-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$report = Join-Path $directory 'report.json'
# Synthetic values exist only in memory and temporary files.
$fake = 'zq-canary-' + [Guid]::NewGuid().ToString('N')
$cases = @(
    @{ Name = 'connection string'; Input = ('"Host=localhost;Password=' + $fake + ';Database=test"'); Rule = 'zapq-connection-password' }
    @{ Name = 'quoted connection password'; Input = ('"Host=localhost;Password=''' + $fake + ''';Database=test"'); Rule = 'zapq-connection-password' }
    @{ Name = 'PowerShell password'; Input = ('$env:PGPASSWORD = "' + $fake + '"'); Rule = 'zapq-environment-password' }
    @{ Name = 'Bash password'; Input = ('PGPASSWORD=' + $fake + ' psql'); Rule = 'zapq-environment-password' }
    @{ Name = 'JSON token'; Input = ('{"ZAPQIO_TOKEN": "' + $fake + '"}'); Rule = 'zapq-application-secret' }
    @{ Name = 'CLI token'; Input = ('install.ps1 -Token "' + $fake + '"'); Rule = 'zapq-cli-secret' }
    @{ Name = 'CLI password'; Input = ('tool --password ' + $fake); Rule = 'zapq-cli-secret' }
    @{ Name = 'CLI equals password'; Input = ('tool --password=' + $fake); Rule = 'zapq-cli-secret' }
    @{ Name = 'SQL command password'; Input = ('sqlcmd -P ' + $fake); Rule = 'zapq-cli-secret' }
    @{ Name = 'unquoted YAML token'; Input = ('ZAPQIO_TOKEN: ' + $fake); Rule = 'zapq-unquoted-application-secret' }
    @{ Name = 'database URI'; Input = ('postgresql://user:' + $fake + '@localhost/test'); Rule = 'zapq-uri-password' }
    @{ Name = 'default private-key rule'; Input = ('-----BEGIN ' + "RSA PRIVATE KEY-----`n" + ('A' * 64) + "`n-----END " + 'RSA PRIVATE KEY-----'); Rule = 'private-key' }
    @{ Name = 'PowerShell reference'; Input = '$env:PGPASSWORD = $env:LOCAL_DB_PASSWORD'; Rule = $null }
    @{ Name = 'Bash reference'; Input = 'PGPASSWORD="$DB_PASSWORD" psql'; Rule = $null }
    @{ Name = 'Compose reference'; Input = 'PGPASSWORD: ${DB_PASSWORD}'; Rule = $null }
    @{ Name = 'documented placeholder'; Input = 'tool --password <PASSWORD>'; Rule = $null }
    @{ Name = 'MSBuild property'; Input = 'dotnet build -p:Version=1.0.0'; Rule = $null }
    @{ Name = 'CSharp property'; Input = ('Password' + ' = request.Password;'); Rule = $null }
    @{ Name = 'interpolated connection'; Input = '"Host=localhost;Password={password};Database=test"'; Rule = $null }
    @{ Name = 'empty token'; Input = '{"ZAPQIO_TOKEN": ""}'; Rule = $null }
)

try {
    foreach ($case in $cases) {
        $output = $case.Input | & $scanner stdin --config $config --redact=100 --no-banner --log-level fatal --ignore-gitleaks-allow --gitleaks-ignore-path (Join-Path $directory 'none') --report-format json --report-path $report 2>&1
        $code = $LASTEXITCODE
        if ($code -notin @(0, 1)) {
            throw "Scanner failed for fixture: $($case.Name)."
        }

        $json = Get-Content -Encoding UTF8 -LiteralPath $report -Raw
        $findings = @($json | ConvertFrom-Json)
        $rules = @($findings | Where-Object { $_ } | ForEach-Object { $_.RuleID })
        if ($case.Rule -and ($code -ne 1 -or $case.Rule -notin $rules)) {
            throw "Missed synthetic secret: $($case.Name)."
        }

        if (-not $case.Rule -and ($code -ne 0 -or $rules.Count -ne 0)) {
            throw "False positive in safe fixture: $($case.Name)."
        }

        if ($json.Contains($fake) -or (($output -join "`n").Contains($fake))) {
            throw "Scanner disclosed a synthetic secret: $($case.Name)."
        }
    }

    $fixtureDirectory = Join-Path $directory 'Zapqio.Platform.Tests/Integration'
    New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureDirectory 'GitAccessTokensApiTests.cs'), ('Token = "' + $fake + '"'), [Text.UTF8Encoding]::new($false))
    $null = & $scanner dir $directory --config $config --redact=100 --no-banner --log-level fatal --report-format json --report-path $report 2>&1
    if ($LASTEXITCODE -ne 1) {
        throw 'A test-fixture exception incorrectly allowed a different token in the same file.'
    }

    Remove-Item -LiteralPath (Join-Path $fixtureDirectory 'GitAccessTokensApiTests.cs')
    $exampleDirectory = Join-Path $directory 'Zapqio.Platform.Web/Documentation/Controllers'
    New-Item -ItemType Directory -Path $exampleDirectory -Force | Out-Null
    foreach ($exampleValue in @(('replace-' + 'with-your-token'), $fake)) {
        [IO.File]::WriteAllText((Join-Path $exampleDirectory 'GitAccessTokens.json'), ('{"token":"' + $exampleValue + '"}'), [Text.UTF8Encoding]::new($false))
        $null = & $scanner dir $directory --config $config --redact=100 --no-banner --log-level fatal --report-format json --report-path $report 2>&1
        $expectedCode = if ($exampleValue -eq $fake) { 1 } else { 0 }
        if ($LASTEXITCODE -ne $expectedCode) {
            throw 'The API example exception must allow only the documented placeholder.'
        }
    }

    Write-Output "$($cases.Count + 3) secret detection and redaction checks passed."
}
finally {
    $resolved = [IO.Path]::GetFullPath($directory)
    $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar + 'zapq-secret-rules-'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a directory outside the test workspace.'
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# Expected rejections leave LASTEXITCODE set to 1. Report success only after cleanup.
exit 0
