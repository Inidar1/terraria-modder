using System.IO;
using Avalonia.Controls;
using TerrariaModManager.ViewModels;

namespace TerrariaModManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            var msg = UnwrapException(ex);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            throw;
        }
    }

    private static string UnwrapException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        var current = ex;
        while (current != null)
        {
            sb.AppendLine($"[{current.GetType().Name}] {current.Message}");
            sb.AppendLine(current.StackTrace);
            sb.AppendLine("---");
            current = current.InnerException;
        }
        return sb.ToString();
    }
}
