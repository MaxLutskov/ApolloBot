using System.Text;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Domain;

public static class HistoryExport
{
    public static async Task<byte[]> BuildAsync(ApolloDbContext db, CancellationToken ct)
    {
        var result = new StringBuilder("\uFEFF");
        void Row(params string[] cells) => result.AppendLine(string.Join(';', cells.Select(Endpoints.Csv)));
        Row("№", "Зареєстровано (UTC)", "Дільниця", "Зміна", "Працівник", "Комбайн", "Частина", "Вузол", "Несправність", "Опис", "Виконання ремонту", "Резервне APOLLO", "Почато ремонт (UTC)", "Ремонт почав (Telegram ID)", "Відновлено (UTC)", "Статус", "Простій, хв", "Ремонт, сек", "Відновив (Telegram ID)");
        var now = DateTimeOffset.UtcNow;
        await foreach (var r in db.Breakdowns.AsNoTracking().OrderByDescending(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            Row(r.Id.ToString(), r.CreatedAt.ToString("O"), r.Site, r.Shift, r.Person, r.Machine, r.Part, r.Node, r.Fault, r.Description, r.RepairMethod,
                r.HasReserveApollo is bool reserve ? (reserve ? "Так" : "Ні") : "", r.RepairStartedAt?.ToString("O") ?? "", r.RepairStartedByUserId?.ToString() ?? "",
                r.RestoredAt?.ToString("O") ?? "", r.RestoredAt is not null ? "Роботу відновлено" : r.RepairStartedAt is not null ? "Ремонт триває" : "Очікує вирішення",
                (BreakdownService.DowntimeSeconds(r, now) / 60m).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("uk-UA")), BreakdownService.RepairSeconds(r, now)?.ToString() ?? "", r.RestoredByUserId?.ToString() ?? "");
            if (result.Length > 12_000_000) throw new RuleException("Журнал завеликий для одного CSV-файлу.");
        }
        return Encoding.UTF8.GetBytes(result.ToString());
    }
}
