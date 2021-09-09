﻿using System;
using System.Diagnostics;
using System.IO;
using CompatibilityReport.CatalogData;
using CompatibilityReport.Util;


// WebCrawler gathers information from the Steam Workshop pages for all mods and updates the catalog with this. This process takes quite some time (roughly 15 minutes).
// The following information is gathered:
// * Mod: name, author, publish and update dates, source url (GitHub only), compatible game version (from tag only), required DLC, required mods, incompatible stability
//        statuses: removed from workshop, unlisted in workshop, no description
// * Author: name, Steam ID and Custom url, last seen date (based on mod updates, not on comments), retired status (no mod update in x months; removed on new mod update)


namespace CompatibilityReport.Updater
{
    internal static class WebCrawler
    {
        // Start the WebCrawler. Download Steam webpages for all mods and updates the catalog with found information.
        internal static void Start(Catalog catalog)
        {
            CatalogUpdater.SetReviewDate(DateTime.Now);

            // Get basic mod and author information from the Steam Workshop 'mod listing' pages
            if (GetBasicInfo(catalog))
            {
                // Get more details from the individual mod pages
                GetDetails(catalog);
            }
        }
        

        // Get mod and author names and IDs from the Steam Workshop 'mod listing' pages and removes unlisted/removed statuses. Returns true if we found at least one mod.
        private static bool GetBasicInfo(Catalog catalog)
        {
            Stopwatch timer = Stopwatch.StartNew();

            Logger.UpdaterLog("Updater started downloading Steam Workshop 'mod listing' pages. This should take less than 1 minute.");

            int totalMods = 0;

            int totalPages = 0;
            
            // Go through the different mod listings: mods and camera scripts, both regular and incompatible
            foreach (string steamURL in ModSettings.SteamModListingURLs)
            {
                Logger.UpdaterLog($"Starting downloads from { steamURL }");
                
                int pageNumber = 0;

                // Download and read pages until we find no more mods, or we reach a maximum number of pages (to avoid missing the mark and continuing for eternity)
                while (pageNumber < ModSettings.SteamMaxModListingPages)
                {
                    pageNumber++;

                    string url = $"{ steamURL }&p={ pageNumber }";

                    if (!Toolkit.Download(url, ModSettings.TempDownloadFullPath))
                    {
                        Logger.UpdaterLog($"Download process interrupted due to a permanent error while downloading { url }", Logger.Error);

                        // Decrease the pageNumber to the last succesful page
                        pageNumber--;
                        break;
                    }

                    // Extract mod and author info from the downloaded page
                    int modsFoundThisPage = ReadModListingPage(catalog, steamURL.Contains("incompatible"));

                    if (modsFoundThisPage == 0)
                    {
                        // No mods found on this page; decrease the page number to the last succesful page
                        pageNumber--;

                        // Log something if no mods were found at all
                        if (pageNumber == 0)
                        {
                            Logger.UpdaterLog("Found no mods on page 1.");
                        }

                        break;
                    }

                    totalMods += modsFoundThisPage;

                    Logger.UpdaterLog($"Found { modsFoundThisPage } mods on page { pageNumber }.");
                }

                totalPages += pageNumber;
            }

            // Delete the temporary file
            Toolkit.DeleteFile(ModSettings.TempDownloadFullPath);

            // Note: about 75% of the total time is downloading, the other 25% is processing
            timer.Stop();

            Logger.UpdaterLog($"Updater finished downloading { totalPages } Steam Workshop 'mod listing' pages in " +
                $"{ Toolkit.TimeString(timer.ElapsedMilliseconds) }. { totalMods } mods found.");

            return totalMods > 0;
        }


