using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Retrace.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>Reads, shows and puts back the view of the active viewport (Editor.GetCurrentView / SetCurrentView, no commands typed). Command or timer context only,
/// never inside a `PromptingFor*` handler (see AGENTS.md). The pure part (what to show for another window shape, whether two views agree) is <see cref="ViewSpec"/>.</summary>
static class ViewControl
{
    /// <summary>The view of the active viewport of <paramref name="doc"/>, which must be the active document (the layout, CVPORT and the view all belong to it): model space, paper
    /// space, or inside a floating viewport of the current layout. Null when it cannot be read or is a perspective view (a typed 2D point does not mean one place there).</summary>
    public static ViewSpec? Read(Document? doc)
    {
        if (doc == null) return null;
        try
        {
            if (!ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument)) return null;
            var ed = doc.Editor;
            bool tile = doc.Database.TileMode;
            int cvport = Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"));
            string space = tile ? "model" : cvport == 1 ? "paper" : "viewport";
            using var v = ed.GetCurrentView();
            if (v.PerspectiveEnabled) return null;
            var d = v.ViewDirection; var t = v.Target; var c = v.CenterPoint;
            return new ViewSpec(space, LayoutManager.Current.CurrentLayout ?? "", cvport,
                new[] { c.X, c.Y }, v.Height, v.Width, new[] { d.X, d.Y, d.Z }, new[] { t.X, t.Y, t.Z }, v.ViewTwist);
        }
        catch (Exception ex) { Log.Error("read view", ex); return null; }
    }

    /// <summary>A key that changes whenever the view or the layout changes: the system variables that say where and how the active viewport looks. Plain variable reads (no
    /// Editor or Document), so the recorder may take it inside a prompt result handler to see whether a view it read earlier is still the one on screen. Null when unreadable.</summary>
    public static string? ViewKey()
    {
        try
        {
            string V(string n) => Convert.ToString(AcApp.GetSystemVariable(n), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return string.Join("|", V("CTAB"), V("CVPORT"), V("VIEWSIZE"), V("VIEWCTR"), V("VIEWDIR"), V("TARGET"), V("VIEWTWIST"), V("SCREENSIZE"));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Why <paramref name="v"/> cannot be shown now (another layout, another space, the current view cannot be read), or null when it can. <paramref name="cur"/> is the
    /// current view, read once by the caller.</summary>
    public static string? Problem(ViewSpec? cur, ViewSpec v)
    {
        if (cur == null) return "the current view cannot be read";
        if (cur.Space != v.Space || !string.Equals(cur.Layout, v.Layout, StringComparison.Ordinal))
            return $"recorded in {Describe(v)}, the replay is in {Describe(cur)}";
        return null;   // (a recorded view inside a floating viewport never gets here: PickReplay.Claim refuses it)
    }

    static string Describe(ViewSpec v) => v.Space == "model" ? "Model" : v.Space == "paper" ? $"layout \"{v.Layout}\"" : $"layout \"{v.Layout}\", viewport {v.Viewport}";

    /// <summary>Shows <paramref name="v"/> in the active viewport, for a window of any shape: the recorded area stays fully visible (<see cref="ViewSpec.Fit"/>), the document locked.
    /// Returns the view the person had (give it to <see cref="Restore"/>), or null when nothing was changed.</summary>
    public static ViewTableRecord? Apply(Document doc, ViewSpec v)
    {
        ViewTableRecord? saved = null, nv = null;
        try
        {
            using var locked = doc.LockDocument();
            var ed = doc.Editor;
            saved = ed.GetCurrentView();
            nv = ed.GetCurrentView();   // a second copy of the same view: keeps every property this code does not set
            var (w, h) = v.Fit(saved.Width / saved.Height);
            nv.Target = new Point3d(v.Target[0], v.Target[1], v.Target[2]);
            nv.ViewDirection = new Vector3d(v.Direction[0], v.Direction[1], v.Direction[2]);
            nv.ViewTwist = v.Twist;
            nv.CenterPoint = new Point2d(v.Center[0], v.Center[1]);
            nv.Height = h; nv.Width = w;
            ed.SetCurrentView(nv);
            var result = saved; saved = null;
            return result;
        }
        catch (Exception ex) { Log.Error("show view", ex); return null; }
        finally { nv?.Dispose(); saved?.Dispose(); }
    }

    /// <summary>Puts back the view <see cref="Apply"/> returned (document locked). Never throws; true when it was set.</summary>
    public static bool Restore(Document doc, ViewTableRecord saved)
    {
        try
        {
            using var locked = doc.LockDocument();
            doc.Editor.SetCurrentView(saved);
            saved.Dispose();
            return true;
        }
        catch (Exception ex) { Log.Error("restore view", ex); return false; }   // the saved view is kept: the caller may try again
    }
}
