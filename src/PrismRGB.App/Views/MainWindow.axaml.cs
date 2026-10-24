using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PrismRGB.App.ViewModels;

namespace PrismRGB.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _previewTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Aperçu : rafraîchi à ~30 img/s, seulement quand la fenêtre est visible.
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (DataContext is MainViewModel vm && IsVisible && WindowState != WindowState.Minimized)
                Preview.Frames = vm.Host.Engine.GetPreview();
        });

        ProfileNameBox.LostFocus += (_, _) => CommitProfileName();
        ProfileNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitProfileName();
        };
    }

    private void CommitProfileName()
    {
        if (DataContext is MainViewModel vm && ProfileNameBox.Text is { } t) vm.RenameSelectedProfile(t);
    }

    private async void BrowseOpenRgb(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir OpenRGB.exe",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenRGB") { Patterns = ["OpenRGB.exe", "*.exe"] }],
        });
        if (files.Count > 0 && DataContext is MainViewModel vm)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null) vm.OpenRgbPath = path;
        }
    }

    private void DismissMessage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Message = "";
    }
}
