/*********************************************************************
 *  SolidWorks Assembly Reference Re‑mapper
 *
 *  What it does:
 *      - Starts SolidWorks (hidden)
 *      - Opens an assembly file
 *      - Replaces the file paths of any part/sub‑assembly that matches
 *        a mapping supplied by the user
 *      - Saves the assembly (overwrites or writes to a new file)
 *
 *  Requirements:
 *      - SolidWorks installed on the machine
 *      - References to SolidWorks.Interop.sldworks & SolidWorks.Interop.swconst
 *      - Build configuration = x86 (SolidWorks is 32‑bit)
 *
 *  Author:  ChatGPT (OpenAI) – 2026
 *********************************************************************/

using SolidWorks.Interop.sldworks;            // Core API
using SolidWorks.Interop.swconst;             // Enums
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;          // Marshal

namespace SwReferenceReplacer
{
    class Program
    {
        // -----------------------------------------------------------------
        // Entry point
        // -----------------------------------------------------------------
        static void Main(string[] args)
        {
            //// Simple argument handling -------------------------------------------------
            //if (args.Length < 2)
            //{
            //    Console.WriteLine("Usage: SwReferenceReplacer.exe <AssemblyPath> <MappingFile>");
            //    Console.WriteLine("   <AssemblyPath>  : Full path to the .sldasm you want to edit.");
            //    Console.WriteLine("   <MappingFile>   : JSON file that maps old paths -> new paths.");
            //    Console.WriteLine("Example mapping file (JSON):");
            //    Console.WriteLine("{");
            //    Console.WriteLine("    \"C:\\OldFolder\\Part1.sldprt\" : \"D:\\NewFolder\\Part1.sldprt\",");
            //    Console.WriteLine("    \"C:\\OldFolder\\SubAssy1.sldasm\" : \"D:\\NewFolder\\SubAssy1.sldasm\"");
            //    Console.WriteLine("}");
            //    return;
            //}

            string assemblyPath = "d:\\solid\\Assem1.SLDASM";
            string mappingFile = "d:\\solid\\Assem1.SLDASM";

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"ERROR: Assembly file not found: {assemblyPath}");
                return;
            }
            //if (!File.Exists(mappingFile))
            //{
            //    Console.WriteLine($"ERROR: Mapping file not found: {mappingFile}");
            //    return;
            //}

            // Load the path‑mapping dictionary -------------------------------------------------
            //var pathMap = LoadPathMap(mappingFile);
            var pathMap = new Dictionary<string, string>();
            pathMap.Add("Part2.SLDPRT", "Part2_.SLDPRT");
            if (pathMap == null || pathMap.Count == 0)
            {
                Console.WriteLine("ERROR: Mapping file is empty or could not be parsed.");
                return;
            }

            // Run the SolidWorks automation -------------------------------------------------
            try
            {
                ReplaceReferences(assemblyPath, pathMap);
                Console.WriteLine("Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unhandled exception: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        // -----------------------------------------------------------------
        // Load JSON mapping into a Dictionary<string,string>
        // -----------------------------------------------------------------
        private static Dictionary<string, string> LoadPathMap(string jsonPath)
        {
            // Minimal JSON parser – we don't want to pull in Newtonsoft just for this demo.
            // The format must be a flat object with string keys/values as shown in the help.
            var map = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            string json = File.ReadAllText(jsonPath);
            json = json.Trim();

            // Very naive parsing (good enough for a small mapping file)
            // Remove surrounding braces
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);

            var entries = json.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var kv = entry.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;

                string key = kv[0].Trim().Trim('\"');
                string value = kv[1].Trim().Trim('\"');

                // Normalise slashes for comparison
                key = Path.GetFullPath(key);
                value = Path.GetFullPath(value);

                map[key] = value;
            }
            return map;
        }

