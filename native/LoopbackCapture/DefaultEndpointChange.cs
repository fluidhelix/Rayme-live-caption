using NAudio.CoreAudioApi;

// One notification latch per helper process. The Core Audio callback does
// not call WASAPI, close streams, or start another recorder.
public sealed class DefaultEndpointChange
{
    // The parent starts a fresh helper only after this process exits.
    public const int ExitCode = 5;
    readonly object gate = new();
    string? selectedId, lastNotification;
    bool notified;
    volatile bool changed;

    public static DefaultEndpointChange? ForSelection(string? selection) => selection == "default" ? new() : null;
    public bool Changed => changed;

    public void Bind(string endpointId)
    {
        lock (gate)
        {
            selectedId = endpointId;
            if (notified && lastNotification != endpointId) changed = true;
        }
    }

    public void Observe(DataFlow flow, Role role, string? endpointId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        lock (gate)
        {
            notified = true;
            lastNotification = endpointId;
            if (selectedId != null && endpointId != selectedId) changed = true;
        }
    }
}
