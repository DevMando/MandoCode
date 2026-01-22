namespace MandoCode.Services;

/// <summary>
/// Simple retry helper with exponential backoff for transient errors.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Executes an async operation with retry logic for transient errors.
    /// Uses exponential backoff: 500ms -> 1s -> 2s
    /// </summary>
    /// <typeparam name="T">Return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 2).</param>
    /// <param name="operationName">Name of the operation for logging (optional).</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 2,
        string? operationName = null)
    {
        var delays = new[] { 500, 1000, 2000 }; // Exponential backoff in ms
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < maxRetries)
            {
                lastException = ex;

                // Calculate delay with exponential backoff
                var delayMs = attempt < delays.Length ? delays[attempt] : delays[^1];

                // Log retry attempt (can be replaced with proper logging)
                System.Diagnostics.Debug.WriteLine(
                    $"[RetryPolicy] {operationName ?? "Operation"} failed (attempt {attempt + 1}/{maxRetries + 1}): {ex.Message}. Retrying in {delayMs}ms...");

                await Task.Delay(delayMs);
            }
        }

        // All retries exhausted, throw the last exception
        throw lastException ?? new Exception("Operation failed after retries");
    }

    /// <summary>
    /// Executes an async operation with retry logic for transient errors (void return).
    /// Uses exponential backoff: 500ms -> 1s -> 2s
    /// </summary>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 2).</param>
    /// <param name="operationName">Name of the operation for logging (optional).</param>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        int maxRetries = 2,
        string? operationName = null)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, maxRetries, operationName);
    }

    /// <summary>
    /// Determines if an exception represents a transient error that should be retried.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        // HTTP connection errors are transient
        if (ex is HttpRequestException)
            return true;

        // Timeout errors are transient
        if (ex is TaskCanceledException tce && tce.CancellationToken == default)
            return true;

        if (ex is OperationCanceledException oce && oce.CancellationToken == default)
            return true;

        // Socket errors are transient
        if (ex is System.Net.Sockets.SocketException)
            return true;

        // Check for connection-related messages
        var message = ex.Message.ToLowerInvariant();
        if (message.Contains("connection") ||
            message.Contains("timeout") ||
            message.Contains("temporarily unavailable") ||
            message.Contains("service unavailable") ||
            message.Contains("502") ||
            message.Contains("503") ||
            message.Contains("504"))
        {
            return true;
        }

        // Check inner exception
        if (ex.InnerException != null)
            return IsTransientError(ex.InnerException);

        return false;
    }
}
