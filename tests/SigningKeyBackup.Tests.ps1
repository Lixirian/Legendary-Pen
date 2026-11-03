# Tests de signing-key-backup.ps1, avec une clé jetable dans un dossier temporaire.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\signing-key-backup.ps1')

$script:Failures = 0
$password = 'mot-de-passe-de-test-jetable'

function Assert-True($Name, $Condition) {
    if ($Condition) { Write-Output "OK     $Name" }
    else { Write-Output "ECHEC  $Name"; $script:Failures++ }
}

function Assert-Throws($Name, [scriptblock]$Action, $Fragment) {
    try {
        & $Action
        Write-Output "ECHEC  $Name (aucune erreur)"
        $script:Failures++
    }
    catch {
        if ($_.Exception.Message -like "*$Fragment*") { Write-Output "OK     $Name" }
        else { Write-Output "ECHEC  $Name : $($_.Exception.Message)"; $script:Failures++ }
    }
}

function New-TestRsa {
    $parameters = New-Object System.Security.Cryptography.CspParameters 24
    $parameters.Flags = [System.Security.Cryptography.CspProviderFlags]::CreateEphemeralKey
    return New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $parameters)
}

function Write-DpapiKey($Path, [byte[]]$Plain) {
    [IO.File]::WriteAllBytes($Path, [Security.Cryptography.ProtectedData]::Protect($Plain, $null, 'CurrentUser'))
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("lp-key-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null

try {
    $rsa = New-TestRsa
    $other = New-TestRsa
    $public = $rsa.ToXmlString($false)
    $private = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    $keyFile = Join-Path $dir 'cle.dpapi'
    $backup = Join-Path $dir 'cle.backup'
    Write-DpapiKey $keyFile $private

    Export-SigningKey $keyFile $backup $public $password
    $bytes = [IO.File]::ReadAllBytes($backup)
    Assert-True 'la sauvegarde est créée, sans fichier temporaire' ((Test-Path $backup) -and @(Get-ChildItem $dir -Filter '*.tmp').Count -eq 0)
    Assert-True 'la clé privée n apparaît pas en clair' (-not ([Text.Encoding]::UTF8.GetString($bytes) -match 'RSAKeyValue|<P>'))

    $round = Unprotect-SigningKey $bytes $password
    Assert-True 'aller-retour identique' ([Convert]::ToBase64String($round) -ceq [Convert]::ToBase64String($private))

    $second = Protect-SigningKey $private $password
    Assert-True 'sel et IV différents à chaque sauvegarde' ([Convert]::ToBase64String($second) -cne [Convert]::ToBase64String($bytes))

    Assert-Throws 'mauvais mot de passe refusé' { Unprotect-SigningKey $bytes 'un-autre-mot-de-passe' } 'incorrect'

    foreach ($position in @(0, 10, 30, 60, ($bytes.Length - 1))) {
        $tampered = [byte[]]$bytes.Clone()
        $tampered[$position] = $tampered[$position] -bxor 1
        Assert-Throws "octet $position altéré refusé" { Unprotect-SigningKey $tampered $password } ''
    }

    Assert-Throws 'fichier trop court refusé' { Unprotect-SigningKey ([byte[]](1, 2, 3)) $password } 'invalide'
    Assert-Throws 'mot de passe court refusé' { Protect-SigningKey $private 'court' } 'trop court'
    Assert-Throws 'sauvegarde existante jamais écrasée' { Export-SigningKey $keyFile $backup $public $password } 'existe déjà'
    Assert-Throws 'export refusé si la clé ne correspond pas' { Export-SigningKey $keyFile (Join-Path $dir 'b2') $other.ToXmlString($false) $password } 'ne correspond pas'
    Assert-Throws 'clé source absente' { Export-SigningKey (Join-Path $dir 'absente') (Join-Path $dir 'b3') $public $password } 'introuvable'

    $restored = Join-Path $dir 'nouveau\cle.dpapi'
    Import-SigningKey $backup $restored $public $password
    $plainRestored = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($restored), $null, 'CurrentUser')
    Assert-True 'restauration sur un nouvel emplacement' ([Convert]::ToBase64String($plainRestored) -ceq [Convert]::ToBase64String($private))

    Assert-Throws 'clé utilisable jamais remplacée' { Import-SigningKey $backup $restored $public $password } 'déjà présente'
    Assert-Throws 'restauration refusée si la clé ne correspond pas' { Import-SigningKey $backup (Join-Path $dir 'c.dpapi') $other.ToXmlString($false) $password } 'ne correspond pas'
    Assert-Throws 'restauration avec mauvais mot de passe' { Import-SigningKey $backup (Join-Path $dir 'd.dpapi') $public 'un-autre-mot-de-passe' } 'incorrect'
    Assert-True 'aucune clé écrite après ces refus' (-not (Test-Path (Join-Path $dir 'c.dpapi')) -and -not (Test-Path (Join-Path $dir 'd.dpapi')))

    # Simule une clé chiffrée pour une ancienne session Windows.
    $stale = Join-Path $dir 'ancienne.dpapi'
    [IO.File]::WriteAllBytes($stale, [byte[]](1, 2, 3, 4))
    Import-SigningKey $backup $stale $public $password
    Assert-True 'ancienne clé illisible conservée à part' (@(Get-ChildItem $dir -Filter 'ancienne.dpapi.ancienne-*').Count -eq 1)
    Assert-True 'nouvelle clé utilisable à la place' ((Test-Path $stale) -and ([IO.File]::ReadAllBytes($stale).Length -gt 4))
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force
}

if ($script:Failures -gt 0) { throw "$($script:Failures) test(s) en échec." }
Write-Output 'Tous les tests de sauvegarde sont passés.'
