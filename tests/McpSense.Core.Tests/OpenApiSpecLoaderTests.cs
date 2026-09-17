using System.Net;
using System.Net.Sockets;
using System.Text;
using McpSense.Core;
using Xunit;

namespace McpSense.Core.Tests;

/// <summary>
/// Covers how a spec is obtained and how its format is settled. The interesting cases are the ones
/// where the source lies about the format or does not say: specs are commonly served as text/plain,
/// and a URL that fails has to come back as a load failure rather than as whatever the transport
/// threw.
/// </summary>
public class OpenApiSpecLoaderTests : IDisposable
{
    private const string JsonSpec =
        """{"openapi":"3.0.3","info":{"title":"Test API","version":"1.0.0"},"paths":{"/pets":{"get":{"operationId":"listPets","responses":{"200":{"description":"ok"}}}}}}""";

    // A plain (non-interpolated) raw string so that the YAML braces stay literal.
    private const string YamlSpec = """
        openapi: 3.0.3
        info:
          title: Test API
          version: 1.0.0
        paths:
          /pets:
            get:
              operationId: listPets
              responses:
                '200': { description: ok }
        """;

    private readonly List<string> _files = [];

    /// <summary>Removes the files the test wrote.</summary>
    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReadsJsonFromAFileNamedJson()
    {
        var loaded = await OpenApiSpecLoader.LoadAsync(WriteSpecFile(".json", JsonSpec));

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task ReadsYamlFromAFileNamedYml()
    {
        var loaded = await OpenApiSpecLoader.LoadAsync(WriteSpecFile(".yml", YamlSpec));

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task InfersJsonFromTheContentWhenTheExtensionSaysNothing()
    {
        var loaded = await OpenApiSpecLoader.LoadAsync(WriteSpecFile(".txt", JsonSpec));

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task InfersYamlFromTheContentWhenTheExtensionSaysNothing()
    {
        var loaded = await OpenApiSpecLoader.LoadAsync(WriteSpecFile(".txt", YamlSpec));

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task InfersTheFormatFromAStreamWhenNoneIsGiven()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSpec));

        var loaded = await OpenApiSpecLoader.LoadAsync(stream);

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task ReportsAMissingFileAsALoadFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mcpsense-{Guid.NewGuid():N}.yaml");

        await Assert.ThrowsAsync<OpenApiSpecLoadException>(() => OpenApiSpecLoader.LoadAsync(missing));
    }

    [Fact]
    public async Task ReportsContentThatIsNotASpecAsALoadFailure()
    {
        var path = WriteSpecFile(".json", "this is not a spec");

        await Assert.ThrowsAsync<OpenApiSpecLoadException>(() => OpenApiSpecLoader.LoadAsync(path));
    }

    /// <summary>
    /// The case that made this worth fixing: raw.githubusercontent.com serves every file as
    /// text/plain, and the reader used to reject the URL with "Format 'plain' is not supported".
    /// </summary>
    [Fact]
    public async Task ReadsAUrlServedAsTextPlain()
    {
        using var server = new StubServer("/api.github.com.json", "text/plain; charset=utf-8", JsonSpec);

        var loaded = await OpenApiSpecLoader.LoadAsync(server.Url.ToString());

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task ReadsAUrlWhoseFormatOnlyTheContentReveals()
    {
        using var server = new StubServer("/openapi", "text/plain", YamlSpec);

        var loaded = await OpenApiSpecLoader.LoadAsync(server.Url.ToString());

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task ReadsAUrlWithNoExtensionFromItsMediaType()
    {
        using var server = new StubServer("/openapi", "application/vnd.oai.openapi+json", JsonSpec);

        var loaded = await OpenApiSpecLoader.LoadAsync(server.Url.ToString());

        Assert.Equal("Test API", loaded.Document.Info?.Title);
    }

    [Fact]
    public async Task ReportsAnHttpErrorAsALoadFailureRatherThanParsingTheErrorPage()
    {
        using var server = new StubServer("/missing.json", "text/plain", "404: Not Found", status: 404);

        var failure = await Assert.ThrowsAsync<OpenApiSpecLoadException>(
            () => OpenApiSpecLoader.LoadAsync(server.Url.ToString()));

        Assert.Contains(failure.Problems, problem => problem.Contains("404"));
    }

    /// <summary>Writes <paramref name="content"/> to a temporary file and schedules its removal.</summary>
    private string WriteSpecFile(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcpsense-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    /// <summary>
    /// Serves one response on a loopback port. A hand-written response keeps the test free of any
    /// URL reservation or elevation the platform's HTTP server might want.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;

        public StubServer(string path, string contentType, string body, int status = 200)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");
            _ = ServeOnceAsync(contentType, body, status);
        }

        /// <summary>Where the served document can be read.</summary>
        public Uri Url { get; }

        public void Dispose() => _listener.Stop();

        private async Task ServeOnceAsync(string contentType, string body, int status)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();

                // The request is read only to keep the response from racing ahead of it; nothing in
                // it is needed, so one read is enough.
                var request = new byte[4096];
                _ = await stream.ReadAsync(request);

                var payload = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
                    + $"Content-Type: {contentType}\r\n"
                    + $"Content-Length: {payload.Length}\r\n"
                    + "Connection: close\r\n\r\n");

                await stream.WriteAsync(head);
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
            {
                // The listener was stopped at the end of the test.
            }
        }
    }
}
