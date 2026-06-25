using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLauncher.Utils;

public static class HashHelper
{
    public static async Task<string> ComputeFileSha1Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = OpenRead(filePath);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return ToHex(hash);
    }

    public static async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return ToHex(hash);
    }

    public static string ComputeFileSha1(string filePath)
    {
        using var stream = OpenRead(filePath);
        return ToHex(SHA1.HashData(stream));
    }

    public static string ComputeFileSha256(string filePath)
    {
        using var stream = OpenRead(filePath);
        return ToHex(SHA256.HashData(stream));
    }

    public static string ComputeSha1(string value)
    {
        return ComputeSha1(Encoding.UTF8.GetBytes(value));
    }

    public static string ComputeSha256(string value)
    {
        return ComputeSha256(Encoding.UTF8.GetBytes(value));
    }

    public static string ComputeMd5(string value)
    {
        return ComputeMd5(Encoding.UTF8.GetBytes(value));
    }

    public static string ComputeSha1(byte[] value)
    {
        return ToHex(SHA1.HashData(value));
    }

    public static string ComputeSha256(byte[] value)
    {
        return ToHex(SHA256.HashData(value));
    }

    public static string ComputeMd5(byte[] value)
    {
        return ToHex(MD5.HashData(value));
    }

    public static bool EqualsHash(string? actualHash, string? expectedHash)
    {
        return !string.IsNullOrWhiteSpace(actualHash) &&
               !string.IsNullOrWhiteSpace(expectedHash) &&
               string.Equals(
                   actualHash.Trim(),
                   expectedHash.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static FileStream OpenRead(string filePath)
    {
        return new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
    }

    private static string ToHex(byte[] hash)
    {
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

