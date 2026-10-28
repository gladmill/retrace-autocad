# Ribbon map

The Map (`RETRACEMAP`, `-RETRACEMAP`) lists every ribbon item and the code it runs. It reads the folders you pick: `.cuix` and `.cui` files, ribbon code in C# and VB.NET, and the code the buttons run (LISP, C++, C#, VB.NET).
Optionally it also reads the ribbon AutoCAD shows now, which finds tabs that plugins build at run time.
The engine is `Engine/Map/RibbonMapper.cs` (`EngineRunner.MapRibbon`); `Engine/Map/RibbonMapWriter.cs` writes the files.

It is a search in text, not a compiler. It would rather find too little than report something wrong, and it says what it could not follow.

## The files

Written to the map folder, `C:\temp\retrace\map\` (the folder is `Paths.Map()`), each over the last map:

| File | For |
|---|---|
| `ribbon-map.md` | Reading. A summary, the folders, one section per tab with a table, then the warnings. |
| `ribbon-map.csv` | Excel. UTF-8 with a byte order mark, one row per ribbon item. A cell that starts with `=`, `+`, `-`, `@`, a tab or a carriage return gets a leading `'`, so Excel does not run it as a formula. |
| `ribbon-map.json` | Tools. `{ "schema": "retrace/ribbon-map@1", "generated", "folders", "liveMapped", "liveTabsLeftOut", "filesRead", "rows", "warnings" }`, camelCase, no `null`s. |

The files hold full local paths (folders, user names). They are your data: do not commit or send them without a look.
Each file is written to a `.tmp` file first and moved over the old one, so a reader never sees half a file.

## The columns of the CSV

The report shows five of them (Panel, Item, Runs, Code, Found by) for each tab, with the file names relative to their folder.

| Column | Meaning |
|---|---|
| Tab, Panel, Item | Where the item is in the ribbon. A member of a split button or drop-down is `Parent › Member`. A panel's small arrow is the item `dialog launcher`. |
| Item id | The id in the CUIX file, the `Id` in the code, or what AutoCAD reports for a live item. |
| Kind | `button`, `split button`, `toggle`, `control`, `gallery`, `drop-down`, `dialog launcher` or `other`. |
| Origin | Where the item comes from. See below. |
| Defined in | The place of the item itself: the element in the `.cuix` (`file.cuix!RibbonRoot.cui:120`) or the line of the code that builds it. Empty for an item only the live ribbon has. |
| Shown in AutoCAD | `yes` or `no`: whether the live ribbon has the item. Empty when the live ribbon was not mapped. |
| Macro | What a click sends, as written: `^C^C_DRAWGRID`. In code it is the button's `CommandParameter`; for a live item, the one AutoCAD reports. |
| Runs | What the click runs, in words (below). |
| Status | `Mapped`, `Not found` or `No action` (below). |
| Language, Code, File, Line | The first definition found: `C#`, `Contoso.Tools.Cmds.DrawGrid`, its file (full path) and line. |
| More code | The other definitions found, as `Language Symbol (file:line)` separated by ` | `. |
| Found by | How the target was found (below). |
| Note | Why the status is `Not found` or `No action`, or which part of a `Mapped` row was not found. |

## Origin

| Origin | Meaning |
|---|---|
| `cuix` | An item of a `.cuix` file in the folders. |
| `cui` | An item of a legacy `.cui` file in the folders. |
| `code` | A ribbon built in C# or VB.NET code in the folders. |
| `loaded cuix` | An item of a `.cuix` / `.cui` file AutoCAD has loaded from outside the folders. It is listed only when the live ribbon shows it. Its macro is known from the file; AutoCAD's own live buttons carry none. |
| `live` | An item only the live ribbon has: a plugin built it at run time and no folder has it. |

A tab that only the live ribbon or a loaded file has is listed only when at least one of its items is `Mapped`, so AutoCAD's own tabs stay out. The option "List every live tab" lists them all. The summary says how many were left out.

## Runs and Found by

