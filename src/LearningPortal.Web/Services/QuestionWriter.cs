using LearningPortal.Core.Ai;
using LearningPortal.Core.Services;

namespace LearningPortal.Web.Services;

// Has the AI write questions for an exam on the server, so the work carries on when the user leaves
// the page or closes the browser. One job per exam at a time. The exam page follows a job through
// Changed and shows its outcome when it ends, also when the user only comes back later.
public sealed class QuestionWriter(ExamService exams, IHostApplicationLifetime lifetime, ILogger<QuestionWriter> logger)
{
    public enum Kind { Fill, Regenerate, Replace }

    public sealed class Job
    {
        public required string UserId { get; init; }
        public required int ExamId { get; init; }
        public required Kind Kind { get; init; }
        public string Message { get; internal set; } = "";
        public bool Finished { get; internal set; }
        public bool Cancelled { get; internal set; }
        public FillResult? Result { get; internal set; }
        public string? Error { get; internal set; }
        public DateTime FinishedAt { get; internal set; }
        internal CancellationTokenSource Cts { get; } = new();
    }

    // Outcomes nobody came back for are dropped after this long.
    private static readonly TimeSpan KeepFinished = TimeSpan.FromDays(1);

    private readonly Dictionary<int, Job> _jobs = [];
    private readonly Lock _lock = new();

    // The exam whose job changed: a new progress message, or it ended.
    public event Action<int>? Changed;

    // Starts a job unless one is already running for the exam. questionId is the question to replace.
    public bool Start(string userId, int examId, Kind kind, AiChoice? choice, int? questionId = null)
    {
        Job job;
        lock (_lock)
        {
            if (_jobs.TryGetValue(examId, out var running) && !running.Finished)
                return false;
            foreach (var old in _jobs.Values.Where(j => j.Finished && DateTime.UtcNow - j.FinishedAt > KeepFinished).ToList())
                _jobs.Remove(old.ExamId);

            job = new Job
            {
                UserId = userId, ExamId = examId, Kind = kind,
                Message = kind switch
                {
                    Kind.Fill => "Adding questions...",
                    Kind.Regenerate => "Writing new questions...",
                    _ => "Writing a replacement question...",
                },
            };
            _jobs[examId] = job;
        }

        _ = Task.Run(() => RunAsync(job, choice, questionId));
        Notify(examId);
        return true;
    }

    // The exam's current or last job, if it belongs to this user.
    public Job? Get(string userId, int examId)
    {
        lock (_lock)
            return _jobs.TryGetValue(examId, out var job) && job.UserId == userId ? job : null;
    }

    public bool IsWriting(string userId, int examId) => Get(userId, examId) is { Finished: false };

    public void Cancel(string userId, int examId)
    {
        if (Get(userId, examId) is { Finished: false } job)
            job.Cts.Cancel();
    }

    // Forgets a finished job once its outcome has been shown.
    public void Dismiss(string userId, int examId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(examId, out var job) && job.UserId == userId && job.Finished)
                _jobs.Remove(examId);
        }
    }

    private async Task RunAsync(Job job, AiChoice? choice, int? questionId)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, lifetime.ApplicationStopping);
        var progress = new Reporter(message =>
        {
            job.Message = message;
            Notify(job.ExamId);
        });

        try
        {
            job.Result = job.Kind switch
            {
                Kind.Fill => await exams.FillAsync(job.UserId, job.ExamId, choice, progress, cts.Token),
                Kind.Regenerate => await exams.RegenerateAllAsync(job.UserId, job.ExamId, choice, progress, cts.Token),
                _ => await ReplaceAsync(job, choice, questionId!.Value, progress, cts.Token),
            };
        }
        catch (OperationCanceledException)
        {
            job.Cancelled = true;
        }
        catch (Exception ex) when (ex is AiException or ArgumentException or InvalidOperationException or NotFoundException)
        {
            job.Error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing questions for exam {ExamId} failed", job.ExamId);
            job.Error = "Something went wrong while writing questions. Try again.";
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            job.Finished = true;
            Notify(job.ExamId);
        }
    }

    private async Task<FillResult> ReplaceAsync(Job job, AiChoice? choice, int questionId, IProgress<string> progress, CancellationToken ct)
    {
        await exams.ReplaceQuestionAsync(job.UserId, job.ExamId, questionId, choice, progress, ct);
        return new FillResult(0, 1, 0);
    }

    private void Notify(int examId)
    {
        try
        {
            Changed?.Invoke(examId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A QuestionWriter subscriber failed");
        }
    }

    // Reports straight away on the job's thread; Progress<T> would post to a context there isn't.
    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
