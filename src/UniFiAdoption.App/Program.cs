using UniFiAdoption.Core.Configuration;

namespace UniFiAdoption.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpected(e.Exception);

        // "--test" starts in test mode, so IT can send staff a shortcut that never changes a device.
        var forceTestMode = args.Any(a =>
            string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/test", StringComparison.OrdinalIgnoreCase));

        var settings = SettingsLoader.Load();
        Application.Run(new MainForm(settings, forceTestMode));
    }

    private static void ShowUnexpected(Exception exception) =>
        MessageBox.Show(
            $"Something unexpected went wrong in the assistant.\n\n{exception.GetType().Name}: {exception.Message}\n\nPlease tell the ITO Networks Team.",
            "UniFi Adoption Assistant – One Acre Fund",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
}
