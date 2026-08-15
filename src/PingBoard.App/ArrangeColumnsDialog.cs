using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;

namespace PingBoard.App;

/// <summary>
/// Reorders the board's columns.
/// <para>
/// A list with move buttons rather than drag-and-drop on the headers. Dragging is what people
/// expect from a grid, but the header here is a hand-built row of TextBlocks rather than a real
/// grid control, so drag targets would have to be invented from scratch — and a fiddly drag is
/// worse than two obvious buttons for something done once and then left alone.
/// </para>
/// </summary>
public static class ArrangeColumnsDialog
{
    public static async Task ShowAsync(XamlRoot root, Action onChanged)
    {
        var layout = ColumnLayout.Instance;

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 320,
            MinWidth = 260,
        };

        void Fill(string? select = null)
        {
            list.Items.Clear();

            foreach (var id in layout.Order)
            {
                // Hidden columns are listed too — they keep their place in the order, and being
                // able to position one before showing it saves doing this twice.
                var label = ColumnLayout.HeaderFor(id);
                if (!layout.IsVisible(id)) label += "   (hidden)";

                list.Items.Add(label);
            }

            if (select is not null)
            {
                var index = layout.Order.ToList().FindIndex(id =>
                    string.Equals(id, select, StringComparison.OrdinalIgnoreCase));

                if (index >= 0) list.SelectedIndex = index;
            }
        }

        Fill();
        list.SelectedIndex = 0;

        string? SelectedId() =>
            list.SelectedIndex >= 0 && list.SelectedIndex < layout.Order.Count
                ? layout.Order[list.SelectedIndex]
                : null;

        void Move(int delta)
        {
            if (SelectedId() is not { } id) return;
            if (!layout.Move(id, delta)) return;

            Fill(id);
            onChanged();
        }

        var up = new Button { Content = "Move up", MinWidth = 110 };
        var down = new Button { Content = "Move down", MinWidth = 110 };
        var reset = new Button { Content = "Reset", MinWidth = 110 };

        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        reset.Click += (_, _) =>
        {
            layout.ResetOrder();
            Fill();
            onChanged();
        };

        var buttons = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(new Border { Height = 12 });
        buttons.Children.Add(reset);

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        body.Children.Add(list);
        body.Children.Add(buttons);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Arrange columns",
            Content = body,
            CloseButtonText = "Done",
        };

        await dialog.ShowAsync();
    }
}
