using System.Net.Http;
namespace AdenRising.Updater.Core;

public static class Retry
{
    /// <summary>
    /// Runs something that talks to the network, giving it a few goes before
    /// admitting defeat. Blob downloads have their own resuming retry; this is
    /// for the small requests -- fetching the manifest above all, where one
    /// dropped connection used to end the whole run.
    /// </summary>
    public static async Task<T> OnNetworkAsync<T>(
        Func<CancellationToken, Task<T>> work,
        int attempts = 4,
        Action<int, Exception>? onRetry = null,
        CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await work(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A timeout surfaces as cancellation even though the caller did
                // not cancel: worth another go.
                last = new TimeoutException("the request timed out");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                last = ex;
            }

            if (attempt == attempts) break;
            onRetry?.Invoke(attempt, last!);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1))), ct);
        }

        throw new HttpRequestException(
            $"could not reach the update server after {attempts} attempts", last);
    }
}