        // Extract mod and author info from the downloaded mod listing page. Returns the number of mods found on this page.
        private static int ReadModListingPage(Catalog catalog, bool incompatibleMods)
        {
            int modsFoundThisPage = 0;

            string line;

            // Read the downloaded file
            using (StreamReader reader = File.OpenText(ModSettings.TempDownloadFullPath))
            {
                // Read all the lines until the end of the file
                while ((line = reader.ReadLine()) != null)
                {
                    // Search for the identifying string for the next mod; continue with next line if not found
                    if (!line.Contains(ModSettings.SteamModListingModFind))
                    {
                        continue;
                    }

                    // Found the identifying string; get the Steam ID
                    ulong steamID = Toolkit.ConvertToUlong(Toolkit.MidString(line, ModSettings.SteamModListingModIDLeft, ModSettings.SteamModListingModIDRight));

                    if (steamID == 0) 
                    {
                        // Steam ID was not recognized. This should not happen. Continue with the next line.
                        Logger.UpdaterLog("Steam ID not recognized on HTML line: " + line, Logger.Error);

                        continue;
                    }

                    modsFoundThisPage++;

                    string modName = Toolkit.CleanHtml(Toolkit.MidString(line, ModSettings.SteamModListingModNameLeft, ModSettings.SteamModListingModNameRight));

                    Mod catalogMod = catalog.GetMod(steamID) ?? CatalogUpdater.AddMod(catalog, steamID, modName, incompatibleMods);

                    // (Re)set incompatible stability on existing mods, if it changed in the Steam Workshop
                    if (incompatibleMods && catalogMod.Stability != Enums.Stability.IncompatibleAccordingToWorkshop)
                    {
                        CatalogUpdater.UpdateMod(catalog, catalogMod, stability: Enums.Stability.IncompatibleAccordingToWorkshop, updatedByWebCrawler: true);
                    }
                    else if (!incompatibleMods && catalogMod.Stability == Enums.Stability.IncompatibleAccordingToWorkshop)
                    {
                        CatalogUpdater.UpdateMod(catalog, catalogMod, stability: Enums.Stability.NotReviewed, updatedByWebCrawler: true);
                    }

                    // Skip one line
                    line = reader.ReadLine();

                    // Get the author ID or custom URL. One will be found, the other will be zero / empty
                    // Todo 0.4 Add a check for author URL changes, to prevent creating a new author.
                    ulong authorID = Toolkit.ConvertToUlong(Toolkit.MidString(line, ModSettings.SteamModListingAuthorIDLeft, ModSettings.SteamModListingAuthorRight));

                    string authorURL = Toolkit.MidString(line, ModSettings.SteamModListingAuthorURLLeft, ModSettings.SteamModListingAuthorRight);

                    // Remove the removed and unlisted statuses, if they exist
                    CatalogUpdater.RemoveStatus(catalogMod, Enums.Status.RemovedFromWorkshop, updatedByWebCrawler: true);

                    CatalogUpdater.RemoveStatus(catalogMod, Enums.Status.UnlistedInWorkshop, updatedByWebCrawler: true);

                    // Update the mod. This will also set the UpdatedThisSession, which is used in GetDetails()
                    CatalogUpdater.UpdateMod(catalog, catalogMod, modName, authorID: authorID, authorURL: authorURL, alwaysUpdateReviewDate: true, updatedByWebCrawler: true);

                    string authorName = Toolkit.CleanHtml(Toolkit.MidString(line, ModSettings.SteamModListingAuthorNameLeft, ModSettings.SteamModListingAuthorNameRight));

                    CatalogUpdater.GetOrAddAuthor(catalog, authorID, authorURL, authorName);
                }
            }

            return modsFoundThisPage;
        }


        // Get mod information from the individual mod pages on the Steam Workshop
        private static void GetDetails(Catalog catalog)
        {
            Stopwatch timer = Stopwatch.StartNew();

            int numberOfMods = catalog.Mods.Count - ModSettings.BuiltinMods.Count;

            // Estimated time is about half a second (500 milliseconds) per download. Note: 90+% of the total time is download, less than 10% is processing
            long estimated = 500 * numberOfMods;

            Logger.UpdaterLog($"Updater started downloading { numberOfMods } individual Steam Workshop mod pages. Estimated time: { Toolkit.TimeString(estimated) }.");

            int modsDownloaded = 0;

            int failedDownloads = 0;

            foreach (Mod catalogMod in catalog.Mods)
            {
                if (!catalog.IsValidID(catalogMod.SteamID, allowBuiltin: false))
                {
                    // Skip builtin mods
                    continue;
                }

                // Download the Steam Workshop page for this mod
                if (!Toolkit.Download(Toolkit.GetWorkshopUrl(catalogMod.SteamID), ModSettings.TempDownloadFullPath))
                {
                    // Download error
                    failedDownloads++;

                    if (failedDownloads <= ModSettings.SteamMaxFailedPages)
                    {
                        // Download error might be mod specific. Go to the next mod.
                        Logger.UpdaterLog("Permanent error while downloading Steam Workshop page for { catalogMod.ToString() }. Will continue with next mod.", 
                            Logger.Error);

                        continue;
                    }
                    else
                    {
                        // Too many failed downloads. Stop downloading
                        Logger.UpdaterLog("Permanent error while downloading Steam Workshop page for { catalogMod.ToString() }. Download process stopped.", 
                            Logger.Error);

                        break;
                    }
                }

                modsDownloaded++;

                // Log a sign of life every 100 mods
                if (modsDownloaded % 100 == 0)
                {
                    Logger.UpdaterLog($"{ modsDownloaded }/{ numberOfMods } mod pages downloaded.");
                }

                // Extract detailed info from the downloaded page
                if (!ReadModPage(catalog, catalogMod))
                {
                    // Redownload and try again, to work around cut-off downloads
                    Toolkit.Download(Toolkit.GetWorkshopUrl(catalogMod.SteamID), ModSettings.TempDownloadFullPath);

                    ReadModPage(catalog, catalogMod);
                }
            }

            // Delete the temporary file
            Toolkit.DeleteFile(ModSettings.TempDownloadFullPath);

            // Note: about 90% of the total time is downloading, the other 10% is processing
            timer.Stop();

            Logger.UpdaterLog($"Updater finished downloading { modsDownloaded } individual Steam Workshop mod pages in " + 
                $"{ Toolkit.TimeString(timer.ElapsedMilliseconds, alwaysShowSeconds: true) }.");

            Logger.Log($"Updater processed { modsDownloaded } Steam Workshop mod pages.");
        }


