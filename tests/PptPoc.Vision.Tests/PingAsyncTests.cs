using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PptPoc.Core.Configuration;
using PptPoc.Vision;

namespace PptPoc.Vision.Tests;

/// <summary>
/// Unit tests for <see cref="OpenAIVisionService.PingAsync"/>.
///
/// Strategy: each test injects a <see cref="FakeHttpMessageHandler"/> via the
/// internal test constructor so no real network call is ever made.
/// Environment variable GNAI_TOKEN is isolated per-test using <see cref="TokenScope"/>.
/// </summary>
public class PingAsyncTests
{
    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PingAsync_ReturnsTrue_WhenApiReturns200()
    {
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task PingAsync_ReturnsTrue_WhenApiReturns201()
    {
        // Any 2xx code should be treated as success
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.True(result);
    }

    // ── Auth / HTTP error paths ───────────────────────────────────────────────

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenApiReturns401()
    {
        using var _ = new TokenScope("bad-token");
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid token\"}", Encoding.UTF8, "application/json")
            });
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenApiReturns403()
    {
        using var _ = new TokenScope("forbidden-token");
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":\"forbidden\"}", Encoding.UTF8, "application/json")
            });
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenApiReturns502_BadGateway()
    {
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("Bad Gateway", Encoding.UTF8, "text/plain")
            });
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
    }

    // ── Network / timeout paths ───────────────────────────────────────────────

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenHttpRequestExceptionThrown()
    {
        // Simulates DNS failure, refused connection, proxy unreachable
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler((_, __) =>
            throw new HttpRequestException("No such host is known"));
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenRequestTimesOut()
    {
        // Simulates a 10-second timeout by throwing TaskCanceledException
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler((_, ct) =>
            throw new TaskCanceledException("Simulated timeout"));
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
    }

    // ── Token guard paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenTokenIsNotSet()
    {
        // No TokenScope — token is absent entirely. Handler should never be called.
        using var _ = new TokenScope(null);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
        Assert.Null(handler.LastRequest); // Confirm no network call was made
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenTokenIsWhitespace()
    {
        using var _ = new TokenScope("   ");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = MakeService(handler);

        var result = await svc.PingAsync();

        Assert.False(result);
        Assert.Null(handler.LastRequest);
    }

    // ── Request shape verification ────────────────────────────────────────────

    [Fact]
    public async Task PingAsync_SendsToCorrectOpenAIEndpoint()
    {
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = MakeService(handler, baseUrl: "https://api.test.internal/openai/v1");

        await svc.PingAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://api.test.internal/openai/v1/chat/completions",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task PingAsync_SendsToCorrectAnthropicEndpoint()
    {
        using var _ = new TokenScope("test-token-valid");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Anthropic provider with the /providers/openai/ → /providers/anthropic/ URL swap
        var svc = MakeService(handler,
            baseUrl: "https://api.test.internal/providers/openai/v1",
            provider: "anthropic");

        await svc.PingAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://api.test.internal/providers/anthropic/v1/messages",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task PingAsync_SendsBearerTokenInAuthorizationHeader()
    {
        const string myToken = "my-super-secret-token";
        using var _ = new TokenScope(myToken);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = MakeService(handler);

        await svc.PingAsync();

        Assert.NotNull(handler.LastRequest);
        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(myToken, auth.Parameter);
    }

    [Fact]
    public async Task PingAsync_SendsMinimalPayload_MaxTokensIsOne()
    {
        using var _ = new TokenScope("test-token-valid");
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var svc = MakeService(handler);

        await svc.PingAsync();

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(1, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OpenAIVisionService MakeService(
        FakeHttpMessageHandler handler,
        string baseUrl = "https://api.test.internal/openai/v1",
        string provider = "openai",
        string model = "gpt-4o")
    {
        var config = new AppConfig
        {
            OpenAIBaseUrl = baseUrl,
            VisionProvider = provider,
            OpenAIModel = model
        };
        return new OpenAIVisionService(config, new HttpClient(handler));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test Infrastructure
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Sets GNAI_TOKEN to a given value for the duration of a using-block, then
/// restores the original value. Prevents tests from leaking env-var state.
/// </summary>
internal sealed class TokenScope : IDisposable
{
    private readonly string? _originalValue;

    public TokenScope(string? value)
    {
        _originalValue = Environment.GetEnvironmentVariable("GNAI_TOKEN");
        Environment.SetEnvironmentVariable("GNAI_TOKEN", value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GNAI_TOKEN", _originalValue);
    }
}

/// <summary>
/// A minimal <see cref="HttpMessageHandler"/> that delegates to a caller-supplied
/// function. Captures the last request so tests can assert on URL, headers, and body.
/// Two constructor overloads:
///   1. <c>Func&lt;HttpRequestMessage, HttpResponseMessage&gt;</c> — for normal responses.
///   2. <c>Func&lt;HttpRequestMessage, CancellationToken, Task&lt;HttpResponseMessage&gt;&gt;</c>
///      — for exception-throwing scenarios (timeout, network errors).
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    /// <summary>Sync convenience overload — wraps the function in a completed Task.</summary>
    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _send = (req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(handler(req));
        };
    }

    /// <summary>
    /// Async overload — use when the handler needs to throw exceptions
    /// (e.g. <see cref="TaskCanceledException"/>, <see cref="HttpRequestException"/>).
    /// </summary>
    public FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _send = handler;
    }

    /// <summary>The last request received. Null if SendAsync was never called.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return _send(request, cancellationToken);
    }
}
