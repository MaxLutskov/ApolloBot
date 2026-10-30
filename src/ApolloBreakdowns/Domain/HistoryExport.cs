using System.Text;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Domain;

public static class HistoryExport
{
    private static string LocalTime(DateTimeOffset? value) => value is null ? "" :
        ApplicationTime.InKyiv(value.Value).ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<byte[]> BuildAsync(ApolloDbContext db, CancellationToken ct)
    {
        var result = new StringBuilder("\uFEFF");
        void Row(params string[] cells) => result.AppendLine(string.Join(';', cells.Select(Endpoints.Csv)));
        Row("№", "Зареєстровано (Київ)", "Дільниця", "Зміна", "Працівник", "Комбайн", "Частина", "Вузол", "Несправність", "Опис", "Виконання ремонту", "Резервне APOLLO", "Почато ремонт (Київ)", "Ремонт почав (Telegram ID)", "Відновлено (Київ)", "Статус", "Простій машини, хв", "Ремонт, сек", "Відновив (Telegram ID)", "Тип заявки", "Простій процесу, хв", "Процес відновлено (Київ)", "Процес відновлено заявкою №", "Режим");
        var now = DateTimeOffset.UtcNow;
        await foreach (var r in db.Breakdowns.AsNoTracking().OrderByDescending(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            Row(r.Id.ToString(), LocalTime(r.CreatedAt), r.Site, r.Shift, r.Person, r.Machine, r.Part, r.Node, r.Fault, r.Description, r.RepairMethod,
                r.HasReserveApollo is bool reserve ? (reserve ? "Так" : "Ні") : "", LocalTime(r.RepairStartedAt), r.RepairStartedByUserId?.ToString() ?? "",
                LocalTime(r.RestoredAt), r.RestoredAt is not null ? "Роботу відновлено" : r.RepairStartedAt is not null ? "Ремонт триває" : "Очікує вирішення",
                BreakdownService.MachineDowntimeMinutes(r, now).ToString(System.Globalization.CultureInfo.InvariantCulture), BreakdownService.RepairSeconds(r, now)?.ToString() ?? "", r.RestoredByUserId?.ToString() ?? "", BreakdownService.RequestTypeLabel(r.RequestType), BreakdownService.ProcessDowntimeMinutes(r, now).ToString(System.Globalization.CultureInfo.InvariantCulture), LocalTime(r.ProcessRestoredAt), r.ProcessRestoredByBreakdownId?.ToString() ?? "", r.IsTest ? "Тестовий" : "Робочий");
            if (result.Length > 12_000_000) throw new RuleException("Журнал завеликий для одного CSV-файлу.");
        }
        return Encoding.UTF8.GetBytes(result.ToString());
    }
}
