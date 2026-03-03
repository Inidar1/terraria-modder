using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace TerrariaModManager.Helpers;

public static class DialogHelper
{
    public static async Task<ButtonResult> ShowDialog(
        string title, string message, ButtonEnum buttons, Icon icon)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title, message, buttons, icon);
        
        var mainWindow = GetMainWindow();
        return mainWindow != null 
            ? await box.ShowWindowDialogAsync(mainWindow) 
            : await box.ShowAsync();
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
