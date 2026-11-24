[CmdletBinding()]
param(
    [ValidateSet('Staged', 'WorkingTree', 'History', 'CI')]
    [string]$Mode = 'Staged',
    [string]$BaseCommit,
    [string]$DefaultBranch
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$binaryName = if ($windows) { 'gitleaks.exe' } else { 'gitleaks' }
$scanner = Join-Path $repository ".local/security/tools/gitleaks/8.30.1/$binaryName"
if (-not (Test-Path -LiteralPath $scanner)) {
    throw 'Gitleaks is missing. Run scripts/security/Install-Gitleaks.ps1. The scan cannot be skipped.'
}

$version = & $scanner version
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne '8.30.1') {
    throw 'Unexpected Gitleaks version. Run scripts/security/Install-Gitleaks.ps1.'
}

$temporaryRoot = if ($windows) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp' } else { [IO.Path]::GetTempPath() }
$temporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
$workspace = Join-Path $temporaryRoot ('zapq-secret-scan-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
$report = Join-Path $workspace 'findings.json'
$config = Join-Path $repository '.gitleaks.toml'
$failed = $false

function Get-GitOutput([string[]]$GitArguments) {
    $output = & git -C $repository -c core.quotepath=false @GitArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Git could not determine the files or commits to scan.'
    }

    return ($output -join "`n")
}

function Assert-AllowedPaths([string[]]$Paths) {
    foreach ($path in $Paths) {
        if ($path -match '(^|/)(\.local|ai_analysis)/|(^|/)\.claude/settings\.local\.json$|(^|/)\.codex/(auth\.json|history\.jsonl|sessions/|archived_sessions/)|(^|/)\.env($|\.)' -and
            $path -notmatch '(^|/)\.env\.(example|sample|template)$') {
            throw "Local-only file must not be committed: $path"
        }
    }
}

function Invoke-Scan([string[]]$ScanArguments) {
    # Never emit scanner output or source snippets, including on failure.
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $scanner @ScanArguments --config $config --redact=100 --no-banner --log-level fatal --ignore-gitleaks-allow --gitleaks-ignore-path (Join-Path $workspace 'no-exceptions') --report-format json --report-path $report 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($code -notin @(0, 1)) {
        throw "Gitleaks failed (exit $code). No scan result was accepted."
    }

    if (-not (Test-Path -LiteralPath $report)) {
        throw 'Gitleaks did not produce its report. No scan result was accepted.'
    }

    $findings = Get-Content -Encoding UTF8 -Raw -LiteralPath $report | ConvertFrom-Json
    foreach ($finding in @($findings)) {
        if ($null -eq $finding) {
            continue
        }

        $displayPath = $finding.File.Replace('\', '/').Replace($workspace.Replace('\', '/') + '/files/', '')
        Write-Output ("{0}:{1}: {2}" -f $displayPath, $finding.StartLine, $finding.RuleID)
        $script:failed = $true
    }

    if ($code -eq 1) {
        $script:failed = $true
    }
}

function Scan-WorkingTree {
    $snapshot = Join-Path $workspace 'files'
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    $paths = (Get-GitOutput @('ls-files', '-z', '--cached', '--others', '--exclude-standard')).Split([char]0) | Where-Object { $_ }
    Assert-AllowedPaths $paths
    foreach ($path in $paths) {
        $source = Join-Path $repository $path
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            continue
        }

        if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to follow a symbolic link while scanning: $path"
        }

        $destination = Join-Path $snapshot $path
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }

    Invoke-Scan @('dir', $snapshot)
}

try {
    if ($Mode -eq 'Staged') {
        # The configuration committed with the files must govern a partially staged commit.
        $stagedConfig = Get-GitOutput @('show', ':.gitleaks.toml')
        $config = Join-Path $workspace 'staged-gitleaks.toml'
        [IO.File]::WriteAllText($config, $stagedConfig, [Text.UTF8Encoding]::new($false))
        $paths = (Get-GitOutput @('diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z')).Split([char]0) | Where-Object { $_ }
        Assert-AllowedPaths $paths
        Invoke-Scan @('git', $repository, '--pre-commit', '--staged')
    }
    elseif ($Mode -eq 'WorkingTree') {
        Scan-WorkingTree
    }
    elseif ($Mode -eq 'History') {
        Invoke-Scan @('git', $repository, '--log-opts=--all --full-history -m')
    }
    else {
        if ((Get-GitOutput @('rev-parse', '--is-shallow-repository')) -eq 'true') {
            throw 'CI secret scanning requires a complete checkout (fetch-depth/GIT_DEPTH: 0).'
        }

        Scan-WorkingTree
        if ($BaseCommit -and $BaseCommit -notmatch '^0+$') {
            if ($BaseCommit -notmatch '^[a-fA-F0-9]{40,64}$') {
                throw 'Invalid CI base commit.'
            }

            $base = Get-GitOutput @('rev-parse', '--verify', "$BaseCommit^{commit}")
        }
        elseif ($DefaultBranch) {
            $base = Get-GitOutput @('merge-base', 'HEAD', "refs/remotes/origin/$DefaultBranch")
            if ($base -eq (Get-GitOutput @('rev-parse', 'HEAD'))) {
                # A new default branch or manual build has no trustworthy push boundary.
                $base = $null
            }
        }
        else {
            $base = $null
        }

        $logOptions = if ($base) { "$base..HEAD --full-history -m" } else { 'HEAD --full-history -m' }
        Invoke-Scan @('git', $repository, "--log-opts=$logOptions")
    }

    if ($failed) {
        throw 'Secret scan found prohibited values. Remove them and rerun; values are never printed.'
    }

    Write-Output "Secret scan passed ($Mode)."
}
finally {
    $resolvedWorkspace = [IO.Path]::GetFullPath($workspace)
    $safePrefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar + 'zapq-secret-scan-'
    if (-not $resolvedWorkspace.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a scan directory outside the temporary directory.'
    }

    Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
}
