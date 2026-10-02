#nullable enable
using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace WindowsGSM.Engine.Events
{
    /// <summary>
    /// In-process publish/subscribe for everything that happens in the engine. The agent forwards these to
    /// WebSocket clients and the hub relays them from every machine — this is what replaces polling.
    ///
    /// Delivery is synchronous and in order, on the publishing thread. Subscribers must be quick (hand the
    /// event to a queue if you need to do real work); a subscriber that throws is logged and skipped, so
    /// one bad subscriber can never break the engine or starve the others.
    /// </summary>
    public sealed class EventBus
    {
        private ImmutableArray<Action<EngineEvent>> _subscribers = ImmutableArray<Action<EngineEvent>>.Empty;

        public IDisposable Subscribe(Action<EngineEvent> handler)
        {
            ImmutableInterlocked.Update(ref _subscribers, list => list.Add(handler));
            return new Subscription(() => ImmutableInterlocked.Update(ref _subscribers, list => list.Remove(handler)));
        }

        /// <summary>Subscribe to one event type only.</summary>
        public IDisposable Subscribe<T>(Action<T> handler) where T : EngineEvent =>
            Subscribe(e => { if (e is T typed) { handler(typed); } });

        public void Publish(EngineEvent e)
        {
            foreach (var handler in _subscribers)
            {
                try { handler(e); }
                catch (Exception ex) { Debug.WriteLine($"[EventBus] subscriber threw on {e.GetType().Name}: {ex}"); }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Action? _unsubscribe;
            public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
            public void Dispose() { _unsubscribe?.Invoke(); _unsubscribe = null; }
        }
    }
}
