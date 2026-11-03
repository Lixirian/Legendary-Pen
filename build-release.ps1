# ============================================================================
#  Publie une version de Legendary Pen sur GitHub.
#
#  Usage courant :
#    .\build-release.ps1 -Notes "Ce qui change", "Autre changement"
#    -> version +0.0.1, tests, installeur, commit, push, release GitHub.
#    Les applications installées affichent ensuite la pastille de mise à jour.
#
#  Options :
#    -Bump minor|major|none   Niveau de version (patch par défaut).
#    -NoPublish               Construit l'installeur sans rien envoyer ni modifier la version.
#    -NewSigningKey           Crée la clé de signature (une seule fois).
#
#  Clé de signature : chemin indiqué dans signing-key.local.txt (hors dépôt), chiffrée pour l'utilisateur
#  Windows courant. Elle ne doit pas aller sur GitHub. Sans elle, les
#  applications installées refusent les nouvelles versions : il faudrait les réinstaller.
#
#  Prérequis : SDK .NET 10, Inno Setup 6, git et gh authentifié.
# ============================================================================
param(
    [string[]]$Notes = @(),
    [ValidateSet('patch', 'minor', 'major', 'none')][string]$Bump = 'patch',
    [switch]$NoPublish,
    [switch]$NewSigningKey
)

$ErrorActionPreference = 'Stop'
$repoName = 'Lixirian/Legendary-Pen'
$root = $PSScriptRoot
# Hors du dossier du projet : une synchronisation pendant la compilation peut corrompre l'installeur.
$dist = Join-Path ([IO.Path]::GetTempPath()) 'LegendaryPen-build'
$publishDir = Join-Path $dist 'publish'
$project = Join-Path $root 'src\LegendaryPen.App\LegendaryPen.App.csproj'
$configFile = Join-Path $root 'src\LegendaryPen.App\UpdateConfig.cs'
$keyLocationFile = Join-Path $root 'signing-key.local.txt'
$keyFile = if (Test-Path -LiteralPath $keyLocationFile) { [IO.File]::ReadAllText($keyLocationFile, [Text.Encoding]::UTF8).Trim() } else { $null }

Add-Type -AssemblyName System.Security

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Échec ($LASTEXITCODE) : $Command $($Arguments -join ' ')" }
}

function New-Rsa {
    $parameters = New-Object System.Security.Cryptography.CspParameters 24
    return New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $parameters)
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

function Find-Tool {
    param([string]$Name, [string[]]$Candidates)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    throw "$Name introuvable."
}

if ($NewSigningKey) {
    if (-not $keyFile) { throw "Indiquez le chemin de la clé dans $keyLocationFile" }

    # Un emplacement inaccessible ressemble à une clé absente : la clé publique inscrite fait foi.
    if ([IO.File]::ReadAllText($configFile) -match 'RSAKeyValue') {
        throw "Une clé publique est déjà inscrite dans $configFile. Une nouvelle clé couperait les mises à jour des applications installées."
    }

    if (Test-Path -LiteralPath $keyFile) {
        throw "Une clé existe déjà ($keyFile). La remplacer couperait les mises à jour des applications installées."
    }

    $rsa = New-Rsa
    New-Item -ItemType Directory -Path (Split-Path $keyFile) -Force | Out-Null
    $private = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    [IO.File]::WriteAllBytes($keyFile, [Security.Cryptography.ProtectedData]::Protect($private, $null, 'CurrentUser'))

    $config = [IO.File]::ReadAllText($configFile)
    $updated = [regex]::Replace($config, 'ReleasePublicKey = "[^"]*"', 'ReleasePublicKey = "' + $rsa.ToXmlString($false) + '"')
    if ($updated -eq $config) { throw "ReleasePublicKey introuvable dans $configFile" }
    Write-TextFile $configFile $updated

    Write-Host "Clé créée : $keyFile" -ForegroundColor Green
    return
}

$iscc = Find-Tool 'ISCC.exe' @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))
$dotnet = Find-Tool 'dotnet.exe' @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
$gh = $null

