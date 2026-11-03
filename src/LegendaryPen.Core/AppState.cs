namespace LegendaryPen.Core;

public enum AppMode
{
    Mouse,
    Draw,
}

public enum BoardBackground
{
    Transparent,
    Whiteboard,
    Blackboard,
}

/// <summary>Décide si l'overlay reçoit les clics (spécification, section 4).</summary>
public static class OverlayInputRules
{
    public static bool AcceptsInput(bool annotationsVisible, AppMode mode, BoardBackground background)
    {
        if (!annotationsVisible)
        {
            return false;
        }

        return mode == AppMode.Draw || background != BoardBackground.Transparent;
    }
}

/// <summary>Source de vérité unique : les fenêtres ne font que refléter cet état.</summary>
public sealed class AppState
{
    public AppMode Mode { get; private set; } = AppMode.Mouse;

    public BoardBackground Background { get; private set; } = BoardBackground.Transparent;

    public bool AnnotationsVisible { get; private set; } = true;

    public DrawingTool Tool { get; private set; } = DrawingTool.Pen;

    public bool FadingEnabled { get; private set; }

    public bool OverlayAcceptsInput => OverlayInputRules.AcceptsInput(AnnotationsVisible, Mode, Background);

    public bool CanDraw => AnnotationsVisible && Mode == AppMode.Draw;

    public event Action? Changed;

    /// <summary>Passe en mode Dessin et réaffiche les annotations si elles étaient masquées.</summary>
    public void EnterDraw()
    {
        AnnotationsVisible = true;
        Mode = AppMode.Draw;
        Changed?.Invoke();
    }

    public void EnterMouse()
    {
        Mode = AppMode.Mouse;
        Changed?.Invoke();
    }

    public void ToggleMode()
    {
        if (CanDraw)
        {
            EnterMouse();
        }
        else
        {
            EnterDraw();
        }
    }

    /// <summary>Masquer repasse en mode Souris et rend le fond transparent.</summary>
    public void ToggleAnnotations()
    {
        if (AnnotationsVisible)
        {
            AnnotationsVisible = false;
            Mode = AppMode.Mouse;
            Background = BoardBackground.Transparent;
        }
        else
        {
            AnnotationsVisible = true;
        }

        Changed?.Invoke();
    }

    public void SelectTool(DrawingTool tool)
    {
        Tool = tool;
        EnterDraw();
    }

    public void ToggleFading()
    {
        FadingEnabled = !FadingEnabled;
        Changed?.Invoke();
    }

    public void RestoreFading(bool enabled)
    {
        FadingEnabled = enabled;
    }

    /// <summary>Affiche ce tableau, ou revient à l'écran s'il est déjà affiché.</summary>
    public void ToggleBoard(BoardBackground board)
    {
        AnnotationsVisible = true;
        Background = Background == board ? BoardBackground.Transparent : board;
        Changed?.Invoke();
    }

    public void SetBackground(BoardBackground background)
    {
        Background = background;
        Changed?.Invoke();
    }
}
