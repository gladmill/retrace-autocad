using Retrace.Engine.Scan.Cuix;
using Retrace.Engine.Util;

namespace Retrace.Engine.Map;

/// <summary>What a macro runs, read from its text.</summary>
/// <param name="Commands">command names, upper case, without the _ . + prefixes: the first command of the macro, a command a LISP
/// expression calls ((c:foo) -> FOO, (command "_foo" ...) -> FOO) and the first word after a LISP expression ("(load \"x\") mytool")</param>
/// <param name="LispFunctions">functions the macro's LISP expressions call, as written (case kept), without AutoLISP's own functions</param>
/// <param name="Loads">files a (load "...") names, as written</param>
/// <param name="Diesel">the macro is a DIESEL expression ($M=...): not followed</param>
public sealed record MacroTargets(IReadOnlyList<string> Commands, IReadOnlyList<string> LispFunctions, IReadOnlyList<string> Loads, bool Diesel)
{
    public static readonly MacroTargets None = new([], [], [], false);
    public bool IsEmpty => Commands.Count == 0 && LispFunctions.Count == 0 && Loads.Count == 0;
}

/// <summary>Reads a CUI macro or a ribbon button's CommandParameter: ^C^C_line, ^C^C(c:mytool), ^C^C(load "tools.lsp") mytool ...
/// Text based and heuristic: it finds the commands and LISP functions a macro names, it does not run or evaluate anything.</summary>
public static class MacroText
{
    /// <summary>Never throws; null or blank gives <see cref="MacroTargets.None"/>. An unbalanced expression takes what is there.</summary>
    public static MacroTargets Parse(string? macro)
    {
        if (string.IsNullOrWhiteSpace(macro)) return MacroTargets.None;
        if (IsDiesel(macro)) return new MacroTargets([], [], [], true);

        var found = new Found();
        // the macro's own first command ("^C^C_line", "'_zoom"): the same reading the CUIX reader gives MenuMacro.Command
        var first = CuixReader.ParseCommand(macro);
        if (first != null) found.Command(first);

        // The LISP expressions, nested ones too. One pass with a stack of open expressions, no recursion, so a macro of
        // thousands of "(" costs memory, not stack. A frame knows what its items are: a call (head, arguments), the clauses of a
        // cond, or data that is only skipped (a quoted list, a parameter list).
        var open = new Stack<Frame>();
        bool quote = false;
        int i = 0, n = macro.Length;
        while (i < n)
        {
            char c = macro[i];
            if (open.Count == 0)
            {
                // outside any expression only "(" matters; a quoted text is skipped whole ("a (b)" is not LISP)
                if (c == '(') open.Push(new Frame(Mode.Call));
                else if (c == '"') i = LispText.EndOfString(macro, i);
                i++;
                continue;
            }
            var top = open.Peek();
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }   // in a macro a ; is Enter, not a comment
            if (c == '\'') { quote = true; i++; continue; }
            if (c == '(')
            {
                open.Push(new Frame(top.OpenList(quote)));
                quote = false;
                i++;
                continue;
            }
            if (c == ')')
            {
                open.Pop();
                quote = false;
                i++;
                // "^C^C(load "tools.lsp") mytool" or ";mytool": while the macro has named no command, the first word after an expression
                // is one; after (c:mytool) or (command ...) the next word answers that command's prompt ("_Yes")
                if (open.Count == 0 && found.Commands.Count == 0 && !found.AfterWord
                    && CuixReader.ParseCommand(macro[i..].TrimStart(';', ' ', '\t', '\r', '\n')) is { } word)
                {
                    found.AfterWord = true;
                    found.Command(word);
                }
                continue;
            }
            if (c == '"')
            {
                int end = LispText.EndOfString(macro, i);
                top.AddString(LispText.Unquote(macro, i, end), found);
                quote = false;
                i = end + 1;
                continue;
            }
            int start = i;
            while (i < n && !LispText.IsDelimiter(macro[i])) i++;
            top.AddAtom(macro[start..i], quote, found);
            quote = false;
        }
        return new MacroTargets(found.Commands, found.Functions, found.Loads, false);
    }

    /// <summary>A DIESEL macro: "$M=...", also after the control codes and repeat flag that may come first ("^C^C$M=...", "*^C^C$M=...").</summary>
    private static bool IsDiesel(string macro)
    {
        int i = 0;
        while (i < macro.Length)
        {
            char c = macro[i];
            if (c == '^' && i + 1 < macro.Length) i += 2;
            else if (c is '*' || char.IsWhiteSpace(c)) i++;
            else break;
        }
        return i < macro.Length && macro[i] == '$';
    }

    // ------------------------------------------------------------ what the expressions name

    private enum Mode { Call, Clause, Data }

    /// <summary>The commands, functions and loads found so far, each once, in order of appearance.</summary>
    private sealed class Found
    {
        public readonly List<string> Commands = new();
        public readonly List<string> Functions = new();
        public readonly List<string> Loads = new();
        public bool AfterWord;

        public void Command(string name) => Add(Commands, name);
        public void Function(string name) => Add(Functions, name);
        public void Load(string name) => Add(Loads, name);

        private static void Add(List<string> list, string item)
        {
            if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
        }
    }

    /// <summary>One open parenthesis.</summary>
    private sealed class Frame(Mode mode)
    {
        private readonly Mode _mode = mode;
        private string? _head;
        private bool _headSeen;
        private int _args;   // items after the head

        /// <summary>The mode of a list that opens inside this one. Counts as an item of this one.</summary>
        public Mode OpenList(bool quoted)
        {
            var child = Mode.Call;
            if (_mode == Mode.Data || quoted) child = Mode.Data;
            else if (_mode == Mode.Call && _headSeen)
            {
                // the parameters of (lambda (x) ...) and (defun name (x) ...) are names, not calls; (quote (a b)) is data; cond holds clauses
                if (Is("quote") || (Is("lambda") && _args == 0) || ((Is("defun") || Is("defun-q")) && _args == 1)) child = Mode.Data;
                else if (Is("cond")) child = Mode.Clause;
            }
            Count();
            return child;
        }

        public void AddAtom(string atom, bool quoted, Found found)
        {
            if (_mode != Mode.Call) return;
            if (_headSeen) { _args++; return; }
            _headSeen = true;
            if (quoted) return;
            _head = atom.ToLowerInvariant();
            NoteHead(atom, found);
        }

        public void AddString(string value, Found found)
        {
            if (_mode != Mode.Call) return;
            if (!_headSeen) { _headSeen = true; return; }
            // the first argument of command / load, when it is text
            if (_args == 0)
            {
                if (_head is "command" or "command-s" or "vl-cmdf") { if (CommandOf(value) is { } cmd) found.Command(cmd); }
                else if (_head == "load" && value.Trim().Length > 0) found.Load(value.Trim());
            }
            _args++;
        }

        private bool Is(string head) => _head == head;

        private void Count()
        {
            if (_mode != Mode.Call) return;
            if (_headSeen) _args++; else _headSeen = true;
        }

        private static void NoteHead(string head, Found found)
        {
            if (head.StartsWith("c:", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCommandWord(head[2..])) found.Command(head[2..].ToUpperInvariant());
                return;
            }
            if (head.Length == 0 || !(char.IsLetter(head[0]) || head[0] == '_')) return;   // 1+, +, *, numbers
            if (IsBuiltIn(head)) return;
            found.Function(head);
        }
    }

    /// <summary>"_.zoom" -> ZOOM, "-layer" -> -LAYER, "ACAD.LINE" -> LINE; null when the text is not a command word.</summary>
    private static string? CommandOf(string text)
    {
        var t = text.Trim().TrimStart('_', '.', '+', '\'');
        if (t.StartsWith("ACAD.", StringComparison.OrdinalIgnoreCase)) t = t[5..];
        return IsCommandWord(t) ? t.ToUpperInvariant() : null;
    }

    /// <summary>Letters, digits, _ and -, with at least one letter.</summary>
    private static bool IsCommandWord(string s)
    {
        bool letter = false;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) letter = true;
            else if (!(char.IsDigit(ch) || ch is '_' or '-')) return false;
        }
        return letter;
    }

    /// <summary>AutoLISP's own functions, which are not the user's code. Visual LISP (vl-, vla-, vlax-, vlr-) and AutoCAD's acad_ / acad-
    /// functions are left out by prefix. The ai_ functions are LISP in AutoCAD's own .mnl files, so they are not here.</summary>
    private static bool IsBuiltIn(string name)
    {
        if (name.StartsWith("vl-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vla-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("vlax-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vlr-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("acad_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("acad-", StringComparison.OrdinalIgnoreCase))
            return true;
        return BuiltIns.Contains(name);
    }

    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        // control and definitions
        "load", "command", "command-s", "defun", "defun-q", "lambda", "quote", "function", "setq", "set", "if", "progn", "and", "or",
        "not", "cond", "repeat", "while", "foreach", "eval", "exit", "quit", "trace", "untrace", "autoload", "arxload", "arxunload", "arx",
        "autoarxload", "xload", "xunload", "startapp", "findfile", "findtrustedfile", "getfiled", "menucmd", "menugroup", "setfunhelp",
        // input and output
        "princ", "prin1", "print", "prompt", "alert", "terpri", "textscr", "textpage", "graphscr", "redraw", "textbox", "grread",
        "grtext", "grdraw", "grvecs", "grclear", "open", "close", "read-line", "read-char", "write-line", "write-char", "read",
        "getstring", "getpoint", "getreal", "getint", "getkword", "getcorner", "getdist", "getangle", "getorient", "initget",
        "entsel", "nentsel", "nentselp", "getvar", "setvar", "getenv", "setenv", "getcfg", "setcfg", "getpropertyvalue",
        "setpropertyvalue", "ispropertyreadonly", "dumpallproperties",
        // numbers, text, logic
        "abs", "atan", "cos", "sin", "exp", "expt", "fix", "float", "gcd", "log", "max", "min", "minusp", "zerop", "rem", "sqrt",
        "boole", "logand", "logior", "lsh", "numberp", "atoi", "atof", "itoa", "rtos", "angtos", "distof", "angtof", "ascii", "chr",
        "strcase", "strcat", "strlen", "substr", "wcmatch", "cvunit", "eq", "equal", "atom", "boundp", "listp", "null", "type",
        // lists
        "append", "assoc", "car", "cdr", "caar", "cadr", "cdar", "cddr", "caaar", "caadr", "cadar", "caddr", "cdaar", "cdadr", "cddar",
        "cdddr", "cadddr", "cons", "last", "length", "list", "member", "mapcar", "nth", "reverse", "subst", "apply", "snvalid",
        // geometry and the drawing database
        "angle", "distance", "polar", "inters", "osnap", "trans", "ssget", "ssadd", "ssdel", "ssname", "sslength", "ssmemb",
        "ssnamex", "sssetfirst", "ssgetfirst", "entget", "entmod", "entmake", "entmakex", "entdel", "entlast", "entnext", "entupd",
        "handent", "tblnext", "tblsearch", "tblobjname", "namedobjdict", "dictsearch", "dictnext", "dictadd", "dictremove",
        "dictrename", "regapp", "vports", "tablet", "xdroom", "xdsize", "ver", "help", "gc", "alloc", "expand",
        // dialog boxes
        "load_dialog", "unload_dialog", "new_dialog", "start_dialog", "done_dialog", "term_dialog", "start_list", "end_list",
        "add_list", "mode_tile", "set_tile", "get_tile", "get_attr", "action_tile", "client_data_tile", "dimx_tile", "dimy_tile",
        "fill_image", "slide_image", "vector_image", "start_image", "end_image",
    };
}
