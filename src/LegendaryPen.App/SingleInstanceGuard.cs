namespace LegendaryPen.App;

/// <summary>Garantit une seule instance par session et permet à un second lancement de prévenir la première.</summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\LegendaryPen.Instance";
    private const string RevealEventName = @"Local\LegendaryPen.Reveal";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _revealRequested;
    private RegisteredWaitHandle? _registration;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _revealRequested = new EventWaitHandle(false, EventResetMode.AutoReset, RevealEventName);
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    public void RequestRevealInFirstInstance() => _revealRequested.Set();

    /// <summary>Le rappel s'exécute sur un thread du pool.</summary>
    public void ListenForRevealRequests(Action onRequested)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _revealRequested,
            (_, _) => onRequested(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _revealRequested.Dispose();

        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
