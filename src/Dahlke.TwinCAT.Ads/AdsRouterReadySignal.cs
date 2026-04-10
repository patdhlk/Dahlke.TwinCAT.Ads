namespace Dahlke.TwinCAT.Ads;

public sealed class AdsRouterReadySignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task WaitAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _tcs.TrySetCanceled());
        await _tcs.Task;
    }

    public void SetReady() => _tcs.TrySetResult();
    public void SetFailed() => _tcs.TrySetCanceled();
}
