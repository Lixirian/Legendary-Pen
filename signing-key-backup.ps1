# ============================================================================
#  Sauvegarde portable de la clé de signature, utilisée par build-release.ps1.
#
#  La clé de travail est chiffrée pour une session Windows (DPAPI). La sauvegarde est
#  chiffrée par un mot de passe : elle reste lisible après une réinstallation ou sur un autre PC.
#
#  Format : 'LPKEY1' | sel (16) | IV (16) | clé chiffrée (AES-256-CBC) | HMAC-SHA256 (32) du tout.
#  Les clés AES et HMAC sont dérivées du mot de passe (PBKDF2-SHA256, 600 000 itérations).
# ============================================================================
Add-Type -AssemblyName System.Security

$script:BackupMagic = [Text.Encoding]::ASCII.GetBytes('LPKEY1')
$script:MinPasswordLength = 12

function Read-BackupPassword {
    param([switch]$Confirm)

    $first = Read-SecretText 'Mot de passe de la sauvegarde'

    if ($Confirm -and (Read-SecretText 'Confirmez le mot de passe') -cne $first) {
        throw 'Les deux mots de passe diffèrent.'
    }

    return $first
}

function Read-SecretText {
    param([string]$Prompt)

    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)

    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Get-BackupKeys {
    param([string]$Password, [byte[]]$Salt)

    $derive = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $Password, $Salt, 600000, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

    try {
        return @{
            Aes = [byte[]]$derive.GetBytes(32)
            Mac = [byte[]]$derive.GetBytes(32)
        }
    }
    finally { $derive.Dispose() }
}

function Get-Mac {
    param([byte[]]$Key, [byte[]]$Data)

    $hmac = New-Object System.Security.Cryptography.HMACSHA256 (, $Key)

    try { return , [byte[]]$hmac.ComputeHash($Data) }
    finally { $hmac.Dispose() }
}

function Protect-SigningKey {
    param([byte[]]$Plain, [string]$Password)

    if ($Password.Length -lt $script:MinPasswordLength) {
        throw "Mot de passe trop court ($($script:MinPasswordLength) caractères minimum)."
    }

    $salt = New-Object byte[] 16
    $iv = New-Object byte[] 16
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $random.GetBytes($salt)
    $random.GetBytes($iv)
    $random.Dispose()

    $keys = Get-BackupKeys $Password $salt
    $aes = [System.Security.Cryptography.Aes]::Create()

    try {
        $aes.Key = $keys.Aes
        $aes.IV = $iv
        $cipher = $aes.CreateEncryptor().TransformFinalBlock($Plain, 0, $Plain.Length)
    }
    finally { $aes.Dispose() }

    $stream = New-Object IO.MemoryStream
    foreach ($part in @($script:BackupMagic, $salt, $iv, $cipher)) { $stream.Write($part, 0, $part.Length) }
    $body = $stream.ToArray()
    $mac = Get-Mac $keys.Mac $body
    $stream.Write($mac, 0, $mac.Length)

    return , [byte[]]$stream.ToArray()
}

function Unprotect-SigningKey {
    param([byte[]]$Backup, [string]$Password)

    $headerLength = $script:BackupMagic.Length + 32
    $macLength = 32

    if ($Backup.Length -le $headerLength + $macLength -or
        [Text.Encoding]::ASCII.GetString($Backup, 0, $script:BackupMagic.Length) -cne 'LPKEY1') {
        throw 'Fichier de sauvegarde invalide.'
    }

    $salt = New-Object byte[] 16
    $iv = New-Object byte[] 16
    $bodyLength = $Backup.Length - $macLength
    $body = New-Object byte[] $bodyLength
    $mac = New-Object byte[] $macLength
    [Array]::Copy($Backup, $script:BackupMagic.Length, $salt, 0, 16)
    [Array]::Copy($Backup, $script:BackupMagic.Length + 16, $iv, 0, 16)
    [Array]::Copy($Backup, 0, $body, 0, $bodyLength)
    [Array]::Copy($Backup, $bodyLength, $mac, 0, $macLength)

    $keys = Get-BackupKeys $Password $salt
    $expected = Get-Mac $keys.Mac $body
    $difference = 0
    for ($i = 0; $i -lt $macLength; $i++) { $difference = $difference -bor ($expected[$i] -bxor $mac[$i]) }

    if ($difference -ne 0) { throw 'Mot de passe incorrect ou sauvegarde altérée.' }

    $aes = [System.Security.Cryptography.Aes]::Create()

    try {
        $aes.Key = $keys.Aes
        $aes.IV = $iv
        $cipherLength = $bodyLength - $headerLength
        return , [byte[]]$aes.CreateDecryptor().TransformFinalBlock($Backup, $headerLength, $cipherLength)
    }
    finally { $aes.Dispose() }
}

function Assert-MatchesPublicKey {
    param([byte[]]$PrivateKey, [string]$ExpectedPublicKey)

    $parameters = New-Object System.Security.Cryptography.CspParameters 24
    $parameters.Flags = [System.Security.Cryptography.CspProviderFlags]::CreateEphemeralKey
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $parameters)

    try {
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($PrivateKey))

        if ($rsa.ToXmlString($false) -cne $ExpectedPublicKey) {
            throw "Cette clé ne correspond pas à la clé publique de l'application."
        }
    }
    finally { $rsa.Dispose() }
}

function Export-SigningKey {
    param([string]$KeyFile, [string]$Destination, [string]$ExpectedPublicKey, [string]$Password)

    if (-not (Test-Path -LiteralPath $KeyFile)) { throw "Clé de signature introuvable : $KeyFile" }
    if (Test-Path -LiteralPath $Destination) { throw "Une sauvegarde existe déjà : $Destination" }

    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($KeyFile), $null, 'CurrentUser')
    Assert-MatchesPublicKey $plain $ExpectedPublicKey

    $backup = Protect-SigningKey $plain $Password

    # La sauvegarde n'existe sous son nom final qu'une fois relue et vérifiée.
    $temporary = "$Destination.$([Guid]::NewGuid().ToString('N')).tmp"

    try {
        [IO.File]::WriteAllBytes($temporary, $backup)
        Assert-MatchesPublicKey (Unprotect-SigningKey ([IO.File]::ReadAllBytes($temporary)) $Password) $ExpectedPublicKey
        [IO.File]::Move($temporary, $Destination)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}

function Import-SigningKey {
    param([string]$Backup, [string]$KeyFile, [string]$ExpectedPublicKey, [string]$Password)

    if (-not (Test-Path -LiteralPath $Backup)) { throw "Sauvegarde introuvable : $Backup" }

    $plain = Unprotect-SigningKey ([IO.File]::ReadAllBytes($Backup)) $Password
    Assert-MatchesPublicKey $plain $ExpectedPublicKey

    if (Test-Path -LiteralPath $KeyFile) {
        $readable = $false

        try {
            [void][Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile), $null, 'CurrentUser')
            $readable = $true
        }
        catch [Security.Cryptography.CryptographicException] { }

        if ($readable) { throw "Une clé utilisable est déjà présente : $KeyFile" }

        # Clé chiffrée pour une ancienne session Windows : conservée sous un autre nom.
        [IO.File]::Move($KeyFile, "$KeyFile.ancienne-$((Get-Date).ToString('yyyyMMdd-HHmmss'))")
    }

    $directory = Split-Path $KeyFile
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

    [IO.File]::WriteAllBytes($KeyFile, [Security.Cryptography.ProtectedData]::Protect($plain, $null, 'CurrentUser'))
}
