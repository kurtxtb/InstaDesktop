using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InstaDesktop.Localization;
using InstaDesktop.Models;
using InstaDesktop.Services;

namespace InstaDesktop;

// Ctrl+/ or F1: every keyboard shortcut, including the system-wide ones as
// currently set in Settings.
internal sealed class ShortcutsWindow : Window
{
    internal int RowCount { get; }

    public ShortcutsWindow(AppSettings settings)
    {
        Title = Loc.T("Shortcuts.Title");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        WindowTheme.Track(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        string Off(string value) => string.IsNullOrEmpty(value) ? Loc.T("Shortcuts.Off") : value;
        var sections = new (string Header, (string Keys, string Text)[] Rows)[]
        {
            (Loc.T("Shortcuts.Global"), new[]
            {
                (Off(settings.HotkeyShowWindow), Loc.T("Shortcuts.ShowWindow")),
                (Off(settings.HotkeyMessages), Loc.T("Shortcuts.MessagesPanel")),
            }),
            (Loc.T("Shortcuts.InApp"), new[]
            {
                ("Ctrl+1 / 2 / 3 / 4", Loc.T("Shortcuts.Sections")),
                ("Ctrl+Shift+M", Loc.T("Shortcuts.MessagesPanel")),
                ("Ctrl+= / Ctrl+-", Loc.T("Shortcuts.Zoom")),
                ("Ctrl+0", Loc.T("Shortcuts.ZoomReset")),
                ("F5 / Ctrl+R", Loc.T("Shortcuts.Reload")),
                ("Alt+Left / Alt+Right", Loc.T("Shortcuts.BackForward")),
                ("Ctrl+,", Loc.T("Shortcuts.Settings")),
                ("Ctrl+/ / F1", Loc.T("Shortcuts.ThisList")),
                ("F12", Loc.T("Shortcuts.DevTools")),
            }),
        };

        var page = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        int rows = 0;
        foreach (var (header, items) in sections)
        {
            var caption = new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 8) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            page.Children.Add(caption);
            var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(16, 6, 16, 6) };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "Outline");
            var list = new StackPanel();
            foreach (var (keys, text) in items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 7, 0, 7) };
                var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(chips, Dock.Right);
                foreach (var chip in Chips(keys)) chips.Children.Add(chip);
                row.Children.Add(chips);
                row.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
                list.Children.Add(row);
                rows++;
            }
            card.Child = list;
            page.Children.Add(card);
        }
        RowCount = rows;
        Content = page;
    }

    // "Ctrl+Alt+I" -> [Ctrl] [Alt] [I]; " / " separates alternatives.
    private static IEnumerable<UIElement> Chips(string keys)
    {
        var alternatives = keys.Split(" / ");
        for (int a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) yield return Separator("/");
            var parts = alternatives[a] == "Ctrl+=" || alternatives[a] == "Ctrl+-" || alternatives[a] == "Ctrl+," || alternatives[a] == "Ctrl+/"
                ? new[] { "Ctrl", alternatives[a][5..] } : alternatives[a].Split('+');
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) yield return Separator("+");
                var text = new TextBlock { Text = parts[p], FontSize = 12, FontWeight = FontWeights.SemiBold };
                var chip = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(7, 2, 7, 3), Child = text };
                chip.SetResourceReference(Border.BackgroundProperty, "Surface");
                chip.SetResourceReference(Border.BorderBrushProperty, "Outline");
                yield return chip;
            }
        }
    }

    private static TextBlock Separator(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        return block;
    }
}
