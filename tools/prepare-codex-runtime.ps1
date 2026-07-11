[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$OutputEncoding = [System.Text.UTF8Encoding]::new()

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repositoryRoot 'Clever\Resources\Codex\runtime-manifest.json'
$noticePath = Join-Path $repositoryRoot 'Clever\Resources\Codex\THIRD_PARTY_NOTICES.md'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'Clever\bin\CodexRelease\Resources\Codex\runtime'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.downloadUrl -notmatch '^https://github\.com/openai/codex/releases/download/') {
    throw 'The runtime manifest does not point at the official OpenAI Codex GitHub release.'
}
if ($manifest.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'The runtime manifest does not contain a valid SHA-256 digest.'
}

if (-not $Force -and (Test-Path -LiteralPath (Join-Path $OutputDirectory 'codex.exe'))) {
    $existingManifestPath = Join-Path $OutputDirectory 'runtime-manifest.json'
    if (Test-Path -LiteralPath $existingManifestPath) {
        $existing = Get-Content -LiteralPath $existingManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $existingVersion = (& (Join-Path $OutputDirectory 'codex.exe') --version 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and $existing.version -eq $manifest.version -and $existingVersion -match [Regex]::Escape($manifest.version)) {
            Write-Host "Codex $($manifest.version) is already prepared at $OutputDirectory"
            return
        }
    }
}

$parent = Split-Path -Parent $OutputDirectory
[System.IO.Directory]::CreateDirectory($parent) | Out-Null
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('clev3r-codex-runtime-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $workRoot $manifest.assetName
$staging = Join-Path $parent ('.runtime-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('.runtime-backup-' + [Guid]::NewGuid().ToString('N'))
$installed = $false

try {
    [System.IO.Directory]::CreateDirectory($workRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($staging) | Out-Null

    Write-Host "Downloading OpenAI Codex $($manifest.version)..."
    Invoke-WebRequest -Uri $manifest.downloadUrl -OutFile $archivePath -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $manifest.sha256.ToLowerInvariant()) {
        throw "Codex archive SHA-256 mismatch. Expected $($manifest.sha256), received $actual."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $staging
    $codex = Get-ChildItem -LiteralPath $staging -Recurse -File |
        Where-Object { $_.Name -ieq 'codex.exe' -or $_.Name -ieq "codex-$($manifest.target).exe" } |
        Select-Object -First 1
    if ($null -eq $codex) {
        throw 'The verified archive does not contain the expected Codex executable.'
    }
    $canonical = Join-Path $staging 'codex.exe'
    if ($codex.FullName -ine $canonical) {
        Move-Item -LiteralPath $codex.FullName -Destination $canonical
    }

    $reported = (& $canonical --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $reported -notmatch [Regex]::Escape($manifest.version)) {
        throw "The extracted runtime failed version verification: $reported"
    }

    Invoke-WebRequest -Uri $manifest.licenseUrl -OutFile (Join-Path $staging 'LICENSE-CODEX.txt') -UseBasicParsing
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $staging 'runtime-manifest.json')
    Copy-Item -LiteralPath $noticePath -Destination (Join-Path $staging 'THIRD_PARTY_NOTICES.md')

    if (Test-Path -LiteralPath $OutputDirectory) {
        Move-Item -LiteralPath $OutputDirectory -Destination $backup
    }

    try {
        Move-Item -LiteralPath $staging -Destination $OutputDirectory
        $installed = $true
    }
    catch {
        if (Test-Path -LiteralPath $backup) {
            Move-Item -LiteralPath $backup -Destination $OutputDirectory
        }
        throw
    }

    if (Test-Path -LiteralPath $backup) {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
    Write-Host "Prepared verified Codex $($manifest.version) runtime at $OutputDirectory"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
    if (-not $installed -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
