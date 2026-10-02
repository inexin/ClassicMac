using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace ClassicMac.App.Tests;

// One headless Avalonia session for the test process, whose own thread is the UI thread; every UI test runs on it.
internal static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static void OnUiThread(Action test) => Session.Value.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    // The app, drawn with Skia (not the headless stub drawing), with the app's own fonts.
    private static class HeadlessApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithAppFonts();
    }

    // The view-model continues on Avalonia's dispatcher, which a test must pump itself.
    public static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(task.IsCompleted, "timed out");
        Dispatcher.UIThread.RunJobs();
    }
}
