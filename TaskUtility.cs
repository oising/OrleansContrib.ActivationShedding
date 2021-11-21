using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hilo.Sys.Orleans.GrainActivationBalancing
{
    internal static class TaskUtility
    {
        internal static async Task RepeatEvery(Func<Task> func,
            TimeSpan interval, CancellationToken cancellationToken, ILogger<ActivationSheddingFilter> logger)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await func();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "TaskUtility.RepeatEvery task failed: {ErrorMessage}", ex.Message);
                }

                Task task = Task.Delay(interval, cancellationToken);

                try
                {
                    await task;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
    }
}