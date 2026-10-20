namespace ApolloBreakdowns;

// A failed integration is restarted independently of the HTTP server.
public abstract class ResilientBackgroundService(ILogger logger) : BackgroundService
{
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(15);
    protected abstract Task RunAsync(CancellationToken ct);

    protected sealed override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogError("Background task {Worker} failed ({ErrorType}); retrying without stopping HTTP.", GetType().Name, e.GetType().Name);
            }
            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
