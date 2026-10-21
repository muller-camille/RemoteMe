using System.Diagnostics;
using Microsoft.Extensions.Options;
using RemoteMe.Web.Configuration;

namespace RemoteMe.Web.Environments;

/// <summary>
/// Tâche de fond : expire les environnements inactifs et lance la commande de réinitialisation
/// (fermeture de session, purge du profil) des comptes libérés.
/// </summary>
public sealed class EnvironmentJanitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly EnvironmentPool _pool;
    private readonly IOptions<RemoteMeOptions> _options;
    private readonly ILogger<EnvironmentJanitor> _logger;
    private readonly SemaphoreSlim _resetSlots = new(2, 2);

    public EnvironmentJanitor(EnvironmentPool pool, IOptions<RemoteMeOptions> options, ILogger<EnvironmentJanitor> logger)
    {
        _pool = pool;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            foreach (var account in _pool.Sweep())
            {
                _ = ResetInBackgroundAsync(account, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ResetInBackgroundAsync(PoolAccount account, CancellationToken stoppingToken)
    {
        var success = false;
        try
        {
            await _resetSlots.WaitAsync(stoppingToken);
            try
            {
                success = await RunResetAsync(account, stoppingToken);
            }
            finally
            {
                _resetSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt de l'application.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Réinitialisation de {Account} impossible.", account);
        }
        finally
        {
            _pool.CompleteReset(account, success);
        }
    }

    private async Task<bool> RunResetAsync(PoolAccount account, CancellationToken stoppingToken)
    {
        var options = _options.Value;
        var environments = options.Environments;
        var start = new ProcessStartInfo(environments.ResetCommand!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in environments.ResetArguments)
        {
            start.ArgumentList.Add(argument
                .Replace("{username}", account.Username, StringComparison.Ordinal)
                .Replace("{domain}", account.Domain ?? "", StringComparison.Ordinal)
                .Replace("{hostname}", options.Rdp.Hostname, StringComparison.Ordinal));
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Impossible de lancer {environments.ResetCommand}.");
        var stdout = process.StandardOutput.ReadToEndAsync(stoppingToken);
        var stderr = process.StandardError.ReadToEndAsync(stoppingToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, environments.ResetTimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            _logger.LogError("Réinitialisation de {Account} : délai dépassé.", account);
            return false;
        }

        var output = (await stdout).Trim();
        var errors = (await stderr).Trim();
        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "Réinitialisation de {Account} : code {Code}. {Errors}", account, process.ExitCode, errors.Length > 0 ? errors : output);
            return false;
        }

        _logger.LogInformation("Compte {Account} réinitialisé. {Output}", account, output);
        return true;
    }
}
