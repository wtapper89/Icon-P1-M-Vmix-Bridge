using System.Windows.Forms;

namespace IconP1MVmixBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var logger = new FileLogger(AppPaths.LogDirectory);
        Application.ThreadException += (_, e) => logger.Error(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                logger.Error(ex, "Unhandled application exception");
            else
                logger.Error("Unhandled application exception: {0}", e.ExceptionObject);
        };

        Application.Run(new MainForm(logger));
    }
}
