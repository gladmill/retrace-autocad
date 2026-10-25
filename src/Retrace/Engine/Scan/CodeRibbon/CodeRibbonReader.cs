using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.CodeRibbon;

/// <summary>Finds ribbon tabs built in C#/VB code (Autodesk.Windows RibbonTab/RibbonPanelSource/RibbonButton).
/// Heuristic, text based: it follows factory calls and helper methods by name and maps literal
/// arguments onto the parameters they flow into. Anything it cannot resolve becomes a "{name}" placeholder.</summary>
public static class CodeRibbonReader
{
    public static List<RibbonTab> Read(IReadOnlyList<SourceFile> files)
    {
        var texts = new List<CodeText>();
        foreach (var f in files)
        {
            if (f.Lang is not (Lang.CSharp or Lang.VbNet) || f.Text.Length == 0) continue;
            if (!f.Text.Contains("Ribbon", StringComparison.Ordinal)) continue;
            texts.Add(new CodeText(f));
        }
        return new Reader(texts).FindTabs();
    }

    // ------------------------------------------------------------------ model of a scanned method

    private sealed record Param(string Type, string Name);

    private sealed class M
    {
        public required CodeText Ct;
        public required MethodSpan Sp;
        public required string Body;
        public required int Base;
        public required List<Param> Params;
        public bool Vb => Ct.File.Lang == Lang.VbNet;
        public SourceRef Ref(int bodyIdx) => Ct.Ref(Base + bodyIdx);
    }

    private sealed class Scope
    {
        public required M Method;
        public Dictionary<string, (string Expr, Scope Scope)> Args { get; } = new();
    }

    private readonly record struct Val(string? Text, bool Literal);

    private sealed class Reader
    {
        private readonly List<CodeText> _texts;
        private readonly List<M> _methods = new();
        // string constant/static fields: name -> (file, value)
        private readonly Dictionary<string, List<(string File, string Value)>> _fields = new();

