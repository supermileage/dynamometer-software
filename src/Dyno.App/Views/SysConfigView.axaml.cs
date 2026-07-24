using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dyno.App.ViewModels;
using Dyno.Core.SysConfig;

namespace Dyno.App.Views;

/// <summary>The SysConfig page: firmware header editor plus the runtime (USB) device settings.
/// Almost pure markup — the only behavior here is the two file dialogs, which need this control's
/// window and so cannot live in the view model. What goes in or out of the file is decided by
/// <see cref="SysConfigViewModel.CurrentConfiguration"/> and
/// <see cref="SysConfigViewModel.ApplyImported"/>, which are where the rules are and are tested.
/// </summary>
public partial class SysConfigView : UserControl
{
    public SysConfigView() => InitializeComponent();

    private async void OnExportJsonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel main)
        {
            return;
        }

        // Every await below is inside this try, deliberately: see StorageFailure.
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                main.AddEvent(StorageUnavailable);
                return;
            }

            var file = await storage.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Export configuration",
                    SuggestedFileName = $"dyno-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                    DefaultExtension = "json",
                    FileTypeChoices = [JsonFiles],
                }
            );
            if (file is null)
            {
                return; // cancelled
            }

            var bundle = main.SysConfig.CurrentConfiguration();
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(ConfigBundleJson.Write(bundle));
            }
            main.AddEvent(
                $"[INFO] exported {bundle.Count} settings to {file.Name} — this file is a record "
                    + "of the configuration, not something the board reads"
            );
        }
        catch (Exception ex)
        {
            main.AddEvent($"[ERR ] config export failed — {StorageFailure(ex)}");
        }
    }

    private async void OnImportJsonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel main)
        {
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                main.AddEvent(StorageUnavailable);
                return;
            }

            var files = await storage.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Import configuration",
                    AllowMultiple = false,
                    FileTypeFilter = [JsonFiles],
                }
            );
            if (files.Count == 0)
            {
                return; // cancelled
            }

            string text;
            await using (var stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync();
            }

            ConfigBundleReadResult read;
            try
            {
                read = ConfigBundleJson.Read(text);
            }
            catch (InvalidDataException ex)
            {
                // The document is unusable as a whole, so nothing is staged: a half-applied import
                // is worse than none, because the page would then hold a mix nobody chose.
                main.AddEvent($"[ERR ] {files[0].Name} could not be read — {ex.Message}");
                return;
            }

            var bundle = read.Bundle;
            main.AddEvent(
                $"[INFO] loaded {bundle.Count} settings from {files[0].Name} — staged, not saved; "
                    + "press Apply to keep them"
            );
            foreach (var problem in read.Problems)
            {
                main.AddEvent($"[WARN] config import: {problem}");
            }
            foreach (var line in main.SysConfig.ApplyImported(bundle))
            {
                main.AddEvent(line);
            }
        }
        catch (Exception ex)
        {
            main.AddEvent($"[ERR ] config import failed — {StorageFailure(ex)}");
        }
    }

    private static readonly FilePickerFileType JsonFiles = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    private const string StorageUnavailable =
        "[ERR ] this window cannot open a file dialog — no storage provider";

    /// <summary>
    /// Describes a file-dialog failure for the event log.
    /// </summary>
    /// <remarks>
    /// The reason every await in these handlers sits inside a try: they are <c>async void</c>, so an
    /// exception that escapes one is not returned to any caller — it goes to the synchronization
    /// context unobserved, and with no global handler installed that ends the process. A file dialog
    /// is exactly where this bites, because on Linux it is not in-process at all: it is a DBus call
    /// to xdg-desktop-portal, which can be missing, refuse, or time out for reasons that have
    /// nothing to do with this app. The old CSV export made this mistake and took the whole app down
    /// when the portal misbehaved, which read as "the export button crashes it".
    /// </remarks>
    private static string StorageFailure(Exception ex) =>
        ex is TimeoutException or OperationCanceledException
            ? "the system file dialog did not respond. On Linux this is xdg-desktop-portal; check "
                + "that a portal backend for your desktop is installed and running"
            : $"{ex.GetType().Name}: {ex.Message}";
}
