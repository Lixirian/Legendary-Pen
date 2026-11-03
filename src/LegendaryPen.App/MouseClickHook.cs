using System.Diagnostics;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Signale les appuis sur les boutons gauche et droit de la souris, où qu'ils tombent (WH_MOUSE_LL).
/// Le crochet vit sur un fil dédié avec sa propre boucle de messages : le rappel se borne à copier la position
/// puis à la poster au fil de l'interface, et rend toujours la main à CallNextHookEx, sans jamais retenir un clic.
/// Aucune position ni aucun clic n'est enregistré ou envoyé. Les méthodes publiques s'appellent depuis le fil de l'interface.
/// </summary>
internal sealed class MouseClickHook : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly Action _onUnavailable;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    // Le délégué reste référencé : le ramasse-miettes ne doit pas le libérer tant que Windows peut l'appeler.
    private readonly Win32.MouseHookProcedure _procedure;

    private uint _threadId;
    private volatile bool _stopping;
    private bool _disposed;

    /// <param name="onUnavailable">Appelé sur le fil de l'interface si Windows refuse le crochet.</param>
    public MouseClickHook(Action onUnavailable)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _onUnavailable = onUnavailable;
        _procedure = OnMouseMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Legendary Pen - Clics" };
        _thread.Start();
    }

    /// <summary>Levé sur le fil de l'interface : position en pixels physiques, vrai pour le bouton droit.</summary>
    public event Action<PixelPoint, bool>? Clicked;

    /// <summary>Retire le crochet et arrête le fil, sans dépasser une seconde.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        var clock = Stopwatch.StartNew();

        // La file de messages du fil doit exister avant qu'on puisse lui demander de s'arrêter.
        if (_ready.Wait(StopTimeout))
        {
            Win32.PostQuit(_threadId);
        }

        if (_thread.Join(StopTimeout > clock.Elapsed ? StopTimeout - clock.Elapsed : TimeSpan.Zero))
        {
            _ready.Dispose();
        }
        else
        {
            Debug.WriteLine("The click hook thread did not stop in time.");
        }
    }

    private void Run()
    {
        var hook = IntPtr.Zero;

        try
        {
            _threadId = Win32.CurrentThreadId();
            Win32.CreateMessageQueue();
            hook = Win32.InstallMouseHook(_procedure);
            _ready.Set();

            if (hook == IntPtr.Zero)
            {
                Debug.WriteLine("Click effects unavailable: the mouse hook was refused.");
                _dispatcher.BeginInvoke(ReportUnavailable);
                return;
            }

            if (!_stopping)
            {
                Win32.RunMessageLoop();
            }
        }
        finally
        {
            if (hook != IntPtr.Zero)
            {
                Win32.RemoveHook(hook);
            }

            // Un échec avant le signal ne doit pas bloquer Dispose.
            _ready.Set();
        }
    }

    private void ReportUnavailable()
    {
        if (!_disposed)
        {
            _onUnavailable();
        }
    }

    // Appelé par Windows sur le fil du crochet : ni calcul, ni verrou, ni attente.
    private IntPtr OnMouseMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_stopping)
        {
            var message = (int)wParam;

            if (message is Win32.WmLButtonDown or Win32.WmRButtonDown)
            {
                var point = Win32.ReadHookPoint(lParam);
                var isRight = message == Win32.WmRButtonDown;

                _dispatcher.BeginInvoke(() => Raise(point, isRight));
            }
        }

        return Win32.CallNextHook(code, wParam, lParam);
    }

    private void Raise(PixelPoint point, bool isRight)
    {
        if (!_disposed)
        {
            Clicked?.Invoke(point, isRight);
        }
    }
}
