using System.Security.Cryptography;
using System.Text;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Documents.Application;

/// <summary>
/// Blob storage port for attachments. Keys are produced by <see cref="StorageKeys"/> (tenant + content hash), so writes are
/// idempotent: storing the same bytes twice is harmless, which is what makes writing the blob before the database commit safe.
/// </summary>
public interface IFileStorage
{
    string Name { get; }

    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <returns>The content, or null when the key does not exist.</returns>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}

public static class StorageKeys
{
    /// <summary><c>{tenant}/{aa}/{sha256}</c> — content-addressed per tenant (no cross-tenant deduplication).</summary>
    public static string For(Guid tenantId, string sha256Hex) => $"{tenantId:N}/{sha256Hex[..2]}/{sha256Hex}";

    /// <summary>
    /// A key is safe when it is exactly what <see cref="For"/> produces: lower-case hex segments only, no dots, no slashes at
    /// either end. Storage adapters check this before touching the file system or bucket.
    /// </summary>
    public static bool IsSafe(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 200)
        {
            return false;
        }

        var parts = key.Split('/');
        return parts.Length == 3
               && parts[0].Length == 32 && parts[1].Length == 2 && parts[2].Length == 64
               && parts.All(p => p.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
               && parts[2].StartsWith(parts[1], StringComparison.Ordinal);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

public sealed class AttachmentOptions
{
    public const string SectionName = "Documents:Attachments";

    /// <summary>Largest accepted upload. Default 20 MB.</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>"Local" (default) or "S3".</summary>
    public string Storage { get; set; } = "Local";

    /// <summary>Root folder of the local store. Relative paths are resolved against the content root.</summary>
    public string LocalRoot { get; set; } = "data/attachments";

    /// <summary>Optional subset of <see cref="FileTypes.Supported"/> extensions (e.g. ".pdf", ".png"); empty = all supported.</summary>
    public List<string> AllowedExtensions { get; set; } = [];

    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class S3StorageOptions
{
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Service URL for S3-compatible stores (MinIO, Ceph, …). Empty = AWS with <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>Path-style addressing (required by most S3-compatible servers).</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Optional static credentials; when empty the SDK's default chain (environment, instance role, …) is used.</summary>
    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Optional prefix inside the bucket, e.g. <c>erp/</c>.</summary>
    public string? Prefix { get; set; }
}

public sealed record FileType(string Extension, string ContentType);

/// <summary>
/// The attachment allowlist. The stored content type is derived from the extension (never from the client header) and the
/// first bytes must match it, so an HTML or script file renamed to <c>.pdf</c> is rejected. Active content (HTML, SVG,
/// scripts, executables, macro-enabled Office files) is not accepted at all.
/// </summary>
public static class FileTypes
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    private static readonly Dictionary<string, (string ContentType, Func<byte[], bool> Matches)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ("application/pdf", b => StartsWith(b, "%PDF-"u8)),
        [".png"] = ("image/png", b => StartsWith(b, Png)),
        [".jpg"] = ("image/jpeg", b => StartsWith(b, [0xFF, 0xD8, 0xFF])),
        [".jpeg"] = ("image/jpeg", b => StartsWith(b, [0xFF, 0xD8, 0xFF])),
        [".gif"] = ("image/gif", b => StartsWith(b, "GIF87a"u8) || StartsWith(b, "GIF89a"u8)),
        [".webp"] = ("image/webp", b => b.Length >= 12 && StartsWith(b, "RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)),
        [".txt"] = ("text/plain", IsPlainText),
        [".csv"] = ("text/csv", IsPlainText),
        [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", b => StartsWith(b, Zip)),
        [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", b => StartsWith(b, Zip)),
        [".pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", b => StartsWith(b, Zip)),
    };

    public static IReadOnlyCollection<string> Supported => Types.Keys;

    public static readonly Error Empty = Error.Validation("documents.file-empty", "The file is empty.");

    public static Error TooLarge(long max) => Error.Validation("documents.file-too-large", $"The file is larger than {max / (1024 * 1024)} MB.");

    public static readonly Error NotAllowed = Error.Validation("documents.file-type-not-allowed",
        "This file type is not accepted. Allowed: PDF, PNG, JPEG, GIF, WebP, TXT, CSV, DOCX, XLSX, PPTX.");

    public static readonly Error ContentMismatch = Error.Validation("documents.file-content-mismatch",
        "The file content does not match its extension.");

    /// <summary>Checks the extension against the allowlist and the content against the extension's signature.</summary>
    public static Result<FileType> Detect(string fileName, byte[] content, IReadOnlyCollection<string>? allowedExtensions = null)
    {
        if (content.Length == 0)
        {
            return Empty;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || !Types.TryGetValue(extension, out var type) ||
            (allowedExtensions is { Count: > 0 } && !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
        {
            return NotAllowed;
        }

        return type.Matches(content) ? new FileType(extension.ToLowerInvariant(), type.ContentType) : ContentMismatch;
    }

    /// <summary>
    /// Reduces a client-supplied name to a plain file name: no directories, no control characters, no characters that are
    /// unsafe in headers or on Windows file systems.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || c is '"' or '<' or '>' or ':' or '|' or '?' or '*' ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimStart('.');
        if (result.Length > 255)
        {
            var extension = Path.GetExtension(result);
            result = result[..(255 - extension.Length)] + extension;
        }

        return result;
    }

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> signature) => content.AsSpan().StartsWith(signature);

    /// <summary>
    /// Text files have no signature; binary content (NUL bytes) is rejected. Single-byte code pages (e.g. Windows-1256 CSV
    /// exports) are accepted on purpose; downloads are always served as attachments with <c>nosniff</c>.
    /// </summary>
    private static bool IsPlainText(byte[] content) => content.AsSpan().IndexOf((byte)0) < 0;
}
