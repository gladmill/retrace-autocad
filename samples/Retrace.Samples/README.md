# Retrace.Samples

A tiny sample plugin to practise the Retrace flow on: record a manual workflow, replay it as a test, and
see the same commands in the guide that Retrace generates from this source. Not a product.

Target: AutoCAD 2026 (R25.1), net8.0-windows, `AutoCAD.NET` 25.1.0. Build: `dotnet build -c Release`
(produces `Retrace.Samples.dll`). It is not part of the release bundle: `NETLOAD` it, or on a VM install it with `tools/vm/install-samples.sh`, which loads it at every AutoCAD start.

## Commands

| Command | Prompts | Draws |
|---|---|---|
| `DRAWLINE` | start point, end point (rubber band from the start) | a LINE |
| `DRAWCIRCLE` | center, radius (drag from the center; Enter = last radius, default 5) | a CIRCLE |
| `DRAWRECT` | first corner, opposite corner | a closed LWPOLYLINE |
| `DRAWLABEL` | text (spaces allowed), insertion point, height (Enter = last, default 2.5) | a single-line TEXT |
| `DRAWGRID` | dialog (rows, columns, spacing, layer name; OK/Cancel), then the lower-left point | rows x columns POINT objects on the layer (created if missing) |
| `DRAWPART` | dialog (WinForms `TreeDialog`: a tree of parts, OK/Cancel) | a single-line TEXT with the selected part's name at 0,0 |
| `DRAWTABS` | dialog with a **tab control** (WinForms `TabsDialog`, three pages: General: Name; Size: Width, Height; Style: Mark the center, Mark radius; OK/Cancel) | a closed LWPOLYLINE of width x height, a single-line TEXT with the name below it, and with Mark the center a CIRCLE at the middle (radius from Mark radius) |
| `DRAWFORMS` | main form (WinForms `FormsDialog`: a **check-box tree** `Layers` with lazily filled nodes, a tree `Items` whose items open a sub form on a **double-click**, a **Notes...** button that opens a form on **MouseDown**; OK/Cancel) | a single-line TEXT at 0,0 with every choice: the ticked items, `qty/coating` of each sub form, the notes |
| `DRAWMENU` | dialog with **right-click context menus** (WinForms `MenuDialog`: a tree with a `ContextMenuStrip`, a list with a Win32 popup menu, a list view with a `ContextMenuStrip`; OK/Cancel) | five single-line TEXTs: the selected part, its note, the number of Tools, the file list, the Tools list view |
| `DRAWSHEET` | a real comctl32 **property sheet** (`SheetCommands.cs`, `PropertySheetW` with pages from in-memory dialog templates, no WinForms: tab control `SysTabControl32`, switched with `PSM_SETCURSEL`): pages General (Name, Note), Size (Width, Height), Options (Mark the center, Radius), the control ids 1001 and 1002 reused on every page; the Size page **refuses to be left** (`PSN_KILLACTIVE` answers TRUE) while Width or Height is not a number above zero; OK / Cancel | a closed LWPOLYLINE of Width x Height, a single-line TEXT `Name / Note` below it and, with Mark the center, a CIRCLE at the middle |
| `DRAWPALETTE` | none | nothing: shows the Retrace Samples palette set (Box and Circle pages, which draw a closed LWPOLYLINE / a CIRCLE) |
| `DRAWPALETTEHIDE` | none | nothing: the same palette set with `AutoRollUp` on, so it rolls up to its title strip when the mouse leaves it (what the palette's auto-hide button does); rest the mouse on the strip (the left edge of the screen) to open it. Use it to test recording and replay in an auto-hidden palette, with the setting `showDrivenUi` on and off |
| `DRAWITEMS` | dialog with buttons in it (text box + **Add**, **Options...** which opens a nested dialog, a label that counts: "2 items"; OK/Cancel) | one single-line TEXT per added item, below each other (spacing and prefix from the Options dialog) |
| `DRAWWPF` | **WPF** dialog with buttons (`WpfItemsWindow`: Name and Qty fields, **Add**, **Details...** which opens a WPF sub window with a Note and **More...**, which opens a WinForms sub-sub dialog for a Depth; OK is the default button, Cancel the IsCancel button) | one single-line TEXT per added item and one with the note and depth, below each other |
| `DRAWWPFTABS` | **WPF** dialog with **static texts** (a Label, TextBlocks, a "Selected:" TextBlock that follows the tree), a **TabControl** (`WpfTabsWindow`: General: Name; Size: Width, Height; Style: Mark the center), a **TreeView** (Parts > Fasteners > Bolts > M6 / M8, Nuts, Frames; Tools > Hammer, Wrench), a Qty field and **Details...** (the WPF sub window of `DRAWWPF`); OK is the default button | three single-line TEXTs: name, size and mark; qty and the selected part; the note and depth |
| `DRAWMIX` | WinForms dialog (Title, **Details...**) whose button opens the same WPF sub window (and its WinForms depth dialog) | a TEXT with the title and one with the note and depth |

Each command works in one transaction and prints one confirmation line. Esc at any prompt (or Cancel in the
dialog) draws nothing. The **Retrace Samples** ribbon tab has a "Draw" panel (Line, Circle, Rectangle, Label) and a
"Dialogs" panel (Grid...). The buttons run the command as `^C^C_DRAWLINE `, so a recording shows the command.

`DRAWGRID` is the one with a dialog (WinForms, `GridDialog`): in a replayed test that step needs a person.

`DRAWPART` has a WinForms dialog with a **tree** (`TreeDialog`, tree `CatalogTree`, label "Catalog"): Parts (Fasteners, Frames), Tools (Hammer, Wrench).
Fasteners, Bolts (under Fasteners) and Frames are filled when they are opened (`BeforeExpand`, see `PartCatalog.cs`), so a replay must open them to find `Parts › Fasteners › Bolts › M8`.
The "Selected:" line under the tree shows how the last selection came (`ByMouse`, `ByKeyboard`, `Unknown`: a replay selects with `TVM_SELECTITEM`, so it reads `Unknown`). OK needs a selected part (else a message box asks for one), then the command draws a TEXT with the part's name (`M8`), which a checkpoint can tell. Not on the ribbon: type `DRAWPART`.
A recorded test: `DRAWPART`, expand and pick an item, OK, checkpoint; steps.md reads "Select `Parts › Fasteners › Bolts › M8` in **Catalog**". `RETRACERUN` (and a `-RETRACESUITE` run) fills the dialog by itself and presses OK; `RETRACEREPLAY` ... Auto stops with `INCOMPLETE` at a dialog.
Frames is read again each time it is opened (collapsing it drops its children), as a tree that lists a folder does: a replay has to fire `BeforeExpand` again for `Parts › Frames › Steel` when Frames was opened and collapsed before.

`DRAWITEMS` (WinForms, `ItemsDialog.cs`, `ItemsCommands.cs`; no ribbon button, type it) has buttons inside its dialog, to test recording and replaying them: type a text and press **Add**
(Enter in the text box does too, it is the form's default button; Alt+A, Alt+C and Alt+O are the mnemonics of **Add**, **Clear** and **Options...**; it joins the list and the box is cleared; the label under the list counts the items; **Clear** asks "Remove all N items?" in a Yes / No message box, a nested dialog of buttons and a text, and then says "N items removed." in a second one: two nested dialogs from one click), press **Options...** (a nested dialog `Item options`: row spacing and a prefix, opened with
`Application.ShowModalDialog`; OK there shows a message box when the prefix has a space and the dialog stays open), then OK. OK draws one TEXT per item at `(0, -i * spacing)`, the prefix in front of its text. A replay that skips an Add or the
Options dialog draws other texts, so a checkpoint after it fails. The labels ("2 items", "Spacing 5, prefix none") are static texts: they show in the `statics` of the recording
(`docs/recording-format.md`).

`DRAWTABS` (WinForms, `TabsDialog.cs`, `TabsCommands.cs`; no ribbon button, type it) has a **TabControl** with three pages and a field or two on each, to test tab context: a field on a page that is not showing is not read, and a replay has to show the page first. Every
control has a `Name` and an `AccessibleName`, so a recording keys them `tab:Size|name:widthBox` and calls them Name, Width, Height, Mark the center, Mark radius. OK draws from every page's value (the outline from Width and Height, the TEXT from Name, a circle from the check box and the combo
box), so a checkpoint after it fails when the replay missed the value of one page. OK needs numbers above zero in Width and Height (else a message box, and the Size page is shown). A recorded test: `DRAWTABS`, change Name, open Size and change Width and Height, open Style and tick Mark the center
and pick a radius, OK, checkpoint; steps.md reads "On the **Size** tab: set **Width** to `20`, set **Height** ...". A variant ends on the first page: after the values, click the General tab again, then OK (the recording says `closeTab: "General"`, and a replay shows that tab before it
presses OK). `RETRACERUN` (and a `-RETRACESUITE` run) switches the pages by itself, in the recorded order, and presses OK.

