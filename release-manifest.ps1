# Fabrique latest.json. Chargé par build-release.ps1 ; les tests le font tourner avec une clé jetable.
#
# Deux signatures cohabitent :
# - "sig" ne couvre que la version et l'empreinte : c'est celle que lisent les versions 0.10.2 et antérieures.
#   La retirer les empêcherait de se mettre à jour.
# - "signed" contient tout le manifeste en base64 et "sig2" le signe : les versions suivantes ne lisent que ce contenu,
#   donc ni l'adresse ni les notes ne peuvent être modifiées sans la clé.
function New-ReleaseManifest {
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.RSACryptoServiceProvider]$Rsa,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$Date,
        [Parameter(Mandatory)][string[]]$Notes
    )

    $utf8 = New-Object Text.UTF8Encoding $false
    $legacySignature = [Convert]::ToBase64String($Rsa.SignData($utf8.GetBytes("LegendaryPen|$Version|$Sha256"), 'SHA256'))

    $payload = $utf8.GetBytes((ConvertTo-Json -Compress -Depth 3 -InputObject ([ordered]@{
        version = $Version
        url     = $Url
        sha256  = $Sha256
        date    = $Date
        notes   = @($Notes)
    })))

    # Le préfixe empêche de présenter une signature faite pour un autre usage de la clé.
    $signedBytes = [byte[]]($utf8.GetBytes("LegendaryPen-manifest-v2`n") + $payload)
    $signature = [Convert]::ToBase64String($Rsa.SignData($signedBytes, 'SHA256'))

    return ConvertTo-Json -Depth 3 -InputObject ([ordered]@{
        version = $Version
        url     = $Url
        sha256  = $Sha256
        sig     = $legacySignature
        date    = $Date
        notes   = @($Notes)
        signed  = [Convert]::ToBase64String($payload)
        sig2    = $signature
    })
}
