using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Configuration.Assemblies;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.ConsoleTest4
{
    internal class Program
    {
        private static SldWorks swApp;
        private static ModelDoc2 swModel;


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

                //var assemblyFile = @"D:\solid\E1031FA-FA1272-1-R7-1.SLDASM";
                var assemblyFile = @"D:\solid\Assem1.SLDASM";



                // 2. Load file name mapping
                //var fileMappings = LoadMappingFile(mappingFile);
                var fileMappings = new Dictionary<string, string>();
                fileMappings.Add("Part2.SLDPRT", @"D:\solid\Part2_.SLDPRT");
                fileMappings.Add("Part1.SLDPRT", @"D:\solid\Part1_.SLDPRT");

                foreach (var part in fileMappings.Keys)
                {
                    ChangePartAddress(assemblyFile, part, fileMappings[part]);
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
            finally
            {
                //swApp.ExitApp();
                swApp = null;
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
        public static void ChangePartAddress(string assemblePath, string oldPartName, string newPartAddress)
        {
            try
            {
                //Initial Solid
                swApp = new SldWorks();
                swApp.Visible = true;

                //Open the assembly
                int errors = 0;
                int warnings = 0;
                swModel = swApp.OpenDoc6(assemblePath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);

                if (swModel == null)
                {
                    Console.WriteLine("Failed to open assembly");
                    return;
                }

                //Cast
                AssemblyDoc swAssembly = (AssemblyDoc)swModel;

                //Call the components
                object[] components = (object[])swAssembly.GetComponents(true);

                foreach (Component2 component in components)
                {
                    string componentPath = component.GetPathName();
                    string componentName = Path.GetFileName(componentPath);

                    if (!string.IsNullOrEmpty(componentPath) && componentName.Equals(oldPartName, StringComparison.OrdinalIgnoreCase))
                    {
                        var n1 = component.Name;
                        var n2 = component.Name2;

                        var componentPathCopy = componentPath;
                        //Working: C:\Users\WORK\Desktop\zand\Part2.SLDPRT
                        //Reading from file: C:\Users\supervise\Desktop\zand\Part2.SLDPRT
                        bool replaced = swApp.ReplaceReferencedDocument(assemblePath, componentPath, newPartAddress);
                        bool replacedP = swApp.ReplaceReferencedDocument(assemblePath, "C:\\Users\\WORK\\Desktop\\zand\\Part2.SLDPRT", newPartAddress);
                        if (replaced)
                        {
                            Console.WriteLine($"Replaced {oldPartName} with {newPartAddress} in {assemblePath}");
                            swModel.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                        }
                        else
                        {
                            //swModel.repl
                            var replace2 = swAssembly.ReplaceComponents(componentPath, newPartAddress, true, true);
                            if (!replace2)
                            {
                                Console.WriteLine($"Could not replace {oldPartName} with {newPartAddress} in {assemblePath}");
                            }
                            else
                            {
                                swModel.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                            }
                        }
                    }
                }
                swApp.CloseDoc(assemblePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                //Cleanup
                swApp.ExitApp();
            }
        }


    }
}
