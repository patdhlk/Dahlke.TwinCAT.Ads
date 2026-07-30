using System.Collections.Concurrent;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The one owner of the simulated/in-memory subscriber mechanics: per-key
/// callback registration, snapshot-then-fire, per-callback exception isolation,
/// and idempotent disposal. Composed alongside
/// <see cref="InMemoryPlcStore{TKey, TValue}"/> by every simulated/in-memory
/// data plane — the store DECIDES whether a write is a change, this registry
/// DELIVERS it.
/// </summary>
/// <remarks>
/// <para>
/// The two are separate types because their lifetimes differ per adapter: the
/// symbol simulation owns store and registry together per connection, while the
/// raw simulation's store is factory-owned and durable but its registry is
/// per-transport — disposing a transport drops its subscriptions exactly as
/// disposing a real <c>AdsClient</c> does, and
/// <see cref="AdsRawChannel"/> re-registers them against the replacement.
/// </para>
/// <para>
/// <b>Delivery mechanics, stated once.</b> A fire takes a snapshot of the key's
/// callbacks under the key's lock, then invokes each OUTSIDE the lock so a
/// callback cannot deadlock on a re-entrant write. Under concurrent
/// fire-vs-dispose a callback is either in the snapshot (and fires) or already
/// removed (and does not) — no torn reads. A throwing callback is caught,
/// reported through the optional error hook, and never suppresses other
/// subscribers nor propagates to the writer. Registration handles are allocated
/// under the same lock; disposing one is idempotent and affects only that
/// registration.
/// </para>
/// <para>
/// The key comparer should MATCH the paired store's: PLC symbol paths are
/// case-insensitive, and a subscriber side that compares differently from the
/// store side silently never fires for a writer using another casing.
/// </para>
/// </remarks>
internal sealed class SubscriberRegistry<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, SubscriberList> _subscribers;
    private readonly Action<TKey, Exception>? _onCallbackError;

    public SubscriberRegistry(
        IEqualityComparer<TKey>? keyComparer = null,
        Action<TKey, Exception>? onCallbackError = null)
    {
        _subscribers = keyComparer is null ? new() : new(keyComparer);
        _onCallbackError = onCallbackError;
    }

    /// <summary>
    /// Registers a callback for one key. The returned disposable unregisters
    /// exactly this callback; dispose is idempotent and thread-safe.
    /// </summary>
    public IDisposable Subscribe(TKey key, Action<TKey, TValue> callback)
        => _subscribers.GetOrAdd(key, _ => new SubscriberList()).Add(callback);

    /// <summary>Delivers a value to every callback registered for the key.</summary>
    public void Fire(TKey key, TValue value)
    {
        if (_subscribers.TryGetValue(key, out var list))
            list.Fire(key, value, _onCallbackError);
    }

    /// <summary>
    /// Holds all callbacks registered for a single key. A plain lock (not a
    /// concurrent collection) because add, remove, and snapshot-and-fire need to
    /// be atomic as a group.
    /// </summary>
    private sealed class SubscriberList
    {
        private readonly object _lock = new();
        private readonly Dictionary<long, Action<TKey, TValue>> _callbacks = new();
        private long _nextId;

        public IDisposable Add(Action<TKey, TValue> callback)
        {
            long id;
            lock (_lock)
            {
                id = _nextId++;
                _callbacks[id] = callback;
            }
            return new Registration(this, id);
        }

        private void Remove(long id)
        {
            lock (_lock)
                _callbacks.Remove(id);
        }

        public void Fire(TKey key, TValue value, Action<TKey, Exception>? onError)
        {
            Action<TKey, TValue>[] snapshot;
            lock (_lock)
                snapshot = [.. _callbacks.Values];

            foreach (var callback in snapshot)
            {
                try
                {
                    callback(key, value);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(key, ex);
                }
            }
        }

        private sealed class Registration(SubscriberList owner, long id) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Remove(id);
            }
        }
    }
}