`DRAWFORMS` (WinForms, `FormsDialog.cs`, `FormsCommands.cs`; no ribbon button, type it) tests trees with check boxes and sub forms opened from a main form. **Layers** (`CheckTree`) is the parts tree of `DRAWPART` with check boxes (`Parts › Fasteners › Bolts` fill when they are opened); **Tools › Hammer** starts ticked, so a recording can untick as well as tick.
The set of ticked items is kept by `AfterCheck` (never read from the nodes at the end), so an item ticked without the form being told is missing from the TEXT. **Items** (`ItemTree`: Housing › Lid / Base, Bracket, Plate): a double-click on an item opens the sub form `Part <path>` (**Quantity**, **Weight** (reformats to three decimals when the focus leaves it: `2` becomes `2.000`, as the mouse goes down on the next button), a **Finish...** button that opens the sub-sub form `Finish` with **Coating**, OK / Cancel);
its result is `qty/coating`. **Notes...** acts on `MouseDown` (there is no `Click` handler) and opens `Notes` (one text), so a replay that presses it with `BM_CLICK` opens nothing. (A double-click on a node with children such as Housing is taken by the tree to expand it; its handler did not run in the VM, so record the double-click on Lid, Base, Bracket or Plate.) A recorded test: `DRAWFORMS`, expand Parts and Fasteners (the nodes fill), tick Nuts, untick Hammer,
double-click Bracket, set Quantity, **Finish...**, type a Coating, OK, OK, **Notes...**, a text, OK, OK, checkpoint. steps.md reads ``Tick `Parts › Fasteners › Nuts` in **Layers**``, ``double-click `Bracket` in **Items**``, a nested dialog `Part Bracket` with its own click and a nested dialog `Finish`. `RETRACERUN` and `-RETRACESUITE` do it all by themselves and a checkpoint on the TEXT fails when a tick, a quantity, a coating or the notes is missing.

