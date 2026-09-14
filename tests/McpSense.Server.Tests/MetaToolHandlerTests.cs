using System.Net;
using System.Text;
using System.Text.Json;
using McpSense.Core;
using McpSense.Server;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpSense.Server.Tests;

/// <summary>
/// Covers the three meta-tools that stand in for a full operation list once a spec is large.
/// </summary>
public class MetaToolHandlerTests
{
    private const string Spec = """
        openapi: 3.0.3
        info:
          title: Pet API
          version: 1.0.0
        servers:
          - url: https://api.example.com
        paths:
          /pets:
            get:
              operationId: listPets
              summary: List all pets
              tags: [pets]
              responses:
                '200': { description: ok }
          /pets/{petId}/image:
            post:
              operationId: uploadPetImage
              summary: Upload an image for a pet
              tags: [pets]
              parameters:
                - name: petId
                  in: path
                  required: true
                  schema: { type: string }
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { type: object }
              responses:
                '200': { description: ok }
          /orders:
            get:
              operationId: listOrders
              summary: List all orders
              tags: [store]
              responses:
                '200': { description: ok }
        """;

    private static async Task<(ToolCatalogHandlers Handlers, CapturingHandler Http)> CreateAsync(
        ToolCatalogMode mode = ToolCatalogMode.MetaTool)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Spec));
        var loaded = await OpenApiSpecLoader.LoadAsync(stream, "yaml");
        var operations = new OperationModelBuilder().Build(loaded.Document);
        var catalog = await new ToolCatalogBuilder().BuildAsync(operations, new ToolGroupingOptions { Mode = mode });

        var http = new CapturingHandler();
        var dispatcher = new ApiDispatcher(
            new HttpClient(http),
            new McpSenseServerOptions { BaseAddress = new Uri("https://api.example.com") });

        return (new ToolCatalogHandlers(catalog, dispatcher), http);
    }

    private static Dictionary<string, JsonElement> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    [Fact]
    public async Task AdvertisesOnlyTheThreeMetaToolsInMetaMode()
    {
        var (handlers, _) = await CreateAsync();

        var listed = handlers.ListTools().Tools.Select(t => t.Name).ToList();

        Assert.Equal(
            [MetaToolNames.SearchOperations, MetaToolNames.DescribeOperation, MetaToolNames.CallOperation],
            listed);
    }

    [Fact]
    public async Task AdvertisesEveryOperationInDirectMode()
    {
        var (handlers, _) = await CreateAsync(ToolCatalogMode.Direct);

        var listed = handlers.ListTools().Tools.Select(t => t.Name).ToList();

        Assert.Equal(["listOrders", "listPets", "uploadPetImage"], listed.Order());
    }

    [Fact]
    public async Task SearchReturnsMatchingOperationNames()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.SearchOperations,
            Args("""{"query":"upload an image"}"""));

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("uploadPetImage", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchHonoursTheTagFilter()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.SearchOperations,
            Args("""{"query":"list","tag":"store"}"""));

        var text = TextOf(result);
        Assert.Contains("listOrders", text, StringComparison.Ordinal);
        Assert.DoesNotContain("listPets", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithoutMatchesPointsAtTheAvailableGroups()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.SearchOperations,
            Args("""{"query":"kubernetes"}"""));

        var text = TextOf(result);
        Assert.Contains("No operation matched", text, StringComparison.Ordinal);
        Assert.Contains("pets", text, StringComparison.Ordinal);
        Assert.Contains("store", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchRejectsAnEmptyQuery()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(MetaToolNames.SearchOperations, Args("""{"query":"  "}"""));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task DescribeReturnsTheOperationSchema()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.DescribeOperation,
            Args("""{"operation":"uploadPetImage"}"""));

        var text = TextOf(result);
        Assert.Contains("POST /pets/{petId}/image", text, StringComparison.Ordinal);
        Assert.Contains("\"petId\"", text, StringComparison.Ordinal);
        Assert.Contains("\"body\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeReachesOperationsThatAreNotAdvertised()
    {
        // In meta-tool mode the operation tools are hidden from tools/list but must stay reachable.
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.DescribeOperation,
            Args("""{"operation":"listPets"}"""));

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task DescribeRejectsAnUnknownNameAndPointsAtSearch()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.DescribeOperation,
            Args("""{"operation":"nope"}"""));

        Assert.True(result.IsError);
        Assert.Contains(MetaToolNames.SearchOperations, TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallOperationUnwrapsNestedArgumentsAndDispatches()
    {
        var (handlers, http) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.CallOperation,
            Args("""{"operation":"uploadPetImage","arguments":{"petId":"p1","body":{"url":"http://x/y.png"}}}"""));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("https://api.example.com/pets/p1/image", http.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("""{"url":"http://x/y.png"}""", http.LastBody);
    }

    [Fact]
    public async Task CallOperationWorksForAnOperationWithoutArguments()
    {
        var (handlers, http) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.CallOperation,
            Args("""{"operation":"listPets"}"""));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("https://api.example.com/pets", http.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task CallOperationSurfacesAMissingRequiredArgument()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.CallOperation,
            Args("""{"operation":"uploadPetImage","arguments":{}}"""));

        Assert.True(result.IsError);
        Assert.Contains("petId", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallOperationRejectsNonObjectArguments()
    {
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync(
            MetaToolNames.CallOperation,
            Args("""{"operation":"listPets","arguments":"oops"}"""));

        Assert.True(result.IsError);
        Assert.Contains("must be an object", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationToolsAreNotCallableByNameInMetaMode()
    {
        // The model was never told these names, and letting them through would make the two modes
        // silently different.
        var (handlers, _) = await CreateAsync();

        var result = await handlers.InvokeAsync("listPets", null);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", TextOf(result), StringComparison.Ordinal);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>The request body, captured before HttpClient disposes the content.</summary>
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // The body is captured here: HttpClient disposes the content once the request ends.
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            LastRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
