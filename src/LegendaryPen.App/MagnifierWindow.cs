using System.Diagnostics;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Loupe ronde fondée sur le contrôle de Magnification.dll, visible en partage d'écran et sans effet miroir (PR-3).
/// Tout ce qui touche à Magnification.dll vit sur un fil dédié : un rafraîchissement peut coûter 30 ms, que le fil de
/// l'interface doit garder pour le stylo. Les méthodes publiques s'appellent depuis le fil de l'interface.
/// Toutes les positions sont en pixels physiques, et le diamètre aussi : 320 pixels physiques, quelle que soit l'échelle
/// de l'écran.
/// </summary>
internal sealed class MagnifierWindow : IDisposable
{
    private const int Diameter = 320;
    private const double Zoom = 2;

    private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan HideTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CaptureExclusionTimeout = TimeSpan.FromMilliseconds(200);

    private static bool _startAttempted;

    private readonly Dispatcher _dispatcher;
    private readonly Action _onUnavailable;
    private readonly Func<PixelPoint> _readPointer;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _hidden = new(true);
    private readonly ManualResetEventSlim _captureExclusionSettled = new(false);
    private readonly Thread _thread;

    // Remplacé en bloc par le fil de l'interface, seulement lu par le fil de la loupe.
    private volatile Request _request;
    private volatile bool _stopping;
    private volatile bool _finished;

    // L'interface écrit ce qu'elle veut, le fil de la loupe s'y aligne à chaque cycle : une réponse tardive ne peut pas laisser la loupe exclue.
    private volatile bool _captureExclusionWanted;
    private volatile bool _captureExcluded;
    private bool _disposed;

