using ApolloBreakdowns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
var invalidBoolean = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["App:TestMode"] = "mistyped", ["Telegram:BotToken"] = "token"
}).Build();
Check(StartupSettings.Normalize(invalidBoolean) == "Polling", "Malformed optional test flag does not crash startup");
var probe = new FailureProbe();
var hostBuilder = Host.CreateApplicationBuilder();
hostBuilder.Services.AddHostedService(_ => probe);
using (var host = hostBuilder.Build())
{
    await host.StartAsync();
    await probe.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(probe.Attempts == 2 && !host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested,
        "Unexpected background failure is retried and does not stop the host");
    await host.StopAsync();
    Check(probe.ExecuteTask!.IsCompletedSuccessfully, "Background shutdown cancellation completes cleanly");
}
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
Check(count >= 176 && await db.Options.CountAsync() == count, "Workbook catalog and Other entries import idempotently");
var expectedSites = Enumerable.Range(1, 14).Concat(new[] { 22, 42, 43, 47, 49 }).Select(n => $"ВДВП №{n}");
Check((await db.Options.Where(x => x.Kind == "site" && x.Active && !x.IsOther).OrderBy(x => x.SortOrder).Select(x => x.Label).ToListAsync()).SequenceEqual(expectedSites), "Site list is exactly 1–14, 22, 42, 43, 47, 49");
var catalog = await db.Options.ToListAsync();
int Id(string kind) => catalog.First(x => x.Kind == kind).Id;
var part = catalog.First(x => x.Kind == "part" && x.Label == "Жатка");
var node = catalog.First(x => x.Kind == "node" && x.ParentId == part.Id);
var fault = catalog.First(x => x.Kind == "fault" && x.ParentId == node.Id && !x.RequiresDescription);
var self = catalog.Single(x => x.Kind == "repairMethod" && !x.RequiresReserve && !x.IsOther);
var assisted = catalog.First(x => x.Kind == "repairMethod" && x.RequiresReserve && x.RequiresRepairStart);
var input = new SubmitBreakdown(Guid.NewGuid(), Id("site"), Id("shift"), "Тестовий працівник", Id("machine"), part.Id, node.Id, fault.Id, "", self.Id, false);
var owner = new AppUser(22, "Автор", false);
var other = new AppUser(33, "Учасник групи", false);
var service = new BreakdownService(db, config);
var report = await service.SubmitAsync(input, owner, default);
Check(report.Id > 0 && report.RestoredAt is null, "Submitting starts an open downtime using server time");
Check(await db.Notifications.CountAsync() == 2 && await db.Notifications.AnyAsync(x => x.ChatId == -100123456 && x.Kind == "new"), "Author and APOLLO group get durable notifications");
Check((await service.SubmitAsync(input, owner, default)).Id == report.Id && await db.Breakdowns.CountAsync() == 1, "Retry does not duplicate report or notifications");
await Reject(() => service.SubmitAsync(input with { Person = "Інша особа" }, owner, default), 409, "Idempotency rejects different payload with same key");
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id, HasReserveApollo = null }, owner, default), 400, "Assisted repair requires reserve answer");
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
await Reject(() => service.RestoreAsync(withReserve.Id, other, default), 409, "Repair service cannot close before arrival/start");
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
await service.StartRepairAsync(withReserve.Id, other, default);
await stale.Entry(await stale.Breakdowns.SingleAsync(x => x.Id == withReserve.Id)).ReloadAsync();
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
var telegram = new TelegramClient(new HttpClient(handler), config, service, new UserDirectory(db, config));
await telegram.SendNotificationAsync(-100123456, "Тест", report.Id, true, default);
Check(handler.Calls.Last().Json.Contains("restore:" + report.Id) && handler.Calls.Last().Method == "sendMessage", "Group notification carries report-specific restore button");
fault.Active = true; await db.SaveChangesAsync();
var callbackReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, owner, default);
using var update = JsonDocument.Parse(JsonSerializer.Serialize(new { callback_query = new {
    id = "callback1", from = new { id = 33, first_name = "Member", is_bot = false }, data = $"restore:{callbackReport.Id}",
    message = new { message_id = 123, chat = new { id = -100123456L, type = "supergroup" } }
} }));
await telegram.HandleUpdateAsync(update.RootElement, default);
Check(callbackReport.RestoredAt is null, "First Telegram click only requests confirmation");
var prompt = JsonDocument.Parse(handler.Calls.Last(x => x.Method == "sendMessage").Json);
var confirmData = prompt.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString()!;
using var confirmedUpdate = JsonDocument.Parse(JsonSerializer.Serialize(new { callback_query = new {
    id = "callback2", from = new { id = 33, first_name = "Member", is_bot = false }, data = confirmData,
    message = new { message_id = 124, chat = new { id = -100123456L, type = "supergroup" } }
} }));
await telegram.HandleUpdateAsync(confirmedUpdate.RootElement, default);
Check(handler.Calls.Any(x => x.Method == "deleteMessage" && JsonDocument.Parse(x.Json).RootElement.GetProperty("message_id").GetInt32() == 124) && !handler.Calls.Any(x => x.Method == "deleteMessage" && JsonDocument.Parse(x.Json).RootElement.GetProperty("message_id").GetInt32() == 123), "Confirmation message is deleted after success while the original report stays in the chat");
Check(callbackReport.RestoredAt is not null && handler.Calls.Any(x => x.Method == "answerCallbackQuery"), "Group callback closes report and acknowledges button");
Check(handler.Calls.Any(x => x.Method == "editMessageReplyMarkup"), "Callback removes completed button");
// New roles are persisted independently of environment settings.
var directory = new UserDirectory(db, config);
var adminUser = await directory.VisitAsync(new AppUser(100, "Адмін", true), default);
var registered = await directory.VisitAsync(new AppUser(101, "Працівник", false), default);
Check(await db.Users.AnyAsync(x => x.Id == 101 && !x.IsAdmin), "Bot visitors are stored with first and last activity");
var profile = await db.Users.FindAsync(101L);
await directory.SetAdminAsync(101, true, profile!.Version, adminUser, default);
Check((await directory.VisitAsync(registered, default)).IsAdmin, "Existing visitor receives admin rights immediately");
await Reject(async () => await directory.SetAdminAsync(100, false, (await db.Users.FindAsync(100L))!.Version, adminUser, default), 400, "Admin cannot revoke their own role");
await directory.SetAdminAsync(101, false, profile.Version, adminUser, default);
Check(!(await directory.VisitAsync(registered with { IsAdmin = true }, default)).IsAdmin, "Revoked role is not restored from stale identity claims");
Check(await db.Options.Where(x => !x.Deleted && x.Kind == "part").AllAsync(p => db.Options.Any(n => n.ParentId == p.Id && n.IsOther && n.Kind == "node")), "Every equipment part has Other child");
Check(await db.Options.Where(x => !x.Deleted && x.Kind == "node").AllAsync(n => db.Options.Any(f => f.ParentId == n.Id && f.IsOther && f.Kind == "fault")), "Every node has Other fault");
Check(await db.Notifications.Where(x => x.ChatId > 0).AllAsync(x => x.Text == "Заявка прийнята." || x.Text == "Роботу відновлено."), "Private bot messages contain only brief confirmations");
var repairReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id, HasReserveApollo = true }, owner, default);
repairReport.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
await db.SaveChangesAsync();
await service.StartRepairAsync(repairReport.Id, other, default);
var repairStarted = repairReport.RepairStartedAt;
await service.StartRepairAsync(repairReport.Id, owner, default);
Check(repairReport.RepairStartedAt == repairStarted && await db.Notifications.CountAsync(x => x.BreakdownId == repairReport.Id && x.Kind == "repairStarted") == 1, "Repair start is idempotent and queues only a group message");
repairReport.RepairStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
await db.SaveChangesAsync();
await service.RestoreAsync(repairReport.Id, other, default);
Check(BreakdownService.DowntimeSeconds(repairReport, DateTimeOffset.UtcNow) >= 1200 && BreakdownService.RepairSeconds(repairReport, DateTimeOffset.UtcNow) is >= 300 and < 310, "Total downtime includes travel while repair time excludes it");
Check(BreakdownService.RepairSeconds(repairReport, DateTimeOffset.UtcNow.AddDays(1)) == BreakdownService.RepairSeconds(repairReport, DateTimeOffset.UtcNow), "Finished repair duration no longer grows");
await telegram.SendNotificationAsync(-100123456, "Ремонт", repairReport.Id, true, default, true);
Check(handler.Calls.Last().Json.Contains("start:"), "Service report gets Start repair Telegram button");
await telegram.SendNotificationAsync(22, "Заявка прийнята.", repairReport.Id, true, default);
Check(!handler.Calls.Last().Json.Contains("reply_markup"), "Private confirmations contain no workflow buttons");
await telegram.SendDocumentAsync(22, await HistoryExport.BuildAsync(db, default), default);
Check(handler.Calls.Last().Method == "sendDocument" && handler.Calls.Last().Json.Contains("Apollo-history.csv"), "CSV is sent as an actual Telegram document");
var csvBytes = await HistoryExport.BuildAsync(db, default);
Check(csvBytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) && Encoding.UTF8.GetString(csvBytes).Contains("Простій машини, хв") && Encoding.UTF8.GetString(csvBytes).Contains("Ремонт, сек"), "CSV includes UTF-8 BOM and separate repair duration");
var reminderService = new ReminderService(db, config);
var morning = new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero); // 07:00 Kyiv.
db.Reminders.AddRange(new ReminderSchedule { Time = "07:00" }, new ReminderSchedule { Time = "12:00" });
await db.SaveChangesAsync();
await reminderService.QueueDueAsync(morning, default);
Check(!await db.ReminderDeliveries.AnyAsync(), "No reminder is queued when all reports are completed");
var pendingReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, owner, default);
await reminderService.QueueDueAsync(morning, default);
await reminderService.QueueDueAsync(morning.AddMinutes(1), default);
Check(await db.ReminderDeliveries.CountAsync() == 1 && (await db.ReminderDeliveries.SingleAsync()).Text.Contains("#" + pendingReport.Id), "Reminder contains open reports and runs once per schedule/day");
await reminderService.QueueDueAsync(morning.AddHours(5), default);
Check(await db.ReminderDeliveries.CountAsync() == 2, "Second daily reminder uses its own delivery key");
await service.RestoreAsync(pendingReport.Id, other, default);
Check(await reminderService.BuildTextAsync(morning, default) is null, "Reminder retry suppresses messages after all reports are resolved");