        // Extract detailed mod information from the downloaded mod page; return false if there was an error with the mod page
        private static bool ReadModPage(Catalog catalog, Mod catalogMod)
        {
            bool steamIDmatched = false;

            // Read the downloaded page back from file
            using (StreamReader reader = File.OpenText(ModSettings.TempDownloadFullPath))
            {
                string line;

                // Read all the lines until the end of the file
                while ((line = reader.ReadLine()) != null)
                {
                    // First find the correct Steam ID on this page; it appears before all other info
                    if (!steamIDmatched)
                    {
                        if (line.Contains(ModSettings.SteamModPageItemNotFound))
                        {
                            // Steam says it can't find the mod, stop processing the page further
                            if (catalogMod.UpdatedThisSession)
                            {
                                // We found the mod in the mod listing, but not now. Must be a Steam error.
                                Logger.UpdaterLog($"We found this mod, but can't read the Steam page for { catalogMod.ToString() }. Mod info not updated.", Logger.Error);

                                // Return false to trigger a retry on the download
                                return false;
                            }
                            else
                            {
                                // Change the mod to removed
                                CatalogUpdater.AddStatus(catalogMod, Enums.Status.RemovedFromWorkshop, updatedByWebCrawler: true);

                                // Return true because no retry on download is needed
                                return true;
                            }
                        }

                        steamIDmatched = line.Contains(ModSettings.SteamModPageSteamID + catalogMod.SteamID.ToString());

                        // Keep trying to find the Steam ID before anything else
                        continue;
                    }

                    // Update removed and unlisted statuses: no longer removed and only unlisted if not found during GetBasicInfo()
                    CatalogUpdater.RemoveStatus(catalogMod, Enums.Status.RemovedFromWorkshop);

                    if (!catalogMod.UpdatedThisSession)
                    {
                        CatalogUpdater.AddStatus(catalogMod, Enums.Status.UnlistedInWorkshop, updatedByWebCrawler: true);
                    }

                    // Try to find data on this line of the mod page

                    // Author Steam ID, Custom URL and author name; only for unlisted mods (we have this info for other mods already)
                    // Todo 0.4 Add a check for author URL changes, to prevent creating a new author.
                    if (line.Contains(ModSettings.SteamModPageAuthorFind) && catalogMod.Statuses.Contains(Enums.Status.UnlistedInWorkshop))
                    {
                        ulong authorID = Toolkit.ConvertToUlong(Toolkit.MidString(line, ModSettings.SteamModPageAuthorFind + "profiles/",
                            ModSettings.SteamModPageAuthorMid));

                        string authorURL = Toolkit.MidString(line, ModSettings.SteamModPageAuthorFind + "id/", ModSettings.SteamModPageAuthorMid);

                        // Empty the author custom URL if author ID was found or if custom URL was not found, preventing updating the custom URL to an empty string
                        authorURL = authorID != 0 || string.IsNullOrEmpty(authorURL) ? null : authorURL;

                        string authorName = Toolkit.CleanHtml(Toolkit.MidString(line, ModSettings.SteamModPageAuthorMid, ModSettings.SteamModPageAuthorRight));

                        catalogMod.Update(authorID: authorID, authorUrl: authorURL);

                        CatalogUpdater.GetOrAddAuthor(catalog, authorID, authorURL, authorName);
                    }

                    // Mod name; only for unlisted mods (we have this info for other mods already)
                    else if (line.Contains(ModSettings.SteamModPageNameLeft) && catalogMod.Statuses.Contains(Enums.Status.UnlistedInWorkshop))
                    {
                        string modName = Toolkit.CleanHtml(Toolkit.MidString(line, ModSettings.SteamModPageNameLeft, ModSettings.SteamModPageNameRight)); 

                        CatalogUpdater.UpdateMod(catalog, catalogMod, modName , updatedByWebCrawler: true);
                    }

                    // Compatible game version tag
                    else if (line.Contains(ModSettings.SteamModPageVersionTagFind))
                    {
                        // Convert the found tag to a game version and back to a formatted game version string, so we have a consistently formatted string
                        string gameVersionString = Toolkit.MidString(line, ModSettings.SteamModPageVersionTagLeft, ModSettings.SteamModPageVersionTagRight);

                        Version gameVersion = Toolkit.ConvertToGameVersion(gameVersionString);

                        gameVersionString = Toolkit.ConvertGameVersionToString(gameVersion);

                        // Update the mod, unless an exclusion exists and the found gameversion is lower than in the catalog. Remove the exclusion on update.
                        if (!catalogMod.ExclusionForGameVersion || gameVersion >= catalogMod.CompatibleGameVersion())
                        {
                            CatalogUpdater.UpdateMod(catalog, catalogMod, compatibleGameVersionString: gameVersionString, updatedByWebCrawler: true);

                            catalogMod.UpdateExclusions(exclusionForGameVersion: false);
                        }
                    }

                    // Publish and update dates. Also update author last seen date.
                    else if (line.Contains(ModSettings.SteamModPageDatesFind))
                    {
                        // Skip two lines for the published data, then one more for the update date (if available)
                        line = reader.ReadLine();
                        line = reader.ReadLine();

                        DateTime published = Toolkit.ConvertWorkshopDateTime(Toolkit.MidString(line, ModSettings.SteamModPageDatesLeft, ModSettings.SteamModPageDatesRight));

                        line = reader.ReadLine();

                        DateTime updated = Toolkit.ConvertWorkshopDateTime(Toolkit.MidString(line, ModSettings.SteamModPageDatesLeft, ModSettings.SteamModPageDatesRight));

                        CatalogUpdater.UpdateMod(catalog, catalogMod, published: published, updated: updated, updatedByWebCrawler: true);
                    }

                    // Required DLC. This line can be found multiple times.
                    else if (line.Contains(ModSettings.SteamModPageRequiredDLCFind))
                    {
                        // Skip one line
                        line = reader.ReadLine();

                        Enums.Dlc dlc = Toolkit.ConvertToEnum<Enums.Dlc>(
                            Toolkit.MidString(line, ModSettings.SteamModPageRequiredDLCLeft, ModSettings.SteamModPageRequiredDLCRight));

                        if (!catalogMod.ExclusionForRequiredDlc.Contains(dlc))
                        {
                            CatalogUpdater.AddRequiredDLC(catalogMod, dlc);
                        }
                    }

                    // Required mods and assets. The 'find' string is a container with all required items on the next lines.
                    else if (line.Contains(ModSettings.SteamModPageRequiredModFind))
                    {
                        // Get all required items from the next lines, until we find no more. Max. 50 times to avoid an infinite loop.
                        for (var i = 1; i <= 50; i++)
                        {
                            // Skip one line, and three more at the end
                            line = reader.ReadLine();

                            ulong requiredID = Toolkit.ConvertToUlong(
                                Toolkit.MidString(line, ModSettings.SteamModPageRequiredModLeft, ModSettings.SteamModPageRequiredModRight));

                            // Exit the for loop if no more Steam ID is found
                            if (requiredID == 0)
                            {
                                break;
                            }

                            // Add the required mod (or asset) if it wasn't added already and no exclusion exists for this ID
                            if (!catalogMod.ExclusionForRequiredMods.Contains(requiredID))
                            {
                                CatalogUpdater.AddRequiredMod(catalog, catalogMod, requiredID, updatedByWebCrawler: true);
                            }

                            line = reader.ReadLine();
                            line = reader.ReadLine();
                            line = reader.ReadLine();
                        }
                    }

                    // Description for 'no description' status and for source url
                    else if (line.Contains(ModSettings.SteamModPageDescriptionFind))
                    {
                        // Skip one line; the complete description is on the next line
                        line = reader.ReadLine();

                        int descriptionLength = line.Length - line.IndexOf(ModSettings.SteamModPageDescriptionLeft) -
                            ModSettings.SteamModPageDescriptionLeft.Length - ModSettings.SteamModPageDescriptionRight.Length;

                        // A 'no description' status is when the description is not at least a few characters longer than the mod name.
                        if (descriptionLength < catalogMod.Name.Length + 5 && !catalogMod.ExclusionForNoDescription)
                        {
                            CatalogUpdater.AddStatus(catalogMod, Enums.Status.NoDescription, updatedByWebCrawler: true);
                        }
                        else if (descriptionLength > catalogMod.Name.Length + 5 && !catalogMod.ExclusionForNoDescription)
                        {
                            CatalogUpdater.RemoveStatus(catalogMod, Enums.Status.NoDescription, updatedByWebCrawler: true);
                        }

                        // Try to get the source url, unless there is an exclusion.
                        if (line.Contains(ModSettings.SteamModPageSourceURLLeft) && !catalogMod.ExclusionForSourceUrl)
                        {
                            CatalogUpdater.UpdateMod(catalog, catalogMod, sourceURL: GetSourceURL(line, catalogMod), updatedByWebCrawler: true);
                        }

                        // Description is the last info we need from the page, so break out of the while loop
                        break;
                    }
                }
            }

            if (!steamIDmatched && !catalogMod.Statuses.Contains(Enums.Status.RemovedFromWorkshop))
            {
                // We didn't find a Steam ID on the page, but no error page either. Must be a download issue or other Steam error.
                Logger.UpdaterLog($"Can't find the Steam ID on downloaded page for { catalogMod.ToString() }. Mod info not updated.", Logger.Error);
            }

            return true;
        }


