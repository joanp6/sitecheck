using System.Net;
using System.Text.Json;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Watching;

namespace SiteCheck.Core.Tests.Watching;

public sealed class TelegramNotifierTests
{
    private const string Token = "123:secret-token";

    private sealed class Captured
    {
        public Uri? Url { get; set; }

        public JsonDocument? Body { get; set; }
    }

    private static (StubHttpMessageHandler Handler, Captured Captured) Telegram(HttpStatusCode status = HttpStatusCode.OK, string reply = """{"ok":true}""")
    {
        var captured = new Captured();
        var handler = StubHttpMessageHandler.Routing(request =>
        {
            captured.Url = request.RequestUri;
            captured.Body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(status) { Content = new StringContent(reply) };
        });

        return (handler, captured);
    }

    [Fact]
    public async Task SendAsync_PostsTheTextToTheChatThroughTheBot()
    {
        var (handler, captured) = Telegram();
        using var client = new HttpClient(handler);

        await new TelegramNotifier(client, Token, " 42 ").SendAsync("Àngela: PASS → WARN", TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://api.telegram.org/bot123:secret-token/sendMessage"), captured.Url);
        Assert.Equal("42", captured.Body!.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("Àngela: PASS → WARN", captured.Body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_CutsAMessageTelegramWouldRejectAsTooLong()
    {
        var (handler, captured) = Telegram();
        using var client = new HttpClient(handler);

        await new TelegramNotifier(client, Token, "42").SendAsync(new string('x', 10_000), TestContext.Current.CancellationToken);

        var sent = captured.Body!.RootElement.GetProperty("text").GetString()!;
        Assert.Equal(TelegramNotifier.MaxLength, sent.Length);
        Assert.EndsWith("run log)", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenTelegramRefuses_ThrowsWithItsReasonButNotTheToken()
    {
        // The exception ends up in a CI log that may be public. The token in the address must not.
        var (handler, _) = Telegram(HttpStatusCode.BadRequest, """{"ok":false,"description":"chat not found"}""");
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => new TelegramNotifier(client, Token, "42").SendAsync("hi", TestContext.Current.CancellationToken));

        Assert.Contains("chat not found", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "42")]
    [InlineData("token", " ")]
    public void Constructor_RejectsMissingSettings(string token, string chatId)
    {
        using var client = new HttpClient();

        Assert.ThrowsAny<ArgumentException>(() => new TelegramNotifier(client, token, chatId));
    }
}