var deleteInput = input with { RequestId = Guid.NewGuid(), Description = "DELETE-CSV-MARKER" };
var toDelete = await service.SubmitAsync(deleteInput, owner, default);
var queued = await db.Notifications.Where(x => x.BreakdownId == toDelete.Id).ToListAsync();
foreach (var item in queued) item.SentAt = null;
await db.SaveChangesAsync();
await Reject(() => service.DeleteAsync(toDelete.Id, owner, default), 403, "Ordinary users cannot delete reports");
Check(await db.Breakdowns.AnyAsync(x => x.Id == toDelete.Id), "Denied deletion preserves the report");
await service.DeleteAsync(toDelete.Id, adminUser, default);
Check(!await db.Breakdowns.AnyAsync(x => x.Id == toDelete.Id), "Deleted reports are excluded from history queries");
var archived = await db.Breakdowns.IgnoreQueryFilters().SingleAsync(x => x.Id == toDelete.Id);
Check(archived.DeletedAt is not null && archived.DeletedByUserId == adminUser.Id, "Deletion preserves the record and records the administrator");
Check(queued.All(x => x.CancelledAt is not null && x.SentAt is null), "Deletion cancels pending notifications without claiming they were sent");
var deletedAt = archived.DeletedAt;
await service.DeleteAsync(toDelete.Id, adminUser, default);
Check(archived.DeletedAt == deletedAt, "Repeated deletion is idempotent");
await Reject(() => service.RestoreAsync(toDelete.Id, owner, default), 404, "Old Telegram restore buttons cannot change deleted reports");
await Reject(() => service.StartRepairAsync(toDelete.Id, owner, default), 404, "Deleted reports cannot start repairs");
await Reject(() => service.SubmitAsync(deleteInput, owner, default), 409, "A retried submission cannot recreate a deleted report");
Check(!Encoding.UTF8.GetString(await HistoryExport.BuildAsync(db, default)).Contains("DELETE-CSV-MARKER"), "CSV excludes deleted reports");
Check(await reminderService.BuildTextAsync(morning, default) is null, "Deleted open reports do not trigger reminders");

