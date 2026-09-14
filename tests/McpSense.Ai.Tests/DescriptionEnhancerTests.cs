using System.Net.Http;
using System.Text.Json;
using McpSense.Ai;
using McpSense.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpSense.Ai.Tests;

/// <summary>
/// Covers the opt-in rewriting of names and descriptions, including what happens when the model
/// misbehaves. The guarantee under test throughout is that the spec's own text survives anything
/// going wrong.
/// </summary>
public class DescriptionEnhancerTests
{
    private static OperationDescriptor Operation(
        string name,
        string? summary = null,
        params string[] parameterNames)
        => new()
        {
            Name = name,
            OperationId = name,
            Method = HttpMethod.Get,
            PathTemplate = $"/{name}",
            Summary = summary,
            Deprecated = false,
            Tags = ["things"],
            Parameters = parameterNames
                .Select(parameterName => new OperationParameter
                {
                    Name = parameterName,
                    Location = OperationParameterLocation.Query,
                    Required = false,
                })
                .ToList(),
            Security = [],
        };

    [Fact]
    public async Task RewritesNamesDescriptionsAndParameters()
    {
        var client = new StubChatClient("""
            [{"id":"op_a","name":"list_widgets","description":"Lists every widget.",
              "parameters":{"q":"Free-text filter."}}]
            """);

        var enhanced = await new DescriptionEnhancer(client)
            .EnhanceAsync([Operation("op_a", "List", "q")]);

        var operation = Assert.Single(enhanced);
        Assert.Equal("list_widgets", operation.Name);
        Assert.Equal("Lists every widget.", operation.Description);
        Assert.Equal("Free-text filter.", Assert.Single(operation.Parameters).Description);
    }

    [Fact]
    public async Task KeepsTheSpecNameWhenRenamingIsTurnedOff()
    {
        var client = new StubChatClient("""
            [{"id":"op_a","name":"list_widgets","description":"Lists every widget."}]
            """);

        var enhanced = await new DescriptionEnhancer(client, new DescriptionEnhancerOptions { RewriteNames = false })
            .EnhanceAsync([Operation("op_a", "List")]);

        var operation = Assert.Single(enhanced);
        Assert.Equal("op_a", operation.Name);
        // The description is still rewritten; only the name is left alone.
        Assert.Equal("Lists every widget.", operation.Description);
    }

    [Fact]
    public async Task KeepsTheSpecTextWhenTheModelThrows()
    {
        var client = new StubChatClient(new InvalidOperationException("the model is down"));

        var enhanced = await new DescriptionEnhancer(client)
            .EnhanceAsync([Operation("op_a", "List")]);

        var operation = Assert.Single(enhanced);
        Assert.Equal("op_a", operation.Name);
        Assert.Equal("List", operation.Summary);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"id":"op_a"}""")]
    [InlineData("[]")]
    public async Task KeepsTheSpecTextWhenTheReplyIsUnusable(string reply)
    {
        var client = new StubChatClient(reply);

        var enhanced = await new DescriptionEnhancer(client).EnhanceAsync([Operation("op_a", "List")]);

        Assert.Equal("op_a", Assert.Single(enhanced).Name);
    }

    [Fact]
    public async Task AcceptsAReplyWrappedInACodeFence()
    {
        // Models add a ```json fence even when told not to.
        var client = new StubChatClient("""
            ```json
            [{"id":"op_a","name":"list_widgets","description":"Lists every widget."}]
            ```
            """);

        var enhanced = await new DescriptionEnhancer(client).EnhanceAsync([Operation("op_a")]);

        Assert.Equal("list_widgets", Assert.Single(enhanced).Name);
    }

    [Fact]
    public async Task IgnoresEntriesAndParametersTheModelInvented()
    {
        var client = new StubChatClient("""
            [{"id":"op_a","name":"list_widgets","description":"Lists.",
              "parameters":{"q":"Filter.","imaginary":"Not a real parameter."}},
             {"id":"never_existed","name":"ghost","description":"Nope."}]
            """);

        var enhanced = await new DescriptionEnhancer(client).EnhanceAsync([Operation("op_a", "List", "q")]);

        var operation = Assert.Single(enhanced);
        Assert.Equal(["q"], operation.Parameters.Select(p => p.Name));
        Assert.Equal("Filter.", Assert.Single(operation.Parameters).Description);
    }

    [Fact]
    public async Task KeepsNamesUniqueWhenTheModelRepeatsItself()
    {
        // The catalog is keyed by name, so two operations must never end up sharing one.
        var client = new StubChatClient("""
            [{"id":"op_a","name":"list_widgets","description":"One."},
             {"id":"op_b","name":"list_widgets","description":"Two."}]
            """);

        var enhanced = await new DescriptionEnhancer(client)
            .EnhanceAsync([Operation("op_a"), Operation("op_b")]);

        Assert.Equal(["list_widgets", "list_widgets_2"], enhanced.Select(o => o.Name));
    }

    [Fact]
    public async Task NormalizesNamesTheModelReturnsInAnInvalidShape()
    {
        var client = new StubChatClient("""
            [{"id":"op_a","name":"List Widgets (all)","description":"Lists."}]
            """);

        var enhanced = await new DescriptionEnhancer(client).EnhanceAsync([Operation("op_a")]);

        Assert.Equal("List_Widgets_all", Assert.Single(enhanced).Name);
    }

    [Fact]
    public async Task SendsOneRequestPerBatch()
    {
        var client = new StubChatClient("[]");
        var operations = Enumerable.Range(1, 7).Select(i => Operation($"op{i}")).ToList();

        await new DescriptionEnhancer(client, new DescriptionEnhancerOptions { BatchSize = 3 })
            .EnhanceAsync(operations);

        // 7 operations at 3 per request is 3 requests, not 7.
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task DoesNotCallTheModelForAnEmptySpec()
    {
        var client = new StubChatClient("[]");

        Assert.Empty(await new DescriptionEnhancer(client).EnhanceAsync([]));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ASecondRunOverAnUnchangedSpecDoesNotReachTheModel()
    {
        // This is the whole point of caching: starting the server again must not pay for the
        // rewrite a second time.
        var stub = new StubChatClient("""
            [{"id":"op_a","name":"list_widgets","description":"Lists every widget."}]
            """);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cached = new ChatClientBuilder(stub).UseDistributedCache(cache).Build();

        var operations = new[] { Operation("op_a", "List") };

        var first = await new DescriptionEnhancer(cached).EnhanceAsync(operations);
        Assert.Equal(1, stub.CallCount);

        var second = await new DescriptionEnhancer(cached).EnhanceAsync(operations);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal(first.Single().Name, second.Single().Name);
        Assert.Equal("list_widgets", second.Single().Name);
    }

    [Fact]
    public async Task AChangedSpecDoesReachTheModelAgain()
    {
        var stub = new StubChatClient("""
            [{"id":"op_a","name":"list_widgets","description":"Lists every widget."}]
            """);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cached = new ChatClientBuilder(stub).UseDistributedCache(cache).Build();

        await new DescriptionEnhancer(cached).EnhanceAsync([Operation("op_a", "List")]);
        await new DescriptionEnhancer(cached).EnhanceAsync([Operation("op_a", "List all the things")]);

        Assert.Equal(2, stub.CallCount);
    }

    /// <summary>A chat client that answers with a fixed reply, or throws, and counts calls.</summary>
    private sealed class StubChatClient(object replyOrException) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            if (replyOrException is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, (string)replyOrException)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
