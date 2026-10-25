using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Retrace.Engine.Scan.Cuix;

/// <summary>Reads a .cuix (zip of .cui XML parts) into ribbon tabs, macros and workspaces.</summary>
public static class CuixReader
{
    public static CuixModel Read(string absPath, string relPath)
    {
        var model = new CuixModel { File = relPath };
        using var zip = ZipFile.OpenRead(absPath);

        XDocument? Part(string name)
        {
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var s = entry.Open();
            return XDocument.Load(s, LoadOptions.SetLineInfo);
        }

        var menu = Part("MenuGroup.cui");
        if (menu?.Root != null)
        {
            model.MenuGroup = (string?)menu.Root.Attribute("Name") ?? "";
            foreach (var mm in menu.Descendants("MenuMacro"))
            {
                var id = (string?)mm.Attribute("UID");
                if (string.IsNullOrEmpty(id)) continue;
                var mac = mm.Element("Macro");
                if (mac == null) continue;
                var def = new MacroDef
                {
                    Id = id,
                    Name = Norm((string?)mac.Element("Name")),
                    Macro = ((string?)mac.Element("Command") ?? "").Trim(),
                    HelpString = NullIfEmpty(Norm((string?)mac.Element("HelpString"))),
                    SmallImage = (string?)mac.Element("SmallImage")?.Attribute("Name"),
                    LargeImage = (string?)mac.Element("LargeImage")?.Attribute("Name"),
                };
                def.Command = ParseCommand(def.Macro);
                if (def.Command == null)
                {
                    var cli = Norm((string?)mac.Element("CLICommand")).ToUpperInvariant();
                    if (cli.Length > 0 && Rx.IsMatch(cli, @"^[-\w]+$")) def.Command = cli;
                }
                model.Macros[id] = def;
            }
        }

        var header = Part("Header.cui");
        if (header?.Root != null)
            foreach (var p in header.Descendants("PartialMenuFile"))
                if (!string.IsNullOrWhiteSpace(p.Value)) model.PartialMenus.Add(p.Value.Trim());

        var ribbon = Part("RibbonRoot.cui");
        if (ribbon?.Root != null)
            ReadRibbon(model, ribbon, $"{relPath}!RibbonRoot.cui");

        var ws = Part("WorkspaceRoot.cui");
        if (ws?.Root != null)
        {
            var byTab = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var w in ws.Root.Elements("Workspace"))
            {
                var name = Norm((string?)w.Element("Name"));
                if (name.Length == 0) name = (string?)w.Attribute("UID") ?? "";
                if (name.Length == 0) continue;
                if (!model.Workspaces.Contains(name)) model.Workspaces.Add(name);
                foreach (var r in w.Descendants("WSRibbonTabSourceReference"))
                {
                    if (string.Equals((string?)r.Attribute("Show"), "false", StringComparison.OrdinalIgnoreCase)) continue;
                    var tabId = (string?)r.Attribute("TabId");
                    if (string.IsNullOrEmpty(tabId)) continue;
                    if (!byTab.TryGetValue(tabId, out var l)) byTab[tabId] = l = new();
                    if (!l.Contains(name)) l.Add(name);
                }
            }
            foreach (var t in model.Tabs)
                if (byTab.TryGetValue(t.Id, out var l)) t.Workspaces.AddRange(l);
        }
        return model;
    }

    // ------------------------------------------------------------------ ribbon

    private static void ReadRibbon(CuixModel model, XDocument doc, string file)
    {
        var root = doc.Root!;
        SourceRef Ref(XElement e) => new(file, (e as IXmlLineInfo)?.HasLineInfo() == true ? ((IXmlLineInfo)e).LineNumber : 0);

        var panels = new Dictionary<string, RibbonPanel>(StringComparer.Ordinal);
        foreach (var ps in root.Descendants("RibbonPanelSource"))
        {
            var id = (string?)ps.Attribute("UID");
            if (string.IsNullOrEmpty(id) || panels.ContainsKey(id)) continue;
            var name = Norm((string?)ps.Element("Name"));
            var text = Norm((string?)ps.Attribute("Text"));
            var panel = new RibbonPanel { Id = id, Title = text.Length > 0 ? text : name, Source = Ref(ps) };
            if (name.Length > 0) panel.Extra["name"] = name;
            if ((string?)ps.Attribute("KeyTip") is { Length: > 0 } kt) panel.Extra["keyTip"] = kt;
            if (ps.Element("DialogBoxLauncher") is { } dbl)
            {
                var cid = (string?)dbl.Attribute("CommandID") ?? (string?)dbl.Attribute("MenuMacroID");
                if (!string.IsNullOrEmpty(cid))
                {
                    panel.DialogLauncherCommand = cid;
                    if (model.Macros.TryGetValue(cid, out var md) && md.Command != null) panel.Extra["dialogLauncherCommand"] = md.Command;
                }
            }
            bool slide = false;
            foreach (var child in ps.Elements())
            {
                if (child.Name == "RibbonPanelBreak") slide = true;
                else if (child.Name == "RibbonRow")
                {
                    var row = ReadRow(model, child, Ref);
                    row.IsSlideout = slide;
                    panel.Rows.Add(row);
                }
            }
            panels[id] = panel;
        }

        var selectors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sel in root.Descendants("RibbonTabSelector"))
        {
            var rule = (string?)sel.Attribute("Rule");
            if (rule == null) continue;
            foreach (var t in ((string?)sel.Attribute("Tabs") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                selectors.TryAdd(t, rule);
        }

        string container = Path.GetFileName(file.Split('!')[0]);
        foreach (var ts in root.Descendants("RibbonTabSource"))
        {
            var id = (string?)ts.Attribute("UID");
            if (string.IsNullOrEmpty(id)) continue;
            var name = Norm((string?)ts.Element("Name"));
            var text = Norm((string?)ts.Attribute("Text"));
            var tab = new RibbonTab
            {
                Id = id,
                Title = text.Length > 0 ? text : name,
                Origin = RibbonOrigin.Cuix,
                Container = container,
                Source = Ref(ts),
                DisplayName = name.Length > 0 ? name : null,
                KeyTip = (string?)ts.Attribute("KeyTip"),
                ContextualRule = selectors.GetValueOrDefault(id),
            };
            var missing = new List<string>();
            foreach (var pr in ts.Elements("RibbonPanelSourceReference"))
            {
                var pid = (string?)pr.Attribute("PanelId");
                if (pid != null && panels.TryGetValue(pid, out var p)) tab.Panels.Add(p);
                else if (pid != null) missing.Add(pid);
            }
            if (missing.Count > 0) tab.Extra["missingPanels"] = string.Join(";", missing);
            model.Tabs.Add(tab);
        }
    }

    private static RibbonRow ReadRow(CuixModel model, XElement rowEl, Func<XElement, SourceRef> refOf)
    {
        var row = new RibbonRow();
        foreach (var e in rowEl.Elements())
            if (ReadItem(model, e, refOf) is { } item) row.Items.Add(item);
        return row;
    }

    private static RibbonItem? ReadItem(CuixModel model, XElement e, Func<XElement, SourceRef> refOf)
    {
        string n = e.Name.LocalName;
        if (!n.StartsWith("Ribbon") || n is "RibbonRow" or "RibbonPanelBreak") return null;
        var kind = n switch
        {
            "RibbonCommandButton" or "RibbonButton" => RibbonItemKind.Button,
            "RibbonSplitButton" => RibbonItemKind.SplitButton,
            "RibbonToggleButton" => RibbonItemKind.ToggleButton,
            "RibbonControl" => RibbonItemKind.Control,
            "RibbonGalleryControl" => RibbonItemKind.Gallery,
            "RibbonDataBoundDropDown" => RibbonItemKind.DropDown,
            "RibbonSeparator" => RibbonItemKind.Separator,
            "RibbonRowPanel" => RibbonItemKind.RowPanel,
            "RibbonLabel" => RibbonItemKind.Label,
            _ => RibbonItemKind.Other,
        };
        string style = (string?)e.Attribute("ButtonStyle") ?? "";
        var item = new RibbonItem
        {
            Id = (string?)e.Attribute("UID") ?? (string?)e.Attribute("Id") ?? n,
            Kind = kind,
            Size = style.Contains("Large") ? RibbonItemSize.Large : style.Contains("Small") ? RibbonItemSize.Small : RibbonItemSize.Standard,
            ShowText = !style.EndsWith("WithoutText", StringComparison.Ordinal),
            KeyTip = (string?)e.Attribute("KeyTip"),
            Source = refOf(e),
        };
        if ((string?)e.Attribute("Id") is { Length: > 0 } cls) item.Extra["controlId"] = cls;
        item.Text = Norm((string?)e.Attribute("Text"));
        var tipTitle = Norm((string?)e.Element("TooltipTitle"));

        var mid = (string?)e.Attribute("MenuMacroID");
        MacroDef? macro = null;
        if (!string.IsNullOrEmpty(mid))
        {
            item.MacroId = mid;
            if (model.Macros.TryGetValue(mid, out macro))
            {
                item.Macro = macro.Macro;
                item.Command = macro.Command;
                if (item.Text.Length == 0) item.Text = macro.Name;
                item.Tooltip = macro.HelpString;
            }
        }
        if (item.Text.Length == 0) item.Text = tipTitle;
        if (tipTitle.Length > 0) item.Extra["tooltipTitle"] = tipTitle;
        if (item.Tooltip == null && tipTitle.Length > 0) item.Tooltip = tipTitle;

        string? small = (string?)e.Attribute("SmallImage") ?? macro?.SmallImage;
        string? large = (string?)e.Attribute("LargeImage") ?? macro?.LargeImage;
        item.Image = item.Size == RibbonItemSize.Large ? large ?? small : small ?? large;

        if (kind == RibbonItemKind.RowPanel)
        {
            foreach (var r in e.Elements("RibbonRow")) item.SubRows.Add(ReadRow(model, r, refOf));
        }
        else
        {
            foreach (var c in e.Elements())
                if (ReadItem(model, c, refOf) is { } child) item.Children.Add(child);
        }
        return item;
    }

    // ------------------------------------------------------------------ helpers

    private static string Norm(string? s) => Rx.Replace(s ?? "", @"\s*[\r\n]+\s*|\s{2,}", " ").Trim();
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>Command name from a menu macro: "^C^C_mtext" -> "MTEXT", "^C^C._-insert" -> "-INSERT",
    /// "'_zoom" -> "ZOOM". DIESEL ("$M=...") and LISP-first macros give null.</summary>
    public static string? ParseCommand(string? macro)
    {
        if (string.IsNullOrWhiteSpace(macro)) return null;
        var s = macro.TrimStart();
        if (s.StartsWith('$')) return null;
        int i = 0;
        // prefixes: repeat-flag '*', control codes ^C ^P ..., transparent ', language marker _, dot (builtin)
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length) i += 2;
            else if (c is '*' or '+' or '\'' or '_' or '.' or ' ' or '\t') i++;
            else break;
        }
        if (i >= s.Length) return null;
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '-' or '_' or '.')) i++;
        var tok = s[start..i].TrimStart('_', '.').TrimEnd('.', '_');
        if (tok.StartsWith("ACAD.", StringComparison.OrdinalIgnoreCase)) tok = tok[5..];
        if (tok.Length == 0 || !tok.Any(char.IsLetter)) return null;
        return tok.ToUpperInvariant();
    }
}