        // Get the source URL. If more than one is found, pick the most likely, which is far from perfect and will need a CSV update for some mods.
        private static string GetSourceURL(string line, Mod catalogMod)
        {
            string sourceURL = "https://github.com/" + Toolkit.MidString(line, ModSettings.SteamModPageSourceURLLeft, ModSettings.SteamModPageSourceURLRight);

            if (sourceURL == "https://github.com/")
            {
                return null;
            }

            // Some commonly listed source url's to always ignore: pardeike's Harmony and Sschoener's detour
            const string pardeike  = "https://github.com/pardeike";
            const string sschoener = "https://github.com/sschoener/cities-skylines-detour";

            string discardedURLs = "";

            int tries = 0;

            // Keep comparing source url's until we find no more; max. 50 times to avoid infinite loops on code errors
            while (line.IndexOf(ModSettings.SteamModPageSourceURLLeft) != line.LastIndexOf(ModSettings.SteamModPageSourceURLLeft) && tries < 50)
            {
                tries++;

                string firstLower = sourceURL.ToLower();

                // Cut off the start of the line to just after the previous occurrence and find the next source url
                int index = line.IndexOf(ModSettings.SteamModPageSourceURLLeft) + 1;

                line = line.Substring(index);

                string nextSourceURL = "https://github.com/" + Toolkit.MidString(line, ModSettings.SteamModPageSourceURLLeft, ModSettings.SteamModPageSourceURLRight);

                string nextLower = nextSourceURL.ToLower();

                // Skip this source URL if it is empty, pardeike, sschoener or the same as the previous one
                if (nextLower == "https://github.com/" || nextLower.Contains(pardeike) || nextLower.Contains(sschoener) || nextLower == firstLower)
                {
                    continue;
                }

                // Silently discard the previous source url if it is pardeike or sschoener
                if (firstLower.Contains(pardeike) || firstLower.Contains(sschoener))
                {
                    sourceURL = nextSourceURL;
                }
                // Discard the previous url if it contains 'issue', 'wiki', 'documentation', 'readme', 'guide' or 'translation'.
                else if (firstLower.Contains("issue") || firstLower.Contains("wiki") || firstLower.Contains("documentation") 
                    || firstLower.Contains("readme") || firstLower.Contains("guide") || firstLower.Contains("translation"))
                {
                    discardedURLs += "\n                      Discarded: " + sourceURL;

                    sourceURL = nextSourceURL;
                }
                // Otherwise discard the new source url
                else
                {
                    discardedURLs += "\n                      Discarded: " + nextSourceURL;
                }
            }

            // Discard the selected source url if it is pardeike or sschoener. This can happen when that is the only github link in the description.
            if (sourceURL.Contains(pardeike) || sourceURL.Contains(sschoener))
            {
                sourceURL = null;
            }

            // Log the selected and discarded source URLs, if the selected source URL is different from the one in the catalog
            if (!string.IsNullOrEmpty(discardedURLs) && sourceURL != catalogMod.SourceUrl)
            {
                Logger.UpdaterLog($"Found multiple source url's for { catalogMod.ToString() }" +
                    $"\n                      Selected:  { (string.IsNullOrEmpty(sourceURL) ? "none" : sourceURL) }{ discardedURLs }");
            }

            return sourceURL;
        }
    }
}
