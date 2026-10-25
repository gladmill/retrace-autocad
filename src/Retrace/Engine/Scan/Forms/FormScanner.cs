namespace Retrace.Engine.Scan.Forms;

/// <summary>Finds dialogs/windows: WinForms designer files, Win32 .rc DIALOGs, XAML views, DCL dialogs, palette sets.</summary>
public static class FormScanner
{
    public static List<UiForm> Scan(IReadOnlyList<SourceFile> files)
    {
        var all = new List<UiForm>();
        Safe(() => all.AddRange(WinFormsScanner.Scan(files)));
        Safe(() => all.AddRange(RcScanner.Scan(files)));
        Safe(() => all.AddRange(XamlScanner.Scan(files)));
        Safe(() => all.AddRange(DclScanner.Scan(files)));
        Safe(() => all.AddRange(PaletteScanner.Scan(files)));
        return MakeIdsUnique(all);
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception) { /* a scanner failing must not hide the others */ }
    }

    /// <summary>Same id twice (e.g. a dialog in two #ifdef branches): append .2, .3 ...</summary>
    internal static List<UiForm> MakeIdsUnique(List<UiForm> forms)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UiForm>(forms.Count);
        foreach (var f in forms)
        {
            if (used.Add(f.Id)) { result.Add(f); continue; }
            int n = 2;
            while (!used.Add(f.Id + "." + n)) n++;
            var copy = new UiForm { Id = f.Id + "." + n, Title = f.Title, Kind = f.Kind, Project = f.Project, Source = f.Source };
            copy.ResourceId = f.ResourceId; copy.Width = f.Width; copy.Height = f.Height; copy.Units = f.Units;
            copy.ClassNames.AddRange(f.ClassNames);
            copy.Controls.AddRange(f.Controls);
            foreach (var kv in f.Extra) copy.Extra[kv.Key] = kv.Value;
            result.Add(copy);
        }
        return result;
    }

    internal static string SimpleName(string typeName)
    {
        var t = typeName;
        int lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        int dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }
}
