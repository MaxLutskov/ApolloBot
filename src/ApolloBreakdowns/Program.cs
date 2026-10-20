using ApolloBreakdowns;
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
var mode = StartupSettings.Normalize(builder.Configuration);
var storage = new StorageRuntime(mode == "Demo" || string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("BotDatabase")));
builder.Services.AddSingleton(storage);
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddHostedService<StorageInitializer>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32768);
builder.Services.AddDbContext<ApolloDbContext>(options => {
    if (storage.InMemory) options.UseInMemoryDatabase("apollo-demo", storage.MemoryRoot);
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
    catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException || e.InnerException is Microsoft.Data.SqlClient.SqlException) {
        app.Logger.LogWarning("SQL request failed ({ErrorType}), trace {TraceId}.", e.GetType().Name, ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { message = "База тимчасово недоступна. Дані форми збережено у вкладці. Повторіть спробу пізніше." }); }
    }
    catch (Exception e) {
        app.Logger.LogError("Request failed ({ErrorType}), trace {TraceId}.", e.GetType().Name, ctx.TraceIdentifier);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { message = "Сервер тимчасово недоступний. Повторіть спробу: заявку не буде продубльовано." }); }
    }
});
app.UseStaticFiles();
app.UseRouting();
app.Use(async (ctx, next) => {
    if (ctx.Request.Path.StartsWithSegments("/api/app")) {
        if (!storage.IsReady) {
            ctx.Response.StatusCode = 503;
            ctx.Response.Headers.RetryAfter = "3";
            await ctx.Response.WriteAsJsonAsync(new { message = "Сховище ще готується. Повторіть спробу за кілька секунд." });
            return;
        }
        var user = ctx.RequestServices.GetRequiredService<TelegramAuth>().Authenticate(ctx.Request);
        if (user is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { message = "Відкрийте форму через Telegram-бота. Якщо сесія закінчилася, закрийте й відкрийте форму знову." }); return; }
        ctx.Items["User"] = user;
    }
    await next();
});
app.UseRateLimiter();
app.MapGet("/", () => Results.Redirect("/miniapp/"));
app.MapGet("/health", () => Results.Ok(new { status = "healthy", storage = storage.InMemory ? "memory" : "sql", storageReady = storage.IsReady, demo = builder.Configuration["Telegram:Mode"] == "Demo" }));
app.MapGet("/health/ready", async (IServiceScopeFactory scopes, CancellationToken ct) => {
    if (!storage.IsReady) return Results.StatusCode(503);
    try {
        await using var scope = scopes.CreateAsyncScope();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var db = scope.ServiceProvider.GetRequiredService<ApolloDbContext>();
        return await db.Database.CanConnectAsync(timeout.Token) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
    }
    catch (Exception) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
});
app.MapPost("/api/telegram/webhook", async (HttpContext ctx, TelegramClient telegram, CancellationToken ct) => {
    var expected = builder.Configuration["Telegram:WebhookSecret"] ?? "";
    var supplied = ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
    if (builder.Configuration["Telegram:Mode"] != "Webhook" || expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied))) return Results.NotFound();
    if (!storage.IsReady) return Results.StatusCode(503);
    using var json = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    await telegram.HandleUpdateAsync(json.RootElement, ct);
    return Results.Ok();
});
app.MapAppApi();
app.MapFallbackToFile("/miniapp/{*path:nonfile}", "miniapp/index.html");
await app.RunAsync();
public partial class Program;
