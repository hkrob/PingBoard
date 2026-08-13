using System.Security.Cryptography;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// Encrypts a secret for storage in the config file, using DPAPI under the current user account.
/// <para>
/// The config is a plain <c>.ini</c> that the user is invited to hand-edit and copy between
/// machines, which is exactly the wrong place for an SMTP password. DPAPI ties the ciphertext to
/// the Windows user profile, so a config file that ends up in a sync folder, a backup or a repo
/// carries no usable credential off this machine.
/// </para>
/// <para>
/// The limitation is the point, not an oversight: copying the config to another machine (or
/// another user account) leaves the secret undecryptable, and it must be re-entered there.
/// That is strictly better than a password that travels wherever the file does. Values are stored
/// with a <c>dpapi:</c> prefix so a plaintext value typed in by hand is still accepted and simply
/// re-protected on the next save.
/// </para>
/// </summary>
public static class ProtectedValue
{
    private const string Prefix = "dpapi:";

    /// <summary>Extra entropy, so a blob lifted from this config cannot be decrypted by another app.</summary>
    private static readonly byte[] Entropy = "PingBoard.AlertCredential.v1"u8.ToArray();

    public static bool IsProtected(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a plaintext secret. Returns the value unchanged if it is already protected, and
    /// returns empty for empty input so an unset password stays visibly unset in the file.
    /// </summary>
    public static string Protect(string plaintext)
    {
        if (plaintext.Length == 0 || IsProtected(plaintext)) return plaintext;

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (CryptographicException)
        {
            // Refuse to silently write the plaintext instead. Losing the credential is recoverable
            // by retyping it; leaking it into a synced file is not.
            return "";
        }
    }

    /// <summary>
    /// Decrypts a stored secret. A value without the prefix is treated as plaintext the user typed
    /// straight into the file, so hand-editing still works. Returns empty when the blob cannot be
    /// decrypted — which is what a config copied from another machine or user looks like.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        if (!IsProtected(stored)) return stored;

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