        private static readonly Regex CsField = Rx.Create(
            "\\b(?:const|static|readonly)\\b[^;=(){}]*?\\bstring\\s+(\\w+)\\s*=\\s*(@?\"(?:[^\"\\\\]|\\\\.|\"\")*\")\\s*;",
            RegexOptions.Compiled);
        private static readonly Regex VbField = Rx.Create(
            "\\b(?:Const|Shared\\s+ReadOnly|ReadOnly)\\s+(\\w+)\\s+As\\s+String\\s*=\\s*(\"(?:[^\"]|\"\")*\")",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Reader(List<CodeText> texts)
        {
            _texts = texts;
            foreach (var ct in texts)
            {
                try
                {
                    foreach (var sp in ct.Methods())
                    {
                        string body = ct.Code[sp.BodyStart..(sp.BodyEnd + 1)];
                        _methods.Add(new M { Ct = ct, Sp = sp, Body = body, Base = sp.BodyStart, Params = ParseParams(sp.Signature) });
                    }
                }
                catch (Exception) { /* heuristic: skip files we cannot split */ }
                var rx = ct.File.Lang == Lang.VbNet ? VbField : CsField;
                foreach (Match m in rx.Matches(ct.Code))
                {
                    var v = CodeText.Literal(m.Groups[2].Value);
                    if (v == null) continue;
                    if (!_fields.TryGetValue(m.Groups[1].Value, out var l)) _fields[m.Groups[1].Value] = l = new();
                    l.Add((ct.File.RelPath, v));
                }
            }
        }

        // ------------------------------------------------------------ tabs

        private static readonly Regex NewTab = Rx.Create(@"\bnew\s+RibbonTab\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public List<RibbonTab> FindTabs()
        {
            var result = new List<RibbonTab>();
            foreach (var creator in _methods)
            {
                var sc = new Scope { Method = creator };
                foreach (Match nm in NewTab.Matches(creator.Body))
                {
                    var (_, init, _) = ParseNew(creator.Body, nm.Index);
                    string? tabVar = VarBefore(creator.Body, nm.Index);
                    var props = CollectProps(creator.Body, tabVar, init, creator.Vb);
                    Val title = props.TryGetValue("Title", out var t) ? Eval(t, sc) : default;
                    Val name = props.TryGetValue("Name", out var n) ? Eval(n, sc) : default;
                    Val id = props.TryGetValue("Id", out var i) ? Eval(i, sc) : default;
                    if (title.Text == null && name.Text == null && id.Text == null) continue; // not a configured tab
                    string tid = id.Text ?? name.Text ?? title.Text!;
                    var tab = new RibbonTab
                    {
                        Id = tid,
                        Title = title.Text ?? name.Text ?? tid,
                        Origin = RibbonOrigin.Code,
                        Container = creator.Ct.File.Project,
                        Source = creator.Ref(nm.Index),
                    };
                    if (name.Text != null && name.Text != tab.Title) tab.DisplayName = name.Text;
                    tab.Extra["class"] = creator.Sp.FullClass;
                    tab.Extra["method"] = creator.Sp.QualifiedName;
                    AddPanels(tab, creator, tabVar, creatorsInClass: CountTabsInClass(creator.Sp.FullClass));
                    result.Add(tab);
                }
            }
            return result;
        }

        private int CountTabsInClass(string fullClass) =>
            _methods.Where(m => m.Sp.FullClass == fullClass)
                .Sum(m => NewTab.Matches(m.Body).Count(x =>
                {
                    var sc = new Scope { Method = m };
                    var (_, init, _) = ParseNew(m.Body, x.Index);
                    var p = CollectProps(m.Body, VarBefore(m.Body, x.Index), init, m.Vb);
                    return p.ContainsKey("Title") || p.ContainsKey("Name") || p.ContainsKey("Id");
                }));

        private void AddPanels(RibbonTab tab, M creator, string? tabVar, int creatorsInClass)
        {
            // methods that add panels: the creator itself, callees given the tab, and (single-tab classes) siblings
            var candidates = new List<M>();
            void Add(M m) { if (!candidates.Contains(m) && m.Body.Contains(".Panels.Add", StringComparison.OrdinalIgnoreCase)) candidates.Add(m); }
            Add(creator);
            if (tabVar != null)
            {
                var call = Rx.Create(@"(?<![\w.])(?:([\w.]+)\.)?(\w+)\s*\(", RegexOptions.Compiled);
                foreach (Match cm in call.Matches(creator.Body))
                {
                    int open = cm.Index + cm.Length - 1;
                    int close = CodeText.MatchBrace(creator.Body, open);
                    if (close < 0) continue;
                    var args = CodeText.SplitArgs(creator.Body[(open + 1)..close]);
                    if (!args.Any(a => a == tabVar)) continue;
                    var target = FindMethod(cm.Groups[2].Value, cm.Groups[1].Success ? cm.Groups[1].Value : null, args.Count, creator);
                    if (target != null) Add(target);
                }
            }
            if (creatorsInClass <= 1)
                foreach (var m in _methods.Where(m => m.Sp.FullClass == creator.Sp.FullClass && m.Ct == creator.Ct)) Add(m);

            foreach (var m in candidates)
            {
                var sc = new Scope { Method = m };
                foreach (Match pm in Rx.Matches(m.Body, @"\.Panels\s*\.\s*Add\s*\(", RegexOptions.IgnoreCase))
                {
                    int open = pm.Index + pm.Length - 1;
                    int close = CodeText.MatchBrace(m.Body, open);
                    if (close < 0) continue;
                    var arg = CodeText.SplitArgs(m.Body[(open + 1)..close]).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(arg)) continue;
                    var panel = ResolvePanel(arg, sc, 0, m.Ref(pm.Index));
                    if (panel != null) tab.Panels.Add(panel);
                }
            }
        }

        // ------------------------------------------------------------ panels

        private static readonly Regex NewPanelSource = Rx.Create(@"\bnew\s+RibbonPanelSource\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private RibbonPanel? ResolvePanel(string expr, Scope sc, int depth, SourceRef addSite)
        {
            if (depth > 5) return null;
            expr = expr.Trim();
            if (IsIdent(expr))
            {
                if (sc.Args.TryGetValue(expr, out var bound)) return ResolvePanel(bound.Expr, bound.Scope, depth + 1, addSite);
                var init = DeclInit(sc.Method, expr);
                return init == null ? null : ResolvePanel(init, sc, depth + 1, addSite);
            }
            if (SplitCall(expr) is { } call)
            {
                var target = FindMethod(call.Name, call.Qualifier, call.Args.Count, sc.Method);
                if (target == null) return null;
                var inner = Bind(target, call.Args, sc);
                if (!NewPanelSource.IsMatch(target.Body))
                {
                    foreach (var r in Returns(target))
                    {
                        var p = ResolvePanel(r, inner, depth + 1, addSite);
                        if (p != null) return p;
                    }
                    return null;
                }
                var panel = BuildPanel(target, inner);
                if (panel != null)
                {
                    panel.Source = addSite;
                    panel.Extra["definedAt"] = target.Ct.Ref(target.Sp.Start).ToString();
                }
                return panel;
            }
            return null;
        }

        private RibbonPanel? BuildPanel(M m, Scope sc)
        {
            var nm = NewPanelSource.Match(m.Body);
            if (!nm.Success) return null;
            var (_, init, _) = ParseNew(m.Body, nm.Index);
            string? psVar = VarBefore(m.Body, nm.Index);
            var props = CollectProps(m.Body, psVar, init, m.Vb);
            string title = props.TryGetValue("Title", out var t) ? Eval(t, sc).Text ?? "" : "";
            var panel = new RibbonPanel { Id = Slug.Of(title), Title = title };

            var rowVars = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match rm in Rx.Matches(m.Body, @"(\w+)\s*=\s*new\s+RibbonRowPanel\b|(\w+)\s+As\s+New\s+RibbonRowPanel\b", RegexOptions.IgnoreCase))
                rowVars.Add(rm.Groups[1].Success ? rm.Groups[1].Value : rm.Groups[2].Value);

            var adds = ItemsAdds(m).ToList();
            // Lines of each row panel: items split at RibbonRowBreak
            List<List<RibbonItem>> LinesOf(string rowVar)
            {
                var lines = new List<List<RibbonItem>> { new() };
                foreach (var a in adds.Where(a => a.Recv == rowVar))
                {
                    if (IsRowBreak(a.Arg)) { lines.Add(new()); continue; }
                    var it = ResolveItem(a.Arg, sc, 0);
                    if (it == null) continue;
                    it.Source = m.Ref(a.Index);
                    lines[^1].Add(it);
                }
                return lines.Where(l => l.Count > 0).ToList();
            }

            var cur = new RibbonRow();
            void Close()
            {
                if (cur.Items.Count > 0) panel.Rows.Add(cur);
                cur = new RibbonRow();
            }
            if (psVar != null)
                foreach (var a in adds.Where(a => a.Recv == psVar))
                {
                    if (IsRowBreak(a.Arg)) { Close(); continue; }
                    if (IsIdent(a.Arg) && rowVars.Contains(a.Arg))
                    {
                        var rp = new RibbonItem { Id = a.Arg, Kind = RibbonItemKind.RowPanel, Source = m.Ref(a.Index) };
                        foreach (var line in LinesOf(a.Arg))
                        {
                            var row = new RibbonRow();
                            row.Items.AddRange(line);
                            rp.SubRows.Add(row);
                        }
                        if (rp.SubRows.Count > 0) cur.Items.Add(rp);
                        continue;
                    }
                    var it = ResolveItem(a.Arg, sc, 0);
                    if (it == null) continue;
                    it.Source = m.Ref(a.Index);
                    cur.Items.Add(it);
                }
            Close();
            return panel;
        }

        private readonly record struct ItemAdd(string Recv, string Arg, int Index);

        private static IEnumerable<ItemAdd> ItemsAdds(M m)
        {
            foreach (Match am in Rx.Matches(m.Body, @"(?<![\w.])(\w+)\s*\.\s*Items\s*\.\s*Add\s*\(", RegexOptions.IgnoreCase))
            {
                int open = am.Index + am.Length - 1;
                int close = CodeText.MatchBrace(m.Body, open);
                if (close < 0) continue;
                var arg = CodeText.SplitArgs(m.Body[(open + 1)..close]).FirstOrDefault() ?? "";
                if (arg.Length > 0) yield return new ItemAdd(am.Groups[1].Value, arg.Trim(), am.Index);
            }
        }

        private static bool IsRowBreak(string expr) => Rx.IsMatch(expr, @"^new\s+RibbonRowBreak\b", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------ items

        private static readonly Regex NewItem = Rx.Create(@"^new\s+(Ribbon\w+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private RibbonItem? ResolveItem(string expr, Scope sc, int depth)
        {
            if (depth > 6) return null;
            expr = expr.Trim();
            if (NewItem.Match(expr) is { Success: true } nm)
            {
                var (type, init, _) = ParseNew(expr, 0);
                return BuildItem(type, init, null, sc, expr);
            }
            if (IsIdent(expr))
            {
                if (sc.Args.TryGetValue(expr, out var bound)) return ResolveItem(bound.Expr, bound.Scope, depth + 1);
                var init = DeclInit(sc.Method, expr);
                if (init == null) return Placeholder(expr);
                init = init.Trim();
                if (NewItem.Match(init) is { Success: true })
                {
                    var (type, ini, _) = ParseNew(init, 0);
                    return BuildItem(type, ini, expr, sc, init);
                }
                var viaCall = ResolveItem(init, sc, depth + 1);
                if (viaCall != null) ApplyLaterProps(viaCall, expr, sc);
                return viaCall ?? Placeholder(expr);
            }
            if (SplitCall(expr) is { } call)
            {
                var target = FindMethod(call.Name, call.Qualifier, call.Args.Count, sc.Method);
                if (target == null) return null;
                return ResolveMethodItem(target, call.Args, sc, depth);
            }
            return null;
        }

        private static RibbonItem Placeholder(string name)
        {
            var it = new RibbonItem { Id = name, Kind = RibbonItemKind.Button, Text = "{" + name + "}" };
            it.Extra["unresolved"] = "true";
            return it;
        }

        private RibbonItem? ResolveMethodItem(M target, List<string> args, Scope caller, int depth)
        {
            var inner = Bind(target, args, caller);
            RibbonItem? item = null;
            foreach (var r in Returns(target))
            {
                if (r is "null" or "Nothing") continue;
                item = ResolveItem(r, inner, depth + 1);
                if (item != null) break;
            }
            if (item == null) return null;
            // the icon flows in through a bitmap-ish parameter
            foreach (var p in target.Params)
            {
                bool bitmapish = Rx.IsMatch(p.Type, @"Bitmap|ImageSource|\bImage\b|Icon", RegexOptions.IgnoreCase)
                                 || Rx.IsMatch(p.Name, @"bitmap|icon|image", RegexOptions.IgnoreCase);
                if (!bitmapish || !inner.Args.TryGetValue(p.Name, out var a)) continue;
                var raw = Rx.Replace(a.Expr, @"\s+", "");
                if (raw is "null" or "Nothing" or "") continue;
                item.Image ??= raw;
            }
            return item;
        }

        private RibbonItem BuildItem(string type, string? init, string? varName, Scope sc, string newText)
        {
            var m = sc.Method;
            var props = CollectProps(m.Body, varName, init, m.Vb);
            string? Str(string prop) => props.TryGetValue(prop, out var e) ? Eval(e, sc).Text : null;
            string? name = Str("Name"), id = Str("Id");
            string text = (Str("Text") ?? "").Trim();
            string? chosen = id ?? name;
            var item = new RibbonItem
            {
                Id = !string.IsNullOrWhiteSpace(chosen) ? chosen : (text.Length > 0 ? text : type),
                Kind = KindOf(type),
                Text = text,
            };
            if (!string.IsNullOrWhiteSpace(chosen)) item.MacroId = chosen;
            item.Tooltip = Str("ToolTip") ?? Str("Tooltip");
            if (Str("Description") is { } d) item.Extra["description"] = d;
            if (props.TryGetValue("IsEnabled", out var en) && Rx.IsMatch(en, @"^\s*(false|False)\s*$")) item.Extra["enabled"] = "false";
            if (props.TryGetValue("ShowText", out var st)) item.ShowText = !Rx.IsMatch(st, @"^\s*(false|False)\s*$");
            if (props.TryGetValue("Size", out var sz) && Rx.Match(sz, @"\.(Large|Standard|Small)\b") is { Success: true } szm)
                item.Size = Enum.Parse<RibbonItemSize>(szm.Groups[1].Value);
            if (props.TryGetValue("Image", out var img) && !Rx.IsMatch(img, @"^\s*\w+\s*$") && !img.Contains('('))
                item.Image = Rx.Replace(img, @"\s+", "");

            if (props.TryGetValue("CommandHandler", out var ch) && NewItemAny(ch) is { } hType
                && id != null && !id.StartsWith('{') && HandlerSendsCommand(hType))
                item.Command = id.ToUpperInvariant();

            if (props.TryGetValue("CommandParameter", out var cp)) ApplyHandlerObject(item, cp, sc);

            if (item.Kind == RibbonItemKind.SplitButton && varName != null)
                foreach (var a in ItemsAdds(m).Where(a => a.Recv == varName))
                    if (ResolveItem(a.Arg, sc, 1) is { } child) { child.Source = m.Ref(a.Index); item.Children.Add(child); }
            return item;
        }

        private void ApplyLaterProps(RibbonItem item, string varName, Scope sc)
        {
            var props = CollectProps(sc.Method.Body, varName, null, sc.Method.Vb);
            string? Str(string p) => props.TryGetValue(p, out var e) ? Eval(e, sc).Text : null;
            if (Str("Text") is { } t) item.Text = t.Trim();
            if (Str("ToolTip") is { } tt) item.Tooltip = tt;
            if (props.TryGetValue("CommandParameter", out var cp)) ApplyHandlerObject(item, cp, sc);
        }

        /// <summary>
        /// CommandParameter = new Handler(assembly, className, methodName, ...) (or a variable or helper parameter holding it): an
        /// object whose second and third arguments read as a class and a method name may tell which method the button calls.
        /// This is only a candidate: <see cref="Linker.KeepDeclaredHandlers"/> keeps it when the scanned sources declare that method,
        /// so a file name or version passed the same way never becomes an entry point.
        /// </summary>
        private void ApplyHandlerObject(RibbonItem item, string cpExpr, Scope sc, int depth = 0)
        {
            string e = cpExpr.Trim();
            if (IsIdent(e) && sc.Args.TryGetValue(e, out var bound))
            {
                if (depth < 8) ApplyHandlerObject(item, bound.Expr, bound.Scope, depth + 1);
                return;
            }
            if (IsIdent(e)) e = DeclInit(sc.Method, e)?.Trim() ?? e;
            var rm = Rx.Match(e, @"^new\s+(?:global::)?[\w.]+\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.IgnoreCase);
            if (!rm.Success) return;
            int open = rm.Length - 1;
            int close = CodeText.MatchBrace(e, open);
            if (close < 0) return;
            var args = CodeText.SplitArgs(e[(open + 1)..close]);
            if (args.Count < 3) return;
            string? asm = Eval(args[0], sc).Text, cls = Eval(args[1], sc).Text, method = Eval(args[2], sc).Text;
            if (cls == null || method == null || !Rx.IsMatch(cls, @"^\w+(\.\w+)*$") || !Rx.IsMatch(method, @"^\w+$")) return;
            item.HandlerMethod = cls + "." + method;
            if (!string.IsNullOrWhiteSpace(asm) && Rx.IsMatch(asm, @"^[\w.]+$")) item.Extra["assembly"] = asm;
        }

        private static string? NewItemAny(string expr)
        {
            var m = Rx.Match(expr.Trim(), @"^new\s+([\w.]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private bool HandlerSendsCommand(string typeName)
        {
            string simple = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
            foreach (var ct in _texts)
            {
                var cm = Rx.Match(ct.Code, @"\bclass\s+" + Regex.Escape(simple) + @"\b");
                if (!cm.Success) continue;
                int open = ct.Code.IndexOf('{', cm.Index);
                if (open < 0) continue;
                int close = CodeText.MatchBrace(ct.Code, open);
                if (close < 0) continue;
                if (ct.Code.AsSpan(open, close - open).Contains("SendStringToExecute", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static RibbonItemKind KindOf(string type) => type.ToLowerInvariant() switch
        {
            "ribbonbutton" => RibbonItemKind.Button,
            "ribbonsplitbutton" => RibbonItemKind.SplitButton,
            "ribbontogglebutton" or "ribboncheckbox" or "ribbonradiobutton" => RibbonItemKind.ToggleButton,
            "ribboncombo" or "ribbonlist" or "ribbongallery" => RibbonItemKind.DropDown,
            "ribbonseparator" => RibbonItemKind.Separator,
            "ribbonlabel" => RibbonItemKind.Label,
            "ribbontextbox" or "ribbonslider" or "ribbonspinner" => RibbonItemKind.Control,
            "ribbonrowpanel" => RibbonItemKind.RowPanel,
            _ => RibbonItemKind.Other,
        };

        // ------------------------------------------------------------ evaluation of expressions

        private Val Eval(string expr, Scope sc, int depth = 0)
        {
            string e = expr.Trim();
            if (e.Length == 0 || depth > 8) return default;
            if (e is "null" or "Nothing") return default;
            var parts = SplitConcat(e);
            if (parts.Count > 1)
            {
                var vs = parts.Select(p => Eval(p, sc, depth + 1)).ToList();
                return new Val(string.Concat(vs.Select(v => v.Text ?? "")).Trim(), vs.All(v => v.Literal));
            }
            if (e[0] == '(' && CodeText.MatchBrace(e, 0) == e.Length - 1) return Eval(e[1..^1], sc, depth + 1);
            var lit = CodeText.Literal(e);
            if (lit != null) return new Val(lit, true);
            if (Rx.Match(e, "^\\$@?\"(.*)\"$", RegexOptions.Singleline) is { Success: true } im)
                return new Val(im.Groups[1].Value.Trim(), false);
            if (IsIdent(e))
            {
                if (sc.Args.TryGetValue(e, out var bound)) return Eval(bound.Expr, bound.Scope, depth + 1);
                return Field(e, sc.Method.Ct.File.RelPath) is { } f ? new Val(f, true) : new Val("{" + e + "}", false);
            }
            if (Rx.IsMatch(e, @"^[\w]+(\.\w+)+$"))
            {
                string last = e[(e.LastIndexOf('.') + 1)..];
                return Field(last, sc.Method.Ct.File.RelPath) is { } f ? new Val(f, true) : new Val("{" + last + "}", false);
            }
            // calls and anything else: name the last call
            string stripped = Rx.Replace(e, @"\.ToString\(\s*\)$", "");
            var cm = Rx.Matches(stripped, @"(\w+)\s*\(");
            return new Val("{" + (cm.Count > 0 ? cm[0].Groups[1].Value : "expr") + "}", false);
        }

        private string? Field(string name, string preferFile)
        {
            if (!_fields.TryGetValue(name, out var l)) return null;
            var same = l.FirstOrDefault(x => x.File == preferFile);
            return same.File == preferFile ? same.Value : l[0].Value;
        }

        private static List<string> SplitConcat(string e)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < e.Length; i++)
            {
                char c = e[i];
                if (c == '"')
                {
                    bool verbatim = i > 0 && e[i - 1] == '@';
                    i++;
                    while (i < e.Length)
                    {
                        if (verbatim ? (e[i] == '"' && !(i + 1 < e.Length && e[i + 1] == '"')) : e[i] == '"') break;
                        if (verbatim && e[i] == '"') i++;
                        else if (!verbatim && e[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (depth == 0 && (c == '+' || (c == '&' && !(i + 1 < e.Length && e[i + 1] == '&') && !(i > 0 && e[i - 1] == '&'))) && i > 0)
                {
                    parts.Add(e[start..i]);
                    start = i + 1;
                }
            }
            parts.Add(e[start..]);
            return parts;
        }

        // ------------------------------------------------------------ text helpers

        private static bool IsIdent(string s) => Rx.IsMatch(s, @"^[A-Za-z_]\w*$");

        private readonly record struct CallInfo(string? Qualifier, string Name, List<string> Args);

        private static CallInfo? SplitCall(string expr)
        {
            var m = Rx.Match(expr, @"^(?:([\w.]+)\.)?(\w+)\s*\(");
            if (!m.Success) return null;
            int open = m.Length - 1;
            int close = CodeText.MatchBrace(expr, open);
            if (close != expr.Length - 1) return null;
            return new CallInfo(m.Groups[1].Success ? m.Groups[1].Value : null, m.Groups[2].Value, CodeText.SplitArgs(expr[(open + 1)..close]));
        }

        /// <summary>Parse "new Type(args) { init }" at idx: type name, initializer inner text, end index.</summary>
        private static (string Type, string? Init, int End) ParseNew(string s, int idx)
        {
            var m = Rx.Match(s[idx..], @"^new\s+([\w.<>]+)", RegexOptions.IgnoreCase);
            int i = idx + m.Length;
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j < s.Length && s[j] == '(')
            {
                int c = CodeText.MatchBrace(s, j);
                if (c > 0) { i = c + 1; j = i; while (j < s.Length && char.IsWhiteSpace(s[j])) j++; }
            }
            string? init = null;
            if (j < s.Length && s[j] == '{')
            {
                int c = CodeText.MatchBrace(s, j);
                if (c > 0) { init = s[(j + 1)..c]; i = c + 1; }
            }
            return (m.Groups[1].Value, init, i);
        }

        /// <summary>Variable that a "new X" at idx is assigned to ("var v = new X", "v = new X", "Dim v As New X").</summary>
        private static string? VarBefore(string body, int idx)
        {
            int s = idx;
            while (s > 0 && body[s - 1] is not (';' or '{' or '}' or '\n')) s--;
            var pre = body[s..idx];
            var m = Rx.Match(pre, @"(\w+)\s*=\s*$");
            if (m.Success) return m.Groups[1].Value;
            m = Rx.Match(pre, @"(\w+)\s+As\s+$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>First assigned expression of each property: object initializer first, then "var.Prop = expr;".</summary>
        private static Dictionary<string, string> CollectProps(string body, string? varName, string? init, bool vb)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(init))
                foreach (var a in CodeText.SplitArgs(init))
                {
                    var m = Rx.Match(a, @"^\.?(\w+)\s*=(?!=)\s*(.*)$", RegexOptions.Singleline);
                    if (m.Success) props.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());
                }
            if (varName != null)
                foreach (Match m in Rx.Matches(body, @"(?<![\w.])" + Regex.Escape(varName) + @"\.(\w+)\s*=(?!=)\s*"))
                {
                    int end = EndOfStatement(body, m.Index + m.Length, vb);
                    props.TryAdd(m.Groups[1].Value, body[(m.Index + m.Length)..end].Trim());
                }
            return props;
        }

        private static int EndOfStatement(string s, int from, bool vb)
        {
            int depth = 0;
            for (int i = from; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    bool verbatim = i > 0 && s[i - 1] == '@';
                    i++;
                    while (i < s.Length)
                    {
                        if (verbatim || vb) { if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; } break; } }
                        else { if (s[i] == '\\') { i += 2; continue; } if (s[i] == '"') break; }
                        i++;
                    }
                    continue;
                }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') { if (--depth < 0) return i; }
                else if (!vb && c == ';' && depth == 0) return i;
                else if (vb && c == '\n' && depth == 0)
                {
                    int k = i - 1;
                    while (k >= from && (s[k] == '\r' || s[k] == ' ')) k--;
                    if (k >= from && s[k] == '_') continue; // line continuation
                    return i;
                }
            }
            return s.Length;
        }

        /// <summary>Initializer expression of the first "name = expr" in the method body.</summary>
        private static string? DeclInit(M m, string name)
        {
            var d = Rx.Match(m.Body, @"(?<![\w.])" + Regex.Escape(name) + @"\s*=(?!=)\s*");
            if (!d.Success)
            {
                d = Rx.Match(m.Body, @"\b" + Regex.Escape(name) + @"\s+As\s+(?=New\b)", RegexOptions.IgnoreCase);
                if (!d.Success) return null;
            }
            int from = d.Index + d.Length;
            return m.Body[from..EndOfStatement(m.Body, from, m.Vb)].Trim();
        }

        private static IEnumerable<string> Returns(M m)
        {
            foreach (Match r in Rx.Matches(m.Body, @"\breturn\s+(?=[^;])"))
            {
                int from = r.Index + r.Length;
                string e = m.Body[from..EndOfStatement(m.Body, from, m.Vb)].Trim();
                if (e.Length > 0) yield return e;
            }
        }

        // ------------------------------------------------------------ methods and parameters

        private static List<Param> ParseParams(string signature)
        {
            var list = new List<Param>();
            int open = signature.IndexOf('(');
            if (open < 0) return list;
            int close = CodeText.MatchBrace(signature, open);
            if (close < 0) return list;
            foreach (var p in CodeText.SplitArgs(signature[(open + 1)..close]))
            {
                var t = Rx.Replace(p, @"\s*=\s*.*$", "");
                t = Rx.Replace(t, @"^\s*(\[[^\]]*\]\s*)*(this|ref|out|in|params)\s+", "");
                var m = Rx.Match(t.Trim(), @"^(.*?)\s*(\w+)$");
                if (m.Success) list.Add(new Param(m.Groups[1].Value, m.Groups[2].Value));
            }
            return list;
        }

        private M? FindMethod(string name, string? qualifier, int argc, M from)
        {
            var c = _methods.Where(m => m.Sp.Name == name).ToList();
            if (c.Count == 0) return null;
            if (qualifier != null)
            {
                string q = qualifier.Contains('.') ? qualifier[(qualifier.LastIndexOf('.') + 1)..] : qualifier;
                var byClass = c.Where(m => m.Sp.ClassName == q).ToList();
                if (byClass.Count > 0) c = byClass;
            }
            return c.OrderByDescending(m => m.Params.Count == argc ? 8 : m.Params.Count >= argc ? 2 : 0)
                .ThenByDescending(m => m.Sp.FullClass == from.Sp.FullClass ? 2 : 0)
                .ThenByDescending(m => m.Ct == from.Ct ? 1 : 0)
                .First();
        }

        private static Scope Bind(M target, List<string> args, Scope caller)
        {
            var sc = new Scope { Method = target };
            for (int i = 0; i < args.Count; i++)
            {
                var named = Rx.Match(args[i], @"^(\w+)\s*:\s*(?!:)(.*)$", RegexOptions.Singleline);
                if (named.Success && target.Params.Any(p => p.Name == named.Groups[1].Value))
                    sc.Args[named.Groups[1].Value] = (named.Groups[2].Value, caller);
                else if (i < target.Params.Count)
                    sc.Args[target.Params[i].Name] = (args[i], caller);
            }
            return sc;
        }
    }
}
