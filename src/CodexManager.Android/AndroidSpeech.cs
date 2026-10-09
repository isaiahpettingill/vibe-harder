using Android.App;
using Android.Content;
using Android.Speech;

namespace CodexManager.Android;

// The system speech recognizer screen. It holds the microphone permission itself, so the app
// needs none, and returns what was said for the composer to insert.
internal sealed class AndroidSpeech(Activity activity) : ISpeechInput
{
    internal const int Request = 9305;
    private TaskCompletionSource<string?>? pending;
    private IDisposable? appLockPause;
    private static Intent Recognize() => new Intent(RecognizerIntent.ActionRecognizeSpeech)
        .PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm)
        .PutExtra(RecognizerIntent.ExtraPartialResults, false);
    public bool Ready => activity.PackageManager?.QueryIntentActivities(Recognize(), 0).Count > 0;
    public bool StopsOnTap => false;

    public Task<string?> Listen(CancellationToken stop)
    {
        Finish(null);
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Leaving for the recognizer must not lock the app.
        appLockPause = MobileAppSecurity.BeginFilePicker();
        try { activity.StartActivityForResult(Recognize(), Request); }
        catch (ActivityNotFoundException) { Finish(null); throw new IOException("No speech recognizer is installed."); }
        return pending.Task;
    }
    public void Result(Result result, Intent? data) =>
        Finish(result == global::Android.App.Result.Ok ? data?.GetStringArrayListExtra(RecognizerIntent.ExtraResults)?.FirstOrDefault() : null);
    private void Finish(string? text)
    {
        appLockPause?.Dispose(); appLockPause = null;
        var waiting = pending; pending = null; waiting?.TrySetResult(text);
    }
}
