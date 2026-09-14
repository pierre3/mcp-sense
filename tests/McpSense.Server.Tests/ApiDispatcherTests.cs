using System.Net;
using System.Text.Json;
using McpSense.Core;
using McpSense.Server;
using Xunit;

namespace McpSense.Server.Tests;

/// <summary>
/// Covers turning a tool call into an HTTP request: where each argument lands, how the base
/// address is joined, and how credentials are attached.
/// </summary>
public class ApiDispatcherTests
{
    private static ApiDispatcher CreateDispatcher(string baseAddress, McpSenseServerOptions? options = null)
    {
        options ??= new McpSenseServerOptions { BaseAddress = new Uri(baseAddress) };
        return new ApiDispatcher(new HttpClient(new NeverCalledHandler()), options);
    }

    private static ToolInvocationPlan Plan(
        HttpMethod method,
        string pathTemplate,
        IReadOnlyList<ToolArgumentBinding>? arguments = null,
        string? bodyArgumentName = null,
        bool bodyRequired = false)
        => new()
        {
            Method = method,
            PathTemplate = pathTemplate,
            Arguments = arguments ?? [],
            BodyArgumentName = bodyArgumentName,
            BodyContentType = bodyArgumentName is null ? null : "application/json",
            BodyRequired = bodyRequired,
            Operation = new OperationDescriptor
            {
                Name = "test",
                Method = method,
                PathTemplate = pathTemplate,
                Deprecated = false,
                Tags = [],
                Parameters = [],
                Security = [],
            },
        };

    private static ToolArgumentBinding Binding(
        string name,
        OperationParameterLocation location,
        bool required = false,
        string? parameterName = null)
        => new()
        {
            ArgumentName = name,
            ParameterName = parameterName ?? name,
            Location = location,
            Required = required,
        };

    private static Dictionary<string, JsonElement> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void SubstitutesAndEscapesPathParameters()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets/{petId}", [Binding("petId", OperationParameterLocation.Path, required: true)]);

        using var request = dispatcher.BuildRequest(plan, Args("""{"petId":"a b/c"}"""));

