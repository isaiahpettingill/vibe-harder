using Avalonia.Controls;
using Avalonia.VisualTree;

namespace CodexManager;

public partial class MainView
{
    private CancellationTokenSource? ruleEditingLifetime;
    private bool ruleEditingEnabled;
    internal bool AgentRuleEditingEnabled => ruleEditingEnabled && !remoteOnly && current?.IsRemote != true;

    private void InitializeAgentRuleEditing() => ruleEditingEnabled = AgentRuleLinks.Desktop &&
        (store.Setting(AgentRuleLinks.SettingKey) is { } choice ? choice == "1" : AgentRuleLinks.Installed.Value is not null);

    private CheckBox CreateAgentRuleSetting(Action<Action> applyChange)
    {
        var setting = new CheckBox
        {
            Name = "AgentWranglerRuleLinks",
            Content = "Open Agent Wrangler rule citations in my text editor",
            IsChecked = ruleEditingEnabled
        };
        setting.IsCheckedChanged += (_, _) => applyChange(() =>
        {
            var enabled = setting.IsChecked == true;
            store.Setting(AgentRuleLinks.SettingKey, enabled ? "1" : "0");
            ruleEditingEnabled = enabled;
            if (!enabled)
            {
                ruleEditingLifetime?.Cancel();
                ruleEditingLifetime?.Dispose();
                ruleEditingLifetime = null;
            }
            foreach (var markdown in this.GetVisualDescendants().OfType<ChatMarkdown>().ToArray()) markdown.RefreshRuleLinks();
        });
        return setting;
    }

    internal async Task OpenAgentRule(string id)
    {
        if (!AgentRuleEditingEnabled)
            throw new IOException("Enable Agent Wrangler rule links in Settings on the desktop that runs Agent Wrangler.");
        ruleEditingLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(discoveryLifetime.Token);
        var token = ruleEditingLifetime.Token;
        var text = await AgentRuleLinks.Run(id, token: token);
        var directory = Directory.CreateTempSubdirectory("vibe-harder-rule-");
        var path = Path.Combine(directory.FullName, id + ".txt");
        await File.WriteAllTextAsync(path, text, token);
        var top = TopLevel.GetTopLevel(this) ?? throw new IOException("No desktop window is available.");
        using var file = await top.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        if (file is null || !await top.Launcher.LaunchFileAsync(file))
            throw new IOException("No default text editor could open " + path + ".");
        StatusText.Text = $"Editing Agent Wrangler rule {id}. Save in your editor to apply changes.";
        _ = WatchAgentRule(id, path, text, token);
    }

    private async Task WatchAgentRule(string id, string path, string text, CancellationToken token)
    {
        try
        {
            await AgentRuleLinks.Watch(path, text, cancel => AgentRuleLinks.Run(id, token: cancel),
                (body, previous, cancel) => AgentRuleLinks.Run(id, body, previous, cancel),
                (conflict, error) => StatusText.Text = conflict
                    ? $"Merge conflicts in {id}. Reload {path} in your editor, resolve the markers, and save."
                    : error is null ? $"Saved Agent Wrangler rule {id}." : $"Could not save {id}: {error} Your edit is in {path}.", token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
