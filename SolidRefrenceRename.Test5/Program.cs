using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
namespace SolidWorksReferenceReplacer
{
    class Program
    {
        static void Main(string[] args)
        {
            string[] assembliesPaths = new string[]
            {   @"D:\solid\Assem1_.SLDASM",
                @"D:\solid\Assem2.SLDASM"
            };
            string[] drawingPaths = new string[]
            {   @"D:\solid\Part2.SLDDRW",
            };
            Dictionary<string, string> renameMap =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { @"Part1.SLDPRT", @"D:\solid\Part1_.SLDPRT" },
                { @"Part2.SLDPRT", @"D:\solid\Part2_.SLDPRT" },
                { @"Assem1.SLDASM", @"D:\solid\Assem1_.SLDASM" }
            };
            SldWorks swApp = null;
            try
            {                // Connect to a running instance of SOLIDWORKS or launch a new background session
                try
                {
                    swApp = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                    Console.WriteLine("Connected to running SOLIDWORKS instance.");
                }
                catch (COMException)
                {
                    Console.WriteLine("SOLIDWORKS is not running. Starting a new session...");
                    Type swType = Type.GetTypeFromProgID("SldWorks.Application");
                    swApp = (SldWorks)Activator.CreateInstance(swType);
                    swApp.Visible = true; // Set to false if you want it to run completely in the background
                }
                if (swApp == null)
                {
                    Console.WriteLine("Failed to initialize SOLIDWORKS application.");
                    return;
                }
                // Standardize path casings and slashes in our map to ensure accurate lookups
                var normalizedRenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in renameMap)
                {
                    normalizedRenameMap[NormalizePath(kvp.Key)] = NormalizePath(kvp.Value);
                }

                // We queue assemblies to process them, including any subassemblies discovered along the way
                Queue<string> assembliesToProcess = new Queue<string>(assembliesPaths);
                HashSet<string> processedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (assembliesToProcess.Count > 0)
                {
                    string currentAssemblyPath = NormalizePath(assembliesToProcess.Dequeue());
                    if (processedAssemblies.Contains(currentAssemblyPath)) continue;
                    if (!File.Exists(currentAssemblyPath))
                    {
                        Console.WriteLine($"Assembly not found on disk: {currentAssemblyPath}");
                        continue;
                    }
                    Console.WriteLine($"\nProcessing assembly: {currentAssemblyPath}");
                    processedAssemblies.Add(currentAssemblyPath);
                    // Get direct dependencies of the current assembly                   
                    // Traverseflag = false (we process level-by-level), Searchflag = true, AddReadOnlyInfo = false
                    object dependsObj = swApp.GetDocumentDependencies2(currentAssemblyPath, false, true, false);
                    if (dependsObj == null) { Console.WriteLine("  No dependencies found or unable to access dependencies."); continue; }
                    // GetDocumentDependencies2 returns an array where:                   
                    // Index [0, 2, 4...] is the short name, Index [1, 3, 5...] is the full absolute path
                    Array dependsArray = (Array)dependsObj;
                    for (int i = 1; i < dependsArray.Length; i += 2)
                    {
                        string oldDependencyPath = NormalizePath(dependsArray.GetValue(i) as string);
                        if (string.IsNullOrEmpty(oldDependencyPath)) continue;
                        string componentName = Path.GetFileName(oldDependencyPath);
                        // Case 1: The dependency is a renamed/relocated part or subassembly
                        if (normalizedRenameMap.TryGetValue(componentName, out string newDependencyPath))
                        {
                            if (newDependencyPath != oldDependencyPath)
                            {
                                Console.WriteLine($"  Replacing: {Path.GetFileName(oldDependencyPath)}");
                                Console.WriteLine($"    Old: {oldDependencyPath}");
                                Console.WriteLine($"    New: {newDependencyPath}");
                            }
                            else
                            {
                                Console.WriteLine($" No need to relpace: Source and destinations are the same: {newDependencyPath}");
                                continue;
                            }

                            // Execute the reference replacement
                            bool success = swApp.ReplaceReferencedDocument(currentAssemblyPath, oldDependencyPath, newDependencyPath);
                            if (success)
                            {
                                Console.WriteLine("    -> Success!");
                                // If the dependency we just replaced is itself an assembly, we queue its NEW path for processing
                                if (IsAssemblyFile(newDependencyPath))
                                {
                                    assembliesToProcess.Enqueue(newDependencyPath);
                                }
                            }
                            else
                            {
                                Console.WriteLine("    -> Failed. Ensure the assembly is not open in SOLIDWORKS.");
                            }
                        }
                        else
                        {
                            // Case 2: The dependency is an assembly that was NOT renamed, but we still need to scan it for renamed parts
                            if (IsAssemblyFile(oldDependencyPath))
                            {
                                assembliesToProcess.Enqueue(oldDependencyPath);
                            }
                        }
                    }
                }

