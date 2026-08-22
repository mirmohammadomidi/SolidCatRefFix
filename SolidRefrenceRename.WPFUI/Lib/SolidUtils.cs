using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.IO;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace SolidRefrenceRenameTest1.Lib
{
    public class SolidUtils
    {
        private static SldWorks swApp;
        private static ModelDoc2 swModel;
        public static void ChangePartAddress(string assemblePath,  Dictionary<string,string> partAddressMap)
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
                object[] components = (object[])swAssembly.GetComponents(false);

                foreach (Component2 component in components)
                {
                    string componentPath = component.GetPathName();
                    string componentName = Path.GetFileName(componentPath);

                    if (!string.IsNullOrEmpty(componentPath) && componentName.Equals(oldPartName, StringComparison.OrdinalIgnoreCase))
                    {
                        var n1 = component.Name;
                        var n2 = component.Name2;

                        //C:\\Users\\WORK\\Desktop\\zand\\Part2.SLDPRT

                        bool replaced = swApp.ReplaceReferencedDocument(assemblePath, componentPath, newPartAddress);
                        if (replaced)
                        {
                            swModel.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                        }
                        else
                        {
                            Console.WriteLine($"Could not replace {oldPartName} with {newPartAddress} in {assemblePath}");
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
