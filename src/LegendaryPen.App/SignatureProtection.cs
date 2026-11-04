using System.Security.Cryptography;
using System.Text;

namespace LegendaryPen.App;

/// <summary>Chiffre la signature mémorisée pour la session Windows : un fichier copié ailleurs reste illisible.</summary>
internal static class SignatureProtection
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("LegendaryPen.Signature.v1");

    public static byte[] Protect(byte[] content) => ProtectedData.Protect(content, Purpose, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] content) => ProtectedData.Unprotect(content, Purpose, DataProtectionScope.CurrentUser);
}
