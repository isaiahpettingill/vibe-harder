namespace CodexManager.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void OrdinaryErrorsRecoverButResourceAndMemoryCorruptionFailuresDoNot()
    {
        Assert.False(AppDiagnostics.IsUnrecoverable(new InvalidOperationException()));
        Assert.False(AppDiagnostics.IsUnrecoverable(new IOException()));
        Assert.False(AppDiagnostics.IsUnrecoverable(new OperationCanceledException()));
        Assert.True(AppDiagnostics.IsUnrecoverable(new OutOfMemoryException()));
        Assert.True(AppDiagnostics.IsUnrecoverable(new AccessViolationException()));
        Assert.True(AppDiagnostics.IsUnrecoverable(new AggregateException(new OutOfMemoryException())));
    }
    [Fact]
    public void ErrorLogsRotateAndCapIndividualEntries()
    {
        var directory = Directory.CreateTempSubdirectory("diagnostics-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "errors.log"), new string('x', 512 * 1024));
            AppDiagnostics.Record("test failure", new IOException(new string('y', 50 * 1024)), directory);
            Assert.True(File.Exists(Path.Combine(directory, "errors.previous.log")));
            var text = File.ReadAllText(Path.Combine(directory, "errors.log"));
            Assert.Contains("test failure", text); Assert.Contains("IOException", text); Assert.Contains("[truncated]", text); Assert.True(text.Length < 18 * 1024);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void AnUnwritableLogLocationDoesNotThrowAnotherException()
    {
        var file = Path.GetTempFileName();
        try { AppDiagnostics.Record("failure", new IOException("original failure"), file); }
        finally { File.Delete(file); }
    }
}
