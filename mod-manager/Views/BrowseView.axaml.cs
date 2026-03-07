using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using TerrariaModManager.Models;
using TerrariaModManager.ViewModels;

namespace TerrariaModManager.Views;

public partial class BrowseView : UserControl
{
    public BrowseView() => InitializeComponent();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BrowseViewModel vm)
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual)
        {
            var current = visual;
            while (current != null && current != sender)
            {
                if (current is Button) return;
                current = current.GetVisualParent() as Visual;
            }
        }

        if (sender is Control control && control.DataContext is NexusMod mod
            && DataContext is BrowseViewModel vm)
        {
            if (vm.IsSelectMode)
                vm.ToggleSelectMod(mod);
            else
                vm.OpenDetailCommand.Execute(mod);
        }
    }
}
