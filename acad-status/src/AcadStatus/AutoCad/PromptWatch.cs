using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcadStatus.Core;

namespace AcadStatus.AutoCad;

/// <summary>Learns which prompt AutoCAD waits at, the way Retrace's recorder does: from the editor's PromptingFor* / PromptedFor* events
/// (their handlers read only <c>Options.Message</c>: reading <c>Keywords</c> there made AutoCAD loop on mouse picks, see Retrace's AGENTS.md).
/// Handlers only note data; nothing here calls into the editor or a document. Everything runs on the UI thread.
/// <para>The state is kept per drawing, as a stack of frames (<see cref="PromptStack"/>): a command, a LISP function, or a transparent command
/// typed at a prompt is a frame, and when the inner one ends the outer prompt is back. One drawing closing, or one command ending, never
/// touches another drawing's prompts.</para></summary>
sealed class PromptWatch : IDisposable
{
    readonly Dictionary<Document, (PromptStack Stack, List<Action> Undo)> _docs = new();
    bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        var dm = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
        dm.DocumentCreated += OnDocCreated;
        dm.DocumentToBeDestroyed += OnDocToBeDestroyed;
        foreach (Document d in dm) Hook(d);
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        Log.Safe("prompt watch stop", () =>
        {
            var dm = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            dm.DocumentCreated -= OnDocCreated;
            dm.DocumentToBeDestroyed -= OnDocToBeDestroyed;
        });
        foreach (var d in new List<Document>(_docs.Keys)) Unhook(d);
    }

    /// <summary>The pending prompt of <paramref name="active"/> (the drawing CMDACTIVE belongs to): null when none, "" when one is pending
    /// without text. A prompt nothing can be waiting at (no command active, no LISP running) is dropped first.</summary>
    public string? PromptOf(Document? active, int cmdActive)
    {
        if (active == null || !_docs.TryGetValue(active, out var d)) return null;
        d.Stack.DropStale(cmdActive);
        return d.Stack.ForSnapshot();
    }

    void OnDocCreated(object? s, DocumentCollectionEventArgs e) => Log.Safe("document created", () => Hook(e.Document));

    void OnDocToBeDestroyed(object? s, DocumentCollectionEventArgs e) => Log.Safe("document destroyed", () => Unhook(e.Document));

    void Hook(Document d)
    {
        if (d == null || _docs.ContainsKey(d)) return;
        var stack = new PromptStack();
        var undo = new List<Action>();
        _docs[d] = (stack, undo);
        Log.Safe("hook document", () =>
        {
            var ed = d.Editor;
            void Pre(string? message) => stack.Push(message);
            void Post() => stack.Pop();
            On<PromptPointOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForPoint += h, h => ed.PromptingForPoint -= h);
            On<PromptPointResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForPoint += h, h => ed.PromptedForPoint -= h);
            On<PromptPointOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForCorner += h, h => ed.PromptingForCorner -= h);
            On<PromptPointResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForCorner += h, h => ed.PromptedForCorner -= h);
            On<PromptDistanceOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForDistance += h, h => ed.PromptingForDistance -= h);
            On<PromptDoubleResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForDistance += h, h => ed.PromptedForDistance -= h);
            On<PromptAngleOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForAngle += h, h => ed.PromptingForAngle -= h);
            On<PromptDoubleResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForAngle += h, h => ed.PromptedForAngle -= h);
            On<PromptDoubleOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForDouble += h, h => ed.PromptingForDouble -= h);
            On<PromptDoubleResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForDouble += h, h => ed.PromptedForDouble -= h);
            On<PromptIntegerOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForInteger += h, h => ed.PromptingForInteger -= h);
            On<PromptIntegerResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForInteger += h, h => ed.PromptedForInteger -= h);
            On<PromptStringOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForString += h, h => ed.PromptingForString -= h);
            On<PromptStringResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForString += h, h => ed.PromptedForString -= h);
            On<PromptKeywordOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForKeyword += h, h => ed.PromptingForKeyword -= h);
            On<PromptStringResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForKeyword += h, h => ed.PromptedForKeyword -= h);
            On<PromptEntityOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForEntity += h, h => ed.PromptingForEntity -= h);
            On<PromptEntityResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForEntity += h, h => ed.PromptedForEntity -= h);
            On<PromptNestedEntityOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.Message), h => ed.PromptingForNestedEntity += h, h => ed.PromptingForNestedEntity -= h);
            On<PromptNestedEntityResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForNestedEntity += h, h => ed.PromptedForNestedEntity -= h);
            On<PromptSelectionOptionsEventHandler>(undo, (s, e) => Pre(e.Options?.MessageForAdding), h => ed.PromptingForSelection += h, h => ed.PromptingForSelection -= h);
            On<PromptSelectionResultEventHandler>(undo, (s, e) => Post(), h => ed.PromptedForSelection += h, h => ed.PromptedForSelection -= h);

            // Every command and every LISP function is a frame. A command that starts inside a pending prompt (a transparent 'ZOOM, our own
            // ACADSTATUS, a LISP expression typed at it) gets a frame of its own, and the outer prompt is back when it ends.
            On<CommandEventHandler>(undo, (s, e) => stack.EnterCommand(), h => d.CommandWillStart += h, h => d.CommandWillStart -= h);
            On<CommandEventHandler>(undo, (s, e) => stack.Leave(FrameKind.Command), h => d.CommandEnded += h, h => d.CommandEnded -= h);
            On<CommandEventHandler>(undo, (s, e) => stack.Leave(FrameKind.Command), h => d.CommandCancelled += h, h => d.CommandCancelled -= h);
            On<CommandEventHandler>(undo, (s, e) => stack.Leave(FrameKind.Command), h => d.CommandFailed += h, h => d.CommandFailed -= h);
            On<LispWillStartEventHandler>(undo, (s, e) => stack.EnterLisp(), h => d.LispWillStart += h, h => d.LispWillStart -= h);
            On<EventHandler>(undo, (s, e) => stack.Leave(FrameKind.Lisp), h => d.LispEnded += h, h => d.LispEnded -= h);
            On<EventHandler>(undo, (s, e) => stack.Leave(FrameKind.Lisp), h => d.LispCancelled += h, h => d.LispCancelled -= h);
        });
    }

    static void On<T>(List<Action> undo, T handler, Action<T> add, Action<T> remove)
    {
        add(handler);
        undo.Add(() => remove(handler));
    }

    void Unhook(Document d)
    {
        if (!_docs.Remove(d, out var entry)) return;
        foreach (var a in entry.Undo) Log.Safe("unhook document", a);
    }
}
