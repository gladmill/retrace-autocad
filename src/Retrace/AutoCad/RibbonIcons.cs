using System.Windows;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>Simple vector icons drawn from geometry strings (32 x 32 design grid), no image files.</summary>
static class RibbonIcons
{
    static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)));
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xC5, 0x30, 0x30)));
    static readonly Pen Line = new(Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0))), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // (path data, filled?) per icon name; unfilled paths are stroked
    static (string Data, bool Filled, bool IsRed) Shape(string name) => name switch
    {
        "record" => ("M16,5 A11,11 0 1 1 15.99,5 Z", true, true),
        "flag" => ("M9,4 L9,28 M9,6 L24,6 L20,11 L24,16 L9,16", false, false),
        "stop" => ("M7,7 L25,7 L25,25 L7,25 Z", true, true),
        "info" => ("M16,4 A12,12 0 1 1 15.99,4 Z M14.5,14 L17.5,14 L17.5,24 L14.5,24 Z M14.5,8 L17.5,8 L17.5,11 L14.5,11 Z", true, false),
        "play" => ("M9,5 L26,16 L9,27 Z", true, false),
        "step" => ("M6,5 L10,5 L10,27 L6,27 Z M13,5 L27,16 L13,27 Z", true, false),
        "book" => ("M5,7 L16,9 L27,7 L27,25 L16,27 L5,25 Z M16,9 L16,27", false, false),
        "build" => ("M5,27 L16,16 M13,6 L19,4 L26,11 L24,17 L18,15 L13,11 Z", false, false),
        "open" => ("M4,8 L13,8 L16,12 L28,12 L28,26 L4,26 Z", false, false),
        "sliders" => ("M5,9 L27,9 M5,16 L27,16 M5,23 L27,23 M10,5 L10,13 M20,12 L20,20 M13,19 L13,27", false, false),
        "terminal" => ("M4,6 L28,6 L28,26 L4,26 Z M9,12 L14,16 L9,20 M16,21 L23,21", false, false),
        "camera" => ("M4,10 L10,10 L12,6 L20,6 L22,10 L28,10 L28,26 L4,26 Z M16,12 A5,5 0 1 1 15.99,12 Z", false, false),
        "opentest" => ("M5,4 L19,4 L19,8 M5,4 L5,28 L19,28 M9,11 L16,11 M9,16 L16,16 M9,21 L13,21 M20,18 A5,5 0 1 1 19.99,18 Z M24,23 L29,28", false, false),
        "closeall" => ("M8,4 L22,4 L22,22 L8,22 Z M4,8 L4,26 L18,26 M11,9 L19,17 M19,9 L11,17", false, false),
        "map" => ("M4,8 L11,5 L21,9 L28,6 L28,25 L21,28 L11,24 L4,27 Z M11,5 L11,24 M21,9 L21,28", false, false),
        "tdoc" => ("M7,4 L20,4 L25,9 L25,28 L7,28 Z M20,4 L20,9 L25,9 M11,15 L21,15 M11,20 L21,20 M11,24 L17,24", false, false),
        _ => ("M6,6 L26,6 L26,26 L6,26 Z", false, false),
    };

    public static ImageSource Make(string name, int size)
    {
        try
        {
            var (data, filled, red) = Shape(name);
            var geo = Geometry.Parse(data);
            var brush = filled ? (red ? Red : Fill) : null;
            var pen = filled ? null : Line;
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
            group.Children.Add(new GeometryDrawing(brush, pen, geo));
            group.Freeze();
            var img = new DrawingImage(group);
            img.Freeze();
            return img;
        }
        catch (System.Exception) { return new DrawingImage(); }
    }
}
