using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
// CATIA COM Interop Namespaces
using INFITF;
using ProductStructureTypeLib;

namespace CatiaReferenceReplacer
{
    class Program
    {
        static void Main(string[] args)
        {
            // Targeting CATIA files instead of SOLIDWORKS files
            string[] assembliesPaths = new string[]
            {
                @"C:\Users\my_user\asm1\Assem1.CATProduct",
                @"C:\Users\my_user\asm2\Assem2.CATProduct"
            };

            Dictionary<string, string> renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { @"C:\Users\my_user\old_loc\Part1.CATPart", @"C:\Users\my_user\new_loc\Part1_.CATPart" },
                { @"C:\Users\my_user\old_loc\Part2.CATPart", @"C:\Users\my_user\new_loc\Part2_.CATPart" }
            };

            INFITF.Application catiaApp = null;

            try
            {
                // Connect to a running instance of CATIA or launch a new background session
                try
                {
                    catiaApp = (INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
                    Console.WriteLine("Connected to running CATIA instance.");
                }
                catch (COMException)
                {
                    Console.WriteLine("CATIA is not running. Starting a new session...");
                    Type catiaType = Type.GetTypeFromProgID("CATIA.Application");
                    catiaApp = (INFITF.Application)Activator.CreateInstance(catiaType);
                    catiaApp.Visible = true;
                }

                if (catiaApp == null)
                {
                    Console.WriteLine("Failed to initialize CATIA application.");
                    return;
                }

                // Standardize path casings and slashes in our map
                var normalizedRenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in renameMap)
                {
                    normalizedRenameMap[NormalizePath(kvp.Key)] = NormalizePath(kvp.Value);
                }

                foreach (string assemblyPath in assembliesPaths)
                {
                    string normAssemblyPath = NormalizePath(assemblyPath);

                    if (!File.Exists(normAssemblyPath))
                    {
                        Console.WriteLine($"Assembly not found on disk: {normAssemblyPath}");
                        continue;
                    }

                    Console.WriteLine($"\nOpening assembly: {normAssemblyPath}");
                    ProductDocument productDoc = null;

                    try
                    {
                        // Open the assembly document. CATIA will open it even if children have broken links.
                        productDoc = (ProductDocument)catiaApp.Documents.Open(normAssemblyPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to open assembly: {ex.Message}");
                        continue;
                    }

                    if (productDoc == null) continue;

                    Product rootProduct = productDoc.Product;
                    HashSet<string> replacedPartNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    Console.WriteLine("Traversing and replacing references...");
                    ReplaceReferencesInProduct(rootProduct, normalizedRenameMap, replacedPartNumbers);

                    // Save and close the document
                    try
                    {
                        productDoc.Save();
                        Console.WriteLine("Assembly saved successfully.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to save assembly: {ex.Message}");
                    }
                    finally
                    {
                        productDoc.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during execution: {ex.Message}");
            }
            finally
            {
                if (catiaApp != null)
                {
                    Marshal.ReleaseComObject(catiaApp);
                }
            }

            Console.WriteLine("\nTask finished. Press any key to exit.");
            Console.ReadKey();
        }

        private static void ReplaceReferencesInProduct(Product currentProduct, Dictionary<string, string> renameMap, HashSet<string> replacedPartNumbers)
        {
            Products childProducts = currentProduct.Products;
            if (childProducts == null) return;

            // We gather candidates into lists to prevent modifying/shifting the COM collection while iterating
            List<Product> childrenToReplace = new List<Product>();
            List<Product> subProductsToRecurse = new List<Product>();

            int count = childProducts.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    Product child = childProducts.Item(i);
                    string partNumber = child.PartNumber;

                    string matchingOldPath = FindMatchingKeyInMap(partNumber, renameMap);
                    if (matchingOldPath != null)
                    {
                        if (!replacedPartNumbers.Contains(partNumber))
                        {
                            childrenToReplace.Add(child);
                        }
                    }
                    else
                    {
                        // Safely check if the child is a subassembly to traverse into
                        bool isSubAssembly = false;
                        try
                        {
                            isSubAssembly = (child.Products != null && child.Products.Count > 0);
                        }
                        catch { }

                        if (isSubAssembly)
                        {
                            subProductsToRecurse.Add(child);
                        }
                    }
                }
                catch (COMException)
                {
                    // Ignore transient COM issues during traversal
                }
            }

            // Perform component replacements
            foreach (Product child in childrenToReplace)
            {
                try
                {
                    string partNumber = child.PartNumber;
                    string matchingOldPath = FindMatchingKeyInMap(partNumber, renameMap);

                    if (matchingOldPath != null && !replacedPartNumbers.Contains(partNumber))
                    {
                        string newPath = renameMap[matchingOldPath];
                        Console.WriteLine($"  Replacing component: {partNumber}");
                        Console.WriteLine($"    Old Path: {matchingOldPath}");
                        Console.WriteLine($"    New Path: {newPath}");

                        // Replaces the element and propagates to all other instances (multi-instances = true)
                        childProducts.ReplaceComponent(child, newPath, true);
                        replacedPartNumbers.Add(partNumber);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed to replace reference: {ex.Message}");
                }
            }

            // Recurse into nested sub-assemblies
            foreach (Product subProd in subProductsToRecurse)
            {
                try
                {
                    // Verify if the sub-assembly COM object is still alive
                    string nameCheck = subProd.Name;
                    ReplaceReferencesInProduct(subProd, renameMap, replacedPartNumbers);
                }
                catch (COMException)
                {
                    // Object is invalid (e.g. its parent assembly was replaced); skip safely
                }
            }
        }

        private static string FindMatchingKeyInMap(string partNumber, Dictionary<string, string> renameMap)
        {
            foreach (var key in renameMap.Keys)
            {
                // Extracts "Part1" from "C:\...\Part1.CATPart"
                string fileName = Path.GetFileNameWithoutExtension(key);
                if (string.Equals(fileName, partNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
            return null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }
    }
}