`DRAWWPFTABS` (WPF, `WpfTabsWindow.cs`, `WpfCommands.cs`; no ribbon button, type it) tests what a WPF dialog holds besides text boxes and buttons. Run in AutoCAD 2026 on 2026-11-07, recorded with real mouse and keyboard input (Name, Size tab: Width and Height, Style tab: Mark the center, Qty, a tree selection `Parts > Fasteners > Bolts > M8`, **Details...** with a Note, OK, a checkpoint on the three TEXTs) and replayed with `RETRACERUN`:
the **static texts** (`Plate designer`, the hint line, `Nuts & Bolts catalog` (the `&` stays), the labels of the page showing, `Selected: Parts > Fasteners > Bolts > M8`, `Note hello, depth 0`) are in the `statics` of the `dialog_click` and the closing `dialog_values` and in the replay result; the **tab** fields are recorded with no tab (key `name:WidthBox`), only while their page is showing, and a replay that starts on General fails the Width, Height and Mark values with a message naming the tab control and hands the step to a person; the **tree** selection is not in the recording at all, the replay passes the dialog with the tree untouched and says `unverified: ... a tree "PartsTree"`, and the checkpoint fails (`qty 3 part none` instead of `part Parts > Fasteners > Bolts > M8`). A recording made without touching the Size and Style tabs or the tree replays by itself (PASS if the TEXTs match).

