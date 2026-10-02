#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.Engine.Events;

namespace WindowsGSM.Engine.Operations
{
    public enum JobStatus { Running, Succeeded, Failed, Cancelled }

    /// <summary>An immutable view of a job — what UIs receive and display.</summary>
    public sealed record JobSnapshot(
        string Id,
        string Kind,
        string? ServerId,
        string Title,
        JobStatus Status,
        int? Percent,
        string? Stage,
        string? Error,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        IReadOnlyList<string> RecentLog);

    /// <summary>What a job's work function uses to report progress, log, and notice cancellation.</summary>
    public sealed class JobContext
    {
        private readonly Job _job;
        internal JobContext(Job job) => _job = job;

        public CancellationToken Cancellation => _job.Cancellation.Token;

        /// <summary>Updates the progress bar and/or the stage text ("Downloading…"). Null leaves a value as is.</summary>
        public void Report(int? percent = null, string? stage = null) => _job.Update(percent, stage);

        /// <summary>Adds a line to the job's log (the last 200 are kept).</summary>
        public void Log(string line) => _job.AppendLog(line);
    }

    /// <summary>
    /// A long-running piece of work — install, update, backup, restore, start/stop — with progress, a log,
    /// a cancel button and a result. The legacy app ran these fire-and-forget with no progress anywhere
    /// but its own window.
    /// </summary>
    public sealed class Job
    {
        private const int MaxLogLines = 200;
        private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);

        private readonly object _gate = new object();
        private readonly EventBus _events;
        private readonly LinkedList<string> _log = new LinkedList<string>();
        private JobStatus _status = JobStatus.Running;
        private int? _percent;
        private string? _stage;
        private string? _error;
        private DateTimeOffset? _endedAt;
        private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;

        internal Job(string kind, string? serverId, string title, EventBus events)
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            Kind = kind;
            ServerId = serverId;
            Title = title;
            StartedAt = DateTimeOffset.UtcNow;
            _events = events;
        }

        /// <summary>The job whose work is running on this logical call path (null outside any job).</summary>
        public static Job? Current => _current.Value;
        private static readonly AsyncLocal<Job?> _current = new AsyncLocal<Job?>();
        internal static void SetCurrent(Job? job) => _current.Value = job;

        public string Id { get; }
        public string Kind { get; }
        public string? ServerId { get; }
        public string Title { get; }
        public DateTimeOffset StartedAt { get; }

        internal CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

        /// <summary>Completes when the job finishes, whatever the outcome. Never faults.</summary>
        public Task<JobSnapshot> Completion => _completion.Task;
        private readonly TaskCompletionSource<JobSnapshot> _completion =
            new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new JobSnapshot(Id, Kind, ServerId, Title, _status, _percent, _stage, _error, StartedAt, _endedAt, _log.ToList());
            }
        }

        internal void Update(int? percent, string? stage)
        {
            bool publishNow = false;
            TimeSpan scheduleIn = TimeSpan.Zero;
            lock (_gate)
            {
                if (_status != JobStatus.Running) { return; }
                bool stageChanged = stage != null && stage != _stage;
                if (percent != null) { _percent = Math.Clamp(percent.Value, 0, 100); }
                if (stage != null) { _stage = stage; }

                // Stage changes always go out at once. Percentage ticks are throttled so a fast download can't
                // flood clients — but trailing-edge: a throttled value is published when the window ends, so
                // the latest progress always arrives (a download that pauses at 97% shows 97%, not 95%).
                var sinceLast = DateTimeOffset.UtcNow - _lastPublished;
                if (stageChanged || sinceLast >= ProgressThrottle)
                {
                    publishNow = true;
                    _lastPublished = DateTimeOffset.UtcNow;
                }
                else if (!_publishScheduled)
                {
                    _publishScheduled = true;
                    scheduleIn = ProgressThrottle - sinceLast;
                }
            }

            if (publishNow) { _events.Publish(new JobChanged(Snapshot())); }
            else if (scheduleIn > TimeSpan.Zero)
            {
                _ = Task.Delay(scheduleIn).ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        _publishScheduled = false;
                        if (_status != JobStatus.Running) { return; } // Finish() publishes the final state itself
                        _lastPublished = DateTimeOffset.UtcNow;
                    }
                    _events.Publish(new JobChanged(Snapshot()));
                }, TaskScheduler.Default);
            }
        }

        private bool _publishScheduled;

        internal void AppendLog(string line)
        {
            lock (_gate)
            {
                _log.AddLast(line);
                while (_log.Count > MaxLogLines) { _log.RemoveFirst(); }
            }
        }

        internal void Finish(JobStatus status, string? error)
        {
            lock (_gate)
            {
                if (_status != JobStatus.Running) { return; }
                _status = status;
                _error = error;
                _endedAt = DateTimeOffset.UtcNow;
                if (status == JobStatus.Succeeded) { _percent = 100; }
            }
            var snapshot = Snapshot();
            _events.Publish(new JobChanged(snapshot));
            _completion.TrySetResult(snapshot);
        }
    }

    /// <summary>Runs jobs, tracks the active ones and keeps a bounded history.</summary>
    public sealed class JobManager
    {
        private const int MaxHistory = 200;
        private readonly EventBus _events;
        private readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>();
        private readonly ConcurrentQueue<string> _order = new ConcurrentQueue<string>();

        public JobManager(EventBus events) => _events = events;

        /// <summary>
        /// Starts <paramref name="work"/> as a job and returns immediately. The work returns an error message
        /// to fail the job (null = success); throwing also fails it; honouring cancellation cancels it.
        /// </summary>
        public Job Start(string kind, string? serverId, string title, Func<JobContext, Task<string?>> work)
        {
            var job = new Job(kind, serverId, title, _events);
            _jobs[job.Id] = job;
            _order.Enqueue(job.Id);
            Trim();
            _events.Publish(new JobChanged(job.Snapshot()));

            _ = Task.Run(async () =>
            {
                Job.SetCurrent(job); // flows into everything the work awaits — lets a plugin question find its job
                try
                {
                    string? error = await work(new JobContext(job)).ConfigureAwait(false);
                    job.Finish(error == null ? JobStatus.Succeeded : JobStatus.Failed, error);
                }
                catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
                {
                    job.Finish(JobStatus.Cancelled, "Cancelled");
                }
                catch (Exception ex)
                {
                    job.Finish(JobStatus.Failed, ex.Message);
                }
            });
            return job;
        }

        public Job? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

        /// <summary>Requests cancellation. Returns false if there's no such running job.</summary>
        public bool Cancel(string id)
        {
            var job = Get(id);
            if (job == null || job.Snapshot().Status != JobStatus.Running) { return false; }
            job.Cancellation.Cancel();
            return true;
        }

        /// <summary>Newest first.</summary>
        public IReadOnlyList<JobSnapshot> Snapshot() =>
            _jobs.Values.Select(j => j.Snapshot()).OrderByDescending(s => s.StartedAt).ToList();

        private void Trim()
        {
            while (_order.Count > MaxHistory && _order.TryPeek(out string? oldest))
            {
                // Never drop a job that's still running.
                if (_jobs.TryGetValue(oldest, out var job) && job.Snapshot().Status == JobStatus.Running) { break; }
                if (_order.TryDequeue(out oldest)) { _jobs.TryRemove(oldest, out _); }
            }
        }
    }
}
