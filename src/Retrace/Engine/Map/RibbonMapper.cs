using Retrace.Engine.Emit;
using Retrace.Engine.Scan;
using Retrace.Engine.Scan.CodeRibbon;
using Retrace.Engine.Scan.Commands;
using Retrace.Engine.Scan.Cuix;
using Retrace.Engine.Util;

namespace Retrace.Engine.Map;

/// <summary>
/// Builds the ribbon map: reads every chosen folder (CUIX and CUI files, ribbon code in C# and VB, commands and functions in LISP, C++, C#
/// and VB), joins the live ribbon to what was read, and resolves each ribbon item to the code it runs. Pure .NET, no AutoCAD; writes nothing
/// (see <see cref="RibbonMapWriter"/>). Heuristic like the scanners: it prefers finding less over reporting something wrong, and says what it
/// could not follow in the row's <see cref="MapRow.Note"/> or the result's warnings.
/// </summary>
public static class RibbonMapper
{
    /// <summary>Never throws for bad input (a missing folder or an unreadable file is a warning); throws OperationCanceledException when
    /// <paramref name="ct"/> is cancelled (checked between folders and between stages).</summary>
    public static RibbonMapResult Build(RibbonMapOptions opt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var folders = ChooseFolders(opt.Folders, warnings);
        var result = new RibbonMapResult { Folders = folders, LiveMapped = opt.Live != null, Warnings = warnings };
        if (folders.Count == 0)
        {
            warnings.Add("no folder left to read, so nothing was mapped");
            return result;
        }
        new Builder(opt, result, ct).Run();
        return result;
    }

    // ------------------------------------------------------------ folders

