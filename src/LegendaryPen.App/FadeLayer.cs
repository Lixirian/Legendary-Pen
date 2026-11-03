using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Dessine les traits évanescents dans une seule couche légère : chacun a son horloge,
/// sans élément d'interface par trait, pour ne pas ralentir le tracé en cours.
/// </summary>
internal sealed class FadeLayer : FrameworkElement
{
    private readonly VisualCollection _visuals;

    public FadeLayer()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => _visuals.Count;

    public void Add(Stroke stroke, FadeTiming timing)
    {
        var animation = new DoubleAnimation(1, 0, new Duration(timing.Duration)) { BeginTime = timing.Delay };
        var clock = animation.CreateClock();
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.PushOpacity(1, clock);
            stroke.Draw(context);
            context.Pop();
        }

        clock.Completed += (_, _) => _visuals.Remove(visual);
        _visuals.Add(visual);
    }

    public void Clear() => _visuals.Clear();

    protected override Visual GetVisualChild(int index) => _visuals[index];
}
