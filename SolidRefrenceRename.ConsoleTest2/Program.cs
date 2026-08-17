using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SolidWorksReferenceUpdater
{
    class Program
    {
        public static AssemblyDoc assy;
        static void Main(string[] args)
        {
            Console.WriteLine("SolidWorks Assembly Reference Updater");
            Console.WriteLine("======================================");

            // Configuration
            string assembliesFolder = @"D:\solid"; // Change to your folder
            //string mappingFile = @"C:\file_mapping.csv";     // Format: OldFileName,NewFileName
            string logFile = @"C:\update_log.txt";

            try
            {
                // 1. Initialize SolidWorks application (invisible mode)
                SldWorks swApp = new SldWorks();
                swApp.Visible = false; // Run in background

                if (swApp == null)
                {
                    Console.WriteLine("Failed to initialize SolidWorks. Is it installed?");
                    return;
                }

                Console.WriteLine("SolidWorks initialized successfully");

                // 2. Load file name mapping
                //var fileMappings = LoadMappingFile(mappingFile);
                var fileMappings = new Dictionary<string, string>();
                fileMappings.Add("Part2.SLDPRT", "Part2_.SLDPRT");
                Console.WriteLine($"Loaded {fileMappings.Count} file mappings");
               
                // 3. Get all assembly files
                var assemblyFiles = Directory.GetFiles(assembliesFolder, "*.sldasm", SearchOption.AllDirectories);
                Console.WriteLine($"Found {assemblyFiles.Length} assembly files to process");

                // 4. Process each assembly
                using (StreamWriter log = new StreamWriter(logFile))
                {
                    log.WriteLine($"Update started: {DateTime.Now}");
                    log.WriteLine($"Assemblies folder: {assembliesFolder}");
                    log.WriteLine($"Mappings loaded: {fileMappings.Count}");
                    log.WriteLine("========================================");

                    int successCount = 0;
                    int failCount = 0;

                    foreach (string assemblyPath in assemblyFiles)
                    {
                        Console.WriteLine($"\nProcessing: {Path.GetFileName(assemblyPath)}");
                        bool result = ProcessAssembly(swApp, assemblyPath, fileMappings, log);

                        if (result)
                        {
                            successCount++;
                            Console.WriteLine("  ✓ Updated successfully");
                        }
                        else
                        {
                            failCount++;
                            Console.WriteLine("  ✗ Failed to update");
                        }
                    }

                    log.WriteLine($"\nUpdate completed: {DateTime.Now}");
                    log.WriteLine($"Successful: {successCount}, Failed: {failCount}");
                }

                // 5. Clean up
                swApp.ExitApp();
                Console.WriteLine($"\nProcessing complete. Check log: {logFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        static Dictionary<string, string> LoadMappingFile(string mappingFilePath)
        {
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(mappingFilePath))
            {
                throw new FileNotFoundException($"Mapping file not found: {mappingFilePath}");
            }

            var lines = File.ReadAllLines(mappingFilePath);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                var parts = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string oldName = parts[0].Trim();
                    string newName = parts[1].Trim();

                    // Remove file extensions if present for consistency
                    oldName = Path.GetFileNameWithoutExtension(oldName);
                    newName = Path.GetFileNameWithoutExtension(newName);

                    if (!mappings.ContainsKey(oldName))
                    {
                        mappings.Add(oldName, newName);
                    }
                }
            }

            return mappings;
        }

        static bool ProcessAssembly(SldWorks swApp, string assemblyPath,
            Dictionary<string, string> fileMappings, StreamWriter log)
        {
            bool allSuccessful = true;
            string assemblyName = Path.GetFileName(assemblyPath);
            AssemblyDoc swAssy=default(AssemblyDoc);
            swAssy = (AssemblyDoc) swApp.ActiveDoc;
            assy = (AssemblyDoc)swAssy;
            AttachSWEvents(assy);
            try
            {
                // Check if the assembly exists
                if (!File.Exists(assemblyPath))
                {
                    log.WriteLine($"ERROR: Assembly file not found: {assemblyPath}");
                    return false;
                }

                // For each renamed file, update references in this assembly
                foreach (var mapping in fileMappings)
                {
                    string oldName = mapping.Key;
                    string newName = mapping.Value;

                    try
                    {
                        // Use IRenamedDocumentReferences to update references without opening
                        bool updated = UpdateReferenceWithoutOpening(swApp, assemblyPath, oldName, newName);

                        if (updated)
                        {
                            log.WriteLine($"UPDATED: {assemblyName} - {oldName} -> {newName}");
                            Console.WriteLine($"    Updated: {oldName} -> {newName}");
                        }
                        else
                        {
                            // Component might not exist in this assembly - that's OK
                            log.WriteLine($"INFO: {assemblyName} - No reference to {oldName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"ERROR: {assemblyName} - {mapping.Key}: {ex.Message}");
                        allSuccessful = false;
                    }
                }

                return allSuccessful;
            }
            catch (Exception ex)
            {
                log.WriteLine($"ERROR: {assemblyName} - {ex.Message}");
                return false;
            }
        }
        static Dictionary<string, object> partItems = new Dictionary<string, object>();
        static bool UpdateReferenceWithoutOpening(SldWorks swApp, string assemblyPath,
            string oldFileName, string newFileName)
        {
            try
            {
                // Open the document silently
                int errors = 0;
                int warnings = 0;

                ModelDoc2 doc = swApp.OpenDoc6(
                    assemblyPath,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "",
                    ref errors,
                    ref warnings);

                if (doc == null)
                {
                    throw new Exception($"Failed to open document. Error code: {errors}");
                }

                try
                {
                    // Get the document's extension
                    ModelDocExtension ext = doc.Extension;

                    // Try to select and rename each component that matches our mapping
                    bool found = false;

                    // Get all components in the assembly
                    AssemblyDoc assy = (AssemblyDoc)doc;
                    object[] components = assy.GetComponents(false) as object[];

                    if (components != null)
                    {
                        foreach (object compObj in components)
                        {
                            Component2 comp = compObj as Component2;
                            if (comp == null) continue;

                            string compName = comp.Name2;
                            string compPath = comp.GetPathName();

                            // Extract file name without extension
                            string compFileName = Path.GetFileName(compPath);

                            // If this component matches our old file name
                            if (string.Equals(compFileName, oldFileName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Select the component

                                var hh = Path.GetFileNameWithoutExtension(doc.GetTitle());
                                string selectionString = $"{compName}@{hh}";

                                bool selected = ext.SelectByID2(selectionString, "COMPONENT", 0, 0, 0, false, 0, null, 0);

                                if (selected)
                                {
                                    if (!partItems.ContainsKey(newFileName.ToLower()))

                                    {
                                        ModelDoc2 part = swApp.OpenDoc6(
                                                assemblyPath,
                                                (int)swDocumentTypes_e.swDocPART,
                                                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                                                "",
                                                ref errors,
                                                ref warnings);
                                        partItems[newFileName.ToLower()] = part;
                                    }

                                    // Rename the component reference
                                    int renameResult = ext.RenameDocument(newFileName);
                                    //4: Means: You can not rename the m component to the specified name because a model is not loaded in memory
                                    if (renameResult == (int)swRenameDocumentError_e.swRenameDocumentError_None)
                                    {
                                        found = true;
                                        Console.WriteLine($"        Renamed: {compName} -> {newFileName}");
                                        ext.Rebuild((int)swRebuildOptions_e.swRebuildAll);
                                        var status=doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent + (int)swSaveAsOptions_e.swSaveAsOptions_SaveReferenced, ref errors, ref warnings);
                                    }
                                    else
                                    {
                                        Console.WriteLine($"        Failed to rename {compName}: Error {renameResult}");
                                    }
                                }
                            }
                        }
                    }

                    // If we found and renamed components, save the assembly
                    if (found)
                    {
                        int saveErrors = 0;
                        int saveWarnings = 0;
                        bool saved = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);

                        if (!saved)
                        {
                            throw new Exception($"Failed to save assembly after updates. Error: {saveErrors}");
                        }
                    }

                    return found;
                }
                finally
                {
                    // Close the document
                    swApp.CloseDoc(doc.GetTitle());
                    
                }
                
            }
            catch (Exception ex)
            {
                throw new Exception($"Error updating references: {ex.Message}", ex);
            }
        }

        public static void AttachSWEvents(AssemblyDoc assy)
        {
            if (assy != null)
            {
                assy.RenameItemNotify += Assy_RenameItemNotify;
                assy.RenamedDocumentNotify += Assy_RenamedDocumentNotify;
            }
        }

        private static int Assy_RenamedDocumentNotify(ref object RenamedDocumentInterface)
        {
           RenamedDocumentReferences swRenamedDocumentReferences=default(RenamedDocumentReferences);
            object[] searchPaths = null;
            object[] pathNames = null;
            int i = 0;
            int nbr = 0;
            swRenamedDocumentReferences = (RenamedDocumentReferences)RenamedDocumentInterface;
            swRenamedDocumentReferences.UpdateWhereUsedReferences = true;
            swRenamedDocumentReferences.IncludeFileLocations = true;

            searchPaths = (object[]) swRenamedDocumentReferences.GetSearchPath();
            nbr=searchPaths.Length-1;
            Console.WriteLine("Search paths:");
            for (i = 0; i < nbr; i++)
            {
                Console.WriteLine(" "+searchPaths[i]);
                
            }
            swRenamedDocumentReferences.Search();

            pathNames = (object[]) swRenamedDocumentReferences.ReferencesArray();
            nbr=pathNames.Length-1;
            Console.WriteLine("Refrences:");
            for (i = 0; i < nbr; i++)
            {
                Console.WriteLine(" " + pathNames[i]);

            }
            swRenamedDocumentReferences.CompletionAction = (int)swRenamedDocumentFinalAction_e.swRenamedDocumentFinalAction_Ok;
            Console.WriteLine("RenamedDocumentNotify fired");
            return 0;
        }

        private static int Assy_RenameItemNotify(int entityType, string oldName, string newName)
        {
            Console.WriteLine("RenameItemNotify fired");
            return 0;
        }
    }
}