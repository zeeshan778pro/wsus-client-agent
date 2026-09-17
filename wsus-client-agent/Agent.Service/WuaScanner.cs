using Agent.Shared;

namespace Agent.Service;

/// <summary>
/// All Windows Update Agent COM interop lives here, late-bound (no
/// interop assembly reference needed at build time - wuapi.dll's COM
/// registration is present on every Windows machine, so this works
/// without depending on a type library being available on whatever
/// machine builds this project). Same technique the existing
/// Push-WSUSPatchAction.ps1 PowerShell script already uses via
/// New-Object -ComObject; this is the C# equivalent.
///
/// Machine-wide "point Windows Update at WSUS" policy (WUServer/
/// UseWUServer under HKLM\SOFTWARE\Policies\Microsoft\Windows\
/// WindowsUpdate) is what makes CreateUpdateSearcher() resolve against
/// WSUS instead of Microsoft's public Windows Update - that policy is
/// applied once by install.ps1, not by this class.
/// </summary>
public class WuaScanner
{
    /// <summary>
    /// WUA's installer (and some searcher paths) can throw
    /// apartment-related COM errors off a plain thread-pool thread -
    /// running everything on one dedicated STA thread sidesteps that
    /// entirely, matching how an interactive script host (which is
    /// always STA) would call the same API.
    /// </summary>
    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }

    public List<UpdateRecord> Scan()
    {
        return RunOnSta(() =>
        {
            var results = new List<UpdateRecord>();
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0");
            dynamic updates = searchResult.Updates;

            int count = updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = updates.Item(i);
                string? kb = null;
                dynamic kbColl = u.KBArticleIDs;
                if (kbColl.Count > 0) kb = "KB" + (string)kbColl.Item(0);

                results.Add(new UpdateRecord
                {
                    UpdateId = (string)u.Identity.UpdateID,
                    KbArticle = kb,
                    Title = (string)u.Title,
                    Severity = (string?)u.MsrcSeverity,
                    SizeMb = Math.Round((double)u.MaxDownloadSize / 1024.0 / 1024.0, 1),
                    Status = (bool)u.IsDownloaded ? "ReadyToInstall" : "Pending",
                    DiscoveredAt = DateTime.UtcNow,
                });
            }
            return results;
        });
    }

    /// <summary>
    /// Downloads then installs the given update IDs (or every pending
    /// one if null), reporting live per-update status into the shared
    /// local DB as it goes so the tray UI reflects real progress
    /// instead of just "Installing..." for the whole batch at once.
    /// Returns whether a reboot is now required.
    /// </summary>
    public bool DownloadAndInstall(LocalDb db, IEnumerable<string>? onlyUpdateIds)
    {
        return RunOnSta(() =>
        {
            var idFilter = onlyUpdateIds?.ToHashSet();

            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0");
            dynamic allUpdates = searchResult.Updates;

            dynamic toDownload = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            var matched = new List<dynamic>();
            var downloadIds = new List<string>();
            int count = allUpdates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = allUpdates.Item(i);
                string updateId = (string)u.Identity.UpdateID;
                if (idFilter != null && !idFilter.Contains(updateId)) continue;

                matched.Add(u);
                if (!(bool)u.IsDownloaded)
                {
                    u.AcceptEula();
                    toDownload.Add(u);
                    downloadIds.Add(updateId);
                }
            }

            if (toDownload.Count > 0)
            {
                foreach (var id in downloadIds) db.SetUpdateStatus(id, "Downloading");
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = toDownload;
                dynamic downloadResult = downloader.Download();
                // ResultCode 2 == orcSucceeded (Microsoft.Update.ResultCode) -
                // anything else means at least one update in the batch failed
                // to download; per-update detail isn't broken out here, the
                // subsequent install attempt (skipped for anything still not
                // downloaded) is what actually surfaces which one(s) failed.
                _ = downloadResult;
            }

            dynamic toInstall = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            foreach (var u in matched)
            {
                if ((bool)u.IsDownloaded) toInstall.Add(u);
                else db.SetUpdateStatus((string)u.Identity.UpdateID, "Failed", "Download did not complete");
            }

            bool rebootRequired = false;
            if (toInstall.Count > 0)
            {
                foreach (var u in matched) if ((bool)u.IsDownloaded) db.SetUpdateStatus((string)u.Identity.UpdateID, "Installing");

                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = toInstall;
                installer.ForceQuiet = true;   // no WUA UI/prompts - this agent's own tray is the only UI
                dynamic installResult = installer.Install();
                rebootRequired = (bool)installResult.RebootRequired;

                for (int i = 0; i < toInstall.Count; i++)
                {
                    dynamic u = toInstall.Item(i);
                    string updateId = (string)u.Identity.UpdateID;
                    dynamic perUpdateResult = installResult.GetUpdateResult(i);
                    int resultCode = (int)perUpdateResult.ResultCode;
                    // orcSucceeded = 2, orcSucceededWithErrors = 3 - both count
                    // as installed for our purposes; anything else is a failure.
                    if (resultCode == 2 || resultCode == 3)
                        db.SetUpdateStatus(updateId, "Installed");
                    else
                        db.SetUpdateStatus(updateId, "Failed", $"WUA result code {resultCode}");
                }
            }

            return rebootRequired;
        });
    }
}
