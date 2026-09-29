using System.Net.Http.Json;

namespace SiteCheck.Watching;

/// <summary>
/// Sends a message to a Telegram chat through a bot.
/// </summary>
/// <remarks>
/// Telegram rather than email because a bot needs no mail server, no password and no account
/// with a sending service: a token from @BotFather and the chat's id. docs/watch.md walks through it.
/// </remarks>
public sealed class TelegramNotifier
{
    /// <summary>Telegram rejects longer messages outright; a truncated alert beats none.</summary>
    public const int MaxLength = 4096;

    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _chatId;

    public TelegramNotifier(HttpClient httpClient, string token, string chatId)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        _httpClient = httpClient;
        _token = token.Trim();
        _chatId = chatId.Trim();
    }

    /// <exception cref="HttpRequestException">
    /// Telegram refused the message. The message deliberately leaves out the request address, which
    /// contains the bot token and would otherwise end up in a public CI log.
    /// </exception>
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > MaxLength)
        {
            const string cut = "\n…(cut: see the full report in the run log)";
            text = text[..(MaxLength - cut.Length)] + cut;
        }

        using var response = await _httpClient
            .PostAsJsonAsync(
                new Uri($"https://api.telegram.org/bot{_token}/sendMessage"),
                new { chat_id = _chatId, text, disable_web_page_preview = true },
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Telegram refused the message ({(int)response.StatusCode}): {reason}", inner: null, response.StatusCode);
        }
    }
}
