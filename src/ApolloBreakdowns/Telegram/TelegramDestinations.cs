using ApolloBreakdowns.Domain;

namespace ApolloBreakdowns.Telegram;

public static class TelegramDestinations
{
    public static long Group(IConfiguration config, bool test)
    {
        long.TryParse(config[test ? "Telegram:TestGroupChatId" : "Telegram:GroupChatId"], out var chat);
        if (chat >= 0) return 0;
        if (test && long.TryParse(config["Telegram:GroupChatId"], out var live) && chat == live) return 0;
        return chat;
    }

    public static long RequireTestGroup(IConfiguration config) => Group(config, true) is < 0 and var chat ? chat :
        throw new RuleException("Вкажіть Telegram__TestGroupChatId — від’ємний ID тестової групи, відмінний від робочої.");
}
