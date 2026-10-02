#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.Engine.Events;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Operations
{
    /// <summary>A plugin question waiting for someone to answer it.</summary>
    public sealed record PendingPrompt(string Id, string JobId, string? ServerId, string Key, string Title, string Message,
        DateTimeOffset AskedAt, DateTimeOffset ExpiresAt);

    /// <summary>A job paused on a question ("Accept the EULA?").</summary>
    public sealed record PromptRaised(PendingPrompt Prompt) : EngineEvent;

    /// <summary>The question was answered, timed out (answer false, no one) or its job was cancelled.</summary>
    public sealed record PromptResolved(string PromptId, string JobId, string? ServerId, bool Answer, string? AnsweredBy) : EngineEvent;

    /// <summary>
    /// Live answers for plugin questions that weren't consented to up front. Once enabled, a question asked
    /// inside a job pauses that job, is published as <see cref="PromptRaised"/>, and waits for
    /// <see cref="Answer"/> — or times out as "no". Questions asked outside a job are still "no": there is
    /// nobody watching them.
    ///
    /// Opt-in because it's process-wide (the legacy plugin API is static): a host with a live UI enables it;
    /// tests and unattended hosts leave it off and get the safe default immediately.
    /// </summary>
    public sealed class PromptBroker : IDisposable
    {
        private sealed class Waiting
        {
            public required PendingPrompt Prompt;
            public readonly TaskCompletionSource<(bool answer, string? by)> Answer =
                new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly EventBus _events;
        private readonly ConcurrentDictionary<string, Waiting> _waiting = new ConcurrentDictionary<string, Waiting>();
        private Func<string, string, string, Task<bool>>? _installed;

        public PromptBroker(EventBus events) => _events = events;

        /// <summary>How long a question waits before it's answered "no".</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

        public bool Enabled => _installed != null && UserPrompt.AsyncHandler == _installed;

        public void Enable()
        {
            _installed ??= AskAsync;
            UserPrompt.AsyncHandler = _installed;
        }

        public void Disable()
        {
            if (_installed != null && UserPrompt.AsyncHandler == _installed) { UserPrompt.AsyncHandler = null; }
            foreach (var w in _waiting.Values) { w.Answer.TrySetResult((false, null)); }
        }

        public IReadOnlyList<PendingPrompt> Pending() =>
            _waiting.Values.Select(w => w.Prompt).OrderBy(p => p.AskedAt).ToList();

        public PendingPrompt? Get(string promptId) => _waiting.TryGetValue(promptId, out var w) ? w.Prompt : null;

        /// <summary>Answers a waiting question. False if it's no longer waiting.</summary>
        public bool Answer(string promptId, bool answer, string? answeredBy) =>
            _waiting.TryGetValue(promptId, out var w) && w.Answer.TrySetResult((answer, answeredBy));

        private async Task<bool> AskAsync(string key, string title, string message)
        {
            var job = Job.Current;
            if (job == null) { return false; }

            var now = DateTimeOffset.UtcNow;
            var waiting = new Waiting
            {
                Prompt = new PendingPrompt(Guid.NewGuid().ToString("N").Substring(0, 12), job.Id, job.ServerId, key,
                    title ?? string.Empty, message ?? string.Empty, now, now + Timeout),
            };
            _waiting[waiting.Prompt.Id] = waiting;
            job.Update(null, "Waiting for an answer: " + waiting.Prompt.Title);
            job.AppendLog($"Question: {waiting.Prompt.Title} — {waiting.Prompt.Message}");
            _events.Publish(new PromptRaised(waiting.Prompt));

            (bool answer, string? by) result = (false, null);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token);
                timeout.CancelAfter(Timeout);
                var cancelled = Task.Delay(System.Threading.Timeout.Infinite, timeout.Token);
                if (await Task.WhenAny(waiting.Answer.Task, cancelled).ConfigureAwait(false) == waiting.Answer.Task)
                {
                    result = waiting.Answer.Task.Result;
                }
            }
            finally
            {
                _waiting.TryRemove(waiting.Prompt.Id, out _);
            }

            job.AppendLog(result.by == null
                ? $"No answer to \"{waiting.Prompt.Title}\" — treated as no."
                : $"{result.by} answered {(result.answer ? "yes" : "no")} to \"{waiting.Prompt.Title}\".");
            job.Update(null, result.answer ? "Continuing" : "Declined");
            _events.Publish(new PromptResolved(waiting.Prompt.Id, job.Id, job.ServerId, result.answer, result.by));
            return result.answer;
        }

        public void Dispose() => Disable();
    }
}
