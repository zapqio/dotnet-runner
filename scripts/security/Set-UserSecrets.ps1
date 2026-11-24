[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string[]]$Names,
    [string]$InputFile
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
[xml]$projectXml = Get-Content -Encoding UTF8 -LiteralPath $projectPath -Raw
$secretId = $projectXml.SelectSingleNode('//UserSecretsId').InnerText
if (-not $secretId) {
    throw 'The project must declare UserSecretsId.'
}

$inputValues = if ($InputFile) { Get-Content -Encoding UTF8 -LiteralPath $InputFile -Raw | ConvertFrom-Json } else { $null }
$values = @{}
foreach ($name in $Names) {
    $value = if ($inputValues) { $inputValues.PSObject.Properties[$name].Value } else { [Environment]::GetEnvironmentVariable($name) }
    if ($inputValues -and [string]::IsNullOrWhiteSpace($value)) {
        throw "The import file must contain a nonempty value for $name."
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $secureValue = Read-Host "Local/test value for $name" -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
        try {
            $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
            $secureValue.Dispose()
        }
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "A nonempty local/test value is required for $name."
    }

    $values[$name] = $value
}

# Standard input keeps values out of process arguments and PowerShell history.
$oldEncoding = $OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $result = ($values | ConvertTo-Json -Compress) | & dotnet user-secrets set --id $secretId 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not save local user secrets. No secret values were printed.'
    }
}
finally {
    $OutputEncoding = $oldEncoding
}

Write-Output "Saved $($values.Count) local development settings outside the repository."
