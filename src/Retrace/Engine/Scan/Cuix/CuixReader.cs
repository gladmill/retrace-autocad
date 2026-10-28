using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Retrace.Engine.Scan.Cuix;

/// <summary>Reads a .cuix (zip of .cui XML parts) or a legacy .cui (the same sections in one XML file) into ribbon tabs, macros and workspaces.</summary>
public static class CuixReader
{
    /// <param name="container">the name tabs are filed under (their Container): the file name by default; a label that tells two files of
    /// the same name apart otherwise ("AutoCAD Electrical 2026/acad.cuix")</param>
    /// <summary>A legacy .cui file: its root element is CustSection. A loose part of an unzipped .cuix (RibbonRoot.cui, MenuGroup.cui...) is not.</summary>
    public static bool IsLegacyCui(string path)
    {
        try
        {
            using var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            return r.MoveToContent() == XmlNodeType.Element && r.LocalName == "CustSection";
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Only the workspace names of a .cuix / .cui (its WorkspaceRoot part), without reading the rest. Empty when there is none.</summary>
    public static List<string> WorkspaceNames(string absPath)
    {
        XElement? root;
        if (absPath.EndsWith(".cui", StringComparison.OrdinalIgnoreCase)) root = XDocument.Load(absPath).Root?.DescendantsAndSelf("WorkspaceRoot").FirstOrDefault();
        else
        {
            using var zip = ZipFile.OpenRead(absPath);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), "WorkspaceRoot.cui", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return [];
            using var s = entry.Open();
            root = XDocument.Load(s).Root;
        }
        if (root == null) return [];
        return root.Elements("Workspace").Select(w => Norm((string?)w.Element("Name"))).Where(n => n.Length > 0).Distinct().ToList();
    }

    public static CuixModel Read(string absPath, string relPath, string? container = null)
    {
        var model = new CuixModel { File = relPath };
        var single = absPath.EndsWith(".cui", StringComparison.OrdinalIgnoreCase);
        using var zip = single ? null : ZipFile.OpenRead(absPath);
        var whole = single ? XDocument.Load(absPath, LoadOptions.SetLineInfo).Root : null;

        // the root element of a part: a .cuix entry's root; in a .cui the section of that name (the header: the whole file)
        XElement? Part(string name)
        {
            if (whole != null)
            {
                var section = Path.GetFileNameWithoutExtension(name);
                return section == "Header" ? whole : whole.DescendantsAndSelf(section).FirstOrDefault();
            }
            var entry = zip!.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var s = entry.Open();
            return XDocument.Load(s, LoadOptions.SetLineInfo).Root;
        }
        string Where(string part) => single ? relPath : $"{relPath}!{part}";
        var tabContainer = container ?? Path.GetFileName(relPath);

        var menu = Part("MenuGroup.cui");
        if (menu != null)
        {
            model.MenuGroup = (string?)menu.Attribute("Name") ?? "";
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
        if (header != null)
            foreach (var p in header.Descendants("PartialMenuFile"))
                if (!string.IsNullOrWhiteSpace(p.Value)) model.PartialMenus.Add(p.Value.Trim());

        var ribbon = Part("RibbonRoot.cui");
        if (ribbon != null)
            ReadRibbon(model, ribbon, Where("RibbonRoot.cui"), tabContainer);

        var ws = Part("WorkspaceRoot.cui");
        if (ws != null)
        {
            var byTab = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var w in ws.Elements("Workspace"))
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

    private static void ReadRibbon(CuixModel model, XElement root, string file, string container)
    {
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
