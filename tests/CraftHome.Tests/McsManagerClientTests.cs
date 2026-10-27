using System.Net;
using System.Text;
using CraftHome.Web.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace CraftHome.Tests;

public class McsManagerClientTests
{
    private static readonly ConnectionSettings Settings = new(false, "http://localhost:23333", "key&with=special+chars", "node-1", "instance-1");

    [Fact]
    public async Task BusinessForbiddenInsideHttp200IsNotSuccess()
    {
        var client = Client("{\"status\":403,\"data\":\"private upstream detail\"}");
        var error = await Assert.ThrowsAsync<EngineException>(() => client.ControlAsync(Settings, "start"));
        Assert.Contains("拒绝访问", error.Message);
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
        Assert.DoesNotContain("private upstream detail", error.Message);
    }

    [Fact]
    public async Task ControlUsesPostAndKeepsCredentialOutOfUrl()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/protected_instance/open", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.False(query.ContainsKey("apikey"));
            Assert.Equal(Settings.ApiKey, request.Headers.GetValues("x-request-api-key").Single());
            Assert.Equal(Settings.InstanceId, query["uuid"]);
            Assert.Equal(Settings.DaemonId, query["daemonId"]);
            Assert.Equal("XMLHttpRequest", request.Headers.GetValues("X-Requested-With").Single());
            return Response("{\"status\":200,\"data\":true}");
        });
        await new McsManagerClient(new HttpClient(handler)).ControlAsync(Settings, "start");
    }

    [Fact]
    public async Task RunningProcessDoesNotImplyGameReady()
    {
        var client = Client("{\"status\":200,\"data\":{\"status\":3,\"info\":{\"mcPingOnline\":false},\"processInfo\":{\"memory\":1048576}}}");
        var state = await client.GetSnapshotAsync(Settings);
        Assert.Equal("进程运行中", state.StatusText);
        Assert.False(state.GameResponding);
        Assert.Equal(1048576, state.MemoryBytes);
    }

    [Theory]
    [InlineData("<html>proxy error</html>")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"status\":200}")]
    [InlineData("{\"status\":200,\"data\":false}")]
    [InlineData("{\"status\":\"200\",\"data\":{}}")]
    [InlineData("[]")]
    public async Task InvalidResponseFailsWithSafeMessage(string body)
    {
        var error = await Assert.ThrowsAsync<EngineException>(() => Client(body).GetSnapshotAsync(Settings));
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
    }

    [Fact]
    public async Task UnsupportedActionDoesNotReachEngine()
    {
        var client = new McsManagerClient(new HttpClient(new StubHandler(_ => throw new Exception("Must not send"))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ControlAsync(Settings, "kill"));
    }

    [Fact]
    public async Task LogsAreRequestedWithBoundedSize()
    {
        var client = new McsManagerClient(new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("16KB", QueryHelpers.ParseQuery(request.RequestUri!.Query)["size"]);
            return Response("{\"status\":200,\"data\":\"<script>not markup</script>\"}");
        })));
        Assert.Equal("<script>not markup</script>", await client.GetLogsAsync(Settings));
    }

    private static McsManagerClient Client(string body) => new(new HttpClient(new StubHandler(_ => Response(body))));

    [Fact]
    public async Task LogsStripTerminalControlSequencesForPlainTextDisplay()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { status = 200, data = "\u001b[33;1mWARN\u001b[m\r\n\u001b[?2004h\u001b=next" });
        Assert.Equal("WARN\nnext", await Client(body).GetLogsAsync(Settings));
    }
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