                Queue<string> drawingsToProcess = new Queue<string>(drawingPaths);
                HashSet<string> processedDrowings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (drawingsToProcess.Count > 0)
                {
                    string currentDrawingPath = NormalizePath(drawingsToProcess.Dequeue());
                    if (processedDrowings.Contains(currentDrawingPath)) continue;
                    if (!File.Exists(currentDrawingPath))
                    {
                        Console.WriteLine($"Drawing not found on disk: {currentDrawingPath}");
                        continue;
                    }
                    Console.WriteLine($"\nProcessing drawing: {currentDrawingPath}");
                    processedDrowings.Add(currentDrawingPath);
                    // Get direct dependencies of the current assembly                   
                    // Traverseflag = false (we process level-by-level), Searchflag = true, AddReadOnlyInfo = false
                    object dependsObj = swApp.GetDocumentDependencies2(currentDrawingPath, false, true, false);
                    if (dependsObj == null) { Console.WriteLine("  No dependencies found or unable to access dependencies."); continue; }
                    // GetDocumentDependencies2 returns an array where:                   
                    // Index [0, 2, 4...] is the short name, Index [1, 3, 5...] is the full absolute path
                    Array dependsArray = (Array)dependsObj;
                    for (int i = 1; i < dependsArray.Length; i += 2)
                    {
                        string oldDependencyPath = NormalizePath(dependsArray.GetValue(i) as string);
                        if (string.IsNullOrEmpty(oldDependencyPath)) continue;
                        string componentName = Path.GetFileName(oldDependencyPath);
                        // Case 1: The dependency is a renamed/relocated part or subassembly
                        if (normalizedRenameMap.TryGetValue(componentName, out string newDependencyPath))
                        {
                            if (newDependencyPath != oldDependencyPath)
                            {
                                Console.WriteLine($"  Replacing: {Path.GetFileName(oldDependencyPath)}");
                                Console.WriteLine($"    Old: {oldDependencyPath}");
                                Console.WriteLine($"    New: {newDependencyPath}");
                            }
                            else
                            {
                                Console.WriteLine($" No need to relpace: Source and destinations are the same: {newDependencyPath}");
                                continue;
                            }

                            // Execute the reference replacement
                            bool success = swApp.ReplaceReferencedDocument(currentDrawingPath, oldDependencyPath, newDependencyPath);
                            if (success)
                            {
                                Console.WriteLine("    -> Success!");
                                // If the dependency we just replaced is itself an assembly, we queue its NEW path for processing
                                if (IsAssemblyFile(newDependencyPath))
                                {
                                    assembliesToProcess.Enqueue(newDependencyPath);
                                }
                            }
                            else
                            {
                                Console.WriteLine("    -> Failed. Ensure the assembly is not open in SOLIDWORKS.");
                            }
                        }
                        else
                        {
                            // Case 2: The dependency is an assembly that was NOT renamed, but we still need to scan it for renamed parts
                            if (IsAssemblyFile(oldDependencyPath))
                            {
                                assembliesToProcess.Enqueue(oldDependencyPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"An error occurred during execution: {ex.Message}"); }
            finally
            {
                if (swApp != null)
                { Marshal.ReleaseComObject(swApp); }
            }
            Console.WriteLine("\nTask finished. Press any key to exit."); Console.ReadKey();
        }
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            //try
            //{
            //    return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            //}
            //catch { return path; }
            return path;
        }
        private static bool IsAssemblyFile(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".sldasm", StringComparison.OrdinalIgnoreCase);
        }
    }
}