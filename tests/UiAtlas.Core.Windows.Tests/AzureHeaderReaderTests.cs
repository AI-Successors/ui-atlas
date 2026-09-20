using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AzureHeaderReaderTests
{
    private static readonly AzureHeaderSettings Settings = new(true, "https://example.openai.azure.com/openai/v1/", "sol-headers");
    private static readonly GridHeaderImage[] Headers = [new("column-0", [1, 2, 3]), new("column-1", [4, 5, 6])];

    [Fact]
    public async Task UsesAzureResponsesLightProfileAndFixedColumnKeys()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("https://example.openai.azure.com/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-secret", Assert.Single(request.Headers.GetValues("api-key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = json.RootElement;
            Assert.Equal("sol-headers", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            var content = root.GetProperty("input")[0].GetProperty("content");
            Assert.Equal(4, content.GetArrayLength());
            Assert.Equal("Column key: column-0", content[0].GetProperty("text").GetString());
            Assert.Equal("data:image/png;base64,AQID", content[1].GetProperty("image_url").GetString());
            Assert.Equal("original", content[1].GetProperty("detail").GetString());
            var format = root.GetProperty("text").GetProperty("format"); Assert.True(format.GetProperty("strict").GetBoolean());
            Assert.Equal(new[] { "column-0", "column-1" }, format.GetProperty("schema").GetProperty("properties").GetProperty("headers")
                .GetProperty("items").GetProperty("properties").GetProperty("key").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
            return Response("""{"headers":[{"key":"column-1","status":"text","text":"Code"},{"key":"column-0","status":"text","text":"Code"}]}""");
        });
        var readings = await new AzureOpenAiHeaderReader(Settings, "test-secret", client).ReadAsync(Headers, default);
        Assert.Equal(Headers.Select(h => h.ColumnKey), readings.Select(r => r.ColumnKey));
        Assert.All(readings, r => Assert.Equal("Code", r.Text)); // Literal duplicates remain distinct physical columns.
    }

    [Theory]
    [InlineData("{\"headers\":[]}")]
    [InlineData("{\"headers\":[{\"key\":\"column-0\",\"status\":\"text\",\"text\":\"Code\"},{\"key\":\"column-0\",\"status\":\"text\",\"text\":\"Code\"}]}")]
    [InlineData("{\"headers\":[{\"key\":\"column-0\",\"status\":\"text\",\"text\":\"\"},{\"key\":\"column-1\",\"status\":\"empty\",\"text\":\"\"}]}")]
    [InlineData("{\"headers\":[{\"key\":\"column-0\",\"status\":\"unreadable\",\"text\":\"Guessed\"},{\"key\":\"column-1\",\"status\":\"empty\",\"text\":\"\"}]}")]
    [InlineData("{\"headers\":[{\"key\":\"invented\",\"status\":\"text\",\"text\":\"Code\"},{\"key\":\"column-1\",\"status\":\"empty\",\"text\":\"\"}]}")]
    [InlineData("{\"headers\":[null,null]}")]
    public async Task RejectsIncompleteDuplicateOrInventedReadings(string structured)
    {
        using var client = Client(_ => Task.FromResult(Response(structured)));
        await Assert.ThrowsAsync<AzureHeaderException>(() => new AzureOpenAiHeaderReader(Settings, "test-secret", client).ReadAsync(Headers, default));
    }

    [Fact]
    public async Task BlankAndUnreadableHeadersAreExplicit()
    {
        using var client = Client(_ => Task.FromResult(Response("""{"headers":[{"key":"column-0","status":"empty","text":""},{"key":"column-1","status":"unreadable","text":null}]}""")));
        var result = await new AzureOpenAiHeaderReader(Settings, "test-secret", client).ReadAsync(Headers, default);
        Assert.Equal(GridCellReadStatus.Empty, result[0].Status); Assert.Equal("", result[0].Text);
        Assert.Equal(GridCellReadStatus.Unreadable, result[1].Status); Assert.Null(result[1].Text);
    }

    [Fact]
    public async Task CellRequestsUseFixedPhysicalKeysAndLiteralTranscription()
    {
        var cells = Enumerable.Range(0, 64).Select(i => new GridHeaderImage("r0-c" + i, [1, 2, 3])).ToArray();
        var requests = 0;
        using var client = Client(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Contains("cropped table cell literally", json.RootElement.GetProperty("instructions").GetString());
            requests++;
            var content = json.RootElement.GetProperty("input")[0].GetProperty("content");
            Assert.Equal(64, content.GetArrayLength());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            var keys = content.EnumerateArray().Where(c => c.GetProperty("type").GetString() == "input_text")
                .Select(c => c.GetProperty("text").GetString()!["Column key: ".Length..]);
            return Response(JsonSerializer.Serialize(new { headers = keys.Reverse().Select(key => new { key, status = "text", text = "00123" }) }));
        });
        var readings = await new AzureOpenAiHeaderReader(Settings, "test-secret", client).ReadCellsAsync(cells, default);
        Assert.Equal(cells.Select(c => c.ColumnKey), readings.Select(r => r.ColumnKey));
        Assert.All(readings, r => Assert.Equal("00123", r.Text));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("{\"status\":123}")]
    [InlineData("{\"status\":\"incomplete\"}")]
    [InlineData("{\"status\":\"completed\",\"model\":\"other-model\",\"output\":[]}")]
    [InlineData("{\"status\":\"completed\",\"model\":\"gpt-5.6-sol\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"test-secret\"}]}]}")]
    [InlineData("not json: test-secret")]
    public async Task MalformedRefusedIncompleteAndWrongModelResponsesAreNotAccepted(string json)
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var ex = await Assert.ThrowsAsync<AzureHeaderException>(() => new AzureOpenAiHeaderReader(Settings, "test-secret", client).ReadAsync(Headers, default));
        Assert.DoesNotContain("test-secret", ex.ToString());
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)] [InlineData(500)]
    public async Task HttpFailuresDoNotExposeBodiesOrCredentials(int status)
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("test-secret and image data") }));
        var ex = await Assert.ThrowsAsync<AzureHeaderException>(() => new AzureOpenAiHeaderReader(Settings, "test-secret", client).ReadAsync(Headers, default));
        Assert.DoesNotContain("test-secret", ex.ToString()); Assert.DoesNotContain("image data", ex.ToString());
    }

    [Fact]
    public async Task CancellationAndLaterBatchFailureCannotReturnPartialNames()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelled = new HttpClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AzureOpenAiHeaderReader(Settings, "test-secret", cancelled).ReadAsync(Headers, cancellation.Token));
        var batch = 0;
        using var client = Client(_ =>
        {
            batch++;
            return Task.FromResult(batch == 1 ? Response(JsonSerializer.Serialize(new { headers = Enumerable.Range(0, 16).Select(i => new { key = "column-" + i, status = "text", text = "Name" }) }))
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        await Assert.ThrowsAsync<AzureHeaderException>(() => new AzureOpenAiHeaderReader(Settings, "test-secret", client)
            .ReadAsync(Enumerable.Range(0, 17).Select(i => new GridHeaderImage("column-" + i, [1])).ToArray(), default));
        Assert.Equal(2, batch);
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        { status = "completed", model = "gpt-5.6-sol-2026-07-09", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = json } } } } }), Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler((request, _) => send(request)));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
