using CatiaReferenceRename.WPFUI.Lib;
using CommunityToolkit.Mvvm.ComponentModel;
using SolidRefrenceRename.WPFUI.Data;
using SolidRefrenceRename.WPFUI.Data.Entities;
using SolidRefrenceRename.WPFUI.Lib;
using SolidRefrenceRename.WPFUI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SolidRefrenceRename.WPFUI.ViewModel
{
    public partial class SolidFixViewModel : ObservableObject
    {
        public ObservableCollection<ProcessOutputDetails> Errors { get; } = new ObservableCollection<ProcessOutputDetails>();
        public event EventHandler<int> OnNumberOfAssembliesChanged;
        public event EventHandler<int> OnNumberOfPartsChanged;
        public event EventHandler<int> OnNumberOfDrawingsChanged;
        public event EventHandler<(int assemblyIndex, string currentItem)> OnItemBeingFixed;
        public event EventHandler<string> EventLogged;
        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set => SetProperty(ref _isRunning, value);   // CommunityToolkit.Mvvm helper
        }
        public SolidFixViewModel()
        {
            CatiaUtils.LogErrors += CatiaUtils_LogErrors;
        }

        private void CatiaUtils_LogErrors(object sender, string e)
        {
            if (EventLogged != null)
            {
                EventLogged(sender, e);
            }
        }

        public async Task StartFixing(SoftwareType softwareType, List<string> fileNamesToInclude = null, string patternToMatchAllFolderAddressWith = null, string replaceAllFolderAddressWith = null, FileExtensionTypes extensionTypes = FileExtensionTypes.All, string site = "All Sites")
        {
            //if (!string.IsNullOrWhiteSpace(convertAllDestinationFilesToFolder))
            //{
            //    if (!convertAllDestinationFilesToFolder.EndsWith(Path.DirectorySeparatorChar.ToString())
            //        && !convertAllDestinationFilesToFolder.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            //    {
            //        convertAllDestinationFilesToFolder += Path.DirectorySeparatorChar.ToString();
            //    }
            //}
            bool allSites = site.ToLower().Contains("all");
            Errors.Clear();
            using (var context = new IFSCodeCleansingDBContext(App.DefaultConnectionString))
            {
                try
                {
                    await Task.Run(async () =>
                    {
                        List<DocFileView> allAssemblies;
                        if ((extensionTypes & FileExtensionTypes.Assembly) == FileExtensionTypes.Assembly && softwareType == SoftwareType.Solid)
                        {
                            if (allSites)
                            {
                                allAssemblies = await context.DocFileViews.Where(uu => uu.EXTENSION.ToUpper() == "SLDASM").ToListAsync();
                            }
                            else
                            {
                                allAssemblies = await context.DocFileViews
                                .Where(uu => uu.EXTENSION.ToUpper() == "SLDASM" && uu.SourceDbCode.ToUpper().Contains(site.ToUpper()))
                                .ToListAsync();
                            }

                        }
                        else if ((extensionTypes & FileExtensionTypes.Assembly) == FileExtensionTypes.Assembly && softwareType == SoftwareType.Catia)
                        {
                            if (allSites)
                            {
                                allAssemblies = await context.DocFileViews.Where(uu => uu.EXTENSION.ToUpper() == "CATPRODUCT").ToListAsync();
                            }
                            else
                            {
                                allAssemblies = await context.DocFileViews
                                .Where(uu => uu.EXTENSION.ToUpper() == "CATPRODUCT" && uu.SourceDbCode.ToUpper().Contains(site.ToUpper()))
                                .ToListAsync();
                            }
                        }
                        else
                        {
                            allAssemblies = new List<DocFileView>();
                        }
                        if (fileNamesToInclude != null && fileNamesToInclude.Any())
                        {
                            List<string> fileNamesToIncludeNormalized = new List<string>();
                            foreach (var file in fileNamesToInclude)
                            {
                                fileNamesToIncludeNormalized.Add(file.ToUpper().Trim());
                            }
                            allAssemblies = allAssemblies.Where(uu => fileNamesToIncludeNormalized.Contains(uu.FileName.ToUpper())).ToList();
                        }
                        if (OnNumberOfAssembliesChanged != null)
                            OnNumberOfAssembliesChanged(this, allAssemblies.Count);


                        var partDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        List<DocFileView> allParts;
                        if (softwareType == SoftwareType.Solid)
                        {
                            if (allSites)
                            {
                                allParts = await context.DocFileViews
                                .Where(uu => uu.EXTENSION.ToUpper() == "SLDPRT" || uu.EXTENSION.ToUpper() == "SLDASM")
                                .ToListAsync();
                            }
                            else
                            {
                                allParts = await context.DocFileViews
                               .Where(uu => (uu.EXTENSION.ToUpper() == "SLDPRT" || uu.EXTENSION.ToUpper() == "SLDASM") && uu.SourceDbCode.ToUpper().Contains(site.ToUpper()))
                               .ToListAsync();
                            }
                        }
                        else if (softwareType == SoftwareType.Catia)
                        {
                            if (allSites)
                            {
                                allParts = await context.DocFileViews
                            .Where(uu => uu.EXTENSION.ToUpper() == "CATPART" || uu.EXTENSION.ToUpper() == "CATPRODUCT").ToListAsync();
                            }
                            else
                            {
                                allParts = await context.DocFileViews
                               .Where(uu => (uu.EXTENSION.ToUpper() == "CATPART" || uu.EXTENSION.ToUpper() == "CATPRODUCT") && uu.SourceDbCode.ToUpper().Contains(site.ToUpper()))
                               .ToListAsync();
                            }
                        }
                        else
                        {
                            allParts = new List<DocFileView>();
                        }
                        if (OnNumberOfPartsChanged != null)
                            OnNumberOfPartsChanged(this, allParts.Count);

                        // Build the CATIA identity cache from the DB.  The
                        // UUIDs are assumed to be pre-filled (via
                        // FillSiteUUID).  No file reads are needed here.
                        var identityCache = new CatiaPartIdentityCache();
                        foreach (var part in allParts)
                        {
                            if (string.IsNullOrWhiteSpace(part.DestinationFileAddress))
                                continue;

                            identityCache.Add(new CatiaPartIdentity
                            {
                                FilePath = part.DestinationFileAddress,
                                Uuid = part.UUID,
                                PartDefinition = part.CatiaPartDefinition
                            });
                        }

                        Console.WriteLine($"Identity cache: {identityCache.Count} entries.");
                        List<DocFileView> allDrawings;
                        if ((extensionTypes & FileExtensionTypes.Drawing) == FileExtensionTypes.Drawing && softwareType == SoftwareType.Solid)
                        {
                            if (allSites)
                            {
                                allDrawings = await context.DocFileViews.Where(uu => uu.EXTENSION.ToUpper() == "SLDDRW").ToListAsync();
                            }
                            else
                            {
                                allDrawings = await context.DocFileViews
                                .Where(uu => uu.EXTENSION.ToUpper() == "SLDDRW" &&
                                    uu.SourceDbCode.ToUpper().Contains(site.ToUpper())).ToListAsync();
                            }
                        }
                        else if ((extensionTypes & FileExtensionTypes.Drawing) == FileExtensionTypes.Drawing && softwareType == SoftwareType.Catia)
                        {
                            if (allSites)
                            {
                                allDrawings = await context.DocFileViews.Where(uu => uu.EXTENSION.ToUpper() == "CATDRAWING").ToListAsync();
                            }
                            else
                            {
                                allDrawings = await context.DocFileViews
                                .Where(uu => uu.EXTENSION.ToUpper() == "CATDRAWING" &&
                                uu.SourceDbCode.ToUpper().Contains(site.ToUpper())).ToListAsync();
                            }

                        }
                        else
                        {
                            allDrawings = new List<DocFileView>();
                        }
                        if (fileNamesToInclude != null && fileNamesToInclude.Any())
                        {
                            List<string> fileNamesToIncludeNormalized = new List<string>();
                            foreach (var file in fileNamesToInclude)
                            {
                                fileNamesToIncludeNormalized.Add(file.ToUpper().Trim());
                            }
                            allDrawings = allDrawings.Where(uu => fileNamesToIncludeNormalized.Contains(uu.FileName.ToUpper())).ToList();
                        }

                        if (OnNumberOfDrawingsChanged != null)
                            OnNumberOfDrawingsChanged(this, allDrawings.Count);
                        try
                        {
                            foreach (var part in allParts)
                            {
                                string partName = $"{part.SourceFileName}.{part.EXTENSION}".ToUpper().Trim();
                                if (!partDict.ContainsKey(partName))
                                {
                                    if (!string.IsNullOrWhiteSpace(part.DestinationFileAddress))
                                    {
                                        if (string.IsNullOrWhiteSpace(replaceAllFolderAddressWith))
                                        {
                                            partDict.Add(partName, part.DestinationFileAddress);
                                        }
                                        else
                                        {
                                            var fileName = Path.GetFileName(part.DestinationFileAddress);
                                            partDict.Add(partName, part.DestinationFileAddress.Replace(patternToMatchAllFolderAddressWith, replaceAllFolderAddressWith));
                                        }
                                    }
                                    else
                                    {
                                        Application.Current.Dispatcher.Invoke(() =>
                                        {
                                            var errorStr = $"Part does not have address in DB";
                                            Console.WriteLine(errorStr);
                                            Errors.Add(new ProcessOutputDetails(errorStr)
                                            {
                                                PartName = partName,
                                            });
                                        });

                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                var errorStr = ex.Message;
                                Console.WriteLine(errorStr);
                                Errors.Add(new ProcessOutputDetails(errorStr));
                            });
                        }
                        int assembliesCount = 0;
                        List<string> assemblyList;
                        if (string.IsNullOrWhiteSpace(replaceAllFolderAddressWith))
                        {
                            assemblyList = allAssemblies.Select(uu => uu.DestinationFileAddress).ToList();
                            var v2 = allDrawings.Select(uu => uu.DestinationFileAddress).ToList();
                            assemblyList.AddRange(v2);
                        }
                        else
                        {
                            assemblyList = allAssemblies.Select(uu => uu.DestinationFileAddress.Replace(patternToMatchAllFolderAddressWith, replaceAllFolderAddressWith)).ToList();
                            var v2 = allDrawings.Select(uu => uu.DestinationFileAddress.Replace(patternToMatchAllFolderAddressWith, replaceAllFolderAddressWith)).ToList();
                            assemblyList.AddRange(v2);
                        }

                        Queue<string> assembliesToProcess = new Queue<string>(assemblyList);
                        HashSet<string> processedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (assembliesToProcess.Count > 0)
                        //foreach (var part in allAssemblies)
                        {
                            string currentAssemblyPath = assembliesToProcess.Dequeue();
                            if (processedAssemblies.Contains(currentAssemblyPath)) continue;
                            if (!File.Exists(currentAssemblyPath))
                            {
                                var errorStr = $"Assembly not found on disk: {currentAssemblyPath}";
                                Console.WriteLine(errorStr);
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    Errors.Add(new ProcessOutputDetails(errorStr));
                                });
                                continue;
                            }
                            assembliesCount++;
                            if (OnItemBeingFixed != null)
                            {
                                OnItemBeingFixed(this, (assembliesCount, currentAssemblyPath));
                            }
                            Console.WriteLine($"\nProcessing assembly: {currentAssemblyPath}");
                            processedAssemblies.Add(currentAssemblyPath);
                            PartChangingOutput g = null;
                            if (softwareType == SoftwareType.Solid)
                            {
                                //g = SolidUtils.ChangePartAddress(currentAssemblyPath, partDict);
                            }
                            else
                            {
                                g = CatiaUtils.ChangePartAddress(currentAssemblyPath, partDict, identityCache);
                            }
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                foreach (var error in g.Errors)
                                {
                                    Errors.Add(error);
                                }
                            });

                            if (g.NewAssembliesFound != null && g.NewAssembliesFound.Any())
                            {
                                foreach (var assembly in g.NewAssembliesFound)
                                {
                                    assembliesToProcess.Enqueue(assembly);
                                    if (OnNumberOfAssembliesChanged != null)
                                        OnNumberOfAssembliesChanged(this, allAssemblies.Count);
                                }
                            }

                        }
                    });

                }
                catch (Exception ex)
                {

                    // Handle exception
                    var errorStr = $"Error reading parts: {ex.Message}";
                    Console.WriteLine(errorStr);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Errors.Add(new ProcessOutputDetails(errorStr));
                    });

                }
                finally
                {
                    //SolidUtils.DoCleanUp();
                    CatiaUtils.DoCleanUp();
                }
            }

        }

        public async Task CatiaTest(string targetFile = null, Dictionary<string, string> sourceFiles = null)
        {
            IsRunning = true;
            if (targetFile == null)
            {
                targetFile = "D:\\catia\\PIC AFTER\\1405-ASSEMBLY1-FA2.CATProduct";
            }
            if (sourceFiles == null)
            {
                sourceFiles = new Dictionary<string, string>()
                    {
                        { "Part1.CATPart","D:\\catia\\PIC AFTER\\1405-Part1-F1.CATPart" },
                        { "Part2.CATPart","D:\\catia\\PIC AFTER\\1405-Part2-F2.CATPart" }
                    };
            }
            PartChangingOutput g = CatiaUtils.ChangePartAddress(targetFile, sourceFiles);
            IsRunning = false;
        }

        /// <summary>
        /// Reads all CATPart and CATProduct files for the given site (or all
        /// sites), extracts their CATIA component UUID from the raw file bytes,
        /// and writes it back to the database in batches of 500 via
        /// <see cref="IFSCodeCleansingDBContext.UpdateUUIDs"/>.
        ///
        /// After running this once per site, the CatiaUuid column is populated
        /// and <see cref="StartFixing"/> can match part relevance purely from
        /// the database - no part file reads are needed during the fixing loop.
        ///
        /// TODO: The actual UUID extraction logic will be filled in later.  For
        /// now the method loads the rows, iterates them in batches of 500, and
        /// calls UpdateUUIDs with empty values.
        /// </summary>
        /// <param name="site">The SourceDbCode to filter by, or "All Sites".</param>
        public async Task FillSiteUUID(string site = "All Sites")
        {
            bool allSites = site.ToLower().Contains("all");
            IsRunning = true;

            try
            {
                using (var context = new IFSCodeCleansingDBContext(App.DefaultConnectionString))
                {
                    List<DocFileView> parts;

                    if (allSites)
                    {
                        parts = await context.DocFileViews
                            .Where(uu => uu.EXTENSION.ToUpper() == "CATPART" || uu.EXTENSION.ToUpper() == "CATPRODUCT")
                            .ToListAsync();
                    }
                    else
                    {
                        parts = await context.DocFileViews
                            .Where(uu => (uu.EXTENSION.ToUpper() == "CATPART" || uu.EXTENSION.ToUpper() == "CATPRODUCT")
                                  && uu.SourceDbCode.ToUpper().Contains(site.ToUpper()))
                            .ToListAsync();
                    }

                    Console.WriteLine($"FillSiteUUID: {parts.Count} CATPart/CATProduct row(s) for {(allSites ? "all sites" : site)}.");

                    // Skip rows that already have a UUID - no need to read
                    // them from disk again.
                    var toProcess = parts.Where(p => string.IsNullOrWhiteSpace(p.UUID)).ToList();
                    int skipped = parts.Count - toProcess.Count;
                    Console.WriteLine($"  {skipped} row(s) already have a UUID - skipped.  {toProcess.Count} row(s) to process.");

                    int processed = 0;
                    int batchSize = 100;

                    for (int i = 0; i < toProcess.Count; i += batchSize)
                    {

                        int remaining = Math.Min(batchSize, toProcess.Count - i);
                        var batch = toProcess.GetRange(i, remaining);
                        var uuidBatch = new List<KeyValuePair<int, string>>();

                        try
                        {
                            foreach (var part in batch)
                            {
                                string uuid = null;

                                if (!string.IsNullOrWhiteSpace(part.DestinationFileAddress)
                                    && File.Exists(part.DestinationFileAddress))
                                {
                                    var uuids = CatiaUtils.ExtractComponentUuidsFromPath(part.DestinationFileAddress);
                                    // Products may contain multiple UUIDs (one
                                    // per component) - store them comma-separated.
                                    var parts2 = new List<string>();
                                    foreach (string u in uuids)
                                    {
                                        if (!string.IsNullOrWhiteSpace(u))
                                            parts2.Add(u);
                                    }
                                    if (parts2.Count > 0)
                                        uuid = string.Join(",", parts2);
                                }

                                uuidBatch.Add(new KeyValuePair<int, string>(part.ID, uuid));

                            }

                            context.UpdateUUIDs(uuidBatch);
                            processed += batch.Count;
                        }
                        catch (Exception ex0)
                        {
                            var errorStr = $"FillSiteUUID1 error: {ex0.Message}";
                            Console.WriteLine(errorStr);
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                Errors.Add(new ProcessOutputDetails(errorStr));
                            });
                        }
                        Console.WriteLine($"  Batch {i / batchSize + 1}: {batch.Count} row(s) updated ({processed}/{parts.Count}).");

                        //OnItemBeingFixed?.Invoke(this, (processed, $"FillSiteUUID: {processed}/{parts.Count}"));
                    }

                    Console.WriteLine($"FillSiteUUID complete: {processed} row(s) updated.");
                }
            }
            catch (Exception ex)
            {
                var errorStr = $"FillSiteUUID2 error: {ex.Message}";
                Console.WriteLine(errorStr);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Errors.Add(new ProcessOutputDetails(errorStr));
                });
            }
            finally
            {
                IsRunning = false;
            }
        }
    }
}
