using System.Diagnostics;
using System.Runtime.InteropServices;
using Whisper.net;

namespace CodexManager;

internal static class DesktopSpeech
{
    public static ISpeechInput? Create() =>
        OperatingSystem.IsWindows() ? new WindowsVoiceTyping()
        : OperatingSystem.IsMacOS() ? new MacDictation()
        : OperatingSystem.IsLinux() ? new WhisperSpeech()
        : null;
}

// Windows voice typing (Win+H) types into the focused field itself.
internal sealed class WindowsVoiceTyping : ISpeechInput
{
    public bool Ready => true;
    public bool StopsOnTap => false;
    public Task<string?> Listen(CancellationToken stop)
    {
        const ushort win = 0x5B, h = 0x48; const uint up = 2;
        Input Key(ushort key, uint flags) => new() { Type = 1, Keyboard = new() { Key = key, Flags = flags } };
        Input[] keys = [Key(win, 0), Key(h, 0), Key(h, up), Key(win, up)];
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length) throw new IOException("Windows did not accept the voice typing shortcut.");
        return Task.FromResult<string?>(null);
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
    // INPUT is a union sized for MOUSEINPUT; pad the keyboard variant to match.
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public KeyboardInput Keyboard; public long Padding; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}

// macOS dictation inserts into the first responder, the focused composer.
internal sealed class MacDictation : ISpeechInput
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    public bool Ready => true;
    public bool StopsOnTap => false;
    public Task<string?> Listen(CancellationToken stop)
    {
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        Send(app, sel_registerName("startDictation:"), 0);
        return Task.FromResult<string?>(null);
    }
    [DllImport(ObjC)] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(ObjC)] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Send(nint receiver, nint selector, nint argument);
}

// Linux has no system dictation service: record from the default microphone and transcribe
// locally with Whisper. The model is downloaded only when the user asks for it in settings.
internal sealed class WhisperSpeech : ISpeechInput, ISpeechModel
{
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static string ModelPath => Path.Combine(Store.DataDirectory, "speech", "ggml-base.en.bin");
    public string Description => "English speech model (Whisper base, 148 MB)";
    public bool Downloaded => File.Exists(ModelPath);
    public bool Ready => Downloaded;
    public bool StopsOnTap => true;

    public async Task Download(IProgress<double> progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".part";
        using (var response = await Http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(partial);
            var buffer = new byte[1 << 16]; long read = 0; int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), token); read += count;
                if (total > 0) progress.Report((double)read / total.Value);
            }
        }
        File.Move(partial, ModelPath, overwrite: true);
        SpeechInput.Refresh();
    }
    public void Remove() { File.Delete(ModelPath); SpeechInput.Refresh(); }

    public async Task<string?> Listen(CancellationToken stop)
    {
        // 16 kHz mono 16-bit samples, what Whisper expects. PulseAudio and PipeWire provide
        // parecord; plain ALSA systems have arecord.
        var recorder = Recorder() ?? throw new IOException("Install parecord (pulseaudio-utils) or arecord (alsa-utils) to record speech.");
        using var process = Process.Start(new ProcessStartInfo(recorder.File, recorder.Arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })
            ?? throw new IOException("Could not start " + recorder.File + ".");
        using var audio = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(audio, CancellationToken.None);
        try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
        try { process.Kill(); } catch (InvalidOperationException) { }
        await copy;
        var bytes = audio.ToArray();
        if (bytes.Length < 3200) return null;
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return await Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(ModelPath);
            await using var processor = factory.CreateBuilder().WithLanguage("en").Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples)) text.Append(segment.Text);
            return text.ToString().Trim();
        });
    }

    private static (string File, string Arguments)? Recorder()
    {
        foreach (var (file, arguments) in new[] { ("parecord", "--raw --format=s16le --rate=16000 --channels=1"), ("arecord", "-q -f S16_LE -r 16000 -c 1 -t raw") })
            if (Environment.GetEnvironmentVariable("PATH")?.Split(':').Any(directory => File.Exists(Path.Combine(directory, file))) == true) return (file, arguments);
        return null;
    }
}
