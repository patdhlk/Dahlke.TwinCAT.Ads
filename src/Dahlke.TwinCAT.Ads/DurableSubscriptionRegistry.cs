using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// One restore attempt's cancellation bound plus whatever must be disposed with
/// it, so an adapter can hand
/// <see cref="DurableSubscriptionRegistry{TTarget, THandle, TMeta}"/> a bound
/// built from linked sources (a per-attempt timeout linked to the adapter's
/// shutdown) and have every piece retired after the attempt. The default value
/// is "no bound": <see cref="CancellationToken.None"/> and nothing to dispose.
/// </summary>
internal readonly struct SubscriptionRestoreBound : IDisposable
{
    private readonly IDisposable? _first;
    private readonly IDisposable? _second;

    public SubscriptionRestoreBound(CancellationToken token, IDisposable? first = null, IDisposable? second = null)
    {
        Token = token;
        _first = first;
        _second = second;
    }

    public CancellationToken Token { get; }

    public void Dispose()
    {
        _first?.Dispose();
        _second?.Dispose();
    }
}

/// <summary>
/// The one owner of the durable-subscription invariants. A durable subscription
/// survives its target being torn down and replaced: the caller's handle stays
/// valid across every swap, and the registry re-registers the subscription on
/// each replacement target. <see cref="AdsConnectionFacade"/> (symbol
/// subscriptions across pool reconnects) and <see cref="AdsRawChannel"/> (raw
/// notifications across transport rebuilds) are its two adapters.
/// </summary>
/// <typeparam name="TTarget">
/// What subscriptions are registered against — a managed connection, a raw
/// transport. Compared by reference identity throughout: a replacement target is
/// a different instance by construction.
/// </typeparam>
/// <typeparam name="THandle">
/// What a successful registration yields and what must be handed back when it
/// ends up owned by nobody — an <see cref="IDisposable"/>, a device notification
/// handle.
/// </typeparam>
/// <typeparam name="TMeta">
/// Adapter-owned description of one subscription (a symbol path, an
/// index-group/offset), surfaced back on restore failures so the adapter can log
/// in its own vocabulary.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Publish-before-first-registration.</b> <see cref="AddAsync"/> places the
/// record in the registry BEFORE its initial registration runs. A target swap in
/// that gap then SEES the record and restores it; registering first and
/// publishing second would lose the subscription entirely. The reservation
/// (below) is what makes that safe rather than double-registering: when the
/// restore gets there first — which it always does when this very subscribe is
/// what caused the target to be built — the subscribe's own registration finds
/// the reservation and skips.
/// </para>
/// <para>
/// <b>Reserve, then commit-or-hand-back.</b> <see cref="RegisterAsync"/>
/// reserves the target under the record's gate BEFORE awaiting the underlying
/// registration, making registration exactly-once per target. After the await it
/// commits only if the record is still live, the reservation is still its own,
/// and the adapter's <c>commitGuard</c> (if any) agrees the target is still the
/// one in service. Otherwise the just-created registration is handed back
/// through <c>discard</c>: the subscriber disposed mid-flight, or a newer swap
/// already won, and a registration committed to a record nobody holds is one
/// nothing will ever remove. A FAILED registration releases the reservation —
/// only if still its own — so the next attempt retries.
/// </para>
/// <para>
/// <b>Restore-on-swap.</b> <see cref="RestoreAllAsync"/> re-registers every live
/// record against the replacement target. Each record is isolated: a failure is
/// reported through <c>onRestoreFailure</c> and the record RETAINED, so the next
/// swap retries it (and a subscribe still parked on that record registers it
/// itself). The method deliberately takes no <see cref="CancellationToken"/>: a
/// swap is triggered by whichever operation happens to touch the adapter next,
/// carrying whatever token that caller passed, and threading it in would mean
/// one caller walking away unregisters EVERY subscription. Each record's attempt
/// is instead bounded by the adapter-configured <c>restoreBound</c>, and the
/// loop cut short only by the adapter's own <c>stopRestoring</c> signal (its
/// shutdown), never by a caller.
/// </para>
/// <para>
/// <b>Disposal and the delivery guarantee.</b> Disposing the handle removes the
/// record from the registry FIRST, then takes the current registration exactly
/// once and hands it to <c>discard</c>. Registry membership is therefore the
/// liveness check a delivery path can trust: it is gone before
/// <see cref="IDisposable.Dispose"/> returns, whereas removing the underlying
/// registration may involve a round trip that completes later or not at all.
/// Adapters whose delivery is push-based consult <see cref="Contains"/> before
/// invoking a handler; that is what makes "no handler fires after disposal
/// completes" true rather than hopeful. Old registrations left behind by a
/// reservation overwrite are dropped, never discarded — they belonged to a
/// now-dead target whose teardown takes its registrations with it.
/// </para>
/// </remarks>
internal sealed class DurableSubscriptionRegistry<TTarget, THandle, TMeta>
    where TTarget : class
{
    /// <summary>
    /// Registers one subscription against <paramref name="target"/> and yields
    /// the registration. The record stores this delegate already bound to its
    /// subscription's arguments and callback, so the registry never branches on
    /// callback shape — re-registration re-invokes one delegate.
    /// <paramref name="record"/> is the registrar's OWN record, handed in so a
    /// push-delivery adapter can close its delivery callback over it for the
    /// <see cref="Contains"/> liveness check — the record does not exist yet at
    /// the point the adapter authors the delegate.
    /// </summary>
    public delegate Task<THandle> Registrar(Record record, TTarget target, CancellationToken ct);

    private readonly ConcurrentDictionary<Record, byte> _records = new();
    private readonly Action<TTarget, THandle> _discard;
    private readonly Func<TTarget, bool>? _commitGuard;
    private readonly Func<SubscriptionRestoreBound>? _restoreBound;
    private readonly Func<bool>? _stopRestoring;
    private readonly Action<TMeta, Exception>? _onRestoreFailure;

    /// <param name="discard">
    /// Hands back a registration that ended up owned by nobody, or tears down the
    /// live registration on dispose. Invoked outside the record's gate. Must not
    /// throw for the "target already dead" case — that is its ordinary input.
    /// </param>
    /// <param name="commitGuard">
    /// Optional extra commit condition, evaluated under the record's gate: is
    /// <typeparamref name="TTarget"/> still the instance in service? The facade
    /// checks its current pointer here; the raw channel relies on reservation
    /// identity alone (its swaps re-reserve before a stale commit can land).
    /// </param>
    /// <param name="restoreBound">
    /// Per-record bound for restore attempts; the registry disposes each bound's
    /// scope after its record's attempt. <see langword="null"/> leaves each
    /// attempt bounded only by the underlying registration path.
    /// </param>
    /// <param name="stopRestoring">
    /// Checked before each record in a restore pass; <see langword="true"/> stops
    /// the pass (adapter shutdown). The records keep their registry membership —
    /// stopping is about not waiting out timeouts onto a dying target, not about
    /// forgetting subscriptions.
    /// </param>
    /// <param name="onRestoreFailure">
    /// Receives each failed restore attempt's metadata and exception. The record
    /// is retained regardless; this is reporting, not policy.
    /// </param>
    public DurableSubscriptionRegistry(
        Action<TTarget, THandle> discard,
        Func<TTarget, bool>? commitGuard = null,
        Func<SubscriptionRestoreBound>? restoreBound = null,
        Func<bool>? stopRestoring = null,
        Action<TMeta, Exception>? onRestoreFailure = null)
    {
        _discard = discard;
        _commitGuard = commitGuard;
        _restoreBound = restoreBound;
        _stopRestoring = stopRestoring;
        _onRestoreFailure = onRestoreFailure;
    }

    /// <summary>No live records — nothing to restore, nothing pinning the adapter.</summary>
    public bool IsEmpty => _records.IsEmpty;

    /// <summary>Number of live records (adapter diagnostics).</summary>
    public int Count => _records.Count;

    /// <summary>
    /// Registry membership — the delivery liveness check. Gone before the
    /// caller's <see cref="IDisposable.Dispose"/> returns.
    /// </summary>
    public bool Contains(Record record) => _records.ContainsKey(record);

    /// <summary>
    /// Creates a record, publishes it to the registry, then runs
    /// <paramref name="initialRegister"/> — the adapter's initial registration,
    /// which acquires its target its own way (wait-then-throw snapshot, transport
    /// build) and calls <see cref="RegisterAsync"/>. On failure the record is
    /// rolled back exactly like a dispose — removed, and anything a concurrent
    /// restore already acquired for it handed back — so a never-registered
    /// subscription cannot linger pinning the adapter. Returns the caller's
    /// handle; disposing it is idempotent and permanent.
    /// </summary>
    public async Task<IDisposable> AddAsync(
        TMeta metadata, Registrar registrar, Func<Record, Task> initialRegister)
    {
        var record = new Record(metadata, registrar);
        _records[record] = 0;

        try
        {
            await initialRegister(record).ConfigureAwait(false);
        }
        catch
        {
            RemoveAndDiscard(record);
            throw;
        }

        return new Handle(this, record);
    }

    /// <summary>
    /// The reserve → register → commit-or-hand-back flow. Exactly-once per
    /// target; safe to race with disposal, a newer swap, and itself.
    /// </summary>
    public async Task RegisterAsync(Record record, TTarget target, CancellationToken ct)
    {
        if (!record.TryReserve(target))
            return; // already reserved/registered against this very target

        THandle fresh;
        try
        {
            fresh = await record.Register(record, target, ct).ConfigureAwait(false);
        }
        catch
        {
            record.ReleaseReservation(target);
            throw;
        }

        if (_records.ContainsKey(record) && record.TryCommit(target, fresh, _commitGuard))
            return;

        // Nobody owns this registration: disposed mid-flight, a newer target took
        // the reservation, or the commit guard saw the target out of service.
        // Ordering makes this airtight — disposal always clears the reservation,
        // so a dispose that lands after the ContainsKey check still fails the
        // commit and the registration is handed back here.
        _discard(target, fresh);
    }

    /// <summary>
    /// Re-registers every live record against <paramref name="target"/>. See the
    /// class remarks for why this takes no <see cref="CancellationToken"/>.
    /// </summary>
    public async Task RestoreAllAsync(TTarget target)
    {
        foreach (var record in _records.Keys)
        {
            if (_stopRestoring?.Invoke() == true)
                break;

            using var bound = _restoreBound is null ? default : _restoreBound();
            try
            {
                await RegisterAsync(record, target, bound.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onRestoreFailure?.Invoke(record.Metadata, ex);
            }
        }
    }

    private void RemoveAndDiscard(Record record)
    {
        // Remove from the registry FIRST so no future restore re-registers it and
        // delivery's liveness check goes false, THEN take the registration —
        // taking also clears the reservation, which is what tells a registration
        // still awaiting its target that its result now belongs to nobody.
        _records.TryRemove(record, out _);

        if (record.TryTakeRegistration(out var target, out var handle))
            _discard(target, handle!);
    }

    /// <summary>
    /// One durable subscription: its registrar, its adapter metadata, and where
    /// it is CURRENTLY registered. The registration — the target it lives on and
    /// the handle THAT target issued — is rewritten on every re-registration; the
    /// record's identity is what stays stable for the caller's lifetime. The two
    /// halves are kept behind one gate because they are only meaningful together:
    /// a handle is scoped to the target that issued it.
    /// </summary>
    public sealed class Record
    {
        private readonly object _gate = new();

        // The target this record is reserved against and — once the target has
        // answered — the handle it issued. _hasHandle distinguishes "reserved,
        // answer still pending" from "registered": only the latter has anything
        // to remove. _disposed flips once, under the same gate, so a
        // registration completing after dispose is refused at commit.
        private TTarget? _target;
        private THandle? _handle;
        private bool _hasHandle;
        private bool _disposed;

        internal Record(TMeta metadata, Registrar register)
        {
            Metadata = metadata;
            Register = register;
        }

        /// <summary>Adapter-owned description, surfaced on restore failures.</summary>
        public TMeta Metadata { get; }

        internal Registrar Register { get; }

        /// <summary>
        /// Claims <paramref name="target"/>, unless disposed or already claimed
        /// for that very target (the exactly-once guard). Any previous
        /// registration reference is dropped rather than discarded: a new target
        /// is only ever swapped in after the previous one was torn down, which
        /// takes its registrations with it.
        /// </summary>
        internal bool TryReserve(TTarget target)
        {
            lock (_gate)
            {
                if (_disposed || ReferenceEquals(_target, target))
                    return false;

                _target = target;
                _handle = default;
                _hasHandle = false;
                return true;
            }
        }

        /// <summary>
        /// Gives up the reservation after a failed registration so the next
        /// attempt retries — but only if it is still this target's.
        /// </summary>
        internal void ReleaseReservation(TTarget target)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_target, target))
                    return;

                _target = null;
                _hasHandle = false;
            }
        }

        /// <summary>
        /// Records the handle <paramref name="target"/> has just issued — if this
        /// record is still live, still holds the reservation for that target, and
        /// the adapter's guard (evaluated under the gate) still considers the
        /// target in service. <see langword="false"/> means the caller must hand
        /// the handle back.
        /// </summary>
        internal bool TryCommit(TTarget target, THandle handle, Func<TTarget, bool>? guard)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_target, target))
                    return false;
                if (guard is not null && !guard(target))
                    return false;

                _handle = handle;
                _hasHandle = true;
                return true;
            }
        }

        /// <summary>
        /// Marks the record disposed and takes the current registration —
        /// reservation included — so exactly one caller ever discards it.
        /// <see langword="false"/> means there is nothing to remove: never
        /// registered, or reserved with the answer still outstanding (clearing
        /// the reservation is what tells that pending registration to hand its
        /// result back).
        /// </summary>
        internal bool TryTakeRegistration([NotNullWhen(true)] out TTarget? target, out THandle? handle)
        {
            lock (_gate)
            {
                var registered = _hasHandle;
                target = registered ? _target : null;
                handle = _handle;

                _disposed = true;
                _target = null;
                _handle = default;
                _hasHandle = false;
                return registered && target is not null;
            }
        }
    }

    /// <summary>
    /// The caller's handle. Holds the record but answers liveness only through
    /// the registry — disposing removes the record first, then discards the
    /// registration exactly once. Idempotent via the record's own disposed flag.
    /// </summary>
    private sealed class Handle : IDisposable
    {
        private readonly DurableSubscriptionRegistry<TTarget, THandle, TMeta> _registry;
        private readonly Record _record;

        public Handle(DurableSubscriptionRegistry<TTarget, THandle, TMeta> registry, Record record)
        {
            _registry = registry;
            _record = record;
        }

        public void Dispose() => _registry.RemoveAndDiscard(_record);
    }
}
