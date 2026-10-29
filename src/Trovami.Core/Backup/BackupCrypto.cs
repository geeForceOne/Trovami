using System.Security.Cryptography;
using System.Text;

namespace Trovami.Core.Backup;

// Password protection for downloaded backups: AES-256-GCM with a key derived from the password
// (PBKDF2-SHA256). File layout: magic, salt, nonce, tag, ciphertext. GCM's tag makes a wrong
// password or a damaged file fail cleanly instead of producing garbage.
public static class BackupCrypto
{
    private static readonly byte[] Magic = "TROVAMI-AES1"u8.ToArray();
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;
    private const int Iterations = 600_000;

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length > Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[Magic.Length + SaltSize + NonceSize + TagSize + cipher.Length];
        var span = output.AsSpan();
        Magic.CopyTo(span); span = span[Magic.Length..];
        salt.CopyTo(span); span = span[SaltSize..];
        nonce.CopyTo(span); span = span[NonceSize..];
        tag.CopyTo(span); span = span[TagSize..];
        cipher.CopyTo(span);
        return output;
    }

    // Throws InputException for a wrong password or a damaged file.
    public static byte[] Decrypt(byte[] data, string password)
    {
        var span = data.AsSpan(Magic.Length);
        if (span.Length < SaltSize + NonceSize + TagSize)
            throw new InputException("The backup file is damaged.");
        var salt = span[..SaltSize].ToArray(); span = span[SaltSize..];
        var nonce = span[..NonceSize].ToArray(); span = span[NonceSize..];
        var tag = span[..TagSize].ToArray(); span = span[TagSize..];
        var plain = new byte[span.Length];
        try
        {
            using var aes = new AesGcm(DeriveKey(password, salt), TagSize);
            aes.Decrypt(nonce, span, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InputException("The password is wrong, or the backup file is damaged.");
        }
        return plain;
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
}
