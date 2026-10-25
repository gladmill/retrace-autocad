using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Retrace.Engine.Scan.Forms;

/// <summary>WPF XAML windows / user controls / pages with an x:Class.</summary>
internal static class XamlScanner
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace D = "http://schemas.microsoft.com/expression/blend/2008";

    private static readonly HashSet<string> SkipTypes = new(StringComparer.Ordinal)
    {
        "ResourceDictionary", "Style", "Setter", "ControlTemplate", "DataTemplate", "HierarchicalDataTemplate",
        "Trigger", "DataTrigger", "EventTrigger", "MultiTrigger", "Storyboard", "ItemsPanelTemplate", "Binding",
        "ColumnDefinition", "RowDefinition", "SolidColorBrush", "LinearGradientBrush", "BooleanToVisibilityConverter",
        "VisualStateManager", "Condition", "Window.Resources",
    };
    private static readonly HashSet<string> RootTypes = new() { "Window", "UserControl", "Page", "NavigationWindow" };

    private static readonly Regex EventName = Rx.Create(
        @"^(Click|DoubleClick|MouseDoubleClick|Checked|Unchecked|Indeterminate|SelectionChanged|TextChanged|Loaded|Unloaded|Closed|Closing|Initialized|Activated|Deactivated|GotFocus|LostFocus|ValueChanged|DropDownClosed|DropDownOpened|SizeChanged|Drop|ContextMenuOpening|Expanded|Collapsed|Executed|Sorting|CellEditEnding|RowEditEnding|AutoGeneratingColumn|SelectedItemChanged|SelectedDateChanged|(Preview)?(Mouse|Key|Drag|Touch|Text)\w*)$",
        RegexOptions.Compiled);

    public static IEnumerable<UiForm> Scan(IReadOnlyList<SourceFile> files)
    {
        foreach (var f in files.Where(f => f.Lang == Lang.Xaml))
        {
            XDocument doc;
            try { doc = XDocument.Parse(f.Text.TrimStart('﻿'), LoadOptions.SetLineInfo); }
            catch (XmlException) { continue; }
            var root = doc.Root;
            if (root == null) continue;
            var cls = (string?)root.Attribute(X + "Class");
            if (cls == null || !RootTypes.Contains(root.Name.LocalName)) continue;

            var simple = cls[(cls.LastIndexOf('.') + 1)..];
            var form = new UiForm
            {
                Id = "xaml." + cls, Title = (string?)root.Attribute("Title") ?? simple, Kind = UiFormKind.Xaml,
                Project = f.Project, Source = new SourceRef(f.RelPath, Line(root)), Units = "auto",
            };
            form.ClassNames.Add(cls);
            form.ResourceId = null;
            form.Extra["root"] = root.Name.LocalName;
            form.Width = Dim(root, "Width", "DesignWidth");
            form.Height = Dim(root, "Height", "DesignHeight");
            foreach (var child in root.Elements())
                AddElement(child, form.Controls, f);
            yield return form;
        }
    }

    private static int? Dim(XElement e, string plain, string design)
    {
        foreach (var a in new[] { e.Attribute(plain), e.Attribute(D + plain), e.Attribute(D + design) })
            if (a != null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) return (int)v;
        return null;
    }

    private static int Line(XElement e) => e is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 1;

    private static void AddElement(XElement e, List<UiControl> into, SourceFile f)
    {
        var name = e.Name.LocalName;
        if (name.Contains('.')) return;                    // property elements: Grid.RowDefinitions, X.Resources ...
        if (SkipTypes.Contains(name) || name.EndsWith("Definition") || name.EndsWith("Converter")) return;
        var c = new UiControl { Type = name, Source = new SourceRef(f.RelPath, Line(e)) };
        c.Name = (string?)e.Attribute(X + "Name") ?? (string?)e.Attribute("Name") ?? "";
        c.Text = FirstAttr(e, "Content", "Text", "Header", "Title", "Label");
        if (c.Text == null)
        {
            var direct = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (direct.Length > 0) c.Text = Rx.Replace(direct, @"\s+", " ");
        }
        var w = Num((string?)e.Attribute("Width")); var h = Num((string?)e.Attribute("Height"));
        if (w != null) c.W = w; if (h != null) c.H = h;

        var row = (string?)e.Attribute("Grid.Row"); var col = (string?)e.Attribute("Grid.Column");
        if (row != null || col != null) c.Extra["grid"] = $"{row ?? "0"},{col ?? "0"}";
        var rs = (string?)e.Attribute("Grid.RowSpan"); if (rs != null) c.Extra["rowspan"] = rs;
        var cs = (string?)e.Attribute("Grid.ColumnSpan"); if (cs != null) c.Extra["colspan"] = cs;
        var orient = (string?)e.Attribute("Orientation"); if (orient != null) c.Extra["orientation"] = orient;
        var dock = (string?)e.Attribute("DockPanel.Dock"); if (dock != null) c.Extra["dock"] = dock;
        var cmd = (string?)e.Attribute("Command"); if (cmd != null) c.Extra["command"] = cmd;
        var items = (string?)e.Attribute("ItemsSource"); if (items != null) c.Extra["itemsSource"] = items;

        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None) continue;
            var an = a.Name.LocalName;
            if (!EventName.IsMatch(an)) continue;
            var v = a.Value.Trim();
            if (v.Length == 0 || v.StartsWith("{") || !Rx.IsMatch(v, @"^\w+$")) continue;
            c.Events.Add($"{an} -> {v}");
        }
        foreach (var child in e.Elements()) AddElement(child, c.Children, f);
        into.Add(c);
    }

    private static string? FirstAttr(XElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var a = e.Attribute(n);
            if (a != null && a.Value.Length > 0) return a.Value;
        }
        return null;
    }

    private static int? Num(string? s) =>
        s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && v < 5000 ? (int)v : null;
}
