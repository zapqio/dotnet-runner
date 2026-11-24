[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$temporaryRoot = if ($windows) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp' } else { [IO.Path]::GetTempPath() }
$temporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
$fixture = Join-Path $temporaryRoot ('zapq-git-security-' + [Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
$binaryName = if ($windows) { 'gitleaks.exe' } else { 'gitleaks' }
$shell = (Get-Process -Id $PID).Path
$fake = 'fixture-' + [Guid]::NewGuid().ToString('N')

function Invoke-Child([string]$Executable, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $Executable @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }

    $text = $output -join "`n"
    if ($text.Contains($fake)) {
        throw 'A security check disclosed the synthetic credential.'
    }

    return [pscustomobject]@{ Code = $code; Output = $text }
}

function Invoke-FixtureGit([string[]]$Arguments) {
    return Invoke-Child 'git' (@('-C', $fixture, '-c', 'commit.gpgsign=false') + $Arguments)
}

function Assert-Git([string[]]$Arguments) {
    $result = Invoke-FixtureGit $Arguments
    if ($result.Code -ne 0) {
        throw "Fixture Git operation failed: $($Arguments[0])."
    }

    return $result.Output
}

function Expect-Rejection([string]$Message, [string]$ExpectedText) {
    $result = Invoke-FixtureGit @('commit', '-m', $Message)
    if ($result.Code -eq 0 -or -not $result.Output.Contains($ExpectedText)) {
        throw "Expected commit rejection was not observed: $Message."
    }
}

New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    foreach ($name in @('.gitleaks.toml', '.githooks/pre-commit', 'scripts/security/Scan-Secrets.ps1', ".local/security/tools/gitleaks/8.30.1/$binaryName")) {
        $destination = Join-Path $fixture $name
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $destination
    }

    if (-not $windows) {
        & chmod +x (Join-Path $fixture '.githooks/pre-commit') (Join-Path $fixture ".local/security/tools/gitleaks/8.30.1/$binaryName")
        if ($LASTEXITCODE -ne 0) {
            throw 'Cannot make fixture tools executable.'
        }
    }

    [IO.File]::WriteAllText((Join-Path $fixture '.gitignore'), ".local/`n", $utf8)
    $null = Assert-Git @('init', '-b', 'main')
    $null = Assert-Git @('config', 'user.name', 'Security fixture')
    $null = Assert-Git @('config', 'user.email', 'fixture@example.invalid')
    $null = Assert-Git @('config', 'core.hooksPath', '.githooks')
    $null = Assert-Git @('add', '.')
    $null = Assert-Git @('commit', '-m', 'Clean fixture')

    $file = Join-Path $fixture 'file with spaces.txt'
    [IO.File]::WriteAllText($file, ('PGPASSWORD=' + $fake), $utf8)
    $null = Assert-Git @('add', 'file with spaces.txt')
    [IO.File]::WriteAllText($file, 'unstaged clean text', $utf8)
    Expect-Rejection 'Reject staged secret despite clean working file' 'zapq-environment-password'

    $null = Assert-Git @('add', 'file with spaces.txt')
    [IO.File]::WriteAllText($file, ('PGPASSWORD=' + $fake), $utf8)
    $null = Assert-Git @('commit', '-m', 'Accept safe index despite dirty working file')
    [IO.File]::WriteAllText($file, 'unstaged clean text', $utf8)

    [IO.File]::WriteAllText((Join-Path $fixture '.local/private.txt'), 'no secret needed', $utf8)
    $null = Assert-Git @('add', '-f', '.local/private.txt')
    Expect-Rejection 'Reject forced local file' 'Local-only file'
    $null = Assert-Git @('rm', '--cached', '.local/private.txt')

    [IO.File]::WriteAllText($file, 'another safe change', $utf8)
    $null = Assert-Git @('add', 'file with spaces.txt')
    $binary = Join-Path $fixture ".local/security/tools/gitleaks/8.30.1/$binaryName"
    Move-Item -LiteralPath $binary -Destination "$binary.saved"
    try {
        Expect-Rejection 'Reject missing scanner' 'Gitleaks is missing'
    }
    finally {
        Move-Item -LiteralPath "$binary.saved" -Destination $binary
    }

    $null = Assert-Git @('commit', '-m', 'Clean baseline')
    $base = Assert-Git @('rev-parse', 'HEAD')
    [IO.File]::WriteAllText($file, ('PGPASSWORD=' + $fake), $utf8)
    $null = Assert-Git @('add', 'file with spaces.txt')
    # Only this disposable synthetic repository bypasses the hook, to prove CI catches it.
    $null = Assert-Git @('-c', 'core.hooksPath=disabled-for-fixture', 'commit', '-m', 'Synthetic leaked credential')
    [IO.File]::WriteAllText($file, 'secret removed from final files', $utf8)
    $null = Assert-Git @('add', 'file with spaces.txt')
    $null = Assert-Git @('commit', '-m', 'Remove synthetic credential')
    $scanScript = Join-Path $fixture 'scripts/security/Scan-Secrets.ps1'
    $scan = Invoke-Child $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scanScript, '-Mode', 'WorkingTree')
    if ($scan.Code -ne 0) {
        throw 'The clean final tree failed its scan.'
    }

    $scan = Invoke-Child $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scanScript, '-Mode', 'CI', '-BaseCommit', $base)
    if ($scan.Code -eq 0 -or -not $scan.Output.Contains('zapq-environment-password')) {
        throw 'CI missed a secret added and removed in intermediate commits.'
    }

    [IO.File]::WriteAllText($file, ('PGPASSWORD=' + $fake), $utf8)
    $null = Assert-Git @('add', 'file with spaces.txt')
    [IO.File]::WriteAllText((Join-Path $fixture '.gitleaks.toml'), "[[rules]]`nid = 'empty-fixture'`nregex = 'cannot-match-any-fixture'`n", $utf8)
    Expect-Rejection 'Use staged rules despite an unstaged configuration change' 'zapq-environment-password'
    Write-Output '8 hook and CI integration checks passed in an isolated temporary repository.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $prefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar + 'zapq-git-security-'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a directory outside the fixture workspace.'
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# Expected rejections leave LASTEXITCODE set to 1. Report success only after cleanup.
exit 0
