using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexManager;

public partial class MainView
{
    private readonly DecisionClassifier decisionClassifier = new();

    private void InitializeDecisionClassifier()
    {
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 420, Height = 240 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        void RefreshLog()
        {
            var value = decisionClassifier.Log;
            if (log.Text == value) return;
            var follow = log.CaretIndex == (log.Text?.Length ?? 0);
            log.Text = value;
            if (follow) log.CaretIndex = value.Length;
        }
        timer.Tick += (_, _) => RefreshLog();
        var flyout = new Flyout { Content = log };
        flyout.Opened += (_, _) => { RefreshLog(); timer.Start(); };
        flyout.Closed += (_, _) => timer.Stop();
        DecisionLogsButton.Flyout = flyout;
        discoveryLifetime.Token.Register(() => Dispatcher.UIThread.Post(timer.Stop));
        DecisionToggleButton.Click += (_, _) =>
        {
            store.Setting("decisionEnabled", DecisionSettings.Read(store).Enabled ? "0" : "1");
            ApplyDecisionClassifier();
        };
        ApplyDecisionClassifier();
    }

    private void ApplyDecisionClassifier()
    {
        var options = DecisionSettings.Read(store);
        var supported = AgentRuleLinks.Desktop && !remoteOnly;
        var host = new Workspace("", "", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        decisionClassifier.Configure(supported ? options : options with { Enabled = false },
            supported && options.Source == DecisionSource.Acp ? AgentProviders.Command(store, host, options.Provider) : "");
        DecisionToolbar.IsVisible = supported && options.Source == DecisionSource.Host && (options.Enabled || options.Command.Length > 0);
        DecisionToggleButton.Content = options.Enabled ? "Classifier on" : "Classifier off";
    }

    private Control CreateDecisionSettings(Action<Action> apply)
    {
        var options = DecisionSettings.Read(store);
        var enabled = new CheckBox { Name = "DecisionEnabled", Content = "Enable the decision classifier", IsChecked = options.Enabled };
        var source = new ComboBox { Name = "DecisionSource", ItemsSource = new[] { "Host System One server", "Remote System One endpoint", "Use ACP (experimental)" }, SelectedIndex = (int)options.Source };
        var command = new TextBox { Name = "DecisionCommand", Text = options.Command, PlaceholderText = DecisionSettings.Example };
        var endpoint = new TextBox { Name = "DecisionEndpoint", Text = options.Endpoint, PlaceholderText = DecisionSettings.LocalAddress };
        var providers = AgentProviders.All;
        var provider = new ComboBox { Name = "DecisionProvider", ItemsSource = providers.Select(p => p.Name).ToArray(), SelectedIndex = Array.FindIndex(providers.ToArray(), p => p.Provider == options.Provider) };
        var model = new TextBox { Name = "DecisionModel", Text = options.Model, PlaceholderText = "Model ID from the ACP provider" };
        var http = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "System One endpoint" }, endpoint,
            new TextBlock { Text = "This expects a valid System One server.", Classes = { "muted" } } } };
        var native = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Command to run on this host" }, command,
            new SelectableTextBlock { Text = DecisionSettings.Example, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } } } };
        var acp = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "ACP provider" }, provider, new TextBlock { Text = "Model ID" }, model } };
        void UpdateFields()
        {
            native.IsVisible = source.SelectedIndex == (int)DecisionSource.Host;
            acp.IsVisible = source.SelectedIndex == (int)DecisionSource.Acp;
            http.IsVisible = !acp.IsVisible;
        }
        source.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();
        var save = new Button { Content = "Apply classifier settings" };
        save.Click += (_, _) => apply(() =>
        {
            var settings = new DecisionSettings(enabled.IsChecked == true, (DecisionSource)source.SelectedIndex, command.Text?.Trim() ?? "",
                endpoint.Text?.Trim() ?? "", providers[Math.Max(0, provider.SelectedIndex)].Provider, model.Text?.Trim() ?? "");
            if (settings.Enabled)
            {
                if (settings.Source == DecisionSource.Acp && settings.Model.Length == 0) throw new IOException("Enter the ACP model ID.");
                if (settings.Source != DecisionSource.Acp) _ = settings.Address();
                if (settings.Source == DecisionSource.Host && settings.Command.Length > 0)
                    _ = DecisionClassifier.NativeProcess(settings.Command, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            foreach (var (key, value) in new[] { ("decisionEnabled", settings.Enabled ? "1" : "0"), ("decisionSource", settings.Source.ToString()),
                ("decisionCommand", settings.Command), ("decisionEndpoint", settings.Endpoint), ("decisionProvider", settings.Provider.ToString()), ("decisionModel", settings.Model) })
                store.Setting(key, value);
            ApplyDecisionClassifier();
        });
        return new Expander
        {
            Header = "Decision classifier",
            Content = new StackPanel { Spacing = 8, Children = { enabled, source, native, http, acp, save } }
        };
    }
}
