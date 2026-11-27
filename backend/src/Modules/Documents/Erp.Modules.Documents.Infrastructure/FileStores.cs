using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Erp.Modules.Documents.Application;

namespace Erp.Modules.Documents.Infrastructure;

/// <summary>
/// Stores attachments on a local or mounted disk (on-prem default; in containers mount a volume). Keys are validated with
/// <see cref="StorageKeys.IsSafe"/> and the resolved path must stay under the root, so a crafted key cannot escape it.
/// Writes go to a temporary file first and are then moved into place.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Name => "local";

    public string Root => _root;

    /// <summary>Resolves a key to a path under the root, or throws when the key is not one produced by <see cref="StorageKeys"/>.</summary>
    public string PathFor(string key)
    {
        if (!StorageKeys.IsSafe(key))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return path;
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            return; // content-addressed: same key = same bytes
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(PathFor(key)));
}

/// <summary>Stores attachments in an S3 bucket or an S3-compatible server (MinIO, Ceph, Wasabi, …).</summary>
public sealed class S3FileStorage(IAmazonS3 client, S3StorageOptions options) : IFileStorage
{
    public string Name => "s3";

    public static AmazonS3Config CreateConfig(S3StorageOptions options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return config;
    }

    public static IAmazonS3 CreateClient(S3StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            throw new InvalidOperationException("Documents:Attachments:S3:Bucket must be set when Storage is S3.");
        }

        var config = CreateConfig(options);
        return string.IsNullOrWhiteSpace(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public string ObjectKey(string key)
    {
        if (!StorageKeys.IsSafe(key))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        var prefix = (options.Prefix ?? string.Empty).Trim('/');
        return prefix.Length == 0 ? key : $"{prefix}/{key}";
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = ObjectKey(key),
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType,
        }, cancellationToken);

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.GetObjectAsync(options.Bucket, ObjectKey(key), cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetObjectMetadataAsync(options.Bucket, ObjectKey(key), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
