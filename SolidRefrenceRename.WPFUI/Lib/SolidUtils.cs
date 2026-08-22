using SolidRefrenceRename.WPFUI.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;


namespace SolidRefrenceRename.WPFUI.Lib
{
    public class SolidUtils
    {
        private static SldWorks swApp;
        private static ModelDoc2 swModel;
        
        public static PartChangingOutput ChangePartAddress(string currentAssemblyPath, Dictionary<string, string> newPartAddressMap)
        {

            List<ProcessOutputDetails> errorsList = new List<ProcessOutputDetails>();
            List<string> newAssembliesFound = new List<string>();
            List<string> convertedItems = new List<string>();
            try
            {
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
                    var errorStr = "Failed to initialize SOLIDWORKS application.";
                    return new PartChangingOutput()
                    {
                        Succeed=false,
                        Errors= new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) },
                        NewAssembliesFound=null
                    };
                    //return (false, new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) }, null);
                }
                // Standardize path casings and slashes in our map to ensure accurate lookups
                var normalizedRenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in newPartAddressMap)
                {
                    normalizedRenameMap[NormalizePath(kvp.Key)] = NormalizePath(kvp.Value);
                }
                // Get direct dependencies of the current assembly                   
                // Traverseflag = false (we process level-by-level), Searchflag = true, AddReadOnlyInfo = false
                object dependsObj = swApp.GetDocumentDependencies2(currentAssemblyPath, false, true, false);

                if (dependsObj == null)
                {
                    var errorStr = "  No dependencies found or unable to access dependencies.";
                    return new PartChangingOutput()
                    {
                        Succeed = false,
                        Errors = new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) },
                        NewAssembliesFound = null
                    };
                    //return (false, new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) }, null);
                }
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
                            convertedItems.Add(oldDependencyPath);
                            // If the dependency we just replaced is itself an assembly, we queue its NEW path for processing
                            if (IsAssemblyFile(newDependencyPath))
                            {
                                newAssembliesFound.Add(newDependencyPath);
                            }
                        }
                        else
                        {

                            var errorStr = "    -> Failed. Ensure the assembly is not open in SOLIDWORKS.";
                            Console.WriteLine(errorStr);
                            return new PartChangingOutput()
                            {
                                Succeed = false,
                                Errors = new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) },
                                NewAssembliesFound = null
                            };
                            //return (false, new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) }, null);
                        }
                    }
                    else
                    {
                        // Case 2: The dependency is an assembly that was NOT renamed, but we still need to scan it for renamed parts
                        if (IsAssemblyFile(oldDependencyPath))
                        {
                            newAssembliesFound.Add(oldDependencyPath);
                        }
                    }
                }
                return new PartChangingOutput()
                {
                    Succeed = convertedItems.Any(),
                    Errors = errorsList,
                    NewAssembliesFound = newAssembliesFound
                };
                //return (convertedItems.Any(), errorsList, newAssembliesFound);
            }
            catch (Exception ex)
            {

                var errorStr = $"Error: {ex.Message}";
                Console.WriteLine(errorStr);
                return new PartChangingOutput()
                {
                    Succeed = false,
                    Errors = new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) },
                    NewAssembliesFound = null
                };
               // return (false, new List<ProcessOutputDetails>() { new ProcessOutputDetails(errorStr, currentAssemblyPath) }, null);
            }
            finally
            {
                //Cleanup

            }
        }
        public static void DoCleanUp()
        {
            if (swApp != null)
                swApp.ExitApp();
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
            return path.ToUpper().Trim();
        }
        private static bool IsAssemblyFile(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".sldasm", StringComparison.OrdinalIgnoreCase);
        }
    }
}
