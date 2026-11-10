public static class CapturePolicy
{
    public static async Task<T> ActivateChecked<T>(int pid, string started, Func<int, AppIdentity?> identity, Func<Task<T>> activate) where T : IDisposable
    {
        if (identity(pid)?.Started != started) throw new InvalidOperationException("Selected process identity is unavailable or replaced.");
        T capture = await activate();
        if (identity(pid)?.Started != started) { capture.Dispose(); throw new InvalidOperationException("Selected process changed during activation."); }
        return capture;
    }
}