var memoryOptions = new DbContextOptionsBuilder<ApolloDbContext>().UseInMemoryDatabase("parallel-visits-" + Guid.NewGuid()).Options;
await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => {
    await using var visitDb = new ApolloDbContext(memoryOptions);
    await new UserDirectory(visitDb, config).VisitAsync(new AppUser(999, "Паралельний вхід", false), default);
}));
await using (var visitDb = new ApolloDbContext(memoryOptions))
    Check(await visitDb.Users.CountAsync() == 1, "Parallel initial form requests register a single visitor without errors");
Check(JsonSerializer.Serialize(TelegramClient.Keyboard(1, true), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Contains("Почато ремонт"), "Telegram button labels preserve Ukrainian encoding");
db.Breakdowns.AddRange(
    new Breakdown { RequestId = Guid.NewGuid(), TelegramUserId = owner.Id, Machine = "TZ-WINTER", CreatedAt = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero), RepairStartedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero), RestoredAt = new DateTimeOffset(2026, 1, 15, 11, 30, 0, TimeSpan.Zero) },
    new Breakdown { RequestId = Guid.NewGuid(), TelegramUserId = owner.Id, Machine = "TZ-SUMMER", CreatedAt = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), RepairStartedAt = new DateTimeOffset(2026, 7, 15, 10, 30, 0, TimeSpan.Zero), RestoredAt = new DateTimeOffset(2026, 7, 15, 11, 30, 0, TimeSpan.Zero) });
