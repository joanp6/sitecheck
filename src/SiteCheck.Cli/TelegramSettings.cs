using SiteCheck.Watching;

namespace SiteCheck.Cli;

/// <summary>
/// Reads the Telegram settings for <c>watch</c> from the environment.
/// </summary>
/// <remarks>
/// Environment variables rather than options: a bot token on the command line shows up in the
/// process list and in shell history, and in CI both values come from secrets anyway.
/// </remarks>
internal sealed record TelegramSettings(TelegramNotifier? Notifier, string? Problem)
{
    public const string TokenVariable = "SITECHECK_TELEGRAM_TOKEN";
    public const string ChatIdVariable = "SITECHECK_TELEGRAM_CHAT_ID";

    // One client for the life of the process, as HttpClient is meant to be used.
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static TelegramSettings FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var chatId = Environment.GetEnvironmentVariable(ChatIdVariable);

        return (string.IsNullOrWhiteSpace(token), string.IsNullOrWhiteSpace(chatId)) switch
        {
            (true, true) => new(null, null),
            (false, false) => new(new TelegramNotifier(Client, token!, chatId!), null),

            // Half configured is a mistake, not a choice: running on silently would mean no alerts
            // and nobody finding out why until something breaks unannounced.
            _ => new(null, $"Set both {TokenVariable} and {ChatIdVariable} to send Telegram messages, or neither."),
        };
    }
}