    /// <summary>Full paths without a trailing separator, once each (case does not matter), in the given order. A folder that does not exist,
    /// or lies inside another chosen folder, is a warning and left out: the outer one reads it.</summary>
    private static List<string> ChooseFolders(IReadOnlyList<string> given, List<string> warnings)
    {
        var full = new List<string>();
        foreach (var raw in given)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = WithoutSeparator(Path.GetFullPath(raw.Trim().Trim('"'))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"folder not valid: {raw}");
                continue;
            }
            if (!full.Contains(path, StringComparer.OrdinalIgnoreCase)) full.Add(path);
        }
        var existing = new List<string>();
        foreach (var f in full)
        {
            if (Exists(f)) existing.Add(f);
            else warnings.Add($"folder not found: {f}");
        }
        var kept = new List<string>();
        foreach (var f in existing)
        {
            var outer = existing.FirstOrDefault(o => IsInside(f, o));
            if (outer != null) warnings.Add($"{f} is inside {outer}: read once");
            else kept.Add(f);
        }
        return kept;
    }

    private static bool Exists(string folder)
    {
        try { return Directory.Exists(folder); }
        catch (Exception) { return false; }
    }

    private static string WithoutSeparator(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var t = full.TrimEnd('\\', '/');
        return t.Length < root.Length ? root : t;   // "C:\" and "/" stay as they are
    }

    /// <summary>Inside at a separator: "C:\src2\a" is not inside "C:\src".</summary>
    private static bool IsInside(string inner, string outer)
    {
        var o = outer.TrimEnd('\\', '/');
        return inner.Length > o.Length && inner.StartsWith(o, StringComparison.OrdinalIgnoreCase) && inner[o.Length] is '\\' or '/';
    }

    /// <summary>The folder's own name, for the front of its warnings; the whole path for a drive root.</summary>
    private static string LeafName(string folder)
    {
        var leaf = folder.TrimEnd('\\', '/');
        leaf = leaf[(leaf.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return leaf.Length > 0 ? leaf : folder;
    }

    // ------------------------------------------------------------ places in files

    /// <summary>A scanner's place in a file of <paramref name="folder"/> as a full path. "acad.cuix!RibbonRoot.cui" is the entry inside the zip.</summary>
    internal static MapSource Source(string folder, SourceRef r)
    {
        var (file, part) = SplitPart(r.File);
        return new MapSource(Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar)), r.Line, folder, part);
    }

    /// <summary>A scanner's place in a customization file outside the chosen folders (its full path is known).</summary>
    private static MapSource Source(string file, int line, string? part) => new(file, line, null, part);

    private static (string File, string? Part) SplitPart(string file)
    {
        int bang = file.LastIndexOf('!');
        return bang > 0 && file[..bang].EndsWith(".cuix", StringComparison.OrdinalIgnoreCase) ? (file[..bang], file[(bang + 1)..]) : (file, null);
    }

    // ------------------------------------------------------------ the work in progress

    private const string DialogLauncher = "dialog launcher";

    /// <summary>A tab as the scanners read it, with where it came from.</summary>
    private sealed class ScanTab
    {
        public required RibbonTab Tab { get; init; }
        /// <summary>"cuix", "cui", "code" or "loaded cuix".</summary>
        public required string Origin { get; init; }
        /// <summary>The chosen folder; null for a loaded customization file outside them.</summary>
        public string? Folder { get; init; }
        /// <summary>The .cuix / .cui file, full path (CUIX tabs).</summary>
        public string? File { get; init; }
        public CuixModel? Cuix { get; init; }
        public bool Loaded => Folder == null;
        public List<ScanPanel> Panels { get; } = new();
        /// <summary>A folder tab's rows in order, then the live items found in it that the folders do not have.</summary>
        public List<Work> Rows { get; } = new();

        public MapSource? SourceOf(SourceRef? r)
        {
            if (r == null) return null;
            if (Folder != null) return Source(Folder, r);
            var (_, part) = SplitPart(r.File);
            return Source(File ?? r.File, r.Line, part);
        }
    }

    private sealed class ScanPanel
    {
        public required RibbonPanel Panel { get; init; }
        public List<ScanItem> Items { get; } = new();
    }

    /// <summary>A ribbon item, or the panel's dialog launcher, with what its row needs.</summary>
    private sealed class ScanItem
    {
        public required ScanTab Tab { get; init; }
        public required ScanPanel Panel { get; init; }
        public string? Id { get; init; }
        public required string Label { get; init; }
        /// <summary>The label as live items are compared to it: one space between words.</summary>
        public required string Key { get; init; }
        public required string Kind { get; init; }
        public MapSource? DefinedIn { get; init; }
        public string? Macro { get; init; }
        /// <summary>The command a code button's handler sends (the Id rule of CodeRibbonReader), or the one its macro names.</summary>
        public string? CodeCommand { get; init; }
        /// <summary>A CUIX / CUI item's command as CuixReader read it: its macro's, else the menu macro's CLICommand.</summary>
        public string? CuiCommand { get; init; }
        public string? HandlerMethod { get; init; }
        public bool Launcher { get; init; }
        public bool Matched { get; set; }
        /// <summary>A folder item's row, made up front. A loaded item gets one for each live item it is matched to.</summary>
        public Work? Work { get; set; }

        public Work Make(string tab, string? tabId)
        {
            var row = new MapRow
            {
                Tab = tab, TabId = tabId, Panel = Panel.Panel.Title, Item = Label, ItemId = Id, Kind = Kind, Origin = Tab.Origin,
                DefinedIn = DefinedIn, Macro = Macro,
            };
            return new Work(row) { CodeCommand = CodeCommand, CuiCommand = CuiCommand, HandlerMethod = HandlerMethod };
        }
    }

    /// <summary>A row, and what resolving it needs besides the row.</summary>
    private sealed class Work(MapRow row)
    {
        public MapRow Row { get; } = row;
        public string? CodeCommand { get; init; }
        public string? CuiCommand { get; init; }
        public string? HandlerMethod { get; init; }
        /// <summary>The live item this row was matched to or made from.</summary>
        public LiveItem? Live { get; set; }
    }

    /// <summary>A live tab that no scanned tab matches, with its rows.</summary>
    private sealed class LiveGroup
    {
        public List<Work> Rows { get; } = new();
    }

    private enum TargetKind { Command, Lisp, File, Method, Class }

    private sealed record Target(TargetKind Kind, string Name, string FoundBy)
    {
        public string Words => Kind switch
        {
            TargetKind.Command => "command " + Name,
            TargetKind.Lisp => "LISP (" + Name + ")",
            TargetKind.File => "loads " + Name,
            TargetKind.Method => "method " + Name,
            _ => "class " + Name,
        };
    }

    private sealed class Builder(RibbonMapOptions opt, RibbonMapResult result, CancellationToken ct)
    {
        private readonly MapIndex _index = new();
        private readonly List<ScanTab> _cuixTabs = new();
        private readonly List<ScanTab> _codeTabs = new();
        private readonly List<ScanTab> _loadedTabs = new();
        private readonly List<LiveGroup> _liveGroups = new();
        // full paths of the .cuix / .cui files the folders held
        private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<ScanItem>>? _byId;

        private void Warn(string w)
        {
            if (!result.Warnings.Contains(w)) result.Warnings.Add(w);
        }

        public void Run()
        {
            for (int i = 0; i < result.Folders.Count; i++) ReadFolder(i, result.Folders[i]);
            ct.ThrowIfCancellationRequested();

            // a handler object (assembly, class, method) is kept when some folder declares that method, as the docs pipeline does
            var model = new EngineModel { InputRoot = "" };
            model.CodeTabs.AddRange(_codeTabs.Select(t => t.Tab));
            Linker.KeepDeclaredHandlers(model, _index.Declares);
            foreach (var w in model.Warnings) Warn(w);

            if (opt.Live != null) ReadCustomization();
            ct.ThrowIfCancellationRequested();

            foreach (var t in _cuixTabs.Concat(_codeTabs).Concat(_loadedTabs)) Populate(t);
            if (opt.Live != null) MapLive(opt.Live);
            ct.ThrowIfCancellationRequested();

            var folderRows = _cuixTabs.Concat(_codeTabs).SelectMany(t => t.Rows).ToList();
            foreach (var w in folderRows.Concat(_liveGroups.SelectMany(g => g.Rows)))
            {
                ct.ThrowIfCancellationRequested();
                Resolve(w);
            }

            foreach (var w in folderRows) result.Rows.Add(w.Row);
            foreach (var g in _liveGroups)
            {
                if (opt.AllLiveTabs || g.Rows.Any(w => w.Row.Status == MapStatus.Mapped)) result.Rows.AddRange(g.Rows.Select(w => w.Row));
                else result.LiveTabsLeftOut++;
            }
        }

        // ------------------------------------------------------------ reading

        private void Try(string folder, string what, Action step)
        {
            ct.ThrowIfCancellationRequested();
            try { step(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Warn($"{folder}: {what}: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void ReadFolder(int index, string folder)
        {
            ct.ThrowIfCancellationRequested();
            var name = LeafName(folder);
            EngineModel model;
            try { model = InputScanner.Scan(folder, lisp: true, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Warn($"{name}: could not be read: {ex.Message}");
                return;
            }
            foreach (var w in model.Warnings) Warn($"{name}: {w}");
            foreach (var s in model.Skipped) Warn($"{name}: skipped {s}");
            var files = model.Files;
            result.FilesRead += files.Count(f => f.Lang != Lang.Other);

            foreach (var f in files.Where(f => f.Lang == Lang.Cuix)) _read.Add(FullPath(f.AbsPath));
            // a loose part of an unzipped .cuix (RibbonRoot.cui, MenuGroup.cui...) is not a legacy .cui: left out
            foreach (var f in files.Where(f => f.Lang == Lang.Cuix))
                Try(name, f.RelPath, () =>
                {
                    var cui = f.AbsPath.EndsWith(".cui", StringComparison.OrdinalIgnoreCase);
                    if (cui && !CuixReader.IsLegacyCui(f.AbsPath)) return;
                    var cuix = CuixReader.Read(f.AbsPath, f.RelPath);
                    foreach (var tab in cuix.Tabs)
                        _cuixTabs.Add(new ScanTab { Tab = tab, Origin = cui ? "cui" : "cuix", Folder = folder, File = f.AbsPath, Cuix = cuix });
                });

            Try(name, "code ribbon", () =>
            {
                foreach (var tab in CodeRibbonReader.Read(files))
                    _codeTabs.Add(new ScanTab { Tab = tab, Origin = "code", Folder = folder });
            });

            var commands = new List<CommandEntry>();
            Try(name, "commands", () => commands.AddRange(CommandScanner.Scan(files)));
            Try(name, "LISP", () => commands.AddRange(LispScanner.Scan(files)));
            Try(name, "definitions", () =>
            {
                var failed = _index.AddFolder(index, folder, files, commands, ct);
                if (failed > 0) Warn($"{name}: {failed} source file(s) could not be searched for methods and classes");
            });
        }

        private static string FullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        /// <summary>The customization files AutoCAD has loaded, except those a folder held: they say which macro a live item runs.</summary>
        private void ReadCustomization()
        {
            foreach (var given in opt.Customization)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(given)) continue;
                var path = FullPath(given.Trim());
                var name = Path.GetFileName(path);
                var cui = path.EndsWith(".cui", StringComparison.OrdinalIgnoreCase);
                if (!cui && !path.EndsWith(".cuix", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_read.Add(path)) continue;
                if (!File.Exists(path)) { Warn($"loaded customization file not found: {name}"); continue; }
                if (InputScanner.LooksSecret(name)) { Warn($"{name}: skipped (the name looks like a secret)"); continue; }
                Try("loaded customization", name, () =>
                {
                    if (cui && !CuixReader.IsLegacyCui(path)) return;
                    var cuix = CuixReader.Read(path, name);
                    result.FilesRead++;
                    foreach (var tab in cuix.Tabs)
                        _loadedTabs.Add(new ScanTab { Tab = tab, Origin = "loaded cuix", File = path, Cuix = cuix });
                });
            }
        }

        // ------------------------------------------------------------ rows of the scanned tabs

        private void Populate(ScanTab t)
        {
            var live = opt.Live != null;
            var walked = Linker.Walk(t.Tab).ToLookup(w => w.panel);
            foreach (var p in t.Tab.Panels)
            {
                var sp = new ScanPanel { Panel = p };
                foreach (var (_, item, parent, _) in walked[p])
                {
                    if (item.Kind is RibbonItemKind.Separator or RibbonItemKind.RowPanel or RibbonItemKind.Label) continue;
                    var label = ModelIndex.Label(item);
                    if (parent != null) label = ModelIndex.Label(parent) + " › " + label;
                    sp.Items.Add(new ScanItem
                    {
                        Tab = t, Panel = sp, Id = item.Id, Label = label, Key = Norm(label),
                        Kind = KindText(item.Kind),
                        DefinedIn = t.SourceOf(item.Source ?? p.Source ?? t.Tab.Source),
                        Macro = Blank(item.Macro),
                        CodeCommand = t.Origin == "code" ? item.Command : null,
                        CuiCommand = t.Origin == "code" ? null : item.Command,
                        HandlerMethod = item.HandlerMethod,
                    });
                }
                if (p.DialogLauncherCommand is { Length: > 0 } macroId)
                    sp.Items.Add(new ScanItem
                    {
                        Tab = t, Panel = sp, Label = DialogLauncher, Key = DialogLauncher, Kind = DialogLauncher, Launcher = true,
                        DefinedIn = t.SourceOf(p.Source ?? t.Tab.Source),
                        Macro = t.Cuix != null && t.Cuix.Macros.TryGetValue(macroId, out var m) ? Blank(m.Macro) : null,
                    });
                t.Panels.Add(sp);
            }
            if (t.Loaded) return;
            foreach (var si in t.Panels.SelectMany(p => p.Items))
            {
                si.Work = si.Make(t.Tab.Title, t.Tab.Id);
                si.Work.Row.Live = live ? false : null;
                t.Rows.Add(si.Work);
            }
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static string KindText(RibbonItemKind k) => k switch
        {
            RibbonItemKind.Button => "button",
            RibbonItemKind.SplitButton => "split button",
            RibbonItemKind.ToggleButton => "toggle",
            RibbonItemKind.Control => "control",
            RibbonItemKind.Gallery => "gallery",
            RibbonItemKind.DropDown => "drop-down",
            _ => "other",
        };

        // ------------------------------------------------------------ the live ribbon

        private sealed record LiveEntry(LiveItem Item, string Label, string Match, bool Launcher);

        private static readonly HashSet<string> NotRows = new(StringComparer.OrdinalIgnoreCase) { "RibbonSeparator", "RibbonPanelBreak", "RibbonRowBreak", "RibbonLabel" };
        private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase) { "RibbonRowPanel", "RibbonFoldPanel" };

        private static string LiveKind(string? type) => (type ?? "").ToLowerInvariant() switch
        {
            "ribbonbutton" => "button",
            "ribbonsplitbutton" => "split button",
            "ribbontogglebutton" or "ribboncheckbox" or "ribbonradiobutton" => "toggle",
            "ribbongallery" => "gallery",
            "ribbonlistbutton" or "ribboncombo" or "ribbonlist" => "drop-down",
            "ribbontextbox" or "ribbonslider" or "ribbonspinner" => "control",
            _ => "other",
        };

        private static string Norm(string? s) => Rx.Replace(s ?? "", @"\s+", " ").Trim();

        /// <summary>Text, else Name, else Id; trimmed, newlines as spaces.</summary>
        private static string LiveLabel(LiveItem i) =>
            Norm(!string.IsNullOrWhiteSpace(i.Text) ? i.Text : !string.IsNullOrWhiteSpace(i.Name) ? i.Name : i.Id);

        private static string Shown(LiveItem i) => LiveLabel(i) is { Length: > 0 } label ? label : $"({i.Type})";

        /// <summary>The rows of a live panel: containers are walked, separators and labels dropped, a member is "Parent › Member".</summary>
        private static List<LiveEntry> Flatten(LivePanel panel)
        {
            var list = new List<LiveEntry>();
            Walk(panel.Items ?? [], null, 0);
            if (panel.DialogLauncher != null) list.Add(new LiveEntry(panel.DialogLauncher, DialogLauncher, DialogLauncher, true));
            return list;

            void Walk(IReadOnlyList<LiveItem> items, LiveItem? parent, int depth)
            {
                if (depth > 12) return;
                foreach (var it in items)
                {
                    var type = it.Type ?? "";
                    if (NotRows.Contains(type)) continue;
                    if (Containers.Contains(type)) { Walk(it.Children ?? [], parent, depth + 1); continue; }
                    list.Add(parent == null
                        ? new LiveEntry(it, Shown(it), LiveLabel(it), false)
                        : new LiveEntry(it, Shown(parent) + " › " + Shown(it), LiveLabel(parent) + " › " + LiveLabel(it), false));
                    Walk(it.Children ?? [], it, depth + 1);
                }
            }
        }

        /// <summary>The scanned tab a live tab is: the same id (folders first, then the loaded files); else, for tabs built in code, the only
        /// code tab with its title.</summary>
        private ScanTab? FindTab(LiveTab live)
        {
            var byId = _cuixTabs.Concat(_codeTabs).Concat(_loadedTabs).FirstOrDefault(t => LiveIds.Matches(live.Id, t.Tab.Id));
            if (byId != null) return byId;
            var sameTitle = _codeTabs.Where(t => LiveIds.Same(live.Title, t.Tab.Title)).ToList();
            return sameTitle.Count == 1 ? sameTitle[0] : null;
        }

        private static ScanPanel? FindPanel(ScanTab? tab, LivePanel live, HashSet<ScanPanel> used)
        {
            if (tab == null) return null;
            var panel = tab.Panels.FirstOrDefault(p => !used.Contains(p) && LiveIds.Matches(live.Id, p.Panel.Id))
                        ?? tab.Panels.FirstOrDefault(p => !used.Contains(p) && LiveIds.Same(live.Title, p.Panel.Title))
                        ?? tab.Panels.FirstOrDefault(p => LiveIds.Matches(live.Id, p.Panel.Id) || LiveIds.Same(live.Title, p.Panel.Title));
            if (panel != null) used.Add(panel);
            return panel;
        }

        /// <summary>The scanned item a live item is: in the matched panel by id, else by label; else anywhere when exactly one item has its id.</summary>
        private ScanItem? FindItem(ScanPanel? panel, LiveEntry e)
        {
            if (panel != null)
            {
                if (e.Launcher) return panel.Items.FirstOrDefault(i => i.Launcher);
                var found = Pick(panel.Items.Where(i => !i.Launcher && LiveIds.Matches(e.Item.Id, i.Id)))
                            ?? Pick(panel.Items.Where(i => !i.Launcher && LiveIds.Same(e.Match, i.Key)));
                if (found != null) return found;
            }
            if (e.Launcher || string.IsNullOrWhiteSpace(e.Item.Id)) return null;
            var id = e.Item.Id.Trim();
            var everywhere = ById(id).Concat(id.LastIndexOf('.') > 0 ? ById(id[(id.LastIndexOf('.') + 1)..]) : []).Distinct().ToList();
            return everywhere.Count == 1 ? everywhere[0] : null;
        }

        /// <summary>The first of several candidates not matched yet, so two items of one label pair up in order.</summary>
        private static ScanItem? Pick(IEnumerable<ScanItem> candidates)
        {
            var list = candidates.ToList();
            return list.FirstOrDefault(i => !i.Matched) ?? list.FirstOrDefault();
        }

        private List<ScanItem> ById(string id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, List<ScanItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (var si in _cuixTabs.Concat(_codeTabs).Concat(_loadedTabs).SelectMany(t => t.Panels).SelectMany(p => p.Items))
                {
                    if (si.Launcher || string.IsNullOrWhiteSpace(si.Id)) continue;
                    if (!_byId.TryGetValue(si.Id.Trim(), out var list)) _byId[si.Id.Trim()] = list = new List<ScanItem>();
                    list.Add(si);
                }
            }
            return _byId.TryGetValue(id, out var found) ? found : [];
        }

        private void MapLive(IReadOnlyList<LiveTab> live)
        {
            foreach (var lt in live)
            {
                ct.ThrowIfCancellationRequested();
                var tab = FindTab(lt);
                // the rows of a tab the folders have go with it; any other tab, live only or from a loaded file, is a group of its own
                List<Work> rows;
                string title, tabId;
                if (tab is { Loaded: false })
                {
                    rows = tab.Rows;
                    title = tab.Tab.Title;
                    tabId = tab.Tab.Id;
                }
                else
                {
                    var group = new LiveGroup();
                    _liveGroups.Add(group);
                    rows = group.Rows;
                    title = !string.IsNullOrWhiteSpace(lt.Title) ? Norm(lt.Title) : tab?.Tab.Title ?? Norm(lt.Id);
                    tabId = lt.Id ?? tab?.Tab.Id ?? "";
                }

                var used = new HashSet<ScanPanel>();
                foreach (var lp in lt.Panels ?? [])
                {
                    var panel = FindPanel(tab, lp, used);
                    foreach (var e in Flatten(lp))
                    {
                        var si = FindItem(panel, e);
                        if (si == null) rows.Add(LiveOnly(title, tabId, lp, e));
                        else if (!si.Tab.Loaded) MarkShown(si, e);
                        else rows.Add(LoadedRow(si, e, title, tabId));
                    }
                }
            }
        }

        /// <summary>A live item the folders have: it is shown in AutoCAD. A row with no macro takes the live item's.</summary>
        private static void MarkShown(ScanItem si, LiveEntry e)
        {
            si.Matched = true;
            var w = si.Work!;
            w.Row.Live = true;
            w.Live ??= e.Item;
            if (w.Row.Macro == null && Blank(e.Item.Macro) is { } macro) w.Row.Macro = macro;
        }

        /// <summary>A live item of a loaded customization file: the file knows its macro, the live item does not.</summary>
        private static Work LoadedRow(ScanItem si, LiveEntry e, string title, string tabId)
        {
            si.Matched = true;
            var w = si.Make(title, tabId);
            w.Row.Live = true;
            w.Live = e.Item;
            return w;
        }

        private static Work LiveOnly(string title, string tabId, LivePanel panel, LiveEntry e)
        {
            var row = new MapRow
            {
                Tab = title, TabId = tabId, Panel = Norm(panel.Title), Item = e.Label, ItemId = Blank(e.Item.Id),
                Kind = e.Launcher ? DialogLauncher : LiveKind(e.Item.Type), Origin = "live", Live = true, Macro = Blank(e.Item.Macro),
            };
            return new Work(row) { Live = e.Item };
        }

        // ------------------------------------------------------------ what a row runs

        private const string BySendingHandler = "button id (the handler sends it)";

        private void Resolve(Work w)
        {
            var row = w.Row;
            var targets = new List<Target>();
            var notes = new List<string>();
            void Add(TargetKind kind, string name, string foundBy)
            {
                if (!targets.Any(t => t.Kind == kind && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) targets.Add(new Target(kind, name, foundBy));
            }

            // a. the macro; b. a CUIX item's own command; c. the command a code button's handler sends; d. the method a handler object names
            var macro = MacroText.Parse(row.Macro);
            var byMacro = row.Origin is "code" or "live" ? "command parameter" : "macro";
            foreach (var c in macro.Commands) Add(TargetKind.Command, c, byMacro);
            foreach (var f in macro.LispFunctions) Add(TargetKind.Lisp, f, byMacro);
            foreach (var l in macro.Loads) Add(TargetKind.File, l, byMacro);
            if (macro.Diesel) notes.Add("DIESEL macro: not followed");
            if (w.CuiCommand != null) Add(TargetKind.Command, w.CuiCommand, byMacro);   // the CLICommand when the macro shows none
            if (w.CodeCommand != null) Add(TargetKind.Command, w.CodeCommand, BySendingHandler);
            if (w.HandlerMethod != null) Add(TargetKind.Method, w.HandlerMethod, "handler object");

            Target? first = null;
            var missing = new List<string>();
            void Look(int from)
            {
                for (int i = from; i < targets.Count; i++)
                {
                    var t = targets[i];
                    var defs = t.Kind switch
                    {
                        TargetKind.Command => _index.Commands(t.Name),
                        TargetKind.Lisp => _index.LispFunctions(t.Name),
                        TargetKind.File => _index.Files(t.Name),
                        TargetKind.Method => _index.Methods(t.Name),
                        _ => _index.Classes(t.Name),
                    };
                    if (defs.Count == 0) { missing.Add(t.Words + ": not in these folders"); continue; }
                    first ??= t;
                    foreach (var d in defs)
                        if (!row.Sources.Any(s => s.What == d.What && s.Source == d.Source)) row.Sources.Add(d);
                }
            }
            Look(0);

            // e. the live item's own handler and parameter classes, only when nothing above found code in the folders: a handler that sends
            // the macro is one class behind every button and would only repeat itself on each row. f. nothing at all: a button's id may be
            // the command (AutoCAD's own buttons, many plugins)
            if (w.Live != null && row.Sources.Count == 0)
            {
                int before = targets.Count;
                if (OwnType(w.Live.HandlerType) is { } handler) Add(TargetKind.Class, handler, "command handler class");
                if (OwnType(w.Live.ParameterType) is { } parameter) Add(TargetKind.Class, parameter, "command parameter object");
                if (targets.Count == 0 && GuessedCommand(w.Live) is { } guess && _index.HasCommand(guess)) Add(TargetKind.Command, guess.ToUpperInvariant(), "button id (a guess)");
                Look(before);
            }

            row.Runs = string.Join(", ", targets.Select(t => t.Words));
            if (targets.Count == 0)
            {
                row.Status = MapStatus.NoAction;
                if (!macro.Diesel)
                    notes.Add(row.Macro == null ? (row.Origin == "live" ? "the live ribbon tells no command (an AutoCAD button?)" : "no macro") : "the macro names no command or function");
            }
            else
            {
                row.Status = row.Sources.Count > 0 ? MapStatus.Mapped : MapStatus.NotFound;
                row.FoundBy = (first ?? targets[0]).FoundBy;
                notes.InsertRange(0, missing);
            }
            row.Note = notes.Count > 0 ? string.Join("; ", notes) : null;
        }

        /// <summary>A live item's own handler or parameter class; AutoCAD's and .NET's are not code of the folders.</summary>
        private static string? OwnType(string? type)
        {
            var t = type?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            foreach (var ns in new[] { "Autodesk.", "System.", "Microsoft.", "MS." })
                if (t.StartsWith(ns, StringComparison.OrdinalIgnoreCase)) return null;
            return t;
        }

        /// <summary>The live item's id without its group in front ("ACAD.NETCMD" is NETCMD).</summary>
        private static string? GuessedCommand(LiveItem live)
        {
            var id = live.Id?.Trim();
            if (string.IsNullOrEmpty(id)) return null;
            id = id[(id.LastIndexOf('.') + 1)..];
            return id.Length > 0 ? id : null;
        }
    }
}