await db.SaveChangesAsync();
var localCsv = Encoding.UTF8.GetString(await HistoryExport.BuildAsync(db, default));
Check(localCsv.Contains("Зареєстровано (Київ)") && localCsv.Contains("Почато ремонт (Київ)") && localCsv.Contains("Відновлено (Київ)") && !localCsv.Contains("(UTC)"), "CSV date columns explicitly identify Kyiv time");
Check(localCsv.Contains("15.01.2026 12:00:00") && localCsv.Contains("15.01.2026 12:30:00") && localCsv.Contains("15.01.2026 13:30:00"), "CSV converts all report timestamps to Kyiv winter time (UTC+2)");
Check(localCsv.Contains("15.07.2026 13:00:00") && localCsv.Contains("15.07.2026 13:30:00") && localCsv.Contains("15.07.2026 14:30:00"), "CSV converts all report timestamps to Kyiv summer time (UTC+3)");
var storedWinter = await db.Breakdowns.AsNoTracking().SingleAsync(x => x.Machine == "TZ-WINTER");
var storedSummer = await db.Breakdowns.AsNoTracking().SingleAsync(x => x.Machine == "TZ-SUMMER");
Check(storedWinter.CreatedAt.Offset == TimeSpan.FromHours(2) && storedWinter.RepairStartedAt!.Value.Offset == TimeSpan.FromHours(2) && storedWinter.RestoredAt!.Value.Offset == TimeSpan.FromHours(2), "Database stores report timestamps with the Kyiv winter offset");
Check(storedSummer.CreatedAt.Offset == TimeSpan.FromHours(3) && storedSummer.RepairStartedAt!.Value.Offset == TimeSpan.FromHours(3) && storedSummer.RestoredAt!.Value.Offset == TimeSpan.FromHours(3), "Database stores report timestamps with the Kyiv summer offset");
Check(storedWinter.CreatedAt.UtcDateTime == new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc) && BreakdownService.DowntimeSeconds(storedWinter, now) == 5400, "Offset conversion preserves the exact instant and downtime");
var localProfile = new BotUser { Id = 777, Name = "Часовий пояс", FirstSeenAt = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero), LastSeenAt = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero) };
db.Users.Add(localProfile); db.SaveChanges();
var storedProfile = await db.Users.AsNoTracking().SingleAsync(x => x.Id == 777);
Check(storedProfile.FirstSeenAt.Offset == TimeSpan.FromHours(2) && storedProfile.LastSeenAt.Offset == TimeSpan.FromHours(3), "Synchronous saving also stores Kyiv offsets for user activity");
localProfile.LastSeenAt = new DateTimeOffset(2026, 1, 16, 11, 0, 0, TimeSpan.Zero);
await db.SaveChangesAsync();
Check((await db.Users.AsNoTracking().SingleAsync(x => x.Id == 777)).LastSeenAt.Offset == TimeSpan.FromHours(2), "Updated timestamps are normalized to Kyiv on subsequent saves");
var autumn = new Breakdown { CreatedAt = ApplicationTime.InKyiv(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)), RestoredAt = ApplicationTime.InKyiv(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)) };
Check(autumn.CreatedAt.Hour == autumn.RestoredAt.Value.Hour && BreakdownService.DowntimeSeconds(autumn, now) == 3600, "Downtime remains correct across the autumn clock change");
foreach (var seconds in new long[] { 0, 1, 59, 302, 599, 600, 629 })
    Check(BreakdownService.DowntimeMinutes(seconds) == 10, $"Downtime minimum applies to {seconds} seconds");
