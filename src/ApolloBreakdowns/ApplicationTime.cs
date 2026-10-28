namespace ApolloBreakdowns;

public static class ApplicationTime
{
    public static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    public static DateTimeOffset InKyiv(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Kyiv);
}
