namespace CodexManager;

public sealed class WorkspaceHistory(Store store)
{
    public IReadOnlyList<Workspace> Entries() => store.Workspaces()
        .Where(w => store.Setting("historyRemoved:" + w.Id) != "1")
        .OrderByDescending(w => store.Setting("lastOpened:" + w.Id) ?? "")
        .ThenBy(w => w.Name).ToArray();
    public void Opened(Workspace workspace)
    {
        store.Setting("historyRemoved:" + workspace.Id, "0");
        store.Setting("lastOpened:" + workspace.Id, DateTimeOffset.UtcNow.ToString("O"));
    }
    public void Remove(Workspace workspace) => store.Setting("historyRemoved:" + workspace.Id, "1");
    public static async Task<bool?> Exists(Workspace workspace)
    {
        try
        {
            if (!workspace.IsWsl)
                return await Task.Run(() => File.GetAttributes(workspace.Path).HasFlag(FileAttributes.Directory));
            // A failed WSL launch is unknown, not proof that its folder disappeared.
            var result = await Hosts.Capture(Hosts.Info("wsl.exe", "-d", workspace.Distro!, "--exec", "sh", "-c",
                "if test -d \"$1\"; then printf present; elif test -e \"$1\"; then printf missing; else p=$(dirname \"$1\"); while ! test -e \"$p\"; do n=$(dirname \"$p\"); test \"$n\" = \"$p\" && break; p=$n; done; if test -d \"$p\" && test -x \"$p\"; then printf missing; else printf unknown; fi; fi", "sh", workspace.Path));
            return result.Trim() switch { "present" => true, "missing" => false, _ => null };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return workspace.IsWsl ? null : false; }
        catch { return null; }
    }
}