Check(BreakdownService.DowntimeMinutes(630) == 11 && BreakdownService.DowntimeMinutes(900) == 15, "Longer downtime rounds to whole minutes");
Check(BreakdownService.FormatDowntime(30) == "10 хв" && BreakdownService.FormatDuration(30) == "менше 1 хв", "Minimum applies only to downtime, not repair duration");
db.Breakdowns.Add(new Breakdown { RequestId = Guid.NewGuid(), TelegramUserId = owner.Id, Machine = "MIN-DOWNTIME", CreatedAt = now.AddSeconds(-30), RepairStartedAt = now.AddSeconds(-20), RestoredAt = now });
await db.SaveChangesAsync();
var minimumRow = Encoding.UTF8.GetString(await HistoryExport.BuildAsync(db, default)).Split('\n').Single(line => line.Contains("MIN-DOWNTIME")).Split(';');
Check(minimumRow[16].Trim('"') == "10" && minimumRow[17].Trim('"') == "20", "CSV exports ten minute minimum while preserving actual repair seconds");

// Real relational persistence and cross-site process recovery.
fault.Active = true;
await db.SaveChangesAsync();
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RequestType = "invalid" }, owner, default), 400, "Unknown request type is rejected");
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid(), HasReserveApollo = null }, owner, default), 400, "Every emergency requires reserve answer even for self repair");
var outageA = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), HasReserveApollo = false }, owner, default);
var secondSite = catalog.First(x => x.Kind == "site" && x.Id != input.SiteId);
var outageB = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), SiteId = secondSite.Id, HasReserveApollo = false }, owner, default);
var reserveOutage = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), HasReserveApollo = true }, owner, default);
var slowReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RequestType = BreakdownService.Productivity, HasReserveApollo = null }, owner, default);
outageA.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-45);
outageB.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
reserveOutage.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-60);
slowReport.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-90);
await db.SaveChangesAsync();
Check(BreakdownService.ProcessDowntimeMinutes(reserveOutage, now.AddDays(1)) == 20, "Reserve process downtime stays exactly twenty minutes regardless of machine duration");
Check(BreakdownService.DowntimeSeconds(slowReport, now.AddDays(1)) == 0 && BreakdownService.MachineDowntimeMinutes(slowReport, now.AddDays(1)) == 0 && BreakdownService.ProcessDowntimeMinutes(slowReport, now.AddDays(1)) == 0, "Productivity reports have zero machine and process downtime while open");
await service.RestoreAsync(slowReport.Id, other, default);
var recoveredA = await db.Breakdowns.AsNoTracking().SingleAsync(x => x.Id == outageA.Id);
var recoveredB = await db.Breakdowns.AsNoTracking().SingleAsync(x => x.Id == outageB.Id);
Check(recoveredA.ProcessRestoredAt == slowReport.RestoredAt && recoveredB.ProcessRestoredAt == slowReport.RestoredAt && recoveredB.ProcessRestoredByBreakdownId == slowReport.Id, "Closing any APOLLO repair persistently ends process downtime across all sites");
Check(recoveredA.RestoredAt is null && recoveredB.RestoredAt is null, "Process recovery does not close outstanding machine repairs");
Check(BreakdownService.ProcessDowntimeMinutes(recoveredA, now.AddDays(1)) == 45 && BreakdownService.ProcessDowntimeMinutes(recoveredB, now.AddDays(1)) == 30, "Recovered process downtime stops growing independently of machine downtime");
Check(BreakdownService.MachineDowntimeMinutes(recoveredA, DateTimeOffset.UtcNow.AddMinutes(20)) >= 65, "Machine downtime continues after another machine restores the process");
var recoveredAt = recoveredA.ProcessRestoredAt;
await service.RestoreAsync(reserveOutage.Id, owner, default);
Check((await db.Breakdowns.AsNoTracking().SingleAsync(x => x.Id == outageA.Id)).ProcessRestoredAt == recoveredAt, "Later repair completions preserve first process recovery");
Check(BreakdownService.ProcessDowntimeMinutes(reserveOutage, now.AddDays(1)) == 20, "Finishing reserve machine repair does not change its twenty minute process downtime");
var nextOutage = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), HasReserveApollo = false }, owner, default);
await service.RestoreAsync(slowReport.Id, other, default);
Check(nextOutage.ProcessRestoredAt is null, "Retrying an earlier repair completion does not end a later process outage");
var removedOutage = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), HasReserveApollo = false }, owner, default);
await service.DeleteAsync(removedOutage.Id, adminUser, default);
Check(nextOutage.ProcessRestoredAt is null, "Deleting a report does not restore the process");
await service.RestoreAsync(outageA.Id, owner, default);
Check(nextOutage.ProcessRestoredByBreakdownId == outageA.Id && nextOutage.ProcessRestoredAt is not null, "A later machine restoration closes the new process outage");
var processCsv = Encoding.UTF8.GetString(await HistoryExport.BuildAsync(db, default));
var slowCsvRow = processCsv.Split('\n').Single(line => line.StartsWith($"\"{slowReport.Id}\";")).Split(';');
Check(processCsv.Contains("Простій процесу, хв") && processCsv.Contains("Тип заявки") && slowCsvRow[16].Trim('"') == "0" && slowCsvRow[20].Trim('"') == "0", "CSV contains both downtime groups and zero downtime for productivity reports");