        Assert.Equal("https://api.example.com/pets/a%20b%2Fc", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void KeepsThePathPrefixOfTheBaseAddress()
    {
        // Resolving a root-relative path through Uri would drop "/v2"; the dispatcher concatenates
        // instead, so a server URL carrying a base path keeps it.
        var dispatcher = CreateDispatcher("https://api.example.com/v2");
        var plan = Plan(HttpMethod.Get, "/pets");

        using var request = dispatcher.BuildRequest(plan, null);

        Assert.Equal("https://api.example.com/v2/pets", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void TreatsATrailingSlashOnTheBaseAddressTheSameWay()
    {
        var dispatcher = CreateDispatcher("https://api.example.com/v2/");
        var plan = Plan(HttpMethod.Get, "/pets");

        using var request = dispatcher.BuildRequest(plan, null);

        Assert.Equal("https://api.example.com/v2/pets", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void SendsArrayQueryValuesAsRepeatedParameters()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets", [Binding("status", OperationParameterLocation.Query)]);

        using var request = dispatcher.BuildRequest(plan, Args("""{"status":["available","sold"]}"""));

        Assert.Equal("https://api.example.com/pets?status=available&status=sold", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void RendersNonStringScalarsWithoutQuotes()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets",
        [
            Binding("limit", OperationParameterLocation.Query),
            Binding("exact", OperationParameterLocation.Query),
        ]);

        using var request = dispatcher.BuildRequest(plan, Args("""{"limit":25,"exact":true}"""));

        Assert.Equal("https://api.example.com/pets?limit=25&exact=true", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void PutsHeaderAndCookieArgumentsInTheRightPlace()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets",
        [
            Binding("X-Trace", OperationParameterLocation.Header),
            Binding("session", OperationParameterLocation.Cookie),
        ]);

        using var request = dispatcher.BuildRequest(plan, Args("""{"X-Trace":"abc","session":"xyz"}"""));

        Assert.Equal("abc", Assert.Single(request.Headers.GetValues("X-Trace")));
        Assert.Equal("session=xyz", Assert.Single(request.Headers.GetValues("Cookie")));
    }

    [Fact]
    public void OmitsOptionalArgumentsThatWereNotSupplied()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets", [Binding("status", OperationParameterLocation.Query)]);

        using var request = dispatcher.BuildRequest(plan, Args("""{}"""));

        Assert.Equal("https://api.example.com/pets", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void TreatsAnExplicitNullAsAbsent()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets", [Binding("status", OperationParameterLocation.Query)]);

        using var request = dispatcher.BuildRequest(plan, Args("""{"status":null}"""));

        Assert.Equal("https://api.example.com/pets", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void RejectsACallMissingARequiredArgument()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Get, "/pets/{petId}", [Binding("petId", OperationParameterLocation.Path, required: true)]);

        var exception = Assert.Throws<ToolInvocationException>(() => dispatcher.BuildRequest(plan, Args("""{}""")));

        Assert.Contains("petId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsACallMissingARequiredBody()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Post, "/pets", bodyArgumentName: "body", bodyRequired: true);

        Assert.Throws<ToolInvocationException>(() => dispatcher.BuildRequest(plan, Args("""{}""")));
    }

    [Fact]
    public async Task SendsTheBodyVerbatimWithTheDeclaredContentType()
    {
        var dispatcher = CreateDispatcher("https://api.example.com");
        var plan = Plan(HttpMethod.Post, "/pets", bodyArgumentName: "body", bodyRequired: true);

        using var request = dispatcher.BuildRequest(plan, Args("""{"body":{"name":"Rex","age":3}}"""));

        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"name":"Rex","age":3}""", await request.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AttachesCredentialsWithoutExposingThemToTheCaller()
    {
        var handler = new CapturingHandler();
        var options = new McpSenseServerOptions
        {
            BaseAddress = new Uri("https://api.example.com"),
            BearerToken = "secret-token",
            ApiKeyHeaderName = "X-Api-Key",
            ApiKeyValue = "secret-key",
        };
        var dispatcher = new ApiDispatcher(new HttpClient(handler), options);

        var tool = new CatalogTool
        {
            Name = "listPets",
            Description = "List pets",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            Kind = CatalogToolKind.Operation,
            Plan = Plan(HttpMethod.Get, "/pets"),
        };

        var result = await dispatcher.InvokeAsync(tool, null);

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer secret-token", handler.LastRequest!.Headers.Authorization?.ToString());
        Assert.Equal("secret-key", Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task SendsConfiguredDefaultHeaders()
    {
        var handler = new CapturingHandler();
        var options = new McpSenseServerOptions
        {
            BaseAddress = new Uri("https://api.example.com"),
            DefaultHeaders = new Dictionary<string, string> { ["User-Agent"] = "mcpsense/test" },
        };
        var dispatcher = new ApiDispatcher(new HttpClient(handler), options);

        await dispatcher.InvokeAsync(Tool(Plan(HttpMethod.Get, "/pets")), null);

        Assert.Equal("mcpsense/test", Assert.Single(handler.LastRequest!.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task LetsAnOperationHeaderWinOverADefault()
    {
        // A header the caller supplied as an argument is specific to that request, so it must not
        // be displaced by a server-wide default.
        var handler = new CapturingHandler();
        var options = new McpSenseServerOptions
        {
            BaseAddress = new Uri("https://api.example.com"),
            DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "default" },
        };
        var dispatcher = new ApiDispatcher(new HttpClient(handler), options);
        var plan = Plan(HttpMethod.Get, "/pets", [Binding("X-Trace", OperationParameterLocation.Header)]);

        await dispatcher.InvokeAsync(Tool(plan), Args("""{"X-Trace":"from-call"}"""));

        Assert.Equal("from-call", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Trace")));
    }

    private static CatalogTool Tool(ToolInvocationPlan plan) => new()
    {
        Name = "test",
        Description = "test",
        InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
        Kind = CatalogToolKind.Operation,
        Plan = plan,
    };

    [Fact]
    public async Task ReportsAnUpstreamFailureAsAnErrorCarryingTheResponseBody()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, """{"message":"no such pet"}""");
        var dispatcher = new ApiDispatcher(
            new HttpClient(handler),
            new McpSenseServerOptions { BaseAddress = new Uri("https://api.example.com") });

        var tool = new CatalogTool
        {
            Name = "getPet",
            Description = "Get a pet",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            Kind = CatalogToolKind.Operation,
            Plan = Plan(HttpMethod.Get, "/pets/1"),
        };

        var result = await dispatcher.InvokeAsync(tool, null);

        Assert.False(result.IsSuccess);
        Assert.Contains("404", result.Content, StringComparison.Ordinal);
        Assert.Contains("no such pet", result.Content, StringComparison.Ordinal);
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This test builds requests only; it must not send them.");
    }

    private sealed class CapturingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        }
    }
}
