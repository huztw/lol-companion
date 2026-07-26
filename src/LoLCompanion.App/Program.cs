using System;
using System.Windows.Forms;
using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Pairing;
using LoLCompanion.Core.Lcu;

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
        var lockfileDiscovery = new LcuLockfileDiscovery(new SystemLcuFileSystem(), new LeagueClientProcessLocator());
        var lcuClientFactory = new LcuHttpClientFactory(TimeSpan.FromSeconds(10));
        var lcuAdapter = new LcuLeagueClientAdapter(lockfileDiscovery, lcuClientFactory);
        var analysisWorkflow = new CompanionAnalysisWorkflow(
            new LcuAnalysisSourceAdapter(lcuAdapter),
            apiClient,
            sessionManager,
            new CompanionAnalysisNormalizer());

        Application.Run(new MainForm(
            sessionManager,
            pairingController,
            lcuAdapter.GetRecentMatchesAsync,
            analysisWorkflow.AnalyzeSelectedMatchAsync));
    }

    private sealed class LcuAnalysisSourceAdapter : ICompanionLeagueAnalysisSource
    {
        private readonly LcuLeagueClientAdapter _adapter;

        public LcuAnalysisSourceAdapter(LcuLeagueClientAdapter adapter)
        {
            _adapter = adapter;
        }

        public Task<LcuCurrentSummoner> GetCurrentSummonerAsync(CancellationToken cancellationToken = default) =>
            _adapter.GetCurrentSummonerAsync(cancellationToken);

        public Task<LcuMatchDetailDto> GetMatchDetailAsync(long gameId, CancellationToken cancellationToken = default) =>
            _adapter.GetMatchDetailAsync(gameId, cancellationToken);

        public Task<LcuTimelineResult> GetTimelineAsync(long gameId, CancellationToken cancellationToken = default) =>
            _adapter.GetTimelineAsync(gameId, cancellationToken);
    }
}