var confirmationTime = DateTimeOffset.UtcNow;
var signedAction = new ActionConfirmation(outageB.Id, other.Id, -100123456, 123, true);
var signedToken = signedAction.Sign("test-token", confirmationTime);
Check(Encoding.UTF8.GetByteCount("ok:" + signedToken) <= 64 && ActionConfirmation.Read(signedToken, "test-token", other.Id, -100123456, confirmationTime) == signedAction, "Telegram confirmation fits callback limit and preserves all signed fields");
Check(ActionConfirmation.Read(signedToken, "test-token", owner.Id, -100123456, confirmationTime) is null, "Another user cannot confirm a repair action");
Check(ActionConfirmation.Read(signedToken, "test-token", other.Id, -999, confirmationTime) is null, "Confirmation cannot be moved into another chat");
Check(ActionConfirmation.Read(signedToken, "test-token", other.Id, -100123456, confirmationTime.AddMinutes(6)) is null, "Expired confirmation cannot change report state");
Check(ActionConfirmation.Read(signedToken, "wrong-token", other.Id, -100123456, confirmationTime) is null && ActionConfirmation.Read("invalid", "test-token", other.Id, -100123456, confirmationTime) is null, "Forged and malformed confirmations are rejected");

var startPromptReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id }, owner, default);
async Task ClickAsync(string data, long actorId = 33, int messageId = 200, long chatId = -100123456) {
    using var eventJson = JsonDocument.Parse(JsonSerializer.Serialize(new { callback_query = new {
        id = Guid.NewGuid().ToString(), from = new { id = actorId, first_name = "Member", is_bot = false }, data,
        message = new { message_id = messageId, chat = new { id = chatId, type = "supergroup" } }
    } }));
    await telegram.HandleUpdateAsync(eventJson.RootElement, default);
}
string PromptButton(int index) {
    using var json = JsonDocument.Parse(handler.Calls.Last(x => x.Method == "sendMessage").Json);
    return json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][index].GetProperty("callback_data").GetString()!;
}
await ClickAsync($"start:{startPromptReport.Id}");
Check(startPromptReport.RepairStartedAt is null, "First start-repair click leaves the report unchanged");
var cancelStart = PromptButton(1);
var deletesBeforeCancel = handler.Calls.Count(x => x.Method == "deleteMessage");
await ClickAsync(cancelStart);
Check(handler.Calls.Count(x => x.Method == "deleteMessage") == deletesBeforeCancel + 1, "Canceling deletes the confirmation message without cluttering the group");
Check(startPromptReport.RepairStartedAt is null, "Canceling repair start does not start repair");
await ClickAsync($"start:{startPromptReport.Id}");
var acceptStart = PromptButton(0);
await ClickAsync(acceptStart, owner.Id);
Check(startPromptReport.RepairStartedAt is null, "Other user clicking confirm cannot start repair");
await ClickAsync(acceptStart);
Check(startPromptReport.RepairStartedAt is not null, "Confirmed repair start records arrival time");
await ClickAsync($"restore:{startPromptReport.Id}");
await ClickAsync(PromptButton(1));
Check(startPromptReport.RestoredAt is null, "Canceling repair completion leaves the machine repair open");
await ClickAsync($"restore:{startPromptReport.Id}");
await ClickAsync(PromptButton(0));
Check(startPromptReport.RestoredAt is not null, "Confirmed completion closes the machine repair");

var unattendedReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id }, owner, default);
await ClickAsync($"start:{unattendedReport.Id}", other.Id);
var unattendedConfirmation = PromptButton(0);
await ClickAsync($"start:{unattendedReport.Id}", owner.Id);
var secondConfirmation = PromptButton(0);
Check(unattendedReport.RepairStartedAt is null && unattendedConfirmation != secondConfirmation, "Unanswered confirmation leaves report available and allows another user's independent prompt");
await ClickAsync(secondConfirmation, owner.Id);
Check(unattendedReport.RepairStartedByUserId == owner.Id, "Another worker can confirm while the first worker's confirmation is unanswered");
var firstArrival = unattendedReport.RepairStartedAt;
await ClickAsync(unattendedConfirmation, other.Id);
Check(unattendedReport.RepairStartedByUserId == owner.Id && unattendedReport.RepairStartedAt == firstArrival, "A late first confirmation cannot overwrite the worker who already started repair");


// Test mode is a per-request data partition, not a global bot switch.
config["Telegram:TestGroupChatId"] = "-100777777";
Check(TelegramDestinations.Group(config, true) == -100777777 && TelegramDestinations.Group(config, false) == -100123456, "Test and production destinations are separate");
config["Telegram:TestGroupChatId"] = "-100123456";
Check(TelegramDestinations.Group(config, true) == 0, "Production group cannot be reused as test group");
config["Telegram:TestGroupChatId"] = "malformed";
Check(TelegramDestinations.Group(config, true) == 0, "Malformed test destination does not crash startup or leak into production");
config["Telegram:TestGroupChatId"] = "-100777777";
db.TestMode = false;
var sharedRequestId = Guid.NewGuid();
var liveModeReport = await service.SubmitAsync(input with { RequestId = sharedRequestId, Description = "LIVE-ONLY-MARKER" }, adminUser, default);
db.TestMode = true;
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, owner, default), 403, "Only admins can submit test reports");
var testModeReport = await service.SubmitAsync(input with { RequestId = sharedRequestId, Description = "TEST-ONLY-MARKER" }, adminUser, default);
Check(testModeReport.IsTest && !liveModeReport.IsTest && testModeReport.Id != liveModeReport.Id, "Same request key is independently idempotent in test and production");
Check((await service.SubmitAsync(input with { RequestId = sharedRequestId, Description = "TEST-ONLY-MARKER" }, adminUser, default)).Id == testModeReport.Id, "Test report retry does not duplicate a test report");
var testNotifications = await db.Notifications.Where(x => x.BreakdownId == testModeReport.Id).ToListAsync();
Check(testNotifications.Count == 1 && testNotifications.All(x => x.ChatId == -100777777 && x.Text.Contains("ТЕСТ")), "All test notifications target only the separate group and are labeled as tests");
Check(!await db.Breakdowns.AnyAsync(x => x.Id == liveModeReport.Id) && await db.Breakdowns.AnyAsync(x => x.Id == testModeReport.Id), "Test history hides production reports");
await Reject(() => service.RestoreAsync(liveModeReport.Id, adminUser, default), 404, "Test mode cannot restore production reports by guessed ID");
await Reject(() => service.DeleteAsync(liveModeReport.Id, adminUser, default), 404, "Test mode cannot delete production reports by guessed ID");
await Reject(() => service.RestoreAsync(testModeReport.Id, owner, default), 403, "A non-admin cannot restore a test report");
var isolatedTestCsv = Encoding.UTF8.GetString(await HistoryExport.BuildAsync(db, default));
Check(isolatedTestCsv.Contains("TEST-ONLY-MARKER") && !isolatedTestCsv.Contains("LIVE-ONLY-MARKER"), "Test CSV contains only test history");
await service.RestoreAsync(testModeReport.Id, adminUser, default);
Check(liveModeReport.RestoredAt is null && liveModeReport.ProcessRestoredAt is null, "Completing test repair does not restore the production machine or process");
var nextTestReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, adminUser, default);
db.TestMode = false;
Check(!await db.Breakdowns.AnyAsync(x => x.Id == testModeReport.Id), "Production history hides test reports");
await Reject(() => service.RestoreAsync(nextTestReport.Id, adminUser, default), 404, "Production mode cannot restore test reports by guessed ID");
var isolatedLiveCsv = Encoding.UTF8.GetString(await HistoryExport.BuildAsync(db, default));
Check(isolatedLiveCsv.Contains("LIVE-ONLY-MARKER") && !isolatedLiveCsv.Contains("TEST-ONLY-MARKER"), "Production CSV excludes test data");
await service.RestoreAsync(liveModeReport.Id, adminUser, default);
Check(nextTestReport.RestoredAt is null && nextTestReport.ProcessRestoredAt is null, "Completing production repair does not restore the test process");
await Reject(() => service.SelectTelegramModeAsync(nextTestReport.Id, owner, -100777777, default), 403, "Non-admin group member cannot operate test callbacks");
await Reject(() => service.SelectTelegramModeAsync(nextTestReport.Id, adminUser, -100123456, default), 403, "Test callback is rejected in production chat");
await service.SelectTelegramModeAsync(nextTestReport.Id, adminUser, -100777777, default);
Check(db.TestMode, "Telegram test callbacks select test partition without switching other requests");
var testSchedule = new ReminderSchedule { Time = "07:00", IsTest = true };
db.Reminders.Add(testSchedule);
await db.SaveChangesAsync();
var reminderDay = morning.AddDays(3);
await reminderService.QueueDueAsync(reminderDay, default);
var testDelivery = await db.ReminderDeliveries.SingleAsync(x => x.ScheduleId == testSchedule.Id);
Check(testDelivery.ChatId == -100777777 && testDelivery.Text.Contains("ТЕСТ") && testDelivery.Text.Contains("#" + nextTestReport.Id), "Test reminders use test reports and the separate test chat");
Check(!await db.Reminders.AnyAsync(x => !x.IsTest), "Test reminder schedule excludes production schedules");
db.TestMode = false;
Check(!await db.Reminders.AnyAsync(x => x.Id == testSchedule.Id), "Production reminder schedule excludes test schedules");
await Reject(() => service.SelectTelegramModeAsync(liveModeReport.Id, adminUser, -100777777, default), 403, "Production callback is rejected in test chat");
config["Telegram:TestGroupChatId"] = "0";
config["Telegram:Mode"] = "Polling";
db.TestMode = true;
await Reject(() => service.SubmitAsync(input with { RequestId = Guid.NewGuid() }, adminUser, default), 400, "Missing test chat blocks only test submission, not the application");
db.TestMode = false;
Check(await service.FindAsync(liveModeReport.Id, default) is not null, "Production remains usable when test chat is missing");
config["Telegram:TestGroupChatId"] = "-100777777";

