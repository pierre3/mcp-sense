using System.Text;
using McpSense.Core;
using Microsoft.OpenApi;
using Xunit;

namespace McpSense.Core.Tests;

/// <summary>
/// Covers the spec-to-model conversion. Fixtures are small hand-written documents so each test
/// pins down one rule; real-world specs are checked separately through `mcpsense dump-operations`.
/// </summary>
public class OperationModelBuilderTests
{
    private const string Preamble = """
        openapi: 3.0.3
        info:
          title: Test API
          version: 1.0.0

        """;

    /// <summary>
    /// Parses <paramref name="documentBody"/> appended to a minimal preamble and models it.
    /// Fixtures are plain (non-interpolated) raw strings so that YAML braces stay literal.
    /// </summary>
    private static async Task<IReadOnlyList<OperationDescriptor>> BuildAsync(string documentBody)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Preamble + documentBody));
        // Go through OpenApiSpecLoader so the tests exercise the same reader configuration the
        // product uses, including the separately registered YAML reader.
        var loaded = await OpenApiSpecLoader.LoadAsync(stream, "yaml");
        return new OperationModelBuilder().Build(loaded.Document);
    }

    [Fact]
    public async Task UsesOperationIdAsTheName()
    {
        var operations = await BuildAsync("""
            paths:
              /pets:
                get:
                  operationId: listPets
                  responses:
                    '200': { description: ok }
            """);

        var operation = Assert.Single(operations);
        Assert.Equal("listPets", operation.Name);
        Assert.Equal("listPets", operation.OperationId);
        Assert.Equal(HttpMethod.Get, operation.Method);
        Assert.Equal("/pets", operation.PathTemplate);
    }

    [Fact]
    public async Task SynthesizesNameFromMethodAndPathWhenOperationIdIsMissing()
    {
        var operations = await BuildAsync("""
            paths:
              /pets/{petId}/photos:
                get:
                  responses:
                    '200': { description: ok }
            """);

        var operation = Assert.Single(operations);
        Assert.Equal("get_pets_by_petId_photos", operation.Name);
        Assert.Null(operation.OperationId);
    }

    [Fact]
    public async Task NormalizesCharactersInvalidForAToolName()
    {
        var operations = await BuildAsync("""
            paths:
              /pets:
                get:
                  operationId: 'Pets.List v2'
                  responses:
                    '200': { description: ok }
            """);

        var operation = Assert.Single(operations);
        Assert.Equal("Pets_List_v2", operation.Name);
        // The raw operationId is preserved so the original spec value is never lost.
        Assert.Equal("Pets.List v2", operation.OperationId);
    }

    [Fact]
    public async Task DeduplicatesCollidingNames()
    {
        var operations = await BuildAsync("""
            paths:
              /a:
                get:
                  operationId: 'same.name'
                  responses:
                    '200': { description: ok }
              /b:
                get:
                  operationId: 'same-name'
                  responses:
                    '200': { description: ok }
            """);

        // "same.name" sanitizes to "same_name"; "same-name" is already valid and keeps its hyphen,
        // so these two do not collide.
        Assert.Equal(["same_name", "same-name"], operations.Select(o => o.Name));
    }

    [Fact]
    public async Task AppendsADeterministicSuffixWhenNamesTrulyCollide()
    {
        var operations = await BuildAsync("""
            paths:
              /a:
                get:
                  operationId: 'same.name'
                  responses:
                    '200': { description: ok }
              /b:
                get:
                  operationId: 'same name'
                  responses:
                    '200': { description: ok }
            """);

        // Both sanitize to "same_name"; the later one in traversal order gets the suffix.
        Assert.Equal(["same_name", "same_name_2"], operations.Select(o => o.Name));
    }

    [Fact]
    public async Task OrdersOperationsDeterministicallyByPathThenMethod()
    {
        var operations = await BuildAsync("""
            paths:
              /zebra:
                post:
                  responses: { '200': { description: ok } }
                get:
                  responses: { '200': { description: ok } }
              /alpha:
                delete:
                  responses: { '200': { description: ok } }
            """);

        Assert.Equal(
            ["delete_alpha", "get_zebra", "post_zebra"],
            operations.Select(o => o.Name));
    }

    [Fact]
    public async Task MergesPathLevelParametersAndLetsTheOperationWin()
    {
        var operations = await BuildAsync("""
            paths:
              /pets/{petId}:
                parameters:
                  - name: petId
                    in: path
                    required: true
                    schema: { type: string }
                    description: from path item
                  - name: trace
                    in: header
                    schema: { type: string }
                get:
                  operationId: getPet
                  parameters:
                    - name: petId
                      in: path
                      required: true
                      schema: { type: string }
                      description: from operation
                  responses:
                    '200': { description: ok }
            """);

        var operation = Assert.Single(operations);
        Assert.Equal(2, operation.Parameters.Count);

        var petId = operation.Parameters.Single(p => p.Name == "petId");
        Assert.Equal(OperationParameterLocation.Path, petId.Location);
        Assert.Equal("from operation", petId.Description);

        // Path-item-only parameters survive the merge.
        var trace = operation.Parameters.Single(p => p.Name == "trace");
        Assert.Equal(OperationParameterLocation.Header, trace.Location);
        Assert.False(trace.Required);
    }

    [Fact]
    public async Task ReportsValidationFindingsWithoutRejectingTheDocument()
    {
        // A path parameter without `required` trips a validation rule. McpSense still models the
        // operation: production specs routinely fail strict validation while remaining usable, and
        // a proxy must not be stricter than the API vendor.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Preamble + """
            paths:
              /pets/{petId}:
                get:
                  operationId: getPet
                  parameters:
                    - name: petId
                      in: path
                      schema: { type: string }
                  responses:
                    '200': { description: ok }
            """));

        var loaded = await OpenApiSpecLoader.LoadAsync(stream, "yaml");

        Assert.NotEmpty(loaded.Problems);
        var operation = Assert.Single(new OperationModelBuilder().Build(loaded.Document));
        Assert.Equal("getPet", operation.Name);
    }

    [Fact]
    public void TreatsPathParametersAsRequiredForProgrammaticallyBuiltDocuments()
    {
        // A document handed to the builder directly (not read from text) can still carry a path
        // parameter without `required`. The builder normalises it, because the OpenAPI
        // specification says path parameters are always required.
        var document = new OpenApiDocument
        {
            Paths = new OpenApiPaths
            {
                ["/pets/{petId}"] = new OpenApiPathItem
                {
                    Operations = new Dictionary<HttpMethod, OpenApiOperation>
                    {
                        [HttpMethod.Get] = new OpenApiOperation
                        {
                            OperationId = "getPet",
                            Parameters =
                            [
                                new OpenApiParameter
                                {
                                    Name = "petId",
                                    In = ParameterLocation.Path,
                                    Required = false,
                                },
                            ],
                        },
                    },
                },
            },
        };

        var operations = new OperationModelBuilder().Build(document);

        var parameter = Assert.Single(Assert.Single(operations).Parameters);
        Assert.Equal(OperationParameterLocation.Path, parameter.Location);
        Assert.True(parameter.Required);
    }

    [Fact]
    public async Task PrefersJsonWhenTheRequestBodyOffersSeveralMediaTypes()
    {
        var operations = await BuildAsync("""
            paths:
              /pets:
                post:
                  operationId: createPet
                  requestBody:
                    required: true
                    content:
                      application/x-www-form-urlencoded:
                        schema: { type: object }
                      application/json:
                        schema: { type: object }
                      text/plain:
                        schema: { type: string }
                  responses:
                    '201': { description: created }
            """);

        var body = Assert.Single(operations).RequestBody;
        Assert.NotNull(body);
        Assert.Equal("application/json", body.ContentType);
        Assert.True(body.Required);
    }

    [Fact]
    public async Task FallsBackToAJsonSuffixedMediaType()
    {
        var operations = await BuildAsync("""
            paths:
              /pets:
                post:
                  operationId: createPet
                  requestBody:
                    content:
                      text/plain:
                        schema: { type: string }
                      application/merge-patch+json:
                        schema: { type: object }
                  responses:
                    '201': { description: created }
            """);

        var body = Assert.Single(operations).RequestBody;
        Assert.NotNull(body);
        Assert.Equal("application/merge-patch+json", body.ContentType);
        Assert.False(body.Required);
    }

    [Fact]
    public async Task InheritsDocumentLevelSecurity()
    {
        var operations = await BuildAsync("""
            components:
              securitySchemes:
                bearerAuth:
                  type: http
                  scheme: bearer
            security:
              - bearerAuth: []
            paths:
              /pets:
                get:
                  operationId: listPets
                  responses:
                    '200': { description: ok }
            """);

        var requirement = Assert.Single(Assert.Single(operations).Security);
        var scheme = Assert.Single(requirement.Schemes);
        Assert.Equal("bearerAuth", scheme.SchemeName);
        Assert.Empty(scheme.Scopes);
    }

    [Fact]
    public async Task EmptyOperationSecurityOverridesTheDocumentDefault()
    {
        var operations = await BuildAsync("""
            components:
              securitySchemes:
                bearerAuth:
                  type: http
                  scheme: bearer
            security:
              - bearerAuth: []
            paths:
              /health:
                get:
                  operationId: health
                  security: []
                  responses:
                    '200': { description: ok }
            """);

        // `security: []` means explicitly unauthenticated, not "inherit the document default".
        Assert.Empty(Assert.Single(operations).Security);
    }

    [Fact]
    public async Task CapturesScopesAndTreatsSeparateRequirementsAsAlternatives()
    {
        var operations = await BuildAsync("""
            components:
              securitySchemes:
                oauth:
                  type: oauth2
                  flows:
                    clientCredentials:
                      tokenUrl: https://example.test/token
                      scopes:
                        read: read access
                        write: write access
                apiKey:
                  type: apiKey
                  name: X-Api-Key
                  in: header
            paths:
              /pets:
                get:
                  operationId: listPets
                  security:
                    - oauth: [read, write]
                    - apiKey: []
                  responses:
                    '200': { description: ok }
            """);

        var security = Assert.Single(operations).Security;
        Assert.Equal(2, security.Count);
        Assert.Equal(["read", "write"], Assert.Single(security[0].Schemes).Scopes);
        Assert.Equal("apiKey", Assert.Single(security[1].Schemes).SchemeName);
    }

    [Fact]
    public async Task CarriesTagsSummaryDescriptionAndDeprecation()
    {
        var operations = await BuildAsync("""
            paths:
              /pets:
                get:
                  operationId: listPets
                  summary: List pets
                  description: Returns every pet.
                  deprecated: true
                  tags: [pets, legacy]
                  responses:
                    '200': { description: ok }
            """);

        var operation = Assert.Single(operations);
        Assert.Equal("List pets", operation.Summary);
        Assert.Equal("Returns every pet.", operation.Description);
        Assert.True(operation.Deprecated);
        Assert.Equal(["pets", "legacy"], operation.Tags);
    }

    [Fact]
    public async Task ReturnsEmptyForADocumentWithoutPaths()
    {
        var operations = await BuildAsync(string.Empty);
        Assert.Empty(operations);
    }
}