if (-not $NoPublish) {
    if ($Notes.Count -eq 0) { throw 'Indiquez ce qui change : -Notes "Première note", "Deuxième note"' }
    if (-not $keyFile) { throw "Indiquez le chemin de la clé dans $keyLocationFile" }
    if (-not (Test-Path -LiteralPath $keyFile)) { throw "Clé de signature introuvable : $keyFile. Son emplacement est-il accessible ?" }
    $gh = Find-Tool 'gh.exe' @((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'))
}

# --- Version -----------------------------------------------------------------
$projectText = [IO.File]::ReadAllText($project)
$versionMatch = [regex]::Match($projectText, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $versionMatch.Success) { throw "Version introuvable dans $project" }

$major = [int]$versionMatch.Groups[1].Value
$minor = [int]$versionMatch.Groups[2].Value
$patch = [int]$versionMatch.Groups[3].Value

if (-not $NoPublish) {
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
}

$version = "$major.$minor.$patch"
$tag = "v$version"

if (-not $NoPublish) {
    $existingTags = & $gh release list --repo $repoName --limit 1000 --json tagName --jq '.[].tagName'
    if ($LASTEXITCODE -ne 0) { throw 'Impossible de lister les releases GitHub.' }
    if (@($existingTags) -contains $tag) { throw "La release $tag existe déjà." }
}

# --- Tests et installeur ---------------------------------------------------------
Invoke-Checked $dotnet @('test', (Join-Path $root 'LegendaryPen.sln'), '-c', 'Release', '-v', 'q', '--nologo')

if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }

Invoke-Checked $dotnet @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    # Sans symboles ni chemins locaux : l'installeur public ne doit révéler aucun dossier du poste.
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true', "-p:PathMap=$root=.",
    "-p:Version=$version", '-o', $publishDir, '-v', 'q', '--nologo')

Invoke-Checked $iscc @('/Q', "/DAppVersion=$version", "/DPublishDir=$publishDir", "/O$dist", (Join-Path $root 'installer\LegendaryPen.iss'))

$installerName = "LegendaryPen-Setup-$version.exe"
$installer = Join-Path $dist $installerName
$sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Installeur : $installer ($sha256)" -ForegroundColor Green

if ($NoPublish) { return }

# --- Signature et manifeste -----------------------------------------------------
$rsa = New-Rsa
$rsa.FromXmlString([Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $null, 'CurrentUser')))

$publicKey = [regex]::Match([IO.File]::ReadAllText($configFile), 'ReleasePublicKey = "([^"]*)"').Groups[1].Value
if ($publicKey -ne $rsa.ToXmlString($false)) { throw 'La clé de signature ne correspond pas à la clé publique de UpdateConfig.cs.' }

$signed = [Text.Encoding]::UTF8.GetBytes("LegendaryPen|$version|$sha256")
$signature = [Convert]::ToBase64String($rsa.SignData($signed, 'SHA256'))

$manifest = Join-Path $dist 'latest.json'
Write-TextFile $manifest (ConvertTo-Json -InputObject ([ordered]@{
    version = $version
    url     = "https://github.com/$repoName/releases/download/$tag/$installerName"
    sha256  = $sha256
    sig     = $signature
    date    = (Get-Date).ToString('yyyy-MM-dd')
    notes   = @($Notes)
}) -Depth 3)

# --- Publication ----------------------------------------------------------------
if ($Bump -ne 'none') {
    Write-TextFile $project ($projectText -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$version</Version>")
}

Push-Location $root
try {
    Invoke-Checked 'git' @('add', '-A')
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { Invoke-Checked 'git' @('commit', '-m', "Version $version") }
    $global:LASTEXITCODE = 0
    Invoke-Checked 'git' @('push', 'origin', 'HEAD')
}
finally {
    Pop-Location
}

$releaseNotes = ($Notes | ForEach-Object { "- $_" }) -join "`n"
Invoke-Checked $gh @(
    'release', 'create', $tag, $installer, $manifest,
    '--repo', $repoName, '--title', "Legendary Pen $version", '--notes', $releaseNotes, '--latest')

Write-Host "Version $version publiée : https://github.com/$repoName/releases/tag/$tag" -ForegroundColor Green
