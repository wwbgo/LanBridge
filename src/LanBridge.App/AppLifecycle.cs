namespace LanBridge.App;

internal enum LifecycleExitResult
{
    Exited,
    Failed,
    AlreadyInProgress
}

/// <summary>Coordinates backend acknowledgement and UI termination without force-exiting.</summary>
internal sealed class AppLifecycle
{
    private int _exitRequested;

    internal bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    internal async Task<LifecycleExitResult> ShutdownAndExitAsync(
        Func<CancellationToken, Task> shutdownAsync,
        Action closeUi,
        Action<string> showError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shutdownAsync);
        ArgumentNullException.ThrowIfNull(closeUi);
        ArgumentNullException.ThrowIfNull(showError);

        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
            return LifecycleExitResult.AlreadyInProgress;

        try
        {
            await shutdownAsync(cancellationToken).ConfigureAwait(true);
            closeUi();
            return LifecycleExitResult.Exited;
        }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
            Volatile.Write(ref _exitRequested, 0);
            showError($"LanBridge 后端未能正常退出：{ex.Message}。应用仍在运行，后台状态未确认。请重试；如为服务模式，请先停止 Windows 服务。");
            return LifecycleExitResult.Failed;
        }
    }
}
