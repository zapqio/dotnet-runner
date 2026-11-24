[CmdletBinding()]
param([switch]$InstallHooks)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$version = '8.30.1'
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (-not [Environment]::Is64BitProcess) {
    throw 'Run this installer in a 64-bit PowerShell process.'
}

if ($windows) {
    $archiveName = "gitleaks_${version}_windows_x64.zip"
    $archiveHash = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'
    $executableName = 'gitleaks.exe'
}
elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux) -and
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'X64') {
    $archiveName = "gitleaks_${version}_linux_x64.tar.gz"
    $archiveHash = '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb'
    $executableName = 'gitleaks'
}
else {
    throw 'This installer supports Windows x64 and Linux x64.'
}

$toolDirectory = Join-Path $repository ".local/security/tools/gitleaks/$version"
$archive = Join-Path $toolDirectory $archiveName
$executable = Join-Path $toolDirectory $executableName
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $download = "$archive.download"
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/gitleaks/gitleaks/releases/download/v$version/$archiveName" -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $archiveHash) {
        throw 'Downloaded Gitleaks archive failed SHA-256 verification.'
    }

    Move-Item -LiteralPath $download -Destination $archive
}

if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $archiveHash) {
    throw 'Cached Gitleaks archive failed SHA-256 verification.'
}

# Restore the binary from the verified archive, including after a damaged installation.
if ($windows) {
    Expand-Archive -LiteralPath $archive -DestinationPath $toolDirectory -Force
}
else {
    & tar -xzf $archive -C $toolDirectory $executableName
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not extract Gitleaks.'
    }
}

$installedVersion = & $executable version
if ($LASTEXITCODE -ne 0 -or $installedVersion.Trim() -ne $version) {
    throw 'Installed Gitleaks version does not match the pinned release.'
}

if ($InstallHooks) {
    $existing = & git -C $repository config --get core.hooksPath
    if ($existing -and $existing -ne '.githooks') {
        throw 'An existing core.hooksPath is configured. Integrate the secret scan with that hook before replacing it.'
    }

    $defaultHook = & git -C $repository rev-parse --git-path hooks/pre-commit
    if (-not $existing -and (Test-Path -LiteralPath (Join-Path $repository $defaultHook))) {
        throw 'An existing pre-commit hook is installed. Integrate it before enabling .githooks.'
    }

    & git -C $repository config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not install the repository hooks.'
    }
}

Write-Output "Gitleaks $version installed and archive verified."
