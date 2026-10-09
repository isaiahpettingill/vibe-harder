using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace CodexManager.Tests;

public class SpeechInputTests
{
    // Draft with | at the caret, what was said, and the draft afterwards.
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("|", "hello there", "hello there|")]
    [InlineData("fix the|", "login bug", "fix the login bug|")]
    [InlineData("fix the |", "login bug", "fix the login bug|")]
    [InlineData("|please", "  fix it  ", "fix it| please")]
    [InlineData("first| last", "middle", "first middle| last")]
    [InlineData("line one\n|", "line two", "line one\nline two|")]
    [InlineData("keep|", "   ", "keep|")]
    public void DictationJoinsTheDraftAtTheCaretWithSingleSpaces(string before, string spoken, string after)
    {
        var caret = before.IndexOf('|'); var draft = before.Replace("|", "");
        var insertion = SpeechInput.Insertion(draft, caret, spoken);
        var result = draft[..caret] + insertion + draft[caret..];
        Assert.Equal(after.Replace("|", ""), result);
        Assert.Equal(after.IndexOf('|'), caret + insertion.TrimEnd().Length);
    }

    private sealed class FakeSpeech(string? said, bool stopsOnTap) : ISpeechInput
    {
        public bool Ready => true;
        public bool StopsOnTap => stopsOnTap;
        public int Listens { get; private set; }
        public async Task<string?> Listen(CancellationToken stop)
        {
            Listens++;
            if (stopsOnTap) { try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { } }
            return said;
        }
    }

    // A recognizer returns text, a recording returns it after the second tap, and system
    // dictation (null) types into the field by itself, so the draft is left alone.
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("add tests", false, "Refactor add tests")]
    [InlineData("add tests", true, "Refactor add tests")]
    [InlineData(null, false, "Refactor")]
    public async Task MicrophoneButtonPutsWhatWasSaidIntoTheDraft(string? said, bool stopsOnTap, string expected)
    {
        var previous = SpeechInput.Current;
        var speech = new FakeSpeech(said, stopsOnTap);
        SpeechInput.Current = speech;
        var composer = new ChatComposer(remote: false, "Message");
        var window = new Window { Content = composer }; window.Show();
        try
        {
            composer.Editor.Text = "Refactor";
            Assert.True(composer.SpeakButton.IsVisible);
            composer.SpeakButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (stopsOnTap)
            {
                await Task.Delay(20); Assert.Equal("Refactor", composer.Editor.Text);
                composer.SpeakButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            var until = DateTime.UtcNow.AddSeconds(5);
            while (composer.Editor.Text != expected && DateTime.UtcNow < until) await Task.Delay(10);
            await Task.Delay(20);
            Assert.Equal(expected, composer.Editor.Text);
            Assert.Equal(1, speech.Listens);
        }
        finally { window.Close(); SpeechInput.Current = previous; }
    }
}
