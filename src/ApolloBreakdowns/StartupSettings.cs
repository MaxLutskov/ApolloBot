namespace ApolloBreakdowns;

public static class StartupSettings
{
    public static string Normalize(IConfiguration config)
    {
        var mode = config["Telegram:Mode"] ?? "Webhook";
        if (config.GetValue<bool>("App:TestMode") || string.IsNullOrWhiteSpace(config["Telegram:BotToken"]) || mode == "Demo")
            mode = "Demo";
        else
        {
            if (mode is not ("Webhook" or "Polling")) mode = "Polling";
            var https = Uri.TryCreate(config["Telegram:PublicUrl"], UriKind.Absolute, out var url) && url.Scheme == "https";
            var secret = config["Telegram:WebhookSecret"] ?? "";
            if (mode == "Webhook" && (!https || secret.Length is < 1 or > 256 || secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
                mode = "Polling";
        }
        config["Telegram:Mode"] = mode;
        return mode;
    }
}
