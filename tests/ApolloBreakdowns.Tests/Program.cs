using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using ApolloBreakdowns.Domain;
using ApolloBreakdowns.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine($"PASS {name}"); passed++; }
async Task Reject(Func<Task> action, int status, string name) {
    try { await action(); throw new Exception("Expected rejection: " + name); }
    catch (RuleException e) { Check(e.Status == status, name); }
}
var emptySettings = new ConfigurationBuilder().AddInMemoryCollection().Build();
Check(ApolloBreakdowns.StartupSettings.Normalize(emptySettings) == "Demo", "Missing all settings enables browser test mode");
var minimalSettings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["Telegram:BotToken"] = "test", ["Telegram:PublicUrl"] = "https://example.com", ["Telegram:WebhookSecret"] = "x"
}).Build();
Check(ApolloBreakdowns.StartupSettings.Normalize(minimalSettings) == "Webhook", "Short valid webhook secret, missing chat and admins do not block startup");
minimalSettings["Telegram:WebhookSecret"] = "";
Check(ApolloBreakdowns.StartupSettings.Normalize(minimalSettings) == "Polling", "Missing webhook secret falls back to polling");
minimalSettings["App:TestMode"] = "true";
Check(ApolloBreakdowns.StartupSettings.Normalize(minimalSettings) == "Demo", "Explicit test mode overrides configured Telegram");
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["Telegram:Mode"] = "Demo", ["Telegram:GroupChatId"] = "-100123456", ["Telegram:BotToken"] = "test-token"
}).Build();
await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var options = new DbContextOptionsBuilder<ApolloDbContext>().UseSqlite(connection).Options;
await using var db = new ApolloDbContext(options);
await db.Database.EnsureCreatedAsync();
await CatalogSeed.ApplyAsync(db);
var count = await db.Options.CountAsync();
await CatalogSeed.ApplyAsync(db);
Check(count == 176 && await db.Options.CountAsync() == count, "Workbook catalog imports once, 176 options");
var expectedSites = Enumerable.Range(1, 14).Concat(new[] { 22, 42, 43, 47, 49 }).Select(n => $"ВДВП №{n}");
Check((await db.Options.Where(x => x.Kind == "site" && x.Active).OrderBy(x => x.SortOrder).Select(x => x.Label).ToListAsync()).SequenceEqual(expectedSites), "Site list is exactly 1–14, 22, 42, 43, 47, 49");
var catalog = await db.Options.ToListAsync();
int Id(string kind) => catalog.First(x => x.Kind == kind).Id;
var part = catalog.First(x => x.Kind == "part" && x.Label == "Жатка");
var node = catalog.First(x => x.Kind == "node" && x.ParentId == part.Id);
var fault = catalog.First(x => x.Kind == "fault" && x.ParentId == node.Id && !x.RequiresDescription);
var self = catalog.Single(x => x.Kind == "repairMethod" && !x.RequiresReserve);
var assisted = catalog.First(x => x.Kind == "repairMethod" && x.RequiresReserve);
var input = new SubmitBreakdown(Guid.NewGuid(), Id("site"), Id("shift"), "Тестовий працівник", Id("machine"), part.Id, node.Id, fault.Id, "", self.Id, null);
var owner = new AppUser(22, "Автор", false);
var other = new AppUser(33, "Учасник групи", false);
var service = new BreakdownService(db, config);
var report = await service.SubmitAsync(input, owner, default);
Check(report.Id > 0 && report.RestoredAt is null, "Submitting starts an open downtime using server time");
Check(await db.Notifications.CountAsync() == 2 && await db.Notifications.AnyAsync(x => x.ChatId == -100123456 && x.Kind == "new"), "Author and APOLLO group get durable notifications");
Check((await service.SubmitAsync(input, owner, default)).Id == report.Id && await db.Breakdowns.CountAsync() == 1, "Retry does not duplicate report or notifications");
await Reject(() => service.SubmitAsync(input with { Person = "Інша особа" }, owner, default), 409, "Idempotency rejects different payload with same key");
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id }, owner, default), 400, "Assisted repair requires reserve answer");
var withReserve = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id, HasReserveApollo = false }, owner, default);
Check(withReserve.HasReserveApollo == false, "No reserve is preserved as false, not missing");
var otherNode = catalog.First(x => x.Kind == "node" && x.ParentId != part.Id);
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid(), NodeId = otherNode.Id }, owner, default), 400, "Server rejects mismatched cascade");
var otherFault = catalog.First(x => x.Kind == "fault" && x.ParentId == node.Id && x.RequiresDescription);
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid(), FaultId = otherFault.Id }, owner, default), 400, "Other fault requires description");
fault.Label = "Перейменована несправність"; await db.SaveChangesAsync();
Check(report.Fault != fault.Label, "Saved labels survive catalog renaming");
fault.Active = false; await db.SaveChangesAsync();
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, owner, default), 409, "Disabled choices are rejected");
await Reject(() => service.RestoreAsync(report.Id, other, default), 403, "Unrelated private user cannot stop downtime");
report.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15).AddSeconds(-10); await db.SaveChangesAsync();
var closed = await service.RestoreAsync(report.Id, other, default, fromConfiguredGroup: true);
Check(closed.RestoredAt is not null && closed.RestoredByUserId == other.Id, "Configured group member can restore work");
Check(BreakdownService.FormatDuration(BreakdownService.DowntimeSeconds(closed, DateTimeOffset.UtcNow)) == "15 хв", "Duration is server-calculated from registration to restoration");
Check(await db.Notifications.CountAsync(x => x.BreakdownId == report.Id && x.Kind == "restored") == 2, "Restoration queues author/group completion messages");
var finishedAt = closed.RestoredAt;
await service.RestoreAsync(report.Id, owner, default);
Check(closed.RestoredAt == finishedAt && await db.Notifications.CountAsync(x => x.BreakdownId == report.Id && x.Kind == "restored") == 2, "Repeated restoration neither changes time nor queues duplicates");
Check(BreakdownService.DowntimeSeconds(closed, DateTimeOffset.UtcNow.AddHours(2)) == BreakdownService.DowntimeSeconds(closed, DateTimeOffset.UtcNow), "Finished downtime no longer grows");
// Stale DbContext simulates concurrent restore requests across Azure instances.
await using var stale = new ApolloDbContext(options);
await stale.Breakdowns.SingleAsync(x => x.Id == withReserve.Id);
await service.RestoreAsync(withReserve.Id, owner, default);
var fromStale = await new BreakdownService(stale, config).RestoreAsync(withReserve.Id, owner, default);
Check(fromStale.RestoredAt == withReserve.RestoredAt && await db.Notifications.CountAsync(x => x.BreakdownId == withReserve.Id && x.Kind == "restored") == 2, "Optimistic concurrency preserves first closure and one completion event");

