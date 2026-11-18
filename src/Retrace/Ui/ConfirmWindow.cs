using System.Windows;
using System.Windows.Controls;

using Retrace.Core;

namespace Retrace.Ui;

/// <summary>"Save changes?" with Save / Don't save / Cancel, in the look of the other Retrace windows (code-only WPF, the same button size and margins). Esc is Cancel, Enter is Save.</summary>
sealed class ConfirmWindow : Window
{
    CloseAnswer _answer = CloseAnswer.Cancel;   // the X of this window is a Cancel too

    ConfirmWindow()
    {
        Title = "Retrace settings";
        Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Retrace.AutoCad.WindowPlacement.MainInterface(this);
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = "Save changes?", FontSize = 15, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "You changed settings that are not saved yet.", Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = Btn("Save"); save.IsDefault = true; save.Click += (_, _) => Answer(CloseAnswer.Save);
        var dont = Btn("Don't save"); dont.Click += (_, _) => Answer(CloseAnswer.DontSave);
        var cancel = Btn("Cancel"); cancel.IsCancel = true; cancel.Click += (_, _) => Answer(CloseAnswer.Cancel);
        buttons.Children.Add(save); buttons.Children.Add(dont); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => save.Focus();
    }

    void Answer(CloseAnswer a) { _answer = a; DialogResult = true; }

    static Button Btn(string text) => new() { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), MinWidth = 64 };

    /// <summary>Asks over <paramref name="owner"/>; the answer, Cancel when the box is closed another way.</summary>
    public static CloseAnswer Ask(Window owner)
    {
        var w = new ConfirmWindow { Owner = owner };
        w.ShowDialog();
        return w._answer;
    }
}