        // -----------------------------------------------------------------
        // Core routine that opens the assembly, swaps component paths, saves.
        // -----------------------------------------------------------------
        private static void ReplaceReferences(string assemblyPath, Dictionary<string, string> pathMap)
        {
            // 1️⃣ Start SolidWorks (hidden)
            var swApp = (SldWorks)Activator.CreateInstance(
                Type.GetTypeFromProgID("SldWorks.Application"));
            if (swApp == null)
                throw new InvalidOperationException("Could not start SolidWorks COM server.");

            // Run in hidden mode – set to true if you want to see the UI for debugging
            swApp.Visible = false;

            // 2️⃣ Open the assembly (silent, no UI)
            int errors = 0, warnings = 0;
            const int openOpts = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
            ModelDoc2 swModel = swApp.OpenDoc6(
                assemblyPath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                openOpts,
                "",
                ref errors,
                ref warnings);

            if (swModel == null)
                throw new InvalidOperationException(
                    $"Failed to open assembly. Errors={errors}, Warnings={warnings}");

            // 3️⃣ Walk the component tree (including hidden components)
            var visited = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            ProcessComponentRecursive(swModel, pathMap, visited);

            // 4️⃣ Rebuild & Save
            swModel.ForceRebuild3(true);
            int saveErrors = 0, saveWarnings = 0;
            bool saved = swModel.Save3(
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                ref saveErrors,
                ref saveWarnings);

            if (!saved)
                Console.WriteLine($"WARNING: Save returned false. Errors={saveErrors}, Warnings={saveWarnings}");
            else
                Console.WriteLine("Assembly saved successfully.");

            // 5️⃣ Clean‑up
            swApp.CloseDoc(swModel.GetTitle());
            // Release COM objects (important to avoid a lingering SolidWorks instance)
            Marshal.ReleaseComObject(swModel);
            Marshal.ReleaseComObject(swApp);
        }

        // -----------------------------------------------------------------
        // Recursive walk of the assembly tree.
        // -----------------------------------------------------------------
        private static void ProcessComponentRecursive(
            ModelDoc2 rootModel,
            Dictionary<string, string> pathMap,
            HashSet<string> visited)
        {
            // The root may be an assembly or a part – we only care about assemblies here.
            if (rootModel == null) return;

            var comp = rootModel as IAssemblyDoc;
            if (comp == null) return; // Not an assembly, nothing to recurse into

            // Get all components (including hidden, suppressed, and those inside sub‑assemblies)
            object[] comps = (object[])comp.GetComponents(false);
            if (comps == null) return;

            foreach (object obj in comps)
            {
                var comp2 = obj as Component2;
                if (comp2 == null) continue;

                // Guard against processing the same component multiple times (possible in multi‑config)
                string compId = comp2.GetPathName();
                if (visited.Contains(compId)) continue;
                visited.Add(compId);

                // ------------------ 1️⃣ Replace path if it exists in the map ------------------
                string currentPath = comp2.GetPathName();   // Full absolute path
                if (string.IsNullOrEmpty(currentPath)) continue; // Should never happen

                string fileName= Path.GetFileName(currentPath);

                if (pathMap.TryGetValue(fileName, out string newPath))
                {
                    // SetPathName returns true on success.
                    //bool ok = comp2.SetPathName(newPath);
                    //Console.WriteLine(ok
                    //    ? $"Re‑mapped: {currentPath} → {newPath}"
                    //    : $"FAILED to re‑map: {currentPath} → {newPath}");
                }

                // ------------------ 2️⃣ Recurse into sub‑assemblies ------------------
                // If the component is an assembly, open it in memory (no UI) and walk its children.
                ModelDoc2 childModel = comp2.GetModelDoc2() as ModelDoc2;
                if (childModel != null && childModel.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    // Recursively process the sub‑assembly
                    ProcessComponentRecursive(childModel, pathMap, visited);
                    // Release the child COM object after recursion
                    Marshal.ReleaseComObject(childModel);
                }
            }
        }
    }
}