var now = DateTimeOffset.UtcNow;
string Signed(long seconds, string token = "test-token") {
    var fields = new SortedDictionary<string, string>(StringComparer.Ordinal) {
        ["auth_date"] = seconds.ToString(), ["query_id"] = "q1", ["user"] = "{\"id\":22,\"first_name\":\"Operator\"}"
    };
    var check = string.Join('\n', fields.Select(x => $"{x.Key}={x.Value}"));
    var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(token));
    var hash = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(check)));
    return string.Join('&', fields.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}")) + "&hash=" + hash;
}
var signed = Signed(now.ToUnixTimeSeconds());
Check(TelegramAuth.Validate(signed, "test-token", now, TimeSpan.FromHours(24))?.Id == 22, "Valid Telegram signature accepted");
Check(TelegramAuth.Validate(signed, "wrong", now, TimeSpan.FromHours(24)) is null, "Wrong bot token rejected");
Check(TelegramAuth.Validate(signed + "&auth_date=1", "test-token", now, TimeSpan.FromHours(24)) is null, "Duplicate auth parameter rejected");
Check(TelegramAuth.Validate(Signed(now.AddDays(-2).ToUnixTimeSeconds()), "test-token", now, TimeSpan.FromHours(24)) is null, "Expired initData rejected");
Check(TelegramAuth.Validate(Signed(now.AddHours(1).ToUnixTimeSeconds()), "test-token", now, TimeSpan.FromHours(24)) is null, "Future initData rejected");
Check(Endpoints.Csv(" =SUM(A1)").StartsWith("\"'"), "CSV formula injection escaped");

// Exercise real Bot API payloads against a recording transport; no Telegram messages sent.
var handler = new RecordingHandler();
var telegram = new TelegramClient(new HttpClient(handler), config, service);
await telegram.SendNotificationAsync(-100123456, "Тест", report.Id, true, default);
Check(handler.Calls.Last().Json.Contains("restore:" + report.Id) && handler.Calls.Last().Method == "sendMessage", "Group notification carries report-specific restore button");
fault.Active = true; await db.SaveChangesAsync();
var callbackReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, owner, default);
using var update = JsonDocument.Parse(JsonSerializer.Serialize(new { callback_query = new {
    id = "callback1", from = new { id = 33, first_name = "Member", is_bot = false }, data = $"restore:{callbackReport.Id}",
    message = new { message_id = 123, chat = new { id = -100123456L, type = "supergroup" } }
} }));
await telegram.HandleUpdateAsync(update.RootElement, default);
Check(callbackReport.RestoredAt is not null && handler.Calls.Any(x => x.Method == "answerCallbackQuery"), "Group callback closes report and acknowledges button");
Check(handler.Calls.Any(x => x.Method == "editMessageReplyMarkup"), "Callback removes completed button");
Console.WriteLine($"{passed} checks passed.");

sealed class RecordingHandler : HttpMessageHandler
{
    public List<(string Method, string Json)> Calls { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        Calls.Add((request.RequestUri!.Segments.Last(), await request.Content!.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json") };
    }
}
