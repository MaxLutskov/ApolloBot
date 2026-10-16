using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using ApolloBreakdowns.Domain;
using ApolloBreakdowns.Telegram;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var mode = builder.Configuration["Telegram:Mode"] ?? "Webhook";
if (mode is not ("Demo" or "Webhook" or "Polling")) throw new InvalidOperationException("Unknown Telegram:Mode");
if (mode == "Demo" && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("Demo is only available in Development.");
if (mode != "Demo")
{
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("BotDatabase"))) throw new InvalidOperationException("ConnectionStrings:BotDatabase is required.");
    if (string.IsNullOrWhiteSpace(builder.Configuration["Telegram:BotToken"])) throw new InvalidOperationException("Telegram:BotToken is required.");
    if (!Uri.TryCreate(builder.Configuration["Telegram:PublicUrl"], UriKind.Absolute, out var url) || url.Scheme != "https") throw new InvalidOperationException("Telegram:PublicUrl must be HTTPS.");
    if (mode == "Webhook" && (builder.Configuration["Telegram:WebhookSecret"] is not { Length: >= 32 and <= 256 } secret || secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
        throw new InvalidOperationException("Telegram:WebhookSecret must contain 32–256 ASCII letters, digits, _ or -.");
    if ((builder.Configuration.GetSection("Admin:TelegramIds").Get<long[]>() ?? []).Length == 0) throw new InvalidOperationException("At least one Admin:TelegramIds entry is required.");
    if (builder.Configuration.GetValue<long>("Telegram:GroupChatId") >= 0) throw new InvalidOperationException("Telegram:GroupChatId must be the negative chat ID of the APOLLO group.");
}
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32768);
builder.Services.AddDbContext<ApolloDbContext>(options => {
    if (mode == "Demo") options.UseInMemoryDatabase("apollo-demo");
    else options.UseSqlServer(builder.Configuration.GetConnectionString("BotDatabase"), sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ApolloDbContext.Schema).EnableRetryOnFailure(3));
    options.UseAsyncSeeding(async (context, _, _) => await CatalogSeed.ApplyAsync((ApolloDbContext)context));
    options.UseSeeding((context, _) => CatalogSeed.ApplyAsync((ApolloDbContext)context).GetAwaiter().GetResult());
});
builder.Services.AddScoped<TelegramAuth>();
builder.Services.AddScoped<BreakdownService>();
builder.Services.AddHttpClient<TelegramClient>(http => http.Timeout = TimeSpan.FromSeconds(40)).RemoveAllLoggers();
builder.Services.AddHostedService<TelegramWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ((AppUser?)ctx.Items["User"])?.Id.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (ctx, next) => {
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' https://telegram.org; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors https://web.telegram.org https://*.telegram.org; base-uri 'self'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (RuleException e) { ctx.Response.StatusCode = e.Status; await ctx.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (BadHttpRequestException e) { ctx.Response.StatusCode = e.StatusCode; await ctx.Response.WriteAsJsonAsync(new { message = "Перевірте формат запиту." }); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) {
        app.Logger.LogError("Request failed ({ErrorType}), trace {TraceId}.", e.GetType().Name, ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { message = "Сервер тимчасово недоступний. Повторіть спробу: заявку не буде продубльовано." }); }
    }
});
app.UseStaticFiles();
app.UseRouting();
app.Use(async (ctx, next) => {
    if (ctx.Request.Path.StartsWithSegments("/api/app")) {
        var user = ctx.RequestServices.GetRequiredService<TelegramAuth>().Authenticate(ctx.Request);
        if (user is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { message = "Відкрийте форму через Telegram-бота. Якщо сесія закінчилася, закрийте й відкрийте форму знову." }); return; }
        ctx.Items["User"] = user;
    }
    await next();
});
app.UseRateLimiter();
app.MapGet("/", () => Results.Redirect("/miniapp/"));
app.MapGet("/health", async (ApolloDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503));
app.MapPost("/api/telegram/webhook", async (HttpContext ctx, TelegramClient telegram, CancellationToken ct) => {
    var expected = builder.Configuration["Telegram:WebhookSecret"] ?? "";
    var supplied = ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
    if (mode != "Webhook" || expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied))) return Results.NotFound();
    using var json = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    await telegram.HandleUpdateAsync(json.RootElement, ct);
    return Results.Ok();
});
app.MapAppApi();
app.MapFallbackToFile("/miniapp/{*path:nonfile}", "miniapp/index.html");
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApolloDbContext>();
    if (mode == "Demo") await db.Database.EnsureCreatedAsync();
    else await db.Database.MigrateAsync();
}
await app.RunAsync();
public partial class Program;
