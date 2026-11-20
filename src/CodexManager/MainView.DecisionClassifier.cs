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

    private static NumericUpDown Number(int value, int min, int max) => new() { Value = value, Minimum = min, Maximum = max, FormatString = "0" };

    private static StackPanel Field(string caption, Control input) => new()
    {
        Spacing = 4, Children = { new Label { Content = caption, Target = input }, input }
    };

    private static TextBlock DecisionHeading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private static TextBox DecisionText(string name, string value) => new()
    {
        Name = name, Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 180
    };

    private Control CreateDecisionSettings(Action<Action> apply)
    {
        var options = DecisionSettings.Read(store);
        var enabled = new CheckBox { Name = "DecisionEnabled", Content = "Enable auto-resume", IsChecked = options.Enabled };
        var source = new ComboBox { Name = "DecisionSource", ItemsSource = new[] { "Host System One server", "Remote System One endpoint", "Use ACP (experimental)" }, SelectedIndex = (int)options.Source };
        var command = new TextBox { Name = "DecisionCommand", Text = options.Command, PlaceholderText = DecisionSettings.Example };
        var endpoint = new TextBox { Name = "DecisionEndpoint", Text = options.Endpoint, PlaceholderText = DecisionSettings.LocalAddress };
        var providers = AgentProviders.All;
        var provider = new ComboBox { Name = "DecisionProvider", ItemsSource = providers.Select(p => p.Name).ToArray(), SelectedIndex = Array.FindIndex(providers.ToArray(), p => p.Provider == options.Provider) };
        var model = new TextBox { Name = "DecisionModel", Text = options.Model, PlaceholderText = "Model ID from the ACP provider" };
        var http = Field("System One endpoint", endpoint);
        var native = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Command to run on this host" }, command,
            new SelectableTextBlock { Text = DecisionSettings.Example, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } } } };
        var acp = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "ACP provider" }, provider, new TextBlock { Text = "Model ID" }, model } };
        var criteria = DecisionText("DecisionCriteria", options.Criteria);
        var resumePrompt = DecisionText("DecisionResumePrompt", options.ResumePrompt);
        var timeout = Number(options.Timeout, 1, 300);
        var attempts = Number(options.Attempts, 1, 10);
        var requestLimit = Number(options.RequestLimit, 1, 50000);
        var responseLimit = Number(options.ResponseLimit, 1, 50000);
        var confidence = Number(options.Confidence, 0, 100);
        var score = Field("Minimum confidence (%)", confidence);
        var reset = new Button { Name = "ResetDecisionDefaults", Content = "Restore decision defaults" };
        reset.Click += (_, _) =>
        {
            criteria.Text = DecisionSettings.DefaultCriteria;
            resumePrompt.Text = DecisionSettings.DefaultResumePrompt;
            timeout.Value = DecisionSettings.DefaultTimeout;
            attempts.Value = DecisionSettings.DefaultAttempts;
            requestLimit.Value = DecisionSettings.DefaultRequestLimit;
            responseLimit.Value = DecisionSettings.DefaultResponseLimit;
            confidence.Value = DecisionSettings.DefaultConfidence;
        };
        var behavior = new StackPanel
        {
            Spacing = 8,
            Children = { DecisionHeading("Auto-resume"), Field("Continuation criteria", criteria), Field("Prompt sent to the agent when it resumes", resumePrompt) }
        };
        var limits = new StackPanel
        {
            Spacing = 8,
            Children = { DecisionHeading("Limits"), score, Field("Timeout (seconds)", timeout), Field("Maximum continuations per turn", attempts),
                Field("User request limit (characters)", requestLimit), Field("Agent response limit (characters)", responseLimit) }
        };
        void UpdateFields()
        {
            native.IsVisible = source.SelectedIndex == (int)DecisionSource.Host;
            acp.IsVisible = source.SelectedIndex == (int)DecisionSource.Acp;
            http.IsVisible = score.IsVisible = !acp.IsVisible;
        }
        source.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();
        var save = new Button { Name = "SaveDecisionSettings", Content = "Apply classifier settings", Classes = { "accent" } };
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
            store.Setting("decisionCriteria", criteria.Text?.Trim() ?? "");
            store.Setting(DecisionSettings.ResumePromptKey, resumePrompt.Text?.Trim() ?? "");
            foreach (var (key, input, fallback) in new[] { ("decisionTimeout", timeout, DecisionSettings.DefaultTimeout),
                ("decisionAttempts", attempts, DecisionSettings.DefaultAttempts), ("decisionRequestLimit", requestLimit, DecisionSettings.DefaultRequestLimit),
                ("decisionResponseLimit", responseLimit, DecisionSettings.DefaultResponseLimit), ("decisionConfidence", confidence, DecisionSettings.DefaultConfidence) })
                store.Setting(key, ((int)(input.Value ?? fallback)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            ApplyDecisionClassifier();
        });
        return new StackPanel
        {
            Name = "ClassifierSettings", Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Automatically resumes an agent that stops without meeting your completion criteria.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                enabled, behavior, new Separator(),
                new StackPanel { Spacing = 8, Children = { DecisionHeading("Classifier source"), Field("Source", source), native, http, acp } },
                new Separator(), limits, new WrapPanel { ItemSpacing = 8, Children = { save, reset } }
            }
        };
    }
}
