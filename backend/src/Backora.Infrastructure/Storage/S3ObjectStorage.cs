using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Backora.Application.Abstractions;
using Backora.Contracts.V1;

namespace Backora.Infrastructure.Storage;

/// <summary>S3 API access via the AWS SDK (TLS certificate validation is always on for https endpoints).</summary>
public sealed class S3ObjectStorage : IObjectStorage
{
    private static AmazonS3Client Client(S3Connection c) => new(
        new BasicAWSCredentials(c.AccessKeyId, c.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = c.Endpoint,
            ForcePathStyle = c.ForcePathStyle,
            AuthenticationRegion = c.Region,
            Timeout = TimeSpan.FromSeconds(60),
            MaxErrorRetry = 3,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public async Task<IReadOnlyList<StoredObject>> ListAsync(S3Connection connection, string prefix, CancellationToken ct)
    {
        using var client = Client(connection);
        var result = new List<StoredObject>();
        string? token = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = connection.Bucket, Prefix = prefix, ContinuationToken = token }, ct);
            foreach (var o in response.S3Objects ?? [])
            {
                result.Add(new StoredObject(o.Key, o.Size ?? 0, o.LastModified is { } lm ? new DateTimeOffset(lm.ToUniversalTime()) : DateTimeOffset.MinValue));
            }
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);
        return result;
    }

    public async Task<S3BrowsePage> BrowseAsync(S3Connection connection, string prefix, string? continuationToken, int maxKeys, bool recursive, CancellationToken ct)
    {
        using var client = Client(connection);
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = connection.Bucket, Prefix = prefix, Delimiter = recursive ? null : "/", MaxKeys = maxKeys,
            ContinuationToken = string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
        }, ct);
        var objects = (response.S3Objects ?? []).Where(o => !o.Key.EndsWith('/'))
            .Select(o => new StoredObject(o.Key, o.Size ?? 0, o.LastModified is { } lm ? new DateTimeOffset(lm.ToUniversalTime()) : DateTimeOffset.MinValue)).ToList();
        return new S3BrowsePage(response.CommonPrefixes ?? [], objects, response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    public async Task<int> DeletePrefixAsync(S3Connection connection, string prefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim('/').Length == 0) throw new ArgumentException("Refusing to delete an empty prefix.", nameof(prefix));
        var objects = await ListAsync(connection, prefix, ct);
        using var client = Client(connection);
        var deleted = 0;
        foreach (var chunk in objects.Chunk(1000))
        {
            if (!NoBatchDelete.ContainsKey(EndpointKey(connection)))
            {
                try
                {
                    var response = await client.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = connection.Bucket,
                        Objects = chunk.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                    }, ct);
                    if (response.DeleteErrors is { Count: > 0 } errors)
                    {
                        throw new InvalidOperationException($"S3 refused to delete {errors.Count} object(s): {errors[0].Code} {errors[0].Message}");
                    }
                    deleted += response.DeletedObjects?.Count ?? 0;
                    continue;
                }
                catch (AmazonS3Exception ex) when (BatchDeleteUnsupported(ex))
                {
                    // The SDK signs DeleteObjects with a CRC32 checksum; storage that insists on Content-MD5 (Dell ECS,
                    // older MinIO/Ceph) rejects it. Single-object deletes need no body checksum, so use those from now on.
                    NoBatchDelete.TryAdd(EndpointKey(connection), 0);
                }
            }
            await Parallel.ForEachAsync(chunk, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (o, token) =>
            {
                await client.DeleteObjectAsync(connection.Bucket, o.Key, token);
                Interlocked.Increment(ref deleted);
            });
        }
        return deleted;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> NoBatchDelete = new(StringComparer.OrdinalIgnoreCase);

    private static string EndpointKey(S3Connection c) => c.Endpoint.TrimEnd('/') + "|" + c.Bucket;

    internal static bool BatchDeleteUnsupported(AmazonS3Exception ex) =>
        ex.Message.Contains("Content-MD5", StringComparison.OrdinalIgnoreCase)
        || ex.ErrorCode is "NotImplemented" or "BadDigest" or "InvalidDigest" or "MissingContentMD5"
        || (ex.ErrorCode == "InvalidRequest" && ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<CheckResultDto>> TestAsync(S3Connection connection, CancellationToken ct)
    {
        var checks = new List<CheckResultDto>();
        using var client = Client(connection);
        try
        {
            await client.HeadBucketAsync(new HeadBucketRequest { BucketName = connection.Bucket }, ct);
            checks.Add(new CheckResultDto("Bucket reachable", CheckState.Passed, $"Bucket '{connection.Bucket}' exists and credentials are accepted."));
        }
        catch (Exception ex) when (ex is AmazonS3Exception or AmazonServiceException or HttpRequestException or TaskCanceledException)
        {
            checks.Add(new CheckResultDto("Bucket reachable", CheckState.Failed, Describe(ex)));
            return checks;
        }

        var key = $".backora-probe/{Guid.NewGuid():N}";
        try
        {
            await client.PutObjectAsync(new PutObjectRequest { BucketName = connection.Bucket, Key = key, ContentBody = "backora connectivity probe" }, ct);
            checks.Add(new CheckResultDto("Write permission", CheckState.Passed, "Probe object written."));
            using (var get = await client.GetObjectAsync(connection.Bucket, key, ct))
            using (var reader = new StreamReader(get.ResponseStream))
            {
                var body = await reader.ReadToEndAsync(ct);
                checks.Add(body == "backora connectivity probe"
                    ? new CheckResultDto("Read permission", CheckState.Passed, "Probe object read back intact.")
                    : new CheckResultDto("Read permission", CheckState.Failed, "Probe object content mismatch."));
            }
            await client.DeleteObjectAsync(connection.Bucket, key, ct);
            checks.Add(new CheckResultDto("Delete permission", CheckState.Passed, "Probe object deleted (required for retention)."));
        }
        catch (Exception ex) when (ex is AmazonS3Exception or AmazonServiceException or HttpRequestException)
        {
            checks.Add(new CheckResultDto("Read/write/delete", CheckState.Failed, Describe(ex)));
        }
        return checks;
    }

    private static string Describe(Exception ex) => ex switch
    {
        AmazonS3Exception s3 when s3.StatusCode == System.Net.HttpStatusCode.NotFound => "Bucket not found.",
        AmazonS3Exception s3 when s3.StatusCode == System.Net.HttpStatusCode.Forbidden => "Access denied: check the access key, secret and bucket policy.",
        AmazonS3Exception s3 => $"S3 error {(int)s3.StatusCode} {s3.ErrorCode}: {s3.Message}",
        HttpRequestException http => $"Endpoint unreachable: {http.Message}",
        TaskCanceledException => "The endpoint did not respond in time.",
        _ => ex.Message,
    };
}
