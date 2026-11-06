using System;
using System.Collections.Generic;

namespace AcadStatus.Core;

/// <summary>The prompts one command (or LISP function) has open. AutoCAD nests prompts (a point prompt inside a corner prompt), each raising
/// its own "prompting" and "prompted" event: a prompt is pushed when it starts and popped when it is answered.</summary>
public sealed class PromptTracker
{
    public const int MaxDepth = 32, MaxText = 200;
    readonly List<string> _open = new();
    bool _reprompt;

    public int Depth => _open.Count;

    /// <summary>A prompt is pending.</summary>
    public bool IsOpen => _open.Count > 0;

    public void Push(string? message)
    {
        // When an inner command that ran inside a pending prompt has ended, AutoCAD asks that prompt again: it is the same prompt, not a
        // second one nested in it (which would never be answered twice).
        if (_reprompt && _open.Count > 0) _open[^1] = Clean(message);
        else
        {
            if (_open.Count >= MaxDepth) _open.RemoveAt(0);
            _open.Add(Clean(message));
        }
        _reprompt = false;
    }

    public void Pop()
    {
        _reprompt = false;
        if (_open.Count > 0) _open.RemoveAt(_open.Count - 1);
    }

    public void Clear()
    {
        _open.Clear();
        _reprompt = false;
    }

    /// <summary>An inner command or LISP function that ran inside the pending prompt has ended: the next prompt is the same one again.</summary>
    public void ExpectReprompt() => _reprompt = _open.Count > 0;

    /// <summary>The text of the innermost open prompt that has one; null when none is open or none has text.</summary>
    public string? Current
    {
        get
        {
            for (int i = _open.Count - 1; i >= 0; i--)
                if (_open[i].Length > 0) return _open[i];
            return null;
        }
    }

    /// <summary>A prompt message without its leading line break, on one line.</summary>
    public static string Clean(string? message) => TextUtil.Clean(message, MaxText);
}

public enum FrameKind { Root, Command, Lisp }

/// <summary>What one drawing's command line is doing: one frame per nesting level, the outermost first. A command is a frame, a LISP
/// function is a frame, a transparent command typed at a prompt is a frame inside the command that waits. Each frame has its own prompts, so
/// when the inner one ends the outer prompt is back.</summary>
public sealed class PromptStack
{
    public const int MaxFrames = 16;

    sealed class Frame
    {
        public FrameKind Kind;
        public readonly PromptTracker Prompts = new();
    }

    readonly List<Frame> _frames = new();

    public int Depth => _frames.Count;

    public bool HasLisp { get { foreach (var f in _frames) if (f.Kind == FrameKind.Lisp) return true; return false; } }

    public bool HasCommand { get { foreach (var f in _frames) if (f.Kind == FrameKind.Command) return true; return false; } }

    public void EnterCommand() => Enter(FrameKind.Command);

    public void EnterLisp() => Enter(FrameKind.Lisp);

    void Enter(FrameKind kind)
    {
        if (_frames.Count >= MaxFrames) _frames.RemoveAt(0);
        _frames.Add(new Frame { Kind = kind });
    }

    /// <summary>A command or a LISP function ended (or was cancelled, or failed): the innermost frame of its kind goes, and no other. A frame of
    /// the other kind above it stays: a LISP function that started a command with <c>(command "_.LINE")</c> and returned leaves that command
    /// waiting at its prompt, and the command's frame must keep saying so. A frame that really is dead (a missed end) is cleaned up by
    /// <see cref="DropStale"/> when CMDACTIVE is 0. The frame below the one that left is the one that waits again. An end with no frame of
    /// its kind (it began before we looked) changes nothing.</summary>
    public void Leave(FrameKind kind)
    {
        int i = _frames.FindLastIndex(f => f.Kind == kind);
        if (i < 0) return;
        _frames.RemoveAt(i);
        if (i > 0) _frames[i - 1].Prompts.ExpectReprompt();
    }

    /// <summary>A prompt starts: it belongs to the innermost frame (to a root frame when nothing was seen start).</summary>
    public void Push(string? message) => Top().Prompts.Push(message);

    /// <summary>A prompt was answered.</summary>
    public void Pop()
    {
        if (_frames.Count > 0) _frames[^1].Prompts.Pop();
    }

    public void Clear() => _frames.Clear();

    Frame Top()
    {
        if (_frames.Count == 0) _frames.Add(new Frame { Kind = FrameKind.Root });
        return _frames[^1];
    }

    /// <summary>A prompt is pending in any frame.</summary>
    public bool IsOpen { get { foreach (var f in _frames) if (f.Prompts.IsOpen) return true; return false; } }

    /// <summary>The text of the innermost prompt that has one, looking from the innermost frame out.</summary>
    public string? Current
    {
        get
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
                if (_frames[i].Prompts.Current is { } c) return c;
            return null;
        }
    }

    /// <summary>The prompt as the snapshot wants it: null when none is open, "" when one is open without text.</summary>
    public string? ForSnapshot() => IsOpen ? Current ?? "" : null;

    /// <summary>A missed event must not leave a ghost, a LISP frame included. CMDACTIVE has a bit for an active command (1), a transparent one
    /// (2), a script (4), a dialog (8) and AutoLISP (32): when it is 0 nothing at all runs in the active drawing, so nothing can be waiting
    /// and every frame goes. Only for the drawing CMDACTIVE describes (the active one). True when it cleared something.</summary>
    public bool DropStale(int cmdActive)
    {
        if (cmdActive != 0 || _frames.Count == 0) return false;
        _frames.Clear();
        return true;
    }
}