`DRAWPALETTE` opens the **Retrace Samples** palette set, the way many plugins keep their UI: a WinForms page **Box** (width, height,
layer, corner, Closed, Draw box), a WPF page **Circle** (radius, layer, Center at 0,0, Draw circle) and a WinForms page **Parts** (the same parts tree as
`DRAWPART`, tree `PartsTree` labelled "Catalog", and Draw part, which adds a TEXT with the selected part's name on layer `Parts`). Its buttons draw straight from the
palette (document lock, no command), so a recording sees them only as palette steps. A replay does the Box and Parts steps by itself (the Parts step selects the tree item, then
presses Draw part); Circle (WPF) steps need a person.
On the Parts page a double-click on a part (an item with no children) draws its name followed by ` x2` at 0,-5 on layer `Parts`, with no button: a recording holds it as a `palette_click` with `kind: "dblclick"` (`tree`, `treeLabel`, `path`), steps.md reads ``double-click `Parts › Fasteners › Bolts › M8` in **Catalog**``, and a replay selects the item and sends the double-click.
A fourth WinForms page, **Tabs**, holds a `TabControl` (tab **Size**: Width, Height; tab **Style**: Mark the center) and **Draw tabs** under it, which draws a closed polyline of width x height on layer `Tabs` and, with Mark the center ticked, a circle at its middle. A recorded palette step carries the tab of each value (`tab:Size|name:TabWidthBox`), and a replay shows the tab (WinForms `SelectedIndex`) before it sets a value on it, then presses Draw tabs.

`DRAWMENU` (WinForms, `MenuDialog.cs`, `MenuCommands.cs`; no ribbon button, type it) has the three kinds of right-click menu Retrace records: the **Catalog** tree (`CatalogTree`, the parts tree) has a `ContextMenuStrip` with **Edit > Rename** (adds " (renamed)" to the node), **Edit > Duplicate** (adds a sibling "... copy") and **Properties...** (opens the sub form `Properties` with a **Note** field and OK / Cancel);
the **Files** list (`FilesList`) shows a Win32 popup menu (`TrackPopupMenu`, `WM_COMMAND` to the form): **Mark**, **Unmark**, **More > Clear all**; the **Tools** list view (`ToolsList`, a WinForms `ListView`, i.e. `SysListView32`, with a `ContextMenuStrip`): **Flag**, **Unflag**, **Clear flags** (a flagged item gets a `*`; Clear flags says in the status line whether an item was selected, so a replay that right-clicks the wrong place shows in the `statics` of the result). The Menu key and Shift+F10 open each of the three menus on the selected item. A right-click selects the item under the mouse first, as most programs do. OK draws `part: <selected node>`, `note: <its note>`, `tools: <nodes under Tools>`, `files: <the list>` and `tools list: <the Tools list view, flagged items marked *>`, so a replay that skipped a menu choice (or a sub form's OK) draws other texts and a checkpoint after it fails.
A recorded test: `DRAWMENU`, right-click `Tools › Hammer`, Edit > Duplicate, right-click Hammer, Properties..., set the note, OK in the sub form, right-click plan.dwg in Files, Mark, OK, checkpoint. steps.md reads "Right-click **Hammer** in **Catalog**, choose **Edit › Duplicate**".
The palette set has a **Menus** page (tree `MenuTree`, "Notes", the same kind of `ContextMenuStrip`, and **Draw note**, which draws the node's text, its note and the number of top-level nodes on layer `Menus`), and the WPF **Circle** page's Radius box has a WPF `ContextMenu` (**Radius > Ten / Twenty**, **Reset**) that sets the radius **Draw circle** uses.

## Practice workflow

1. In the Retrace ribbon tab (see `../../README.md`): Record (name the test, e.g. `sample-basic`).
2. Retrace Samples tab > Draw > Rectangle: pick two corners.
3. Add a Checkpoint. Retrace Samples > Draw > Label: type `Room 1`, pick a point, Enter for the height. Add a Checkpoint.
4. Retrace Samples > Dialogs > Grid...: set values, OK, pick a point (the dialog is the manual step).
5. Stop, then Run test on the recording; then build the docs from this folder (Retrace ribbon > Build docs) and compare it.

`DRAWSHEET` (`SheetCommands.cs`; no ribbon button, type it) is the one real property sheet here: stock AutoCAD dialogs tried so far (DIMSTYLE, OPTIONS, DSETTINGS, the plotter configuration editor) are not comctl32 property sheets. A recorded test: `DRAWSHEET`, change Name and Note, open Size and change Width and Height, open Options, tick Mark the center and set Radius, click General again, OK, checkpoint; `RETRACERUN` switches the pages with `PSM_SETCURSEL` in the recorded order. To see a page that refuses to be left, record a value of 0 in Width (or edit a recording that way): the replay sets it, cannot leave the Size page and stops with that reason for a person to finish.