db.TestMode = true;
var telegramTestReport = await service.SubmitAsync(input with { RequestId = Guid.NewGuid(), RepairMethodId = assisted.Id }, adminUser, default);
db.TestMode = false;
await ClickAsync($"start:{telegramTestReport.Id}", adminUser.Id, chatId: -100777777);
var testStartPrompt = handler.Calls.Last(x => x.Method == "sendMessage");
Check(JsonDocument.Parse(testStartPrompt.Json).RootElement.GetProperty("chat_id").GetInt64() == -100777777 && db.TestMode, "Telegram test confirmation remains in test chat and selects its own data partition");
await ClickAsync(PromptButton(0), adminUser.Id, chatId: -100777777);
Check(telegramTestReport.RepairStartedAt is not null && telegramTestReport.IsTest, "Confirmed Telegram action can start a test repair while production remains active");
await ClickAsync($"restore:{telegramTestReport.Id}", adminUser.Id, chatId: -100777777);
await ClickAsync(PromptButton(0), adminUser.Id, chatId: -100777777);
Check(telegramTestReport.RestoredAt is not null && (await db.Notifications.Where(x => x.BreakdownId == telegramTestReport.Id).ToListAsync()).All(x => x.ChatId == -100777777), "Completed Telegram test repair sends every status event only to test chat");

Console.WriteLine($"{passed} checks passed.");

sealed class RecordingHandler : HttpMessageHandler
{
    public List<(string Method, string Json)> Calls { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        Calls.Add((request.RequestUri!.Segments.Last(), await request.Content!.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json") };
    }
}

sealed class FailureProbe() : ResilientBackgroundService(NullLogger.Instance)
{
    public int Attempts;
    public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override TimeSpan RetryDelay => TimeSpan.FromMilliseconds(20);
    protected override async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref Attempts) == 1) throw new InvalidOperationException("Simulated integration outage");
        Recovered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
