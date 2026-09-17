using Agent.Shared;
using Microsoft.Extensions.Hosting;

namespace Agent.Service;

/// <summary>
/// Two independent cadences in one loop: a full WSUS scan on a long
/// interval (default 4 hours - configurable via appsettings.json, not
/// meant to be aggressive since it's a full WUA search), and a short
/// poll (15s) for commands the tray app has queued (Install Now / Scan
/// Now) so clicking a button in the tray doesn't feel like it waits
/// for the next scheduled scan.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly LocalDb _db;
    private readonly WuaScanner _scanner;
    private readonly TimeSpan _scanInterval;
    private DateTime _lastScanAt = DateTime.MinValue;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _db = new LocalDb();
        _scanner = new WuaScanner();
        var hours = config.GetValue<double?>("Agent:ScanIntervalHours") ?? 4.0;
        _scanInterval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ProcessPendingCommands();

                if (DateTime.UtcNow - _lastScanAt >= _scanInterval)
                {
                    RunScan();
                    _lastScanAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop iteration failed");
                _db.UpdateStatus(s => s.LastError.Set(ex.Message));
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private void RunScan()
    {
        _logger.LogInformation("Starting WSUS scan");
        _db.UpdateStatus(s => s.ScanInProgress.Set(true));
        try
        {
            var found = _scanner.Scan();
            _db.ReplaceUpdates(found);
            _db.UpdateStatus(s =>
            {
                s.LastScanAt.Set(DateTime.UtcNow);
                s.LastError.Set(null);
            });
            _logger.LogInformation("Scan complete - {Count} update(s) pending", found.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            _db.UpdateStatus(s => s.LastError.Set(ex.Message));
        }
        finally
        {
            _db.UpdateStatus(s => s.ScanInProgress.Set(false));
        }
    }

    private void ProcessPendingCommands()
    {
        foreach (var cmd in _db.GetUnprocessedCommands())
        {
            _logger.LogInformation("Processing command {Action} ({Id})", cmd.Action, cmd.Id);
            try
            {
                switch (cmd.Action)
                {
                    case "ScanNow":
                        RunScan();
                        _lastScanAt = DateTime.UtcNow;
                        break;

                    case "InstallAll":
                        RunInstall(null);
                        break;

                    case "InstallSelected":
                        var ids = (cmd.UpdateIdsCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        RunInstall(ids);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command {Action} ({Id}) failed", cmd.Action, cmd.Id);
                _db.UpdateStatus(s => s.LastError.Set(ex.Message));
            }
            finally
            {
                _db.MarkCommandProcessed(cmd.Id);
            }
        }
    }

    private void RunInstall(IEnumerable<string>? onlyUpdateIds)
    {
        _db.UpdateStatus(s => s.InstallInProgress.Set(true));
        try
        {
            bool rebootRequired = _scanner.DownloadAndInstall(_db, onlyUpdateIds);
            _db.UpdateStatus(s =>
            {
                s.PendingReboot.Set(rebootRequired);
                s.LastError.Set(null);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install failed");
            _db.UpdateStatus(s => s.LastError.Set(ex.Message));
        }
        finally
        {
            _db.UpdateStatus(s => s.InstallInProgress.Set(false));
        }
    }
}
