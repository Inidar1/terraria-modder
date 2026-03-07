using Avalonia.Controls;
using Avalonia.Interactivity;
using TerrariaModManager.Models;
using TerrariaModManager.ViewModels;
using System.Linq;

namespace TerrariaModManager.Views;

public partial class InstalledModsView : UserControl
{
    public InstalledModsView()
    {
        InitializeComponent();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is InstalledModsViewModel vm)
        {
            var combined = (EnabledList.SelectedItems?.Cast<object>() ?? Enumerable.Empty<object>())
                .Concat(DisabledList.SelectedItems?.Cast<object>() ?? Enumerable.Empty<object>())
                .ToList();
            vm.UpdateSelection(combined);
        }
    }

    private void OnModToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle &&
            toggle.DataContext is InstalledMod mod &&
            DataContext is InstalledModsViewModel vm)
        {
            // toggle.IsChecked is already the new state after the click
            vm.SetModEnabled(mod, toggle.IsChecked == true);
        }
    }
}
