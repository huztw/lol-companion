using System;
using System.Windows.Forms;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Pairing;

namespace LoLCompanion.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var options = CompanionAppOptions.Load();
        using var httpClient = new HttpClient
        {
            BaseAddress = options.ApiBaseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };

        var apiClient = new CompanionApiClient(httpClient);
        var sessionManager = new InMemoryCompanionSessionManager();
        var pairingController = new CompanionPairingController(apiClient, sessionManager);

        Application.Run(new MainForm(sessionManager, pairingController));
    }
}
