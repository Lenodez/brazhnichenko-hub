using System.Windows;

namespace LocalAIHub.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
        {
            string? installDir = GetArgValue(e.Args, "--install-dir");
            string? dataDir = GetArgValue(e.Args, "--data-dir");
            bool shortcuts = !e.Args.Any(a => a.Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase));
            bool registerUninstall = !e.Args.Any(a => a.Equals("--no-registry", StringComparison.OrdinalIgnoreCase));
            bool registerMcp = e.Args.Any(a => a.Equals("--register-mcp", StringComparison.OrdinalIgnoreCase));
            try
            {
                await SetupEngine.InstallAsync(
                    installDir ?? SetupEngine.DefaultInstallDir,
                    dataDir ?? SetupEngine.DefaultDataRoot,
                    createShortcuts: shortcuts,
                    registerUninstall: registerUninstall,
                    registerClaudeMcp: registerMcp,
                    installSviNode: true,
                    onLog: null);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        var win = new MainWindow();
        win.Show();
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
