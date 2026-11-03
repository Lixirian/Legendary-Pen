using System.Diagnostics;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Loupe ronde fondée sur le contrôle de Magnification.dll, visible en partage d'écran et sans effet miroir (PR-3).
/// Toutes les positions sont en pixels physiques.
/// </summary>
internal sealed class MagnifierWindow : IDisposable
{
    private const int Diameter = 320;
    private const double Zoom = 2;

    // Un rafraîchissement bloque le fil de l'interface 3 à 30 ms (mesuré) : à chaque image de l'écran, il priverait
    // le stylo de temps. Ce délai court depuis la fin du précédent, et s'allonge si celui-ci a coûté cher.
    private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromMilliseconds(30);

    private static bool _creationAttempted;

    private readonly IntPtr _host;
    private readonly IntPtr _control;

    private long _lastRefreshEnd;
    private TimeSpan _lastRefreshCost;
    private bool _disposed;

    private MagnifierWindow(IntPtr host, IntPtr control)
    {
        _host = host;
        _control = control;
    }

    /// <summary>
    /// Une seule tentative par processus. Renvoie null en cas d'échec ou si elle a déjà eu lieu :
    /// la loupe reste alors indisponible pour la session, sans message ni plantage.
    /// </summary>
    public static MagnifierWindow? TryCreate()
    {
        if (_creationAttempted)
        {
            return null;
        }

        _creationAttempted = true;

        try
        {
            if (!Win32.InitializeMagnification())
            {
                Debug.WriteLine("Loupe indisponible : MagInitialize a échoué.");
                return null;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Debug.WriteLine($"Loupe indisponible : {exception.Message}");
            return null;
        }

        var host = Win32.CreateMagnifierHost(Diameter);
        var control = host != IntPtr.Zero ? Win32.CreateMagnifierControl(host, Diameter) : IntPtr.Zero;

        if (control == IntPtr.Zero || !Win32.SetMagnifierZoom(control, (float)Zoom))
        {
            Debug.WriteLine("Loupe indisponible : la création de la fenêtre ou du contrôle a échoué.");

            if (host != IntPtr.Zero)
            {
                Win32.Destroy(host);
            }

            Win32.UninitializeMagnification();
            return null;
        }

        // L'essai a montré l'absence de miroir même sans ce filtre : son échec n'empêche pas la loupe.
        if (!Win32.ExcludeFromMagnifier(control, host))
        {
            Debug.WriteLine("Loupe : la liste de filtrage a été refusée.");
        }

        return new MagnifierWindow(host, control);
    }

    /// <summary>
    /// Suit le pointeur et rafraîchit le contenu, même immobile : l'écran change dessous. La place dans l'empilement
    /// est réaffirmée à chaque fois : une fenêtre remontée sous <paramref name="below"/> ne doit pas recouvrir la loupe.
    /// </summary>
    public void Update(PixelPoint pointer, PixelRect monitor, IntPtr below)
    {
        if (_lastRefreshEnd != 0
            && Stopwatch.GetElapsedTime(_lastRefreshEnd) < PresentationGeometry.MagnifierGap(MinimumUpdateInterval, _lastRefreshCost))
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var box = PresentationGeometry.MagnifierWindow(pointer, Diameter, monitor);

        Win32.PlaceBelow(_host, new PixelPoint(box.Left, box.Top), below);
        Win32.RefreshMagnifier(_control, PresentationGeometry.MagnifierSource(pointer, Diameter, Zoom, monitor));

        _lastRefreshEnd = Stopwatch.GetTimestamp();
        _lastRefreshCost = Stopwatch.GetElapsedTime(start, _lastRefreshEnd);
    }

    public void Show() => Win32.ShowWithoutActivating(_host);

    public void Hide()
    {
        Win32.HideWindow(_host);

        // Au retour, la première mise à jour doit passer : la loupe ne réapparaît pas à son ancienne place.
        _lastRefreshEnd = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Win32.Destroy(_host);
        Win32.UninitializeMagnification();
    }
}
