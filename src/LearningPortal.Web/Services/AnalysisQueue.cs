using System.Threading.Channels;
using LearningPortal.Core.Services;

namespace LearningPortal.Web.Services;

// Runs material analysis (the AI outline) in the background, so it finishes even if the user
// leaves the page right after uploading. Pages subscribe to MaterialAnalyzed to refresh.
public sealed class AnalysisQueue(MaterialService materials, ILogger<AnalysisQueue> logger) : BackgroundService
{
    private readonly Channel<(string UserId, int MaterialId)> _queue = Channel.CreateUnbounded<(string, int)>();
    private readonly HashSet<int> _pending = [];
    private readonly Lock _lock = new();

    public event Action<string, int>? MaterialAnalyzed;

    public void Enqueue(string userId, int materialId)
    {
        lock (_lock)
        {
            if (!_pending.Add(materialId))
                return;
        }
        _queue.Writer.TryWrite((userId, materialId));
    }

    public bool IsPending(int materialId)
    {
        lock (_lock)
            return _pending.Contains(materialId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (userId, materialId) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await materials.AnalyzeAsync(userId, materialId, null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analysis of material {MaterialId} failed", materialId);
            }
            finally
            {
                lock (_lock)
                    _pending.Remove(materialId);
            }

            try
            {
                MaterialAnalyzed?.Invoke(userId, materialId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A MaterialAnalyzed subscriber failed");
            }
        }
    }
}
