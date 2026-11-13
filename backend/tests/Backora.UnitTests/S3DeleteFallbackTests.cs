using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Infrastructure.Storage;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>
/// Dell ECS (and some older S3-compatible stores) reject the batch DeleteObjects call because the AWS SDK sends a CRC32
/// checksum instead of Content-MD5. Retention must still delete expired recovery points there.
/// </summary>
public sealed class S3DeleteFallbackTests : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly string _url;
    private readonly ConcurrentBag<string> _deleted = [];
    private int _batchCalls;
    private readonly CancellationTokenSource _stop = new();

    public S3DeleteFallbackTests()
    {
        var port = FreePort();
        _url = $"http://127.0.0.1:{port}";
        _http.Prefixes.Add(_url + "/");
        _http.Start();
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            var req = ctx.Request;
            var res = ctx.Response;
            string body = "";
            if (req.HttpMethod == "POST" && req.Url!.Query.Contains("delete", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _batchCalls);
                res.StatusCode = 400;
                body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>InvalidRequest</Code><Message>Missing required header for this request: Content-MD5</Message></Error>";
            }
            else if (req.HttpMethod == "GET")
            {
                var keys = new[] { "job/2026-11-01-r1/base.tar", "job/2026-11-01-r1/pg_wal.tar", "job/2026-11-01-r1/backup_manifest" };
                var items = string.Concat(keys.Select(k => $"<Contents><Key>{k}</Key><Size>10</Size><LastModified>2026-11-01T00:00:00.000Z</LastModified></Contents>"));
                body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><Name>b</Name><Prefix>job/2026-11-01-r1/</Prefix><KeyCount>{keys.Length}</KeyCount><IsTruncated>false</IsTruncated>{items}</ListBucketResult>";
            }
            else if (req.HttpMethod == "DELETE")
            {
                _deleted.Add(Uri.UnescapeDataString(req.Url!.AbsolutePath["/b/".Length..]));
                res.StatusCode = 204;
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            if (bytes.Length > 0) res.ContentType = "application/xml";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    [Fact]
    public async Task Falls_back_to_single_deletes_when_the_store_rejects_the_batch_call()
    {
        var conn = new S3Connection(_url, "us-east-1", "b", "AKIDEXAMPLE", "secret", true);
        var storage = new S3ObjectStorage();

        (await storage.DeletePrefixAsync(conn, "job/2026-11-01-r1/", CancellationToken.None)).Should().Be(3);
        _deleted.Should().BeEquivalentTo(["job/2026-11-01-r1/base.tar", "job/2026-11-01-r1/pg_wal.tar", "job/2026-11-01-r1/backup_manifest"]);
        _batchCalls.Should().Be(1);

        _deleted.Clear();
        (await storage.DeletePrefixAsync(conn, "job/2026-11-01-r1/", CancellationToken.None)).Should().Be(3);
        _batchCalls.Should().Be(1, "the endpoint is remembered, so the batch call is not tried again");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Stop();
        _http.Close();
    }
}
