"""Write dialogs.json for build-res.sh.

usage: dialogs.py <source-root> <manifest.tsv> <out.json>

manifest.tsv has "<rc path relative to root><TAB><dll name>" per compiled .rc.
For each dialog in each .rc: the resource key inside the DLL (numeric id, or the upper-cased string
name that rc.exe stores), the symbolic name, the header line, and the Retrace form id
("rc.<Project>.<name as written in the .rc>", see RcScanner/InputScanner).
"""
import json
import os
import re
import sys

PROJECT_EXTS = (".csproj", ".vbproj", ".vcxproj")
DIALOG_HEAD = re.compile(r"^\s*([A-Za-z_]\w*|\d+)\s+DIALOG(EX)?\b")
DEFINE = re.compile(r"^\s*#\s*define\s+([A-Za-z_]\w*)\s+\(?\s*(-?(?:0[xX][0-9a-fA-F]+|\d+))[lLuU]*\s*\)?\s*(?://.*|/\*.*)?$")
INCLUDE = re.compile(r'^\s*#\s*include\s+"([^"]+)"')
PREFERRED = ("IDD_", "IDR_", "IDC_", "ID_")


def read_text(path):
    data = open(path, "rb").read()
    if b"\x00" in data[:200]:
        return data.decode("utf-16", errors="replace")
    return data.decode("utf-8-sig", errors="replace")


def strip_comments(text):
    """Remove // and /* */ comments but keep every newline, so line numbers stay right."""
    out, i, n = [], 0, len(text)
    in_str = False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == '"':
                in_str = False
            i += 1
        elif c == '"':
            in_str = True
            out.append(c)
            i += 1
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def collect_defines(path, seen, defines):
    path = os.path.normpath(path)
    if path in seen or not os.path.isfile(path):
        return
    seen.add(path)
    text = strip_comments(read_text(path))
    for line in text.splitlines():
        m = DEFINE.match(line)
        if m:
            defines.setdefault(m.group(1), int(m.group(2), 0))
    for line in text.splitlines():
        m = INCLUDE.match(line)
        if m:
            inc = m.group(1).replace("\\", "/")
            if inc.lower().endswith((".h", ".rc", ".hpp", ".inl")):
                collect_defines(os.path.join(os.path.dirname(path), inc), seen, defines)


def project_name(root, rc_abs):
    rel = os.path.relpath(rc_abs, root).replace(os.sep, "/")
    d = os.path.dirname(rc_abs)
    while True:
        try:
            projs = sorted(f for f in os.listdir(d) if f.lower().endswith(PROJECT_EXTS))
        except OSError:
            projs = []
        if projs:
            return os.path.splitext(projs[0])[0]
        if os.path.normpath(d) == os.path.normpath(root) or len(d) <= len(root):
            break
        d = os.path.dirname(d)
    return rel.split("/")[0] if "/" in rel else "(root)"


def main():
    root, manifest, out = sys.argv[1:4]
    root = os.path.abspath(root)
    result = []
    for line in open(manifest, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line:
            continue
        rel, dll = line.split("\t")
        rc_abs = os.path.join(root, rel)
        defines = {}
        collect_defines(rc_abs, set(), defines)
        by_value = {}
        for name, val in defines.items():
            by_value.setdefault(val, []).append(name)
        project = project_name(root, rc_abs)
        text = strip_comments(read_text(rc_abs)).replace("\r\n", "\n").replace("\r", "\n")
        dialogs = []
        for i, l in enumerate(text.split("\n"), 1):
            m = DIALOG_HEAD.match(l)
            if not m:
                continue
            token = m.group(1)
            if token.isdigit():
                resource, symbol = int(token), None
                names = by_value.get(resource, [])
                names = sorted(names, key=lambda s: (not s.startswith(PREFERRED), s))
                symbol = names[0] if names else None
            elif token in defines:
                resource, symbol = defines[token], token
            else:  # string-named resource: rc.exe stores the upper-cased name
                resource, symbol = token.upper(), token
            dialogs.append({
                "resource": resource,
                "name": symbol if symbol else (f"#{resource}" if isinstance(resource, int) else resource),
                "token": token,
                "line": i,
                "formId": f"rc.{project}.{token}",
            })
        result.append({"dll": dll + ".dll", "rc": rel, "project": project, "dialogs": dialogs})
    with open(out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
        f.write("\n")
    total = sum(len(r["dialogs"]) for r in result)
    print(f"dialogs.json: {total} dialogs in {len(result)} DLLs")


if __name__ == "__main__":
    main()