The click can run several things; each is a target. Targets are looked up in the folders in this order:

1. The macro (`Found by`: `macro` for an item of a `.cuix` / `.cui`, `command parameter` for code and live items): its first command (`^C^C_line` is `command LINE`), a command a LISP expression calls (`(c:mytool)`, `(command "_.zoom" "e")`), a LISP function it calls (`LISP (mytool)`), a file it loads (`loads tools.lsp`), and, while the macro has named no command, the first word after a LISP expression (`(load "tools.lsp") mytool`, also after a `;`: `(load "tools");mytool`). For a `.cuix` / `.cui` item also the menu macro's `CLICommand` when the macro names no command. A code button's `CommandParameter` counts as a macro when it reads as one (`^C`, a `_` or `.` prefix, LISP, DIESEL, a trailing space or `;`) or when its handler class sends commands: plain text like `"Grid"` may be a key the handler reads.
2. The command a code button's handler sends, taken from the button's `Id` (`button id (the handler sends it)`), when the button has no macro.
3. The method a handler object names (`method Contoso.Ui.Actions.Go`; `handler object`).
4. Only when nothing above found code in the folders: the classes of the live item, its command handler (`command handler class`) and its parameter object (`command parameter object`), when they are not AutoCAD's or .NET's (`Autodesk.`, `System.`, `Microsoft.`, `MS.`). The class is found at its `Execute` method when it has one. A handler that only sends the macro is one class behind every button, so it is not listed once the macro's command is found.
5. Only when nothing above gave a target: the live item's id, when it is the name of a command the folders define (`button id (a guess)`).

`Found by` is how the first target that was found got found, else the first target.

A DIESEL macro (`$M=...`, also after control codes: `^C^C$M=...`) is not followed. A macro whose commands are built at run time is not followed either.

## Status

| Status | Meaning |
|---|---|
| `Mapped` | At least one definition was found in the folders. |
| `Not found` | The item runs something, but no folder defines it: `command DRAWX: not in these folders`. Built-in AutoCAD commands (`LINE`) are always `Not found`. |
| `No action` | There is nothing to look up: no macro, no handler, a DIESEL macro, a drop-down, a gallery, or an AutoCAD button the live ribbon tells nothing about. |

## What it finds in the code

- LISP: `(defun c:NAME ...)` and `(vlax-add-cmd "NAME" 'fn)` are commands; `(defun NAME ...)` is a function. Comments (`;`, `;| ... |;`) and strings are skipped. `.lsp` and `.mnl` files are read; `.fas` and `.vlx` are only listed, so a `(load "x")` finds them, and they are never read.
- C# and VB.NET: `[CommandMethod]` / `<CommandMethod>` (the name and the local name), `[LispFunction]`, methods, classes.
- C++: `addCommand` and `ACED_ARXCOMMAND_ENTRY_AUTO`, functions, classes.
- Code ribbons: `RibbonTab`, `RibbonPanelSource` and `RibbonButton` as the docs scanner reads them (`Engine/Scan/CodeRibbon/CodeRibbonReader.cs`).

When a name is defined more than once, all places are listed: first by the order of your folders, then by file path and line.

## Limits

- A folder inside another picked folder is read once, by the outer one (a warning says so). A folder that does not exist is a warning.
- Files whose names look like secrets are listed as skipped and never opened. `bin`, `obj`, `.vs`, `.git` and similar folders are skipped.
- Compiled code is not read: a ribbon built in a `.dll` shows up only through the live ribbon, and its code is found only if its source is in a folder.
- A command name built at run time, a macro assembled from variables, and code that registers commands from a table are not found.
- Names are matched by text. Two classes of the same name in different namespaces are told apart by the namespace. A VB project's root namespace is a project setting and not in its source, so for VB a live type `MyPlugin.Ui.Handler` matches the class `Ui.Handler`.
- The live ribbon is a snapshot of the moment the map was made. Tabs of other workspaces and contextual tabs are in it, whether or not they are showing.
