using System.Security.Cryptography;
using System.Text;

namespace Klip.Core;

/// <summary>Content normalization and hashing. See docs/protocol.md §3.</summary>
public static class Content
{
    public static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Normalize(NormalizationForm.FormC);

    public static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(s))));

    /// <summary>Hash of an image: SHA-256 of its exact bytes (docs/protocol.md §3).</summary>
    public static string HashBytes(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Image formats carried by the protocol.</summary>
    public static readonly IReadOnlyList<string> ImageMimeTypes = ["image/png", "image/jpeg"];

    /// <summary>True if empty or made only of Unicode white space.</summary>
    public static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);
}
