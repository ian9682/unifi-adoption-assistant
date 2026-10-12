using UniFiAdoption.Core;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Inventory;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpected(e.Exception);

        // "--test" starts in test mode, so the ITO Networks Team can hand out a shortcut that never changes a device.
        var forceTestMode = args.Any(a =>
            string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/test", StringComparison.OrdinalIgnoreCase));

        var settings = SettingsLoader.Load();
        var log = DiagnosticLog.CreateDefault();
        var hostKeys = new JsonHostKeyStore(JsonHostKeyStore.DefaultPath, settings.Settings.PinnedHostKeys);
        var credentials = new DpapiCredentialStore();
        var dependencies = new WorkflowDependencies(
            new SystemLocalNetworkProvider(),
            new UbntDiscoveryScanner(),
            Environment.OSVersion.Platform == PlatformID.Win32NT ? new WindowsArpScanner() : new NullNeighborScanner(),
            new NetworkControllerProbe(),
            new TcpSshBannerProbe(),
            new SshNetConnector(),
            hostKeys,
            credentials);

        using var host = new AssistantHost(settings.Settings, dependencies, log);
        Application.Run(new MainForm(settings, host, credentials, hostKeys, forceTestMode));
    }

    private static void ShowUnexpected(Exception exception) =>
        MessageBox.Show(
            $"Something unexpected went wrong in the assistant.\n\n{exception.GetType().Name}: {exception.Message}\n\nPlease tell the ITO Networks Team.",
            "UniFi Adoption Assistant – One Acre Fund",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
}
