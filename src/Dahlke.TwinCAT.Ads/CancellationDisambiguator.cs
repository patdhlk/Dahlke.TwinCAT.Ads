namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Helper that maps a caught <see cref="OperationCanceledException"/> to the correct
/// public exception type based on which token actually fired.
/// </summary>
/// <remarks>
/// <para>
/// Beckhoff async APIs accept a single <see cref="CancellationToken"/>. Inside
/// <see cref="AdsConnection"/> we always pass a <em>linked</em> token that unifies the
/// caller's token with a per-operation timeout. When the linked token fires we therefore
/// cannot tell from the exception alone which source caused it.
/// </para>
/// <para>
/// The standard disambiguation pattern checks <c>callerCt.IsCancellationRequested</c>:
/// </para>
/// <list type="bullet">
///   <item>If it is set, the caller requested cancellation → re-throw (or create) an
///         <see cref="OperationCanceledException"/> with the original caller token so that
///         <c>catch (OperationCanceledException ex) when (ex.CancellationToken == myCt)</c>
///         works correctly up-stack.</item>
///   <item>Otherwise only the timeout CTS fired → throw a <see cref="TimeoutException"/>
///         with a message that names the symbol, PLC, and configured timeout so
///         operators can diagnose without correlating IDs.</item>
/// </list>
/// <para>
/// Exposed as <see langword="internal"/> so unit tests in
/// <c>Dahlke.TwinCAT.Ads.Tests</c> (via <c>InternalsVisibleTo</c>) can cover the
/// logic independently of hardware.
/// </para>
/// </remarks>
internal static class CancellationDisambiguator
{
    /// <summary>
    /// Creates the correct exception for a cancellation event during a PLC read or write.
    /// </summary>
    /// <param name="callerCt">The original cancellation token supplied by the caller.</param>
    /// <param name="symbolPath">Symbol path, used in the <see cref="TimeoutException"/> message.</param>
    /// <param name="plcId">PLC identifier, used in the <see cref="TimeoutException"/> message.</param>
    /// <param name="timeoutMs">Configured timeout in milliseconds, used in the message.</param>
    /// <returns>
    /// An <see cref="OperationCanceledException"/> carrying <paramref name="callerCt"/> when the
    /// caller cancelled, or a <see cref="TimeoutException"/> when the per-target timeout elapsed.
    /// </returns>
    public static Exception CreateException(
        CancellationToken callerCt,
        string symbolPath,
        string plcId,
        int timeoutMs)
    {
        if (callerCt.IsCancellationRequested)
            return new OperationCanceledException(callerCt);

        return new TimeoutException(
            $"Read/write of symbol '{symbolPath}' on PLC '{plcId}' exceeded the configured timeout of {timeoutMs} ms.");
    }
}
