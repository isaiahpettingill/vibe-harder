using Markdig;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Graphics;
using System.Text.Json.Nodes;
using CodexManager;
using Path = System.IO.Path;
using OperationCanceledException = System.OperationCanceledException;

namespace CodexManager.Android;

[Activity(Label = "Vibe Harder", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.Material.NoActionBar", ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private RemoteConnection? connection;
    private CancellationTokenSource? session;
    private FrameLayout root = null!;
    private ScrollView drawerContainer = null!;
    private LinearLayout drawer = null!, transcript = null!, approvals = null!, configs = null!;
    private TextView status = null!;
    private EditText input = null!;
    private string? chatId, workspaceId;
    private string rendered = "", approvalState = "", configState = "";
    private RemoteHost? host;
    private readonly List<JsonObject> attachments = [];
    private string KeyFile => Path.Combine(FilesDir!.AbsolutePath, "client-key");
    private ThemePalette ThemeColors => ThemeCatalog.All.FirstOrDefault(p => p.Name == Preferences.GetString("theme", "Original")) ?? ThemeCatalog.All[0];
    private ISharedPreferences Preferences => GetSharedPreferences("remote", FileCreationMode.Private)!;
    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
    private LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };
    private Button Button(string text, Action action)
    {
        var button = new Button(this) { Text = text, Typeface = Typeface.CreateFromAsset(Assets, "NotoSans-Regular.ttf") }; button.SetMinHeight(Dp(48)); button.Click += (_, _) => action(); return button;
    }
    private TextView Label(string text, bool mono = false)
    {
        var view = new TextView(this) { Text = text, TextSize = mono ? 12 : 15 };
        view.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8)); view.SetTextColor(Color.ParseColor(mono ? ThemeColors.Muted : ThemeColors.Text));
        view.Typeface = Typeface.CreateFromAsset(Assets, mono ? "NeoSpleenNerdFont-Regular.ttf" : "NotoSans-Regular.ttf"); view.SetTextIsSelectable(true); return view;
    }
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        root = new FrameLayout(this); root.SetBackgroundColor(Color.ParseColor(ThemeColors.Background));
        root.SetOnApplyWindowInsetsListener(new InsetsListener());
        var main = Column(); root.AddView(main, new FrameLayout.LayoutParams(-1, -1));
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.AddView(Button("☰ Chats", () => drawerContainer.Visibility = ViewStates.Visible));
        header.AddView(Button("Connect", ConnectionDialog)); main.AddView(header);
        header.AddView(Button("Theme", () => new AlertDialog.Builder(this)!.SetItems(ThemeCatalog.All.Select(p => p.Name).ToArray(), (_, e) =>
        {
            Preferences.Edit()!.PutString("theme", ThemeCatalog.All[e.Which].Name)!.Apply();
            root.SetBackgroundColor(Color.ParseColor(ThemeColors.Background)); drawer.SetBackgroundColor(Color.ParseColor(ThemeColors.Surface));
            void Apply(View view) { if (view is TextView text) text.SetTextColor(Color.ParseColor(ThemeColors.Text)); if (view is ViewGroup group) for (var i = 0; i < group.ChildCount; i++) Apply(group.GetChildAt(i)!); }
            Apply(root); rendered = "";
        })!.Show()));
        status = Label("Connect to a host to view its workspaces and agents."); main.AddView(status);
        transcript = Column(); var scroll = new ScrollView(this); scroll.AddView(transcript); main.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        approvals = Column(); main.AddView(approvals); configs = Column(); main.AddView(configs);
        input = new EditText(this) { Hint = "Message the agent…", TextSize = 16 }; input.SetMaxLines(5); input.SetMinLines(2); input.SetTextColor(Color.Rgb(205, 214, 244)); main.AddView(input);
        var actions = new HorizontalScrollView(this); var row = new LinearLayout(this) { Orientation = Orientation.Horizontal }; actions.AddView(row);
        row.AddView(Button("Attach", () => Pick(2)));
        foreach (var (title, method) in new[] { ("Send", "send"), ("Queue", "queue"), ("Steer", "steer"), ("Stop", "stop"), ("Resume", "resume") })
            row.AddView(Button(title, async () => { if (chatId is null) return; var text = input.Text ?? ""; var result = await Call(new() { ["method"] = method, ["chatId"] = chatId, ["text"] = text, ["attachments"] = new JsonArray(attachments.Select(a => (JsonNode)a.DeepClone()).ToArray()) }); if (result is not null && method is not ("stop" or "resume") && result.ToJsonString() != "false" && input.Text == text) { input.Text = ""; attachments.Clear(); } }));
        main.AddView(actions);
        drawer = Column(); drawer.SetBackgroundColor(Color.ParseColor(ThemeColors.Surface)); drawerContainer = new ScrollView(this); drawerContainer.AddView(drawer);
        root.AddView(drawerContainer, new FrameLayout.LayoutParams(Dp(300), -1, GravityFlags.Left)); drawerContainer.Visibility = ViewStates.Gone;
        SetContentView(root);
        ConnectionDialog();
    }
    private void Pick(int code)
    {
        var intent = new Intent(Intent.ActionOpenDocument); intent.SetType("*/*"); intent.AddCategory(Intent.CategoryOpenable); StartActivityForResult(intent, code);
    }
    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data); if (resultCode != Result.Ok || data?.Data is null) return;
        try
        {
            await using var stream = ContentResolver!.OpenInputStream(data.Data)!; using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer)) > 0) { if (bytes.Length + count > 20 * 1024 * 1024) throw new IOException("File exceeds 20 MB."); bytes.Write(buffer, 0, count); }
            if (requestCode == 1) { File.WriteAllBytes(KeyFile, bytes.ToArray()); File.Delete(KeyFile + ".pub"); status.Text = "Private key imported into app-private storage."; }
            else
            {
                var mime = ContentResolver.GetType(data.Data) ?? "text/plain";
                attachments.Add(new() { ["Name"] = "Attachment " + (attachments.Count + 1), ["MimeType"] = mime, ["Data"] = mime.StartsWith("image/") ? Convert.ToBase64String(bytes.ToArray()) : System.Text.Encoding.UTF8.GetString(bytes.ToArray()), ["SourcePath"] = "file:///attachment" }); status.Text = $"{attachments.Count} attachments ready";
            }
        }
        catch (Exception error) { status.Text = error.Message; }
    }
    private void ConnectionDialog()
    {
        var form = Column(); form.SetPadding(Dp(16), 0, Dp(16), 0);
        var address = new EditText(this) { Hint = "Host address", Text = Preferences.GetString("address", "") }; var port = new EditText(this) { Hint = "Port", Text = Preferences.GetString("port", "2222"), InputType = global::Android.Text.InputTypes.ClassNumber };
        var pin = new EditText(this) { Hint = "SHA256: fingerprint from host settings", Text = Preferences.GetString("fingerprint", "") };
        var password = new EditText(this) { Hint = "Key passphrase (optional)", InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword };
        foreach (var view in new View[] { address, port, pin, password }) form.AddView(view);
        form.AddView(Button("Import private key", () => Pick(1)));
        form.AddView(Button("Generate key / copy public key", () =>
        {
            if (!File.Exists(KeyFile)) RemoteKey.Generate(KeyFile);
            if (!File.Exists(KeyFile + ".pub")) { status.Text = "For an imported private key, add its matching public key on the host."; return; }
            ((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip = ClipData.NewPlainText("Public key", File.ReadAllText(KeyFile + ".pub")); status.Text = "Public key copied. Add it in the host's Remote settings.";
        }));
        new AlertDialog.Builder(this)!.SetTitle("Remote host")!.SetView(form)!.SetNegativeButton("Cancel", (_, _) => { })!.SetPositiveButton("Connect", async (_, _) =>
        {
            if (!int.TryParse(port.Text, out var number) || number is < 1 or > 65535 || !File.Exists(KeyFile) || pin.Text?.StartsWith("SHA256:") != true) { status.Text = "Enter a valid port and fingerprint, and import or generate a key."; return; }
            Preferences.Edit()!.PutString("address", address.Text)!.PutString("port", port.Text)!.PutString("fingerprint", pin.Text)!.Apply();
            host = new RemoteHost(address.Text!, address.Text!, number, KeyFile, pin.Text!.Trim()); await Connect(password.Text);
        })!.Show();
    }
    private async Task Connect(string? passphrase)
    {
        session?.Cancel(); connection?.Dispose(); session = new(); connection = null; RemoteConnection? candidate = null;
        try { candidate = new RemoteConnection(host!, passphrase); await candidate.Connect(session.Token); connection = candidate; status.Text = "Connected to " + host!.Name; await RefreshDrawer(); _ = Poll(session.Token); }
        catch (Exception error) { candidate?.Dispose(); status.Text = error.Message + "\nObserved fingerprint: " + candidate?.ObservedFingerprint; }
    }
    private async Task<JsonNode?> Call(JsonObject request)
    {
        try { return connection is null ? null : await connection.Request(request, session!.Token); }
        catch (RemoteOperationException error) { status.Text = error.Message; return null; }
        catch (Exception error) { status.Text = "Disconnected. Agents remain on the host. " + error.Message; connection?.Dispose(); connection = null; return null; }
    }
    private async Task RefreshDrawer()
    {
        var result = await Call(new() { ["method"] = "list" }); if (result is null) return;
        drawer.RemoveAllViews(); drawer.AddView(Button("Close drawer", () => drawerContainer.Visibility = ViewStates.Gone));
        foreach (var workspace in result["workspaces"]!.AsArray())
        {
            var id = workspace!["id"]!.GetValue<string>(); drawer.AddView(Label(workspace["name"]!.GetValue<string>()));
            drawer.AddView(Button("＋ New chat", () => new AlertDialog.Builder(this)!.SetItems(new[] { "Codex", "Claude", "OpenCode" }, async (_, e) => { var created = await Call(new() { ["method"] = "create", ["workspaceId"] = id, ["provider"] = new[] { "Codex", "Claude", "OpenCode" }[e.Which] }); if (created is not null) { chatId = created["id"]!.GetValue<string>(); workspaceId = id; rendered = ""; drawerContainer.Visibility = ViewStates.Gone; await RefreshDrawer(); } })!.Show()));
            foreach (var chat in result["chats"]!.AsArray().Where(c => c!["workspaceId"]!.GetValue<string>() == id && !c["archived"]!.GetValue<bool>()))
                drawer.AddView(Button(chat!["title"]!.GetValue<string>(), () => { chatId = chat["id"]!.GetValue<string>(); workspaceId = id; rendered = ""; drawerContainer.Visibility = ViewStates.Gone; }));
        }
        if (chatId is null) drawerContainer.Visibility = ViewStates.Visible;
    }
    private async Task Poll(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && connection is not null)
            {
                await Task.Delay(350, token); if (chatId is null) continue; var id = chatId;
                var result = await Call(new() { ["method"] = "chat", ["chatId"] = id }); if (result is null || chatId != id) continue;
                status.Text = result["status"]!.GetValue<string>() + " · " + result["queued"] + " queued";
                var text = result["messages"]!.ToJsonString();
                if (text != rendered)
                {
                    rendered = text; transcript.RemoveAllViews();
                    foreach (var message in result["messages"]!.AsArray())
                    {
                        var role = message!["role"]!.GetValue<string>(); var body = Label(message["text"]!.GetValue<string>(), role is "tool" or "thought");
                        if (role is "tool" or "thought") { body.Visibility = ViewStates.Gone; transcript.AddView(Button(role + " ▾", () => body.Visibility = body.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone)); }
                        if (role is not ("tool" or "thought"))
                        {
                            body.TextFormatted = global::Android.Text.Html.FromHtml(Markdig.Markdown.ToHtml(message["text"]!.GetValue<string>(), new Markdig.MarkdownPipelineBuilder().DisableHtml().Build()), global::Android.Text.FromHtmlOptions.ModeCompact);
                            body.MovementMethod = global::Android.Text.Method.LinkMovementMethod.Instance;
                        }
                        transcript.AddView(body);
                    }
                }
                var pending = result["permissions"]!.ToJsonString();
                if (pending != approvalState)
                {
                    approvalState = pending; approvals.RemoveAllViews();
                    foreach (var permission in result["permissions"]!.AsArray())
                    {
                        approvals.AddView(Label(permission!["toolCall"]!.ToJsonString(), true));
                        foreach (var option in permission["options"]!.AsArray()) approvals.AddView(Button(option!["name"]!.GetValue<string>(), async () => await Call(new() { ["method"] = "approve", ["permissionId"] = permission["id"]!.DeepClone(), ["optionId"] = option["optionId"]!.DeepClone() })));
                    }
                }
                var settings = result["config"]!.ToJsonString();
                if (settings != configState)
                {
                    configState = settings; configs.RemoveAllViews();
                    foreach (var config in result["config"]!.AsArray()) configs.AddView(Button(config!["name"]!.GetValue<string>() + ": " + config["current"], () =>
                    {
                        var values = config["values"]!.AsArray(); new AlertDialog.Builder(this)!.SetItems(values.Select(v => v!["name"]!.GetValue<string>()).ToArray(), async (_, e) => await Call(new() { ["method"] = "config", ["chatId"] = chatId, ["configId"] = config["id"]!.DeepClone(), ["value"] = values[e.Which]!["value"]!.DeepClone() }))!.Show();
                    }));
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    protected override void OnDestroy() { session?.Cancel(); connection?.Dispose(); base.OnDestroy(); }
    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View? view, WindowInsets? insets)
        {
            if (insets is null) return null!;
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) { var bars = insets.GetInsets(WindowInsets.Type.SystemBars()); view?.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom); }
            else view?.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
            return insets;
        }
    }
}
