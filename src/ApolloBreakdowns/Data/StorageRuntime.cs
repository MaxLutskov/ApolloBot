using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ApolloBreakdowns.Data;

public sealed class StorageRuntime(bool inMemory)
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool memory = inMemory;
    public bool InMemory { get => memory; set => memory = value; }
    public bool IsReady => ready.Task.IsCompletedSuccessfully;
    public InMemoryDatabaseRoot MemoryRoot { get; } = new();
    public Task WaitAsync(CancellationToken ct) => ready.Task.WaitAsync(ct);
    public void MarkReady() => ready.TrySetResult();
}

public sealed class StorageInitializer(IServiceScopeFactory scopes, StorageRuntime storage, IConfiguration config,
    ILogger<StorageInitializer> logger) : ResilientBackgroundService(logger)
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApolloDbContext>();
            if (storage.InMemory) await db.Database.EnsureCreatedAsync(ct);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await db.Database.MigrateAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (!storage.InMemory)
        {
            logger.LogWarning("SQL initialization failed ({ErrorType}); starting test mode with temporary in-memory storage.", e.GetType().Name);
            storage.InMemory = true;
            config["Telegram:Mode"] = "Demo";
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApolloDbContext>().Database.EnsureCreatedAsync(ct);
        }
        if (storage.InMemory) logger.LogWarning("Temporary in-memory storage is active. Data is lost on restart.");
        storage.MarkReady();
        logger.LogInformation("Application storage is ready ({Storage}).", storage.InMemory ? "memory" : "sql");
    }
}
