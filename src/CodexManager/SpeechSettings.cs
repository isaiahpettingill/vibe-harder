using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

// Turns speak to type on or off, and on Linux downloads or removes the local speech model.
public static class SpeechSettings
{
    public static Control? Create(Store store)
    {
        if (SpeechInput.Current is not { } speech) return null;
        var panel = new StackPanel { Spacing = 6 };
        var enabled = new CheckBox { Name = "SpeechInputEnabled", Content = "Speak to type: show a microphone button in the message box", IsChecked = SpeechInput.Enabled };
        enabled.IsCheckedChanged += (_, _) => SpeechInput.Set(store, enabled.IsChecked == true);
        panel.Children.Add(enabled);
        if (speech is not ISpeechModel model) return panel;
        var status = new TextBlock { Name = "SpeechModelStatus", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false, Height = 6 };
        var download = new Button { Name = "DownloadSpeechModel" };
        CancellationTokenSource? downloading = null;
        void Update()
        {
            status.Text = model.Downloaded ? model.Description + " is installed. Speech is transcribed on this computer." : "Speech is transcribed on this computer with a downloadable model: " + model.Description + ".";
            download.Content = downloading is not null ? "Cancel download" : model.Downloaded ? "Remove speech model" : "Download speech model";
        }
        download.Click += async (_, _) =>
        {
            if (downloading is { } active) { active.Cancel(); return; }
            if (model.Downloaded) { model.Remove(); Update(); return; }
            using var cancel = downloading = new CancellationTokenSource();
            progress.Value = 0; progress.IsVisible = true; Update();
            try { await model.Download(new Progress<double>(value => progress.Value = value), cancel.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { status.Text = AppDiagnostics.Message("Could not download the speech model", error); downloading = null; progress.IsVisible = false; download.Content = "Download speech model"; return; }
            downloading = null; progress.IsVisible = false; Update();
        };
        Update();
        panel.Children.Add(status); panel.Children.Add(progress); panel.Children.Add(download);
        return panel;
    }
}
