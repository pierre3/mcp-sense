using System.Text;
using System.Text.Json;
using McpSense.Core;
using Xunit;

namespace McpSense.Core.Tests;

/// <summary>
/// Covers the operation-to-tool conversion: the shape of the input schema an MCP client receives,
/// and the plan used to invoke the operation.
/// </summary>
public class ToolCatalogBuilderTests
{
    private const string Preamble = """
        openapi: 3.0.3
        info:
          title: Test API
          version: 1.0.0

        """;

    private static async Task<ToolCatalog> BuildAsync(string documentBody)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Preamble + documentBody));
        var loaded = await OpenApiSpecLoader.LoadAsync(stream, "yaml");
        var operations = new OperationModelBuilder().Build(loaded.Document);
        return await new ToolCatalogBuilder().BuildAsync(operations);
    }

    [Fact]
    public async Task ExposesParametersAndBodyAsSiblingProperties()
    {
        var catalog = await BuildAsync("""
            paths:
              /pets/{petId}:
                put:
                  operationId: updatePet
                  parameters:
                    - name: petId
                      in: path
                      required: true
                      schema: { type: string }
                    - name: dryRun
                      in: query
                      schema: { type: boolean }
                  requestBody:
                    required: true
                    content:
                      application/json:
                        schema: { type: object }
                  responses:
                    '200': { description: ok }
            """);

        var tool = Assert.Single(catalog.Tools);
        var properties = tool.InputSchema.GetProperty("properties");

        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.True(properties.TryGetProperty("petId", out _));
        Assert.True(properties.TryGetProperty("dryRun", out _));
        Assert.True(properties.TryGetProperty("body", out _));

        var required = tool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("petId", required);
        Assert.Contains("body", required);
        Assert.DoesNotContain("dryRun", required);
    }

    [Fact]
    public async Task RecordsHowEachArgumentReachesTheRequest()
    {
        var catalog = await BuildAsync("""
            paths:
              /pets/{petId}:
                put:
                  operationId: updatePet
                  parameters:
                    - name: petId
                      in: path
                      required: true
                      schema: { type: string }
                    - name: X-Trace
                      in: header
                      schema: { type: string }
                  requestBody:
                    content:
                      application/json:
                        schema: { type: object }
                  responses:
                    '200': { description: ok }
            """);

        var plan = Assert.Single(catalog.Tools).Plan!;

        Assert.Equal(HttpMethod.Put, plan.Method);
        Assert.Equal("/pets/{petId}", plan.PathTemplate);
        Assert.Equal("body", plan.BodyArgumentName);
        Assert.Equal("application/json", plan.BodyContentType);
        Assert.False(plan.BodyRequired);

        Assert.Equal(
            [
                ("petId", OperationParameterLocation.Path, true),
                ("X-Trace", OperationParameterLocation.Header, false),
            ],
            plan.Arguments.Select(a => (a.ArgumentName, a.Location, a.Required)));
    }

    [Fact]
    public async Task InlinesComponentSchemasSoTheClientNeedsNoSpec()
    {
        var catalog = await BuildAsync("""
            components:
              schemas:
                Pet:
                  type: object
                  properties:
                    name: { type: string }
                  required: [name]
            paths:
              /pets:
                post:
                  operationId: createPet
                  requestBody:
                    required: true
                    content:
                      application/json:
                        schema:
                          $ref: '#/components/schemas/Pet'
                  responses:
                    '201': { description: created }
            """);

        var schema = JsonSerializer.Serialize(Assert.Single(catalog.Tools).InputSchema);

        Assert.DoesNotContain("$ref", schema, StringComparison.Ordinal);
        Assert.Contains("\"name\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlesSelfReferencingSchemasWithoutHanging()
    {
        // A schema that refers to itself must not send the inliner into infinite recursion.
        var catalog = await BuildAsync("""
            components:
              schemas:
                Node:
                  type: object
                  properties:
                    value: { type: string }
                    child:
                      $ref: '#/components/schemas/Node'
            paths:
              /nodes:
                post:
                  operationId: createNode
                  requestBody:
                    required: true
                    content:
                      application/json:
                        schema:
                          $ref: '#/components/schemas/Node'
                  responses:
                    '201': { description: created }
            """);

        var schema = JsonSerializer.Serialize(Assert.Single(catalog.Tools).InputSchema);

        Assert.Contains("\"value\"", schema, StringComparison.Ordinal);
        // The exact depth the inliner stops at is its own concern; what matters is that it
        // terminates and produces something bounded.
        Assert.True(schema.Length < 100_000, $"Schema grew to {schema.Length} chars.");
    }

    [Fact]
    public async Task SuffixesArgumentNamesThatClashAcrossLocations()
    {
        var catalog = await BuildAsync("""
            paths:
              /pets:
                get:
                  operationId: listPets
                  parameters:
                    - name: token
                      in: query
                      schema: { type: string }
                    - name: token
                      in: header
                      schema: { type: string }
                  responses:
                    '200': { description: ok }
            """);

        var plan = Assert.Single(catalog.Tools).Plan!;

        // Both keep their real parameter name; only the exposed argument name is disambiguated.
        Assert.Equal(["token", "token_header"], plan.Arguments.Select(a => a.ArgumentName));
        Assert.Equal(["token", "token"], plan.Arguments.Select(a => a.ParameterName));
    }

    [Fact]
    public async Task RenamesTheBodyArgumentWhenAParameterAlreadyOwnsTheName()
    {
        var catalog = await BuildAsync("""
            paths:
              /pets:
                post:
                  operationId: createPet
                  parameters:
                    - name: body
                      in: query
                      schema: { type: string }
                  requestBody:
                    required: true
                    content:
                      application/json:
                        schema: { type: object }
                  responses:
                    '201': { description: created }
            """);

        var plan = Assert.Single(catalog.Tools).Plan!;

        Assert.Equal("body", Assert.Single(plan.Arguments).ArgumentName);
        Assert.Equal("body_2", plan.BodyArgumentName);
    }

    [Fact]
    public async Task DescribesTheOperationIncludingItsMethodAndPath()
    {
        var catalog = await BuildAsync("""
            paths:
              /pets:
                get:
                  operationId: listPets
                  summary: List pets
                  description: Returns every pet.
                  deprecated: true
                  responses:
                    '200': { description: ok }
            """);

        var description = Assert.Single(catalog.Tools).Description;

        Assert.Contains("List pets", description, StringComparison.Ordinal);
        Assert.Contains("Returns every pet.", description, StringComparison.Ordinal);
        Assert.Contains("Calls GET /pets.", description, StringComparison.Ordinal);
        Assert.Contains("deprecated", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProducesAnObjectSchemaForOperationsWithoutArguments()
    {
        var catalog = await BuildAsync("""
            paths:
              /health:
                get:
                  operationId: health
                  responses:
                    '200': { description: ok }
            """);

        var tool = Assert.Single(catalog.Tools);

        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
        Assert.False(tool.InputSchema.TryGetProperty("required", out _));
    }

    [Fact]
    public async Task LooksToolsUpByName()
    {
        var catalog = await BuildAsync("""
            paths:
              /pets:
                get:
                  operationId: listPets
                  responses:
                    '200': { description: ok }
            """);

        Assert.True(catalog.TryGet("listPets", out var tool));
        Assert.Equal("listPets", tool.Name);
        Assert.False(catalog.TryGet("nope", out _));
    }
}
