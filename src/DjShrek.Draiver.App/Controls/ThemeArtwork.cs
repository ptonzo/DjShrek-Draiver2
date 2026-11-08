using System.Windows;
using System.Windows.Media;
using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App.Controls;

/// <summary>Small original vector illustration that changes with the selected theme.</summary>
public sealed class ThemeArtwork : FrameworkElement
{
    private string _id = "01";
    private Brush _primary = Brushes.MediumPurple;
    private Brush _secondary = Brushes.DeepSkyBlue;
    private Brush _surface = Brushes.Black;
    private Brush _text = Brushes.White;

    public void SetTheme(AppTheme theme)
    {
        _id = theme.Id;
        _primary = BrushFrom(theme.Accent);
        _secondary = BrushFrom(theme.Accent2);
        _surface = BrushFrom(theme.Surface);
        _text = BrushFrom(theme.Text);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(230, double.IsInfinity(availableSize.Width) ? 210 : availableSize.Width),
            Math.Min(150, double.IsInfinity(availableSize.Height) ? 138 : availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;

        var p = new Pen(_primary, 4);
        var s = new Pen(_secondary, 2.5);
        var faint = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
        var center = new Point(w * .52, h * .52);

        dc.DrawEllipse(null, new Pen(_secondary, 1.2), center, w * .44, h * .44);
        dc.DrawEllipse(null, new Pen(_primary, 2), center, w * .34, h * .34);
        dc.DrawEllipse(null, faint, center, w * .25, h * .25);

        switch (_id)
        {
            case "01": DrawNeon(dc, w, h); break;
            case "02": DrawGlass(dc, w, h); break;
            case "03": DrawMinimal(dc, w, h); break;
            case "04": DrawChip(dc, w, h); break;
            case "05": DrawPc(dc, w, h); break;
            case "06": DrawFan(dc, w, h); break;
            case "07": DrawDesktop(dc, w, h); break;
            case "08": DrawWaves(dc, w, h); break;
            case "09": DrawCyberpunk(dc, w, h); break;
            case "10": DrawRadar(dc, w, h); break;
            case "11": DrawTank(dc, w, h); break;
            case "12": DrawFieldCamp(dc, w, h); break;
            default: DrawChip(dc, w, h); break;
        }

        var label = new FormattedText("DS", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 17, _text,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        label.TextAlignment = TextAlignment.Center;
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
    }

    private static Brush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Geometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawNeon(DrawingContext dc, double w, double h)
    {
        var hood = Polygon(new Point(w*.52,h*.08),new Point(w*.77,h*.31),new Point(w*.73,h*.87),
            new Point(w*.31,h*.87),new Point(w*.27,h*.31));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 16, 10, 45)), new Pen(_primary,4), hood);
        dc.DrawEllipse(_surface, new Pen(_secondary,3), new Point(w*.52,h*.43), w*.12,h*.18);
        dc.DrawLine(new Pen(_primary,4),new Point(w*.39,h*.43),new Point(w*.65,h*.43));
        dc.DrawEllipse(_secondary,null,new Point(w*.47,h*.45),4,3);
        dc.DrawEllipse(_secondary,null,new Point(w*.57,h*.45),4,3);
    }

    private void DrawGlass(DrawingContext dc, double w, double h)
    {
        var b1 = Polygon(new Point(w*.08,h*.68),new Point(w*.28,h*.23),new Point(w*.64,h*.15),
            new Point(w*.88,h*.45),new Point(w*.69,h*.82),new Point(w*.28,h*.78));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(55, 80, 170, 255)), new Pen(_secondary,2), b1);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(105,255,255,255)),
            new Pen(_secondary,2),new Rect(w*.36,h*.28,w*.32,h*.44),12,12);
        dc.DrawEllipse(_primary,null,new Point(w*.58,h*.5),w*.035,h*.035);
    }

    private void DrawMinimal(DrawingContext dc, double w, double h)
    {
        dc.DrawEllipse(null,new Pen(_secondary,5),new Point(w*.5,h*.48),w*.26,h*.32);
        dc.DrawLine(new Pen(_primary,5),new Point(w*.37,h*.49),new Point(w*.47,h*.6));
        dc.DrawLine(new Pen(_primary,5),new Point(w*.47,h*.6),new Point(w*.66,h*.36));
        for (var i=0;i<4;i++)
            dc.DrawRoundedRectangle(_primary,null,new Rect(w*(.18+i*.17),h*.82,w*.1,h*(.08+i*.035)),3,3);
    }

    private void DrawChip(DrawingContext dc, double w, double h)
    {
        var r=new Rect(w*.32,h*.25,w*.38,h*.48);
        dc.DrawRoundedRectangle(_surface,new Pen(_primary,4),r,7,7);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(100,255,150,40)),new Pen(_secondary,2),
            new Rect(w*.41,h*.36,w*.2,h*.25),5,5);
        for(var i=0;i<5;i++)
        {
            var x=w*(.36+i*.075);
            dc.DrawLine(new Pen(_secondary,2),new Point(x,h*.12),new Point(x,h*.25));
            dc.DrawLine(new Pen(_secondary,2),new Point(x,h*.73),new Point(x,h*.88));
            var y=h*(.31+i*.08);
            dc.DrawLine(new Pen(_secondary,2),new Point(w*.18,y),new Point(w*.32,y));
            dc.DrawLine(new Pen(_secondary,2),new Point(w*.70,y),new Point(w*.84,y));
        }
    }

    private void DrawPc(DrawingContext dc, double w, double h)
    {
        dc.DrawRoundedRectangle(_surface,new Pen(_secondary,3),new Rect(w*.19,h*.2,w*.25,h*.65),8,8);
        dc.DrawRoundedRectangle(_surface,new Pen(_primary,3),new Rect(w*.52,h*.3,w*.34,h*.42),5,5);
        dc.DrawEllipse(null,new Pen(_secondary,3),new Point(w*.315,h*.48),w*.085,h*.17);
        dc.DrawEllipse(null,new Pen(_primary,2),new Point(w*.315,h*.48),w*.05,h*.1);
        dc.DrawLine(new Pen(_secondary,4),new Point(w*.68,h*.72),new Point(w*.68,h*.83));
        dc.DrawLine(new Pen(_secondary,4),new Point(w*.59,h*.84),new Point(w*.77,h*.84));
    }

    private void DrawFan(DrawingContext dc, double w, double h)
    {
        var c=new Point(w*.52,h*.5);
        dc.DrawEllipse(null,new Pen(_secondary,4),c,w*.29,h*.29);
        dc.DrawEllipse(_surface,new Pen(_primary,3),c,w*.07,h*.07);
        for(var i=0;i<8;i++)
        {
            var a=i*Math.PI/4;
            var end=new Point(c.X+Math.Cos(a)*w*.24,c.Y+Math.Sin(a)*h*.24);
            dc.DrawLine(new Pen(i%2==0?_primary:_secondary,5),c,end);
        }
    }

    private void DrawDesktop(DrawingContext dc, double w, double h)
    {
        dc.DrawRoundedRectangle(_surface,new Pen(_secondary,3),new Rect(w*.2,h*.19,w*.6,h*.48),7,7);
        var x=w*.34; var y=h*.31; var size=w*.19;
        dc.DrawRectangle(_primary,null,new Rect(x,y,size,size*.62));
        dc.DrawRectangle(_secondary,null,new Rect(x+size*.56,y,size*.44,size*.62));
        dc.DrawRectangle(_secondary,null,new Rect(x,y+size*.67,size,size*.62));
        dc.DrawRectangle(_primary,null,new Rect(x+size*.56,y+size*.67,size*.44,size*.62));
        dc.DrawLine(new Pen(_secondary,4),new Point(w*.5,h*.67),new Point(w*.5,h*.82));
        dc.DrawLine(new Pen(_secondary,4),new Point(w*.36,h*.84),new Point(w*.64,h*.84));
    }

    private void DrawWaves(DrawingContext dc, double w, double h)
    {
        dc.DrawGeometry(null,new Pen(_secondary,5),
            Polygon(new Point(w*.07,h*.62),new Point(w*.25,h*.42),new Point(w*.42,h*.53),
                new Point(w*.61,h*.28),new Point(w*.91,h*.48)));
        dc.DrawGeometry(null,new Pen(_primary,7),
            Polygon(new Point(w*.08,h*.76),new Point(w*.27,h*.6),new Point(w*.46,h*.7),
                new Point(w*.66,h*.49),new Point(w*.92,h*.65)));
    }


    private void DrawCyberpunk(DrawingContext dc, double w, double h)
    {
        var sky = new LinearGradientBrush(Color.FromRgb(7, 5, 25), Color.FromRgb(30, 5, 48), 90);
        dc.DrawRoundedRectangle(sky, new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 45, 190)), 1.5),
            new Rect(2, 2, w - 4, h - 4), 14, 14);
        var random = new Random(9);
        for (int i = 0; i < 18; i++)
        {
            double bw = w * (.025 + random.NextDouble() * .045);
            double bh = h * (.18 + random.NextDouble() * .43);
            double x = w * .02 + i * w * .052;
            double y = h * .68 - bh;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 12, 20, 55)),
                new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 160, 255)), 1), new Rect(x, y, bw, bh));
            for (double wy = y + 5; wy < h * .65; wy += 9)
                for (double wx = x + 4; wx < x + bw - 2; wx += 7)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 0, 210, 255)), null,
                        new Rect(wx, wy, 2.2, 3.2));
        }
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 5, 10, 30),
            Color.FromArgb(235, 4, 7, 24), 90), null, new Rect(0, h * .66, w, h * .34));
        for (int i = 0; i < 7; i++)
        {
            double y = h * (.73 + i * .035);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(130, 255, 45, 220)), 1.2 + i * .15),
                new Point(w * .04, y), new Point(w * .78, y + 2));
        }
        var car = Polygon(new Point(w*.05,h*.78), new Point(w*.16,h*.70),
            new Point(w*.29,h*.61), new Point(w*.54,h*.61), new Point(w*.66,h*.72),
            new Point(w*.78,h*.75), new Point(w*.81,h*.84), new Point(w*.08,h*.84));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(245, 8, 12, 34)),
            new Pen(new SolidColorBrush(Color.FromRgb(0, 220, 255)), 2.4), car);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(210, 15, 28, 66)),
            new Pen(new SolidColorBrush(Color.FromRgb(255, 40, 205)), 1.4),
            Polygon(new Point(w*.29,h*.64), new Point(w*.37,h*.57),
                new Point(w*.51,h*.57), new Point(w*.61,h*.70)));
        foreach (double x in new[] { w*.24, w*.66 })
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(5, 8, 22)),
                new Pen(new SolidColorBrush(Color.FromRgb(0, 220, 255)), 2), new Point(x,h*.83), w*.055,h*.09);
            dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromRgb(255,45,190)),1.5),
                new Point(x,h*.83),w*.027,h*.045);
        }
        var hood = Polygon(new Point(w*.84,h*.08), new Point(w*.98,h*.22),
            new Point(w*.99,h*.82), new Point(w*.76,h*.82), new Point(w*.75,h*.31));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(235, 8, 8, 28)),
            new Pen(new SolidColorBrush(Color.FromRgb(255, 40, 205)), 2.5), hood);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(5, 8, 25)),
            new Pen(new SolidColorBrush(Color.FromRgb(0, 210, 255)), 2),
            new Point(w*.87,h*.42), w*.075,h*.16);
        dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromRgb(0,230,255)),2),
            new Point(w*.845,h*.42),w*.018,h*.014);
        dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromRgb(0,230,255)),2),
            new Point(w*.895,h*.42),w*.018,h*.014);
        dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromRgb(255,40,205)),3),
            new Point(w*.79,h*.43),w*.025,h*.09);
        dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromRgb(0,220,255)),3),
            new Point(w*.95,h*.43),w*.025,h*.09);
        dc.DrawGeometry(null,new Pen(new SolidColorBrush(Color.FromRgb(0,220,255)),3),
            Polygon(new Point(w*.79,h*.38),new Point(w*.81,h*.18),
                new Point(w*.88,h*.12),new Point(w*.95,h*.38)));
    }

    private void DrawCar(DrawingContext dc, double w, double h)
    {
        var body=Polygon(new Point(w*.12,h*.62),new Point(w*.25,h*.52),new Point(w*.36,h*.32),
            new Point(w*.65,h*.32),new Point(w*.79,h*.52),new Point(w*.9,h*.59),
            new Point(w*.88,h*.73),new Point(w*.13,h*.73));
        dc.DrawGeometry(_surface,new Pen(_primary,4),body);
        dc.DrawLine(new Pen(_secondary,3),new Point(w*.38,h*.36),new Point(w*.62,h*.36));
        dc.DrawEllipse(_surface,new Pen(_secondary,4),new Point(w*.29,h*.72),w*.07,h*.1);
        dc.DrawEllipse(_surface,new Pen(_secondary,4),new Point(w*.73,h*.72),w*.07,h*.1);
    }

    private void DrawRadar(DrawingContext dc, double w, double h)
    {
        var c=new Point(w*.52,h*.5);
        for(var i=1;i<=4;i++) dc.DrawEllipse(null,new Pen(i%2==0?_primary:_secondary,1.6),c,w*.11*i,h*.11*i);
        dc.DrawLine(new Pen(_secondary,1),new Point(c.X-w*.39,c.Y),new Point(c.X+w*.39,c.Y));
        dc.DrawLine(new Pen(_secondary,1),new Point(c.X,c.Y-h*.39),new Point(c.X,c.Y+h*.39));
        dc.DrawLine(new Pen(_primary,4),c,new Point(w*.81,h*.24));
        dc.DrawEllipse(_secondary,null,new Point(w*.67,h*.35),4,4);
    }

    private void DrawTank(DrawingContext dc, double w, double h)
    {
        dc.DrawRoundedRectangle(_surface,new Pen(_primary,4),new Rect(w*.16,h*.62,w*.7,h*.2),18,18);
        dc.DrawRoundedRectangle(null,new Pen(_secondary,3),new Rect(w*.22,h*.65,w*.58,h*.14),12,12);
        dc.DrawGeometry(_surface,new Pen(_primary,3),Polygon(new Point(w*.31,h*.61),
            new Point(w*.38,h*.43),new Point(w*.67,h*.43),new Point(w*.76,h*.62)));
        dc.DrawLine(new Pen(_secondary,6),new Point(w*.58,h*.47),new Point(w*.9,h*.37));
        dc.DrawEllipse(_secondary,null,new Point(w*.42,h*.72),5,5);
        dc.DrawEllipse(_secondary,null,new Point(w*.58,h*.72),5,5);
    }

    private void DrawFieldCamp(DrawingContext dc, double w, double h)
    {
        dc.DrawGeometry(_surface,new Pen(_primary,4),Polygon(new Point(w*.15,h*.76),
            new Point(w*.48,h*.24),new Point(w*.82,h*.76)));
        dc.DrawLine(new Pen(_secondary,3),new Point(w*.48,h*.24),new Point(w*.48,h*.76));
        dc.DrawLine(new Pen(_secondary,3),new Point(w*.48,h*.3),new Point(w*.75,h*.33));
        dc.DrawGeometry(_primary,null,Polygon(new Point(w*.75,h*.33),new Point(w*.91,h*.39),new Point(w*.75,h*.45)));
        dc.DrawRectangle(_surface,new Pen(_secondary,2),new Rect(w*.08,h*.71,w*.17,h*.1));
        dc.DrawRectangle(_surface,new Pen(_secondary,2),new Rect(w*.74,h*.71,w*.17,h*.1));
    }
}