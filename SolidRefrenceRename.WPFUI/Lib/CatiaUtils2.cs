
using INFITF;
using ProductStructureTypeLib;
using SolidRefrenceRename.WPFUI.Lib;
using SolidRefrenceRename.WPFUI.Models;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using File = System.IO.File;

namespace CatiaReferenceRename.WPFUI.Lib
{
    public class CatiaUtils2
    {
        private static INFITF.Application catiaApp;

        /*
         * IMPORTANT:
         * Do not keep CATIA Document/Product/View COM objects in static fields.
         * They can keep large portions of CATIA's object graph alive for a long
         * time when thousands of documents are processed.
         */

        private static int processedDocumentCount = 0;

        // Perform a full .NET COM cleanup periodically rather than after every file.
        // This is configurable in case performance testing shows a better value.
        private const int GcCleanupInterval = 50;

        public static event EventHandler<string> LogErrors;

        #region Public API -----------------------------------------------------

        /// <summary>
        /// Opens a CATIA product (*.CATProduct) or drawing (*.CATDrawing) and
        /// replaces every referenced part/assembly whose path is present in
        /// <paramref name="newPartAddressMap"/>.
        /// </summary>
        public static PartChangingOutput ChangePartAddress(
            string currentAssemblyPath,
            Dictionary<string, string> newPartAddressMap)
        {
            var errorsList = new List<ProcessOutputDetails>();
            var newAssembliesFound = new List<string>();
            var convertedItems = new List<string>();

            try
            {
                // -------------------------------------------------------------
                // 1. Initialise / attach to CATIA
                // -------------------------------------------------------------
                if (!TryInitCatia())
                {
                    return Fail(
                        "Failed to initialise CATIA.",
                        currentAssemblyPath);
                }

                // -------------------------------------------------------------
                // 2. Normalise rename map
                // -------------------------------------------------------------
                string baseDir = Path.GetDirectoryName(currentAssemblyPath);

                var renameMap = NormaliseRenameMap(
                    newPartAddressMap,
                    baseDir);

                // -------------------------------------------------------------
                // 3. Validate input
                // -------------------------------------------------------------
                if (!File.Exists(currentAssemblyPath))
                {
                    return Fail(
                        $"Assembly not found on disk: {currentAssemblyPath}",
                        currentAssemblyPath);
                }

                // -------------------------------------------------------------
                // 4. Dispatch
                // -------------------------------------------------------------
                string ext = Path.GetExtension(currentAssemblyPath)
                    .ToLowerInvariant();

                if (ext == ".catproduct")
                {
                    ProcessProduct(
                        currentAssemblyPath,
                        renameMap,
                        convertedItems,
                        newAssembliesFound);
                }
                else if (ext == ".catdrawing")
                {
                    ProcessDrawing(
                        currentAssemblyPath,
                        renameMap,
                        convertedItems,
                        newAssembliesFound);
                }
                else
                {
                    return Fail(
                        $"Unsupported file type \"{ext}\".",
                        currentAssemblyPath);
                }

                // -------------------------------------------------------------
                // 5. Periodic managed/COM cleanup
                // -------------------------------------------------------------
                PeriodicGarbageCollection();

                return new PartChangingOutput
                {
                    Succeed = true,
                    Errors = errorsList,
                    NewAssembliesFound = newAssembliesFound
                };
            }
            catch (Exception ex)
            {
                return Fail(
                    $"Unexpected error: {ex.Message}",
                    currentAssemblyPath);
            }
        }

        #endregion ------------------------------------------------------------

        #region CATIA initialisation -------------------------------------------

        private static void ConsoleWriteLine(string message)
        {
            Console.WriteLine(message);

            try
            {
                LogErrors?.Invoke(null, message);
            }
            catch
            {
                // Logging must never interfere with CATIA processing.
            }
        }

        private static bool TryInitCatia()
        {
            if (catiaApp != null)
            {
                try
                {
                    // Touch the object to make sure the COM server is still alive.
                    _ = catiaApp.Documents.Count;

                    return true;
                }
                catch
                {
                    ReleaseComObject(catiaApp);
                    catiaApp = null;
                }
            }

            try
            {
                catiaApp =
                    (INFITF.Application)Marshal.GetActiveObject(
                        "CATIA.Application");

                ConsoleWriteLine(
                    "Connected to a running CATIA instance.");

                return true;
            }
            catch (COMException)
            {
                ConsoleWriteLine(
                    "CATIA not running - launching a new background session.");

                try
                {
                    Type catiaType =
                        Type.GetTypeFromProgID("CATIA.Application");

                    if (catiaType == null)
                    {
                        ConsoleWriteLine(
                            "CATIA.Application ProgID was not found.");

                        return false;
                    }

                    catiaApp =
                        (INFITF.Application)Activator.CreateInstance(
                            catiaType);

                    catiaApp.Visible = true;

                    return true;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"Failed to start CATIA: {ex.Message}");

                    return false;
                }
            }
        }

        #endregion ------------------------------------------------------------

        #region Product handling ----------------------------------------------

        private static void ProcessProduct(
            string productPath,
            Dictionary<string, string> renameMap,
            List<string> convertedItems,
            List<string> newAssembliesFound)
        {
            ConsoleWriteLine(
                $"\nOpening assembly: {productPath}");

            ProductDocument productDoc = null;
            Product rootProduct = null;

            try
            {
                // -------------------------------------------------------------
                // Open document
                // -------------------------------------------------------------
                productDoc = OpenProductDocument(productPath);

                if (productDoc == null)
                {
                    throw new Exception(
                        "ProductDocument returned null after opening.");
                }

                rootProduct = productDoc.Product;

                if (rootProduct == null)
                {
                    throw new Exception(
                        "ProductDocument.Product returned null.");
                }

                var alreadyReplaced =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                ReplaceReferencesInProduct(
                    rootProduct,
                    renameMap,
                    alreadyReplaced,
                    convertedItems);

                // -------------------------------------------------------------
                // Save
                // -------------------------------------------------------------
                try
                {
                    productDoc.Save();

                    ConsoleWriteLine(
                        "Assembly saved successfully.");
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"Failed to save assembly '{productPath}': " +
                        $"{ex.Message}");

                    throw;
                }

                newAssembliesFound.Add(productPath);
            }
            finally
            {
                // -------------------------------------------------------------
                // IMPORTANT:
                //
                // Close ONLY this document.
                //
                // We deliberately do NOT call:
                //
                //     productDoc.Close();
                //     CloseAllDocumentsExcept(...);
                //
                // because the second operation can close the same document
                // and leave productDoc pointing to an invalid CATIA COM object.
                // -------------------------------------------------------------
                CloseDocumentByFullPath(
                    productPath,
                    "product");

                ReleaseComObject(rootProduct);
                rootProduct = null;

                ReleaseComObject(productDoc);
                productDoc = null;

                PeriodicGarbageCollection();
            }
        }

        private static ProductDocument OpenProductDocument(
            string productPath)
        {
            try
            {
                dynamic documents = catiaApp.Documents;

                try
                {
                    dynamic existing =
                        FindOpenDocumentByFullPath(
                            documents,
                            productPath);

                    if (existing != null)
                    {
                        ConsoleWriteLine(
                            $"Product already open: {productPath}");

                        return (ProductDocument)existing;
                    }
                }
                catch
                {
                    // Continue with Open().
                }

                ProductDocument doc =
                    (ProductDocument)documents.Open(productPath);

                ConsoleWriteLine(
                    $"Product opened: {productPath}");

                return doc;
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to open assembly: {ex.Message}",
                    ex);
            }
        }

        /// <summary>
        /// Recursively walks a Product tree and replaces every child component
        /// whose file name matches a key in renameMap.
        /// </summary>
        private static void ReplaceReferencesInProduct(
            Product currentProduct,
            Dictionary<string, string> renameMap,
            HashSet<string> replacedPartNumbers,
            List<string> convertedItems)
        {
            Products childProducts = null;

            try
            {
                childProducts = currentProduct.Products;

                if (childProducts == null)
                    return;

                var toReplace = new List<Product>();
                var toRecurse = new List<Product>();

                int count = childProducts.Count;

                for (int i = 1; i <= count; i++)
                {
                    Product child = null;

                    try
                    {
                        child = childProducts.Item(i);

                        string partNumber = child.get_Name();

                        if (string.IsNullOrWhiteSpace(partNumber))
                            continue;

                        partNumber =
                            StripInstanceSuffix(partNumber);

                        string oldKey =
                            FindMatchingKeyInMap(
                                partNumber,
                                renameMap);

                        if (oldKey != null &&
                            !replacedPartNumbers.Contains(partNumber))
                        {
                            /*
                             * Keep this COM reference alive because it will be
                             * used in the replacement phase.
                             */
                            toReplace.Add(child);
                            child = null;
                        }
                        else
                        {
                            bool hasChildren = false;

                            try
                            {
                                Products children = null;

                                try
                                {
                                    children = child.Products;

                                    hasChildren =
                                        children != null &&
                                        children.Count > 0;
                                }
                                finally
                                {
                                    ReleaseComObject(children);
                                }
                            }
                            catch
                            {
                                // Ignore invalid/transient CATIA objects.
                            }

                            if (hasChildren)
                            {
                                /*
                                 * Keep this object alive for recursion.
                                 */
                                toRecurse.Add(child);
                                child = null;
                            }
                        }
                    }
                    catch (COMException ex)
                    {
                        ConsoleWriteLine(
                            $"    COM issue while reading product " +
                            $"child {i}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine(
                            $"    Issue while reading product " +
                            $"child {i}: {ex.Message}");
                    }
                    finally
                    {
                        ReleaseComObject(child);
                    }
                }

                // -------------------------------------------------------------
                // Perform replacements
                // -------------------------------------------------------------
                foreach (Product child in toReplace)
                {
                    try
                    {
                        string partNumber =
                            StripInstanceSuffix(child.get_Name());

                        string oldKey =
                            FindMatchingKeyInMap(
                                partNumber,
                                renameMap);

                        if (oldKey == null)
                            continue;

                        string newPath = renameMap[oldKey];

                        ConsoleWriteLine(
                            $"  Replacing component: {partNumber}");

                        ConsoleWriteLine(
                            $"    Old Path: {oldKey}");

                        ConsoleWriteLine(
                            $"    New Path: {newPath}");

                        childProducts.ReplaceComponent(
                            child,
                            newPath,
                            true);

                        replacedPartNumbers.Add(partNumber);
                        convertedItems.Add(oldKey);
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine(
                            $"    Failed to replace reference: " +
                            $"{ex.Message}");
                    }
                    finally
                    {
                        ReleaseComObject(child);
                    }
                }

                toReplace.Clear();

                // -------------------------------------------------------------
                // Recurse into subassemblies
                // -------------------------------------------------------------
                foreach (Product sub in toRecurse)
                {
                    try
                    {
                        string name = sub.get_Name();

                        ReplaceReferencesInProduct(
                            sub,
                            renameMap,
                            replacedPartNumbers,
                            convertedItems);
                    }
                    catch (COMException ex)
                    {
                        ConsoleWriteLine(
                            $"    COM issue while recursing into " +
                            $"subassembly: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine(
                            $"    Issue while recursing into " +
                            $"subassembly: {ex.Message}");
                    }
                    finally
                    {
                        ReleaseComObject(sub);
                    }
                }

                toRecurse.Clear();
            }
            finally
            {
                ReleaseComObject(childProducts);
            }
        }

        #endregion ------------------------------------------------------------

        #region Drawing handling ----------------------------------------------

        private static void ProcessDrawing(
            string drawingPath,
            Dictionary<string, string> renameMap,
            List<string> convertedItems,
            List<string> newAssembliesFound)
        {
            ConsoleWriteLine(
                $"\nOpening drawing: {drawingPath}");

            var placeholders =
                CreatePlaceholdersForMissingLinks(
                    renameMap,
                    drawingPath);

            var createdPlaceholders =
                placeholders.Item1;

            var unavailableKeys =
                placeholders.Item2;

            List<string> preloadedPaths = null;

            dynamic drawingDoc = null;

            try
            {
                // -------------------------------------------------------------
                // Pre-load required old documents
                // -------------------------------------------------------------
                preloadedPaths =
                    PreloadOldDocuments(
                        renameMap,
                        unavailableKeys,
                        createdPlaceholders,
                        drawingPath);

                // -------------------------------------------------------------
                // Open drawing
                // -------------------------------------------------------------
                drawingDoc =
                    OpenDocument(drawingPath);

                if (drawingDoc == null)
                {
                    throw new Exception(
                        "DrawingDocument returned null after opening.");
                }

                // -------------------------------------------------------------
                // Read phase
                // -------------------------------------------------------------
                var workList =
                    CollectDrawingWorkItems(
                        drawingDoc,
                        renameMap,
                        unavailableKeys);

                // -------------------------------------------------------------
                // Close old documents and load new documents
                // -------------------------------------------------------------
                var newDocs =
                    LoadReplacementDocuments(workList);

                try
                {
                    // ---------------------------------------------------------
                    // Apply replacements
                    // ---------------------------------------------------------
                    ApplyDrawingReplacements(
                        workList,
                        newDocs,
                        convertedItems);

                    // ---------------------------------------------------------
                    // Regenerate
                    // ---------------------------------------------------------
                    try
                    {
                        drawingDoc.Update();
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine(
                            $"    Drawing update failed: {ex.Message}");
                    }

                    // ---------------------------------------------------------
                    // Save
                    // ---------------------------------------------------------
                    drawingDoc.Save();

                    ConsoleWriteLine(
                        "Drawing saved successfully.");

                    newAssembliesFound.Add(drawingPath);
                }
                finally
                {
                    /*
                     * Release document references AFTER ApplyDrawingReplacements.
                     * The documents are still open here; we close them below.
                     */

                    ReleaseDictionaryComObjects(newDocs);

                    foreach (var item in workList)
                    {
                        ReleaseComObject(item.View);
                        item.View = null;
                    }

                    workList.Clear();
                }
            }
            finally
            {
                // -------------------------------------------------------------
                // IMPORTANT ORDER
                //
                // 1. Close replacement documents.
                // 2. Close preloaded old documents.
                // 3. Close the drawing itself.
                // 4. Release drawing COM reference.
                // 5. Delete placeholders.
                //
                // There is deliberately NO "CloseAllDocumentsExcept".
                // -------------------------------------------------------------

                // Close documents loaded as replacements.
                //
                // We cannot rely solely on the dictionary here because it was
                // released above. Re-acquire them by path from CATIA.
                //
                // The work list has already been cleared, therefore the
                // original replacement paths are not available here.
                //
                // Close everything except the drawing as a final safety net,
                // but this implementation never closes the drawing.
                CloseAllDocumentsExcept(
                    drawingPath,
                    "drawing cleanup");

                // Close preloaded old documents explicitly.
                if (preloadedPaths != null)
                {
                    foreach (string path in
                        preloadedPaths.Distinct(
                            StringComparer.OrdinalIgnoreCase))
                    {
                        CloseDocumentByFullPath(
                            path,
                            "preloaded drawing document");
                    }

                    preloadedPaths.Clear();
                }

                // Close drawing exactly once.
                CloseDocumentByFullPath(
                    drawingPath,
                    "drawing");

                // Now the COM reference can safely be released.
                ReleaseComObject(drawingDoc);
                drawingDoc = null;

                DeletePlaceholders(
                    createdPlaceholders);

                PeriodicGarbageCollection();
            }
        }

        /// <summary>
        /// Opens every old document of the rename map that exists on disk and
        /// whose name is present in newDocuments.
        /// </summary>
        private static List<string> PreloadOldDocuments(
            Dictionary<string, string> renameMap,
            HashSet<string> unavailableKeys,
            List<string> newDocuments,
            string drawingPath)
        {
            var preloaded =
                new List<string>();

            foreach (var kvp in renameMap)
            {
                string oldPath = kvp.Key;

                if (unavailableKeys.Contains(oldPath))
                    continue;

                string oldBaseName =
                    Path.GetFileNameWithoutExtension(oldPath);

                if (!newDocuments.Contains(
                        oldBaseName))
                {
                    continue;
                }

                if (!File.Exists(oldPath))
                    continue;

                dynamic doc = null;

                try
                {
                    doc = OpenDocument(oldPath);

                    if (doc != null)
                    {
                        preloaded.Add(oldPath);

                        ConsoleWriteLine(
                            $"  Pre-loaded linked document: {oldPath}");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"    Could not pre-load '{oldPath}': " +
                        $"{ex.Message}");
                }
                finally
                {
                    /*
                     * IMPORTANT:
                     *
                     * Do NOT release a document that we just opened here.
                     * CATIA still needs it loaded and it is represented by
                     * the path in preloaded.
                     *
                     * We therefore intentionally do NOT release 'doc' here.
                     */
                }
            }

            return preloaded;
        }

        /// <summary>
        /// Closes all open documents except the specified main document.
        ///
        /// This is a safety function, not the normal document lifecycle.
        /// Each document is re-acquired from CATIA before attempting to close
        /// it, and no COM object is closed twice.
        /// </summary>
        private static void CloseAllDocumentsExcept(
            string mainDocumentPath,
            string reason = null)
        {
            string mainPath =
                NormalizePath(mainDocumentPath);

            try
            {
                dynamic documents =
                    catiaApp.Documents;

                int count =
                    documents.Count;

                /*
                 * Build a snapshot first.
                 *
                 * Do not keep a live CATIA document COM reference while
                 * modifying Documents collection.
                 */
                var pathsToClose =
                    new List<string>();

                for (int i = 1; i <= count; i++)
                {
                    dynamic doc = null;

                    try
                    {
                        doc = documents.Item(i);

                        string fullName =
                            GetDocumentFullPath(doc);

                        if (string.IsNullOrWhiteSpace(fullName))
                            continue;

                        if (!string.Equals(
                                fullName,
                                mainPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            pathsToClose.Add(fullName);
                        }
                    }
                    catch
                    {
                        // Ignore unnamed/dead CATIA documents.
                    }
                    finally
                    {
                        ReleaseComObject(doc);
                    }
                }

                foreach (string path in pathsToClose.Distinct(
                    StringComparer.OrdinalIgnoreCase))
                {
                    CloseDocumentByFullPath(
                        path,
                        reason ?? "CloseAllDocumentsExcept");
                }

                pathsToClose.Clear();
            }
            catch (Exception ex)
            {
                ConsoleWriteLine(
                    $"Failed to enumerate CATIA documents: " +
                    $"{ex.Message}");
            }
        }

        #endregion ------------------------------------------------------------

        #region Drawing work items --------------------------------------------

        private class DrawingWorkItem
        {
            public dynamic View;
            public string SheetName;
            public string OldPath;
            public string NewPath;
        }

        private static List<DrawingWorkItem>
            CollectDrawingWorkItems(
                dynamic drawingDoc,
                Dictionary<string, string> renameMap,
                HashSet<string> unavailableKeys)
        {
            var workList =
                new List<DrawingWorkItem>();

            var knownNewPaths =
                new HashSet<string>(
                    renameMap.Values,
                    StringComparer.OrdinalIgnoreCase);

            dynamic sheets = null;

            try
            {
                sheets = drawingDoc.Sheets;

                int sheetCount =
                    sheets.Count;

                for (int s = 1; s <= sheetCount; s++)
                {
                    dynamic sheet = null;
                    dynamic views = null;

                    try
                    {
                        sheet =
                            sheets.Item(s);

                        string sheetName =
                            SafeGetString(
                                sheet,
                                "Name");

                        views =
                            sheet.Views;

                        int viewCount =
                            views.Count;

                        for (int v = 1; v <= viewCount; v++)
                        {
                            dynamic view = null;

                            try
                            {
                                view =
                                    views.Item(v);

                                string oldPath =
                                    GetReferencePath(view);

                                if (string.IsNullOrWhiteSpace(oldPath))
                                {
                                    ConsoleWriteLine(
                                        $"    View \"{GetViewNameSafe(view)}\" " +
                                        $"on sheet \"{sheetName}\" has no " +
                                        $"readable 3D link - skipped.");

                                    continue;
                                }

                                if (knownNewPaths.Contains(oldPath))
                                {
                                    ConsoleWriteLine(
                                        $"  View on sheet \"{sheetName}\" " +
                                        $"is already up-to-date: {oldPath}");

                                    continue;
                                }

                                string matchingKey =
                                    renameMap.ContainsKey(oldPath)
                                        ? oldPath
                                        : FindMatchingKeyInMap(
                                            Path.GetFileNameWithoutExtension(
                                                oldPath),
                                            renameMap);

                                if (matchingKey == null)
                                    continue;

                                if (unavailableKeys.Contains(
                                    matchingKey))
                                {
                                    continue;
                                }

                                string newPath =
                                    renameMap[matchingKey];

                                if (string.Equals(
                                    oldPath,
                                    newPath,
                                    StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                /*
                                 * Keep the View COM object alive until the
                                 * replacement has been applied.
                                 */
                                workList.Add(
                                    new DrawingWorkItem
                                    {
                                        View = view,
                                        SheetName = sheetName,
                                        OldPath = oldPath,
                                        NewPath = newPath
                                    });

                                view = null;
                            }
                            catch (COMException comEx)
                            {
                                ConsoleWriteLine(
                                    $"    COM issue on view {v}: " +
                                    $"{comEx.Message}");
                            }
                            catch (Exception ex)
                            {
                                ConsoleWriteLine(
                                    $"    Unexpected issue on view {v}: " +
                                    $"{ex.Message}");
                            }
                            finally
                            {
                                ReleaseComObject(view);
                            }
                        }
                    }
                    finally
                    {
                        ReleaseComObject(views);
                        ReleaseComObject(sheet);
                    }
                }
            }
            finally
            {
                ReleaseComObject(sheets);
            }

            return workList;
        }

        private static string GetViewNameSafe(dynamic view)
        {
            try
            {
                return (string)view.Name;
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// Closes old documents and opens replacement documents.
        /// </summary>
        private static Dictionary<string, dynamic>
            LoadReplacementDocuments(
                List<DrawingWorkItem> workList)
        {
            var newDocs =
                new Dictionary<string, dynamic>(
                    StringComparer.OrdinalIgnoreCase);

            if (workList == null ||
                workList.Count == 0)
            {
                return newDocs;
            }

            // -------------------------------------------------------------
            // Close old documents FIRST.
            // -------------------------------------------------------------
            foreach (string oldPath in
                workList
                    .Select(w => w.OldPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CloseDocumentByFullPath(
                    oldPath,
                    "old drawing reference");
            }

            // -------------------------------------------------------------
            // Open replacement documents.
            // -------------------------------------------------------------
            foreach (string newPath in
                workList
                    .Select(w => w.NewPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                dynamic doc = null;

                try
                {
                    doc =
                        GetOrOpenDocument(newPath);

                    if (doc == null)
                    {
                        ConsoleWriteLine(
                            $"    Failed to load replacement: {newPath}");

                        continue;
                    }

                    string loadedPath =
                        GetDocumentFullPath(doc);

                    if (!string.Equals(
                        loadedPath,
                        NormalizePath(newPath),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleWriteLine(
                            $"    ERROR: name conflict - CATIA returned " +
                            $"'{loadedPath}' instead of '{newPath}'.");

                        ReleaseComObject(doc);
                        doc = null;

                        continue;
                    }

                    /*
                     * The dictionary owns this COM reference until the
                     * replacement has been applied.
                     */
                    newDocs[newPath] = doc;
                    doc = null;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"    Failed to open '{newPath}': " +
                        $"{ex.Message}");
                }
                finally
                {
                    ReleaseComObject(doc);
                }
            }

            return newDocs;
        }

        private static void ApplyDrawingReplacements(
            List<DrawingWorkItem> workList,
            Dictionary<string, dynamic> newDocs,
            List<string> convertedItems)
        {
            foreach (var item in workList)
            {
                try
                {
                    if (!newDocs.TryGetValue(
                        item.NewPath,
                        out dynamic newDoc))
                    {
                        ConsoleWriteLine(
                            $"    Skipped - new document not loaded: " +
                            $"{item.NewPath}");

                        continue;
                    }

                    ConsoleWriteLine(
                        $"  Replacing view reference on sheet " +
                        $"\"{item.SheetName}\":");

                    ConsoleWriteLine(
                        $"    Old Path: {item.OldPath}");

                    ConsoleWriteLine(
                        $"    New Path: {item.NewPath}");

                    item.View
                        .GenerativeBehavior
                        .Document = newDoc;

                    convertedItems.Add(
                        item.OldPath);
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"    Failed to replace drawing view reference: " +
                        $"{ex.Message}");
                }
            }
        }

        #endregion ------------------------------------------------------------

        #region Placeholder files ---------------------------------------------

        private static Tuple<
            List<string>,
            HashSet<string>>
            CreatePlaceholdersForMissingLinks(
                Dictionary<string, string> renameMap,
                string drawingPath)
        {
            var created =
                new List<string>();

            var failed =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            string drawingBaseName =
                Path.GetFileNameWithoutExtension(
                    drawingPath);

            foreach (var kvp in renameMap)
            {
                string oldPath = kvp.Key;
                string newPath = kvp.Value;

                string oldFileName =
                    Path.GetFileNameWithoutExtension(
                        oldPath);

                string oldBaseName =
                    Path.GetFileNameWithoutExtension(
                        oldFileName);

                if (!drawingBaseName.ToLower().Contains(
                    oldBaseName.ToLower() + "-"))
                {
                    continue;
                }

                if (!File.Exists(newPath))
                {
                    failed.Add(oldPath);

                    ConsoleWriteLine(
                        $"    ERROR: new file not found - " +
                        $"cannot fix link: {newPath}");

                    continue;
                }

                try
                {
                    string dir =
                        Path.GetDirectoryName(oldPath);

                    if (!string.IsNullOrEmpty(dir) &&
                        !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (!File.Exists(oldPath))
                    {
                        File.Copy(
                            newPath,
                            oldPath,
                            overwrite: false);

                        ConsoleWriteLine(
                            $"  Temporary placeholder created: " +
                            $"{oldPath}");

                        /*
                         * Store the FULL path.
                         *
                         * The original implementation stored only the
                         * filename, which makes cleanup unsafe when the
                         * placeholder lives outside the process directory.
                         */
                        created.Add(oldPath);
                    }
                    else
                    {
                        ConsoleWriteLine(
                            $"  Temporary placeholder already exists: " +
                            $"{oldPath}");
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(oldPath);

                    ConsoleWriteLine(
                        $"    ERROR: could not create placeholder " +
                        $"'{oldPath}': {ex.Message}");
                }
            }

            return Tuple.Create(
                created,
                failed);
        }

        private static void DeletePlaceholders(
            List<string> createdPlaceholders)
        {
            if (createdPlaceholders == null)
                return;

            foreach (string path in createdPlaceholders)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);

                        ConsoleWriteLine(
                            $"  Temporary placeholder removed: " +
                            $"{path}");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"    WARNING: could not delete temporary " +
                        $"placeholder '{path}' - please remove it " +
                        $"manually. ({ex.Message})");
                }
            }

            createdPlaceholders.Clear();
        }

        #endregion ------------------------------------------------------------

        #region COM document management ---------------------------------------

        /// <summary>
        /// Opens a document through CATIA.
        ///
        /// If the document is already open, the existing document is returned.
        /// </summary>
        private static dynamic OpenDocument(
            string path)
        {
            if (catiaApp == null)
                throw new InvalidOperationException(
                    "CATIA application is not initialised.");

            dynamic documents = null;

            try
            {
                documents =
                    catiaApp.Documents;

                dynamic existing =
                    FindOpenDocumentByFullPath(
                        documents,
                        path);

                if (existing != null)
                {
                    return existing;
                }

                return documents.Open(path);
            }
            finally
            {
                /*
                 * Do not release 'documents' aggressively here.
                 *
                 * CATIA's Documents collection is a relatively short-lived
                 * COM proxy, but releasing it while a returned document is
                 * still being established can be problematic with older CATIA
                 * versions.
                 */
                ReleaseComObject(documents);
            }
        }

        /// <summary>
        /// Returns an already-open document or opens it.
        /// </summary>
        private static dynamic GetOrOpenDocument(
            string path)
        {
            dynamic documents = null;

            try
            {
                documents =
                    catiaApp.Documents;

                dynamic existing =
                    FindOpenDocumentByFullPath(
                        documents,
                        path);

                if (existing != null)
                {
                    return existing;
                }

                try
                {
                    return documents.Open(path);
                }
                catch
                {
                    /*
                     * Some CATIA versions can report an error when opening
                     * an already-loaded document. Re-acquire it by filename.
                     */
                    dynamic byName =
                        documents.Item(
                            Path.GetFileName(path));

                    if (byName != null)
                        return byName;

                    throw;
                }
            }
            finally
            {
                ReleaseComObject(documents);
            }
        }

        /// <summary>
        /// Finds an open CATIA document by FULL PATH.
        ///
        /// A fresh COM reference is returned. The caller owns that reference.
        /// </summary>
        private static dynamic FindOpenDocumentByFullPath(
            dynamic documents,
            string targetPath)
        {
            if (documents == null)
                return null;

            string normalizedTarget =
                NormalizePath(targetPath);

            int count =
                documents.Count;

            for (int i = 1; i <= count; i++)
            {
                dynamic doc = null;

                try
                {
                    doc =
                        documents.Item(i);

                    string fullName =
                        GetDocumentFullPath(doc);

                    if (string.Equals(
                        fullName,
                        normalizedTarget,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        /*
                         * Transfer ownership of this COM reference to caller.
                         */
                        dynamic result = doc;
                        doc = null;

                        return result;
                    }
                }
                catch
                {
                    // Ignore dead/unnamed documents.
                }
                finally
                {
                    ReleaseComObject(doc);
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the normalised full path of a CATIA document.
        /// </summary>
        private static string GetDocumentFullPath(
            dynamic doc)
        {
            if (doc == null)
                return string.Empty;

            try
            {
                string fullName =
                    doc.FullName as string;

                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    return NormalizePath(fullName);
                }
            }
            catch
            {
                // Ignore invalid COM object.
            }

            return string.Empty;
        }

        /// <summary>
        /// Safely closes one CATIA document by full path.
        ///
        /// IMPORTANT:
        /// We never call Close() twice on the same COM reference.
        ///
        /// If the first attempt fails, the second attempt obtains a NEW COM
        /// reference from CATIA.Documents.
        /// </summary>
        private static bool CloseDocumentByFullPath(
            string fullPath,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return true;

            string targetPath =
                NormalizePath(fullPath);

            if (catiaApp == null)
                return true;

            const int maxAttempts = 2;

            for (int attempt = 1;
                 attempt <= maxAttempts;
                 attempt++)
            {
                dynamic documents = null;
                dynamic doc = null;

                try
                {
                    documents =
                        catiaApp.Documents;

                    /*
                     * IMPORTANT:
                     *
                     * Re-acquire the document every attempt.
                     * Never reuse a COM proxy whose Close() has already failed.
                     */
                    doc =
                        FindOpenDocumentByFullPath(
                            documents,
                            targetPath);

                    if (doc == null)
                    {
                        /*
                         * The document is already closed.
                         */
                        return true;
                    }

                    ConsoleWriteLine(
                        $"  Closing CATIA document " +
                        $"(attempt {attempt}/{maxAttempts}, {reason}): " +
                        $"{targetPath}");

                    try
                    {
                        ((INFITF.Document)doc).Close();
                    }
                    catch (COMException ex)
                    {
                        ConsoleWriteLine(
                            $"    CATIA Close COM error for " +
                            $"'{targetPath}': " +
                            $"HRESULT=0x{ex.HResult:X8}, " +
                            $"Message={ex.Message}");

                        /*
                         * Do NOT call Close() again on 'doc'.
                         *
                         * The finally block releases this RCW.
                         * The next attempt obtains a fresh COM reference.
                         */
                        if (attempt < maxAttempts)
                        {
                            Thread.Sleep(100);
                            continue;
                        }

                        return false;
                    }

                    /*
                     * The Close call succeeded. Verify that CATIA no longer
                     * exposes this document.
                     */
                    if (!IsDocumentOpen(targetPath))
                    {
                        ConsoleWriteLine(
                            $"  Closed CATIA document: " +
                            $"{targetPath}");

                        return true;
                    }

                    /*
                     * CATIA reported success but the document is still in
                     * Documents. This can happen with older CATIA releases.
                     *
                     * Retry using a fresh COM reference.
                     */
                    ConsoleWriteLine(
                        $"    CATIA reported Close success but " +
                        $"document is still open: {targetPath}");

                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    return false;
                }
                catch (COMException ex)
                {
                    ConsoleWriteLine(
                        $"    CATIA COM error while closing " +
                        $"'{targetPath}': " +
                        $"HRESULT=0x{ex.HResult:X8}, " +
                        $"Message={ex.Message}");

                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"    Error while closing " +
                        $"'{targetPath}': {ex.Message}");

                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    return false;
                }
                finally
                {
                    /*
                     * Release ONLY this local document reference.
                     *
                     * We never release catiaApp here.
                     */
                    ReleaseComObject(doc);
                    ReleaseComObject(documents);

                    doc = null;
                    documents = null;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether a document is still present in CATIA.
        /// </summary>
        private static bool IsDocumentOpen(
            string fullPath)
        {
            dynamic documents = null;
            dynamic doc = null;

            try
            {
                documents =
                    catiaApp.Documents;

                doc =
                    FindOpenDocumentByFullPath(
                        documents,
                        fullPath);

                return doc != null;
            }
            catch
            {
                /*
                 * If CATIA itself cannot enumerate the document collection,
                 * do not claim that the document is closed.
                 */
                return true;
            }
            finally
            {
                ReleaseComObject(doc);
                ReleaseComObject(documents);
            }
        }

        /// <summary>
        /// Releases all COM document references in a dictionary.
        /// </summary>
        private static void ReleaseDictionaryComObjects(
            Dictionary<string, dynamic> documents)
        {
            if (documents == null)
                return;

            foreach (var pair in documents)
            {
                ReleaseComObject(pair.Value);
            }

            documents.Clear();
        }

        /// <summary>
        /// Releases a COM RCW safely.
        ///
        /// We intentionally use ReleaseComObject rather than
        /// FinalReleaseComObject. FinalReleaseComObject can invalidate
        /// shared RCWs unexpectedly when CATIA exposes the same underlying
        /// COM object through multiple references.
        /// </summary>
        private static void ReleaseComObject(
            object comObject)
        {
            if (comObject == null)
                return;

            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch
            {
                // COM cleanup must never mask the original operation.
            }
        }

        /// <summary>
        /// Periodically lets .NET release RCWs whose managed references have
        /// disappeared.
        /// </summary>
        private static void PeriodicGarbageCollection()
        {
            processedDocumentCount++;

            if (processedDocumentCount %
                GcCleanupInterval != 0)
            {
                return;
            }

            ConsoleWriteLine(
                $"Performing periodic .NET COM cleanup " +
                $"after {processedDocumentCount} documents.");

            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            GC.WaitForPendingFinalizers();

            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            GC.WaitForPendingFinalizers();
        }

        #endregion ------------------------------------------------------------

        #region Drawing reference helpers ------------------------------------

        private static string GetReferencePath(
            dynamic view)
        {
            dynamic generativeBehavior = null;
            dynamic pointedObject = null;

            try
            {
                generativeBehavior =
                    view.GenerativeBehavior;

                if (generativeBehavior == null)
                    return string.Empty;

                try
                {
                    pointedObject =
                        generativeBehavior.Document;
                }
                catch
                {
                    return string.Empty;
                }

                string path =
                    GetFullNameFromObject(
                        pointedObject);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    return NormalizePath(path);
                }
            }
            catch
            {
                // Not a generative view or broken reference.
            }
            finally
            {
                ReleaseComObject(pointedObject);
                ReleaseComObject(generativeBehavior);
            }

            return string.Empty;
        }

        private static string GetFullNameFromObject(
            dynamic catiaObject)
        {
            dynamic current =
                catiaObject;

            for (int depth = 0;
                 depth < 6 &&
                 current != null;
                 depth++)
            {
                dynamic next = null;

                try
                {
                    string fullName =
                        current.FullName as string;

                    if (!string.IsNullOrWhiteSpace(fullName))
                    {
                        return fullName;
                    }
                }
                catch
                {
                    // Continue through Parent.
                }

                try
                {
                    next =
                        current.Parent;
                }
                catch
                {
                    break;
                }

                /*
                 * Do not release 'current' here.
                 *
                 * In a COM parent chain CATIA may return the same RCW or
                 * an object whose lifetime is tied to the parent.
                 */
                current = next;
            }

            return string.Empty;
        }

        private static string SafeGetString(
            dynamic obj,
            string propertyName)
        {
            if (obj == null)
                return string.Empty;

            try
            {
                //return propertyName switch
                //{
                //    "Name" => (string)obj.Name,
                //    "FullName" => (string)obj.FullName,
                //    _ => string.Empty
                //};
                if (propertyName == "Name")
                    return (string)obj.Name;
                if (propertyName == "FullName")
                    return (string)obj.FullName;
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion ------------------------------------------------------------

        #region Misc helpers ---------------------------------------------------

        private static Dictionary<string, string>
            NormaliseRenameMap(
                Dictionary<string, string> rawMap,
                string baseDirectory = null)
        {
            var norm =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (rawMap == null)
                return norm;

            foreach (var kvp in rawMap)
            {
                norm[
                    NormalizePath(
                        kvp.Key,
                        baseDirectory)] =
                    NormalizePath(
                        kvp.Value,
                        baseDirectory);
            }

            return norm;
        }

        private static PartChangingOutput Fail(
            string message,
            string filePath)
        {
            ConsoleWriteLine(message);

            return new PartChangingOutput
            {
                Succeed = false,

                Errors =
                    new List<ProcessOutputDetails>
                    {
                        new ProcessOutputDetails(
                            message,
                            filePath)
                    },

                NewAssembliesFound = null
            };
        }

        private static string StripInstanceSuffix(
            string partNumber)
        {
            if (string.IsNullOrEmpty(partNumber))
                return partNumber;

            var segments =
                partNumber.Split('.');

            if (segments.Length > 1 &&
                int.TryParse(
                    segments.Last(),
                    out _))
            {
                return string.Join(
                    ".",
                    segments.Take(
                        segments.Length - 1));
            }

            return partNumber;
        }

        private static string FindMatchingKeyInMap(
            string partNumber,
            Dictionary<string, string> renameMap)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                return null;

            foreach (string key in renameMap.Keys)
            {
                string fileName =
                    Path.GetFileNameWithoutExtension(
                        key);

                if (string.Equals(
                    fileName,
                    partNumber,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }

            return null;
        }

        private static string NormalizePath(
            string path,
            string baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                if (!Path.IsPathRooted(path) &&
                    !string.IsNullOrEmpty(baseDirectory))
                {
                    path =
                        Path.Combine(
                            baseDirectory,
                            path);
                }

                string normalized =
                    path.Replace(
                            '/',
                            Path.DirectorySeparatorChar)
                        .Replace(
                            '\\',
                            Path.DirectorySeparatorChar)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);

                return normalized.ToUpperInvariant();
            }
            catch
            {
                return path.Trim()
                    .ToUpperInvariant();
            }
        }

        private static string NormalizePath(
            string path)
        {
            return NormalizePath(
                path,
                baseDirectory: null);
        }

        public static void DoCleanUp()
        {
            /*
             * Do not quit CATIA here.
             *
             * The caller owns the CATIA session.
             *
             * We can, however, force managed COM cleanup if the caller
             * explicitly asks for cleanup.
             */
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            GC.WaitForPendingFinalizers();

            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            GC.WaitForPendingFinalizers();
        }

        #endregion ------------------------------------------------------------
    }
}

