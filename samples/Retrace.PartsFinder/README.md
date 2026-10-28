# Retrace.PartsFinder

A test add-in for Retrace: a palette set the way many plugins keep their UI, with what makes palettes hard to record and replay.
It is not part of the release bundle: `NETLOAD` it, or on a VM install it with `tools/vm/install-samples.sh`, which loads it at every AutoCAD start.

Build: `dotnet build -c Release` (AutoCAD 2026, net8.0-windows, `AutoCAD.NET` 25.1.0). The palette calls https://dummyjson.com (a public
test API), so the machine needs internet access.

## What it has

- **Ribbon tab "Parts"**, panel "Finder": **Parts finder** (`PARTSFINDER`) opens the palette set floating with auto roll-up (it rolls up to its
  title bar when the mouse leaves it); **Docked finder** (`PARTSFINDERDOCK`) docks it on the left with auto-hide (a tab at the side that slides
  out when the mouse is over it). Seen in R25.1: `PaletteSet.Anchored` is read-only; a floating set with `AutoRollUp` that is then docked
  becomes anchored, while `AutoRollUp` on an already docked set changes nothing.
- **The "Search" palette** (WinForms): Search text, Category (filled by an async API call when the palette opens), Max price, In stock only,
  API delay (ms, passed to the API's `delay` so every call answers that late), Search (async; the results list fills when the answer comes),
  Results, **Insert** (puts the selected part as TEXT on layer PARTS straight from the palette, under a document lock), and **Place...** (runs
  `PARTSPLACE` with `SendStringToExecute`, which asks for a point).
- The results list has `AccessibleName = "Results"`: without it Retrace would name it after the status text above it ("21 parts").
- **WPF finder** (`PARTSFINDERWPF`) opens "Parts Finder WPF": the same search written the WPF way, with a view model, bindings (the text boxes
  update their source only on focus loss), an async-loaded category combo, a results list with a data template, and Search / Insert / Place...
  as commands whose CanExecute enables the buttons. Floating, with auto roll-up.

## What it showed (AutoCAD Electrical 2026, 2026-10-27)

- Auto roll-up (floating): the rolled-up palette's controls stay "visible" windows, moved off to the corner; recording and replay are not affected.
- Auto-hide (docked): while hidden the palette's windows are hidden; the recorder keeps the values it read while the palette was out, and
  replay sets values and presses buttons through WinForms without showing it.
- Async calls: replay retries a palette step until the results are there and the button is enabled (up to 15 s).
- Place...: the command a palette button starts, and its point, are part of the palette step; replay clicks, then answers the prompt.
- Both flows (floating: search, insert, category, place; docked: search, insert) replayed unattended with every checkpoint PASS, and the WPF
  flow (search, insert, category through the drop-down, place) too.
