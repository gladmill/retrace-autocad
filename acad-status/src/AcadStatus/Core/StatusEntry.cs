using System;
using System.Collections.Generic;

namespace AcadStatus.Core;

/// <summary>A dialog or message box as the <c>acad</c> detail describes it.</summary>
public sealed record DialogDetail(string Title, string Class, bool Modal, IReadOnlyList<string> Texts, IReadOnlyList<string> Buttons, DateTime? Since);

/// <summary>The <c>detail</c> object of an <c>acad</c> file. Every field is optional; a wrong type reads as absent.</summary>
public sealed record AcadDetail(string? Doc, int? Docs, int? CmdActive, string? CmdNames, string? Prompt, DialogDetail? Dialog);

/// <summary>One status file, read (CONTRACT.md, "Fields"). <see cref="State"/> is the text as written (trimmed).</summary>
public sealed record StatusEntry(
    string File, string Source, string Writer, int Pid, DateTime Updated, int TtlSec, string State, string Summary,
    string? Short, DateTime? Since, string? Owner, string? DetailJson, AcadDetail? Acad, string Raw, DateTime? PidStart = null)
{
    /// <summary>The file name without ".json": "acad-4812".</summary>
    public string Id => File.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? File[..^5] : File;

    public string StateKey => States.Normalize(State);

    /// <summary>Past this moment the entry is stale.</summary>
    public DateTime StaleAfter => Updated.AddSeconds(TtlSec);
}