    private MagnifierWindow(IReadOnlyList<MonitorArea> monitors, IntPtr below, Action onUnavailable, Func<PixelPoint> readPointer)
    {
        _request = new Request(monitors.ToArray(), below, false);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _onUnavailable = onUnavailable;
        _readPointer = readPointer;
        _thread = new Thread(Run) { IsBackground = true, Name = "Legendary Pen - Loupe" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>
    /// Démarre le fil, une seule fois par processus. Renvoie null si c'est déjà fait. Un échec sur le fil arrive ensuite
    /// par <paramref name="onUnavailable"/>, sur le fil de l'interface, sans message ni plantage.
    /// </summary>
    public static MagnifierWindow? TryStart(IReadOnlyList<MonitorArea> monitors, IntPtr below, Action onUnavailable, Func<PixelPoint>? readPointer = null)
    {
        if (_startAttempted)
        {
            return null;
        }

        _startAttempted = true;

        var magnifier = new MagnifierWindow(monitors, below, onUnavailable, readPointer ?? Win32.GetPointer);
        magnifier._thread.Start();
        return magnifier;
    }

    public void SetMonitors(IReadOnlyList<MonitorArea> monitors) => Publish(_request with { Monitors = monitors.ToArray() });

    public void KeepBelow(IntPtr window) => Publish(_request with { Below = window });

    /// <summary>La loupe apparaît au cycle suivant, déjà à sa place.</summary>
    public void Show() => Publish(_request with { Visible = true });

    /// <summary>Ne rend la main qu'une fois la loupe réellement cachée, ou après le délai maximal (PR-8).</summary>
    public void Hide()
    {
        if (_disposed || (!_request.Visible && _hidden.IsSet))
        {
            return;
        }

        _hidden.Reset();
        Publish(_request with { Visible = false });

        // Après le Reset : si le fil s'est arrêté avant, plus personne ne le rétablira.
        if (!_finished && !_hidden.Wait(HideTimeout))
        {
            Debug.WriteLine("La loupe n'a pas confirmé sa disparition.");
        }
    }

    /// <summary>
    /// Retire la loupe des captures d'écran, sans la masquer à l'écran, jusqu'à <see cref="RestoreToCapture"/>. Seul le fil
    /// de la loupe peut le faire sur sa fenêtre : la demande lui est transmise, et la réponse attendue au plus 200 ms.
    /// Renvoie faux si la loupe peut encore figurer dans une capture.
    /// </summary>
    public bool ExcludeFromCapture()
    {
        // Une loupe cachée ou arrêtée ne figure dans aucune capture.
        if (_disposed || _finished || (!_request.Visible && _hidden.IsSet))
        {
            return true;
        }

        _captureExclusionSettled.Reset();
        _captureExclusionWanted = true;
        _wake.Set();

        if (_captureExclusionSettled.Wait(CaptureExclusionTimeout) && _captureExcluded)
        {
            return true;
        }

        Debug.WriteLine("La loupe n'a pas confirmé son retrait des captures.");
        RestoreToCapture();
        return _finished;
    }

    /// <summary>Rend la loupe aux captures d'écran, au prochain cycle de son fil : l'interface ne l'attend pas.</summary>
    public void RestoreToCapture()
    {
        if (!_disposed && _captureExclusionWanted)
        {
            _captureExclusionWanted = false;
            _wake.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _wake.Set();

        if (_thread.Join(StopTimeout))
        {
            _wake.Dispose();
            _hidden.Dispose();
            _captureExclusionSettled.Dispose();
        }
        else
        {
            Debug.WriteLine("La loupe ne s'est pas arrêtée à temps.");
        }
    }

    private void Publish(Request request)
    {
        if (!_disposed)
        {
            _request = request;
            _wake.Set();
        }
    }

    private void Run()
    {
        var initialized = false;
        var host = IntPtr.Zero;
        var failed = false;

        try
        {
            initialized = Win32.InitializeMagnification();
            host = initialized ? Win32.CreateMagnifierHost(Diameter) : IntPtr.Zero;
            var control = host != IntPtr.Zero ? Win32.CreateMagnifierControl(host, Diameter) : IntPtr.Zero;

            if (control != IntPtr.Zero && Win32.SetMagnifierZoom(control, (float)Zoom))
            {
                // L'essai a montré l'absence de miroir même sans ce filtre : son échec n'empêche pas la loupe.
                if (!Win32.ExcludeFromMagnifier(control, host))
                {
                    Debug.WriteLine("Loupe : la liste de filtrage a été refusée.");
                }

                RunCycles(host, control);
            }
            else
            {
                Debug.WriteLine("Loupe indisponible : la création de la fenêtre ou du contrôle a échoué.");
                failed = true;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Loupe indisponible : {exception.Message}");
            failed = true;
        }

        Release(initialized, host);

        _finished = true;
        _hidden.Set();
        _captureExclusionSettled.Set();

        if (failed)
        {
            _dispatcher.BeginInvoke(_onUnavailable);
        }
    }

    private void RunCycles(IntPtr host, IntPtr control)
    {
        var wake = _wake.SafeWaitHandle.DangerousGetHandle();
        var shown = false;

        while (!_stopping)
        {
            var start = Stopwatch.GetTimestamp();
            var request = _request;

            AlignCaptureExclusion(host);

            if (request.Visible)
            {
                shown |= Refresh(request, host, control, shown);
                Win32.PumpMessages();
                WaitForNextCycle(wake, start);
            }
            else
            {
                if (shown)
                {
                    Win32.HideWindow(host);
                    shown = false;
                }

                _hidden.Set();
                Win32.PumpMessages();

                // Cachée, la loupe ne consomme rien : seul un message ou une nouvelle demande la réveille.
                while (Win32.WaitForMessageOrSignal(wake, uint.MaxValue))
                {
                    Win32.PumpMessages();
                }
            }
        }
    }

    private void AlignCaptureExclusion(IntPtr host)
    {
        var wanted = _captureExclusionWanted;

        if (wanted != _captureExcluded)
        {
            if (Win32.SetExcludedFromCapture(host, wanted))
            {
                _captureExcluded = wanted;
            }
            else
            {
                Debug.WriteLine("Loupe : le changement d'exclusion des captures a été refusé.");
            }
        }

        if (wanted)
        {
            _captureExclusionSettled.Set();
        }
    }

    /// <summary>
    /// Suit le pointeur et rafraîchit le contenu, même immobile : l'écran change dessous. La place dans l'empilement est
    /// réaffirmée à chaque cycle : une fenêtre remontée sous la barre d'outils ne doit pas recouvrir la loupe.
    /// Renvoie vrai si la loupe est désormais affichée.
    /// </summary>
    private bool Refresh(Request request, IntPtr host, IntPtr control, bool shown)
    {
        var pointer = _readPointer();

        foreach (var monitor in request.Monitors)
        {
            if (monitor.Bounds.Contains(pointer.X, pointer.Y))
            {
                var box = PresentationGeometry.MagnifierWindow(pointer, Diameter, monitor.Bounds);

                Win32.PlaceBelow(host, new PixelPoint(box.Left, box.Top), request.Below);
                Win32.RefreshMagnifier(control, PresentationGeometry.MagnifierSource(pointer, Diameter, Zoom, monitor.Bounds));

                if (!shown)
                {
                    Win32.ShowWithoutActivating(host);
                }

                return true;
            }
        }

        return false;
    }

    // Une nouvelle demande interrompt l'attente : cacher la loupe n'attend pas la fin de l'intervalle.
    private static void WaitForNextCycle(IntPtr wake, long start)
    {
        var remaining = MinimumUpdateInterval - Stopwatch.GetElapsedTime(start);

        while (remaining > TimeSpan.Zero && Win32.WaitForMessageOrSignal(wake, (uint)Math.Ceiling(remaining.TotalMilliseconds)))
        {
            Win32.PumpMessages();
            remaining = MinimumUpdateInterval - Stopwatch.GetElapsedTime(start);
        }
    }

    private static void Release(bool initialized, IntPtr host)
    {
        try
        {
            if (host != IntPtr.Zero)
            {
                Win32.Destroy(host);
            }

            if (initialized)
            {
                Win32.UninitializeMagnification();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.Message);
        }
    }

    private sealed record Request(MonitorArea[] Monitors, IntPtr Below, bool Visible);
}
