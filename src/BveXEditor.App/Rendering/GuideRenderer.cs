using System.Windows.Media;
using System.Windows.Media.Media3D;
using BveXEditor.Core.Editing;
using HelixToolkit.Wpf;

namespace BveXEditor.App.Rendering;

/// <summary>線路・車両限界・建築限界・ホーム高さの目安線を 3D ビューに描く。選択には引っかからない。</summary>
public sealed class GuideRenderer
{
    public ModelVisual3D Visual { get; } = new();

    public GuideGauge Gauge { get; private set; } = GuideGauge.None;
    public bool Platform { get; private set; } = true;

    public void Set(GuideGauge gauge, bool platform)
    {
        Gauge = gauge;
        Platform = platform;
        Visual.Children.Clear();
        foreach (var line in TrackGuides.Build(gauge, platform))
        {
            // Z=0 の断面だけ濃く、前後の繰り返しは薄く
            bool faint = line.Closed && line.Points[0].Z != 0;
            var (color, thickness) = line.Kind switch
            {
                GuideKind.Rail => (Color.FromRgb(210, 210, 210), 2.0),
                GuideKind.VehicleGauge => (Color.FromRgb(255, 160, 40), 1.5),
                GuideKind.StructureGauge => (Color.FromRgb(80, 200, 255), 1.5),
                _ => (Color.FromRgb(110, 220, 110), 1.5),
            };
            if (faint) color.A = 90;
            var visual = new LinesVisual3D { Color = color, Thickness = faint ? 1 : thickness };
            int n = line.Points.Length;
            int segments = line.Closed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                visual.Points.Add(SceneRenderer.ToWpf(line.Points[i]));
                visual.Points.Add(SceneRenderer.ToWpf(line.Points[(i + 1) % n]));
            }
            Visual.Children.Add(visual);
        }
    }

    public string Legend => Gauge == GuideGauge.None
        ? ""
        : $"ガイド（{TrackGuides.Describe(Gauge)}・目安）: 白=レール 橙=車両限界 水色=建築限界" + (Platform ? " 緑=ホーム端・高さ" : "");
}
