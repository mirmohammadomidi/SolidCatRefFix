using INFITF;
using ProductStructureTypeLib;
using SolidRefrenceRename.WPFUI.Lib;
using SolidRefrenceRename.WPFUI.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static System.Net.WebRequestMethods;

namespace CatiaReferenceRename.WPFUI.Lib
{
    public class CatiaUtils
    {
        private static INFITF.Application catiaApp;
        private static ProductDocument productDoc;

        /// <summary>
        /// The "CATIA cannot SaveAs at all" diagnosis is logged only once per
        /// session - it would otherwise repeat for every drawing.
        /// </summary>
        private static bool saveAsCapabilityReported;

        public static event EventHandler<string> LogErrors;

        #region Public API -----------------------------------------------------

        /// <summary>
        /// Public entry that ensures CATIA COM calls are made from an STA thread.
        /// If the current thread is not STA we spawn an STA thread to run the
        /// actual work to avoid COM E_FAIL caused by apartment mismatch.
        /// </summary>
        public static PartChangingOutput ChangePartAddress(
            string currentAssemblyPath,
            Dictionary<string, string> newPartAddressMap)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                PartChangingOutput result = null;
                Exception threadEx = null;
                using (var done = new System.Threading.AutoResetEvent(false))
                {
                    var t = new Thread(() =>
                    {
                        try
                        {
                            result = ChangePartAddressInternal(currentAssemblyPath, newPartAddressMap);
                        }
                        catch (Exception ex)
                        {
                            threadEx = ex;
                        }
                        finally
                        {
                            done.Set();
                        }
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.IsBackground = true;
                    t.Start();
                    done.WaitOne();
                }

                if (threadEx != null)
                    throw threadEx;

                return result;
            }

            return ChangePartAddressInternal(currentAssemblyPath, newPartAddressMap);
        }

        private static PartChangingOutput ChangePartAddressInternal(
            string currentAssemblyPath,
            Dictionary<string, string> newPartAddressMap)
        {
            var errorsList = new List<ProcessOutputDetails>();
            var newAssembliesFound = new List<string>();
            var convertedItems = new List<string>();

            try
            {
                // -------------------------------------------------------------
                // 1️⃣  Initialise / attach to a CATIA session
                // -------------------------------------------------------------
                if (!TryInitCatia())
                {
                    return Fail("Failed to initialise CATIA.", currentAssemblyPath);
                }

                // -------------------------------------------------------------
                // 2️⃣  Normalise the rename map (case‑insensitive, slashes, etc.)
                // -------------------------------------------------------------
                string baseDir = Path.GetDirectoryName(currentAssemblyPath);
                var renameMap = NormaliseRenameMap(newPartAddressMap, baseDir);

                // -------------------------------------------------------------
                // 3️⃣  Validate the input file
                // -------------------------------------------------------------
                if (!System.IO.File.Exists(currentAssemblyPath))
                {
                    return Fail($"Assembly not found on disk: {currentAssemblyPath}", currentAssemblyPath);
                }

                // -------------------------------------------------------------
                // 4️⃣  Dispatch to the proper handler (product vs drawing)
                // -------------------------------------------------------------
                string ext = Path.GetExtension(currentAssemblyPath).ToLowerInvariant();

                if (ext == ".catproduct")
                {
                    ProcessProduct(currentAssemblyPath, renameMap,
                                   convertedItems, newAssembliesFound);
                }
                else if (ext == ".catdrawing")
                {
                    ProcessDrawing(currentAssemblyPath, renameMap,
                                   convertedItems, newAssembliesFound);
                }
                else
                {
                    return Fail($"Unsupported file type \"{ext}\".", currentAssemblyPath);
                }

                // -------------------------------------------------------------
                // 5️⃣  Return the result
                // -------------------------------------------------------------
                return new PartChangingOutput
                {
                    Succeed = true,
                    Errors = errorsList,
                    NewAssembliesFound = newAssembliesFound
                };
            }
            catch (Exception ex)
            {
                return Fail($"Unexpected error: {ex.Message}", currentAssemblyPath);
            }
            finally
            {
                // NOTE: the CATIA instance lives outside of this class – we do not
                // quit the application here.
            }
        }

        #endregion ------------------------------------------------------------

        #region ──  Private helpers for the public entry point  ─────────────────

        private static void ConsoleWriteLine(string message)
        {
            Console.WriteLine(message);
            if (LogErrors != null)
            {
                LogErrors(null, message);
            }
        }
        private static bool TryInitCatia()
        {
            try
            {
                catiaApp = (INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
                ConsoleWriteLine("Connected to a running CATIA instance.");

                // Suppress modal alert dialogs (broken links, save warnings…).
                // A hidden modal dialog makes every subsequent COM call return
                // E_FAIL until a user clicks it away.
                try { catiaApp.DisplayFileAlerts = false; } catch { }

                return true;
            }
            catch (COMException)
            {
                ConsoleWriteLine("CATIA not running – launching a new background session.");
                try
                {
                    Type catiaType = Type.GetTypeFromProgID("CATIA.Application");
                    catiaApp = (INFITF.Application)Activator.CreateInstance(catiaType);
                    catiaApp.Visible = true;
                    try { catiaApp.DisplayFileAlerts = false; } catch { }

                    // A freshly launched CATIA session answers every COM call
                    // with E_FAIL until its workbenches are fully initialised.
                    // Wait until the session is actually usable.
                    if (!WaitForCatiaReady())
                    {
                        ConsoleWriteLine("CATIA was launched but never became ready (timeout).");
                        return false;
                    }
                    ConsoleWriteLine("CATIA session is ready.");
                    return true;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"Failed to start CATIA: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Polls the freshly started CATIA session until basic API calls
        /// (Documents.Count) succeed. A new session can take 30-90 seconds
        /// before its automation interfaces stop returning E_FAIL.
        /// </summary>
        private static bool WaitForCatiaReady(int timeoutSeconds = 120)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    // These calls fail with E_FAIL while CATIA is booting.
                    var docs = catiaApp.Documents;
                    int _ = docs.Count;
                    string __ = catiaApp.FullName;
                    return true;
                }
                catch (COMException)
                {
                    System.Threading.Thread.Sleep(2000);
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(2000);
                }
            }
            return false;
        }

        private static Dictionary<string, string> NormaliseRenameMap(
            Dictionary<string, string> rawMap,
            string baseDirectory = null)
        {
            var norm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in rawMap)
            {
                norm[NormalizePath(kvp.Key, baseDirectory)] = NormalizePath(kvp.Value, baseDirectory);
            }
            return norm;
        }

        private static PartChangingOutput Fail(string message, string filePath)
        {
            ConsoleWriteLine(message);
            return new PartChangingOutput
            {
                Succeed = false,
                Errors = new List<ProcessOutputDetails> { new ProcessOutputDetails(message, filePath) },
                NewAssembliesFound = null
            };
        }

        #endregion ------------------------------------------------------------

        #region ──  Product (CATProduct) handling  ───────────────────────────────

        private static void ProcessProduct(
            string productPath,
            Dictionary<string, string> renameMap,
            List<string> convertedItems,
            List<string> newAssembliesFound)
        {
            ConsoleWriteLine($"\nOpening assembly: {productPath}");

            productDoc = null;
            try
            {
                productDoc = (ProductDocument)catiaApp.Documents.Open(productPath);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to open assembly: {ex.Message}");
            }

            if (productDoc == null)
                throw new Exception("ProductDocument returned null after opening.");

            var rootProduct = productDoc.Product;
            var alreadyReplaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ReplaceReferencesInProduct(rootProduct, renameMap, alreadyReplaced, convertedItems);

            // Save & close -------------------------------------------------
            try
            {
                productDoc.Save();
                ConsoleWriteLine("Assembly saved successfully.");
            }
            finally
            {
                productDoc.Close();
                CloseAllDocumentsExcept(productPath);
            }

            // The file we just processed must be fed back to the caller so
            // that a possible caller‑side loop can continue with the next item.
            newAssembliesFound.Add(productPath);
        }

        /// <summary>
        /// Recursively walks a Product tree and replaces every child component
        /// whose file name matches a key in <paramref name="renameMap"/>.
        /// </summary>
        private static void ReplaceReferencesInProduct(
            Product currentProduct,
            Dictionary<string, string> renameMap,
            HashSet<string> replacedPartNumbers,
            List<string> convertedItems)
        {
            Products childProducts = currentProduct.Products;
            if (childProducts == null) return;

            // -------------------------------------------------------------
            // 1️⃣  Build two temporary lists – we never modify the COM
            //     collection while we iterate it.
            // -------------------------------------------------------------
            var toReplace = new List<Product>();
            var toRecurse = new List<Product>();

            int count = childProducts.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic child = childProducts.Item(i);
                    string partNumber = child.Name;
                    if (string.IsNullOrWhiteSpace(partNumber)) continue;

                    // Strip the trailing “.n” that CATIA adds for duplicated
                    // instances (e.g. Part1.1, Part1.2, …)
                    partNumber = StripInstanceSuffix(partNumber);

                    // Is there a mapping for this part?
                    string oldKey = FindMatchingKeyInMap(partNumber, renameMap);
                    if (oldKey != null && !replacedPartNumbers.Contains(partNumber))
                    {
                        toReplace.Add(child);
                    }
                    else
                    {
                        // Might be a sub‑assembly – check if it itself contains children
                        bool hasChildren = false;
                        try { hasChildren = child.Products != null && child.Products.Count > 0; }
                        catch { /* ignore */ }

                        if (hasChildren) toRecurse.Add(child);
                    }
                }
                catch (COMException) { /* ignore transient COM issues */ }
            }

            // -------------------------------------------------------------
            // 2️⃣  Perform the actual replacements
            // -------------------------------------------------------------
            foreach (dynamic child in toReplace)
            {
                try
                {
                    string partNumber = StripInstanceSuffix(child.Name);
                    string oldKey = FindMatchingKeyInMap(partNumber, renameMap);
                    if (oldKey == null) continue;               // should not happen

                    string newPath = renameMap[oldKey];
                    ConsoleWriteLine($"  Replacing component: {partNumber}");
                    ConsoleWriteLine($"    Old Path: {oldKey}");
                    ConsoleWriteLine($"    New Path: {newPath}");

                    // true => propagate to every other instance of this component
                    childProducts.ReplaceComponent(child, newPath, true);

                    replacedPartNumbers.Add(partNumber);
                    convertedItems.Add(oldKey);
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    Failed to replace reference: {ex.Message}");
                }
            }

            // -------------------------------------------------------------
            // 3️⃣  Recurse into sub‑assemblies
            // -------------------------------------------------------------
            foreach (Product sub in toRecurse)
            {
                try
                {
                    // Guard against a sub‑assembly that has just been replaced
                    // (its COM proxy would throw).
                    string _ = sub.get_Name(); // will throw if the object is dead
                    ReplaceReferencesInProduct(sub, renameMap, replacedPartNumbers, convertedItems);
                }
                catch (COMException) { /* ignore dead objects */ }
            }
        }

        #endregion ------------------------------------------------------------

        #region ──  Drawing (CATDrawing) handling  ───────────────────────────────

        /// <summary>
        /// Processes a CATDrawing by relinking its referenced parts to new
        /// file locations. The method stages replacement part files under their
        /// original names, opens them so the drawing binds to the staged
        /// documents, performs SaveAs to move staged parts to their real new
        /// locations (forcing CATIA to rewrite the drawing links), updates
        /// and saves the drawing, and performs cleanup of opened documents
        /// and temporary files.
        /// The method ProcessDrawing in CatiaReferenceRename.WPFUI.Lib.CatiaUtils is intended to replace missing refrences of CatPart files in a CATDrawing document.
        /// The method gets the address of the catdrawing file and a dictionary of fileName:New address files.For example
        /// drawingPath: C:\catia\E1031F-F20435V1-1-R1-1.CATDrawing
        /// renameMap: [{F20435V1.CATPart - C:\CatiaParts\E1031F-F20435V1-2-R1-1.CATPart}]
        /// it means the method should replace any refrences(with any address) to the file "F20435V1.CATPart" to the new address C:\CatiaParts\E1031F-F20435V1-2-R1-1.CATPart
        /// However it is not working
        /// </summary>
        private static void ProcessDrawing(
            string drawingPath,
            Dictionary<string, string> renameMap,
            List<string> convertedItems,
            List<string> newAssembliesFound)
        {
            ConsoleWriteLine($"\nProcessing drawing: {drawingPath}");

            string normalizedDrawingPath = NormalizePath(drawingPath);

            // -----------------------------------------------------------------
            // 0-  CATIA resolves external links ONLY while a document is being
            //     opened.  A drawing that is already loaded keeps its broken
            //     links for the whole session, so close it now and let step 3
            //     re-open it AFTER the replacement files have been staged.
            // -----------------------------------------------------------------
            if (IsDocumentOpen(normalizedDrawingPath))
            {
                ConsoleWriteLine("  Drawing is already open - closing it so CATIA re-resolves its links on open.");
                CloseDocumentByFullPath(drawingPath, "drawing (force fresh link resolution)");
            }

            // -----------------------------------------------------------------
            // 1-  Which rename-map entries does this drawing actually point at?
            //     The stored link paths are read straight out of the CATDrawing
            //     file, so the match works for ANY old address.
            // -----------------------------------------------------------------
            List<DrawingLinkFix> fixes = BuildDrawingLinkFixes(drawingPath, renameMap);
            if (fixes.Count == 0)
            {
                ConsoleWriteLine("  No reference of this drawing matches the rename map - nothing to do.");
                newAssembliesFound.Add(drawingPath);
                return;
            }

            var stagedFiles = new List<string>();
            // backupPath -> originalPath (real old files that were moved aside)
            var movedOriginals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var documentsToClose = new List<string>();
            var workList = new List<DrawingWorkItem>();
            var newDocs = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            dynamic drawingDoc = null;

            try
            {
                // -------------------------------------------------------------
                // 2-  Stage every replacement file UNDER ITS ORIGINAL NAME at
                //     the locations CATIA searches (stored link folder, drawing
                //     folder, rename-map key folder) and open it, so the
                //     drawing binds its views to the staged document.
                // -------------------------------------------------------------
                foreach (DrawingLinkFix fix in fixes)
                {
                    StageReplacementFile(fix, stagedFiles, movedOriginals);
                }

                foreach (DrawingLinkFix fix in fixes)
                {
                    if (string.IsNullOrEmpty(fix.StagedPath)) continue;

                    // CATIA cannot hold two documents with the same file name.
                    CloseConflictingDocumentsByName(
                        Path.GetFileName(fix.StagedPath),
                        NormalizePath(fix.StagedPath));

                    if (OpenStagedDocument(fix.StagedPath))
                        documentsToClose.Add(NormalizePath(fix.StagedPath));
                    else
                        fix.StagedPath = null;
                }

                var openableFixes = new List<DrawingLinkFix>();
                foreach (DrawingLinkFix fix in fixes)
                {
                    if (!string.IsNullOrEmpty(fix.StagedPath))
                        openableFixes.Add(fix);
                }

                if (openableFixes.Count == 0)
                    throw new Exception("No replacement file could be staged and opened - the drawing links cannot be redirected.");

                // -------------------------------------------------------------
                // 3-  Open the drawing.  Its links now resolve to the staged
                //     (already loaded) documents.
                // -------------------------------------------------------------
                drawingDoc = OpenDrawingDocument(drawingPath);
                if (drawingDoc == null)
                    throw new Exception("DrawingDocument returned null after opening.");

                string loadedDrawingPath = GetDocumentFullPathWithRetry(drawingDoc);
                if (!string.IsNullOrEmpty(loadedDrawingPath) &&
                    !string.Equals(loadedDrawingPath, normalizedDrawingPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        $"Name conflict: CATIA returned '{loadedDrawingPath}' instead of the drawing " +
                        $"'{drawingPath}'. Close the conflicting document in CATIA and retry.");
                }
                if (string.IsNullOrEmpty(loadedDrawingPath))
                    ConsoleWriteLine("  WARNING: could not verify opened drawing full path (CATIA transient COM state). Continuing.");

                // -------------------------------------------------------------
                // 4-  Read phase: collect the views that point at one of the
                //     staged documents (used for verification / fallback).
                // -------------------------------------------------------------
                try
                {
                    workList = CollectDrawingWorkItems(
                        drawingDoc,
                        BuildStagedRenameMap(openableFixes),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    WARNING: could not inspect the drawing views: {ex.Message}");
                }

                // -------------------------------------------------------------
                // 5-  Redirect the links: SaveAs the staged (pointed) document
                //     to its real new location.  CATIA then rebinds every open
                //     pointing document - exactly like File > Save As.
                // -------------------------------------------------------------
                int redirected = 0;
                foreach (DrawingLinkFix fix in openableFixes)
                {
                    if (SaveAsRedirect(fix.StagedPath, fix.NewPath))
                    {
                        fix.Redirected = true;
                        redirected++;
                        documentsToClose.Add(NormalizePath(fix.NewPath));
                        if (!convertedItems.Contains(fix.MapKey))
                            convertedItems.Add(fix.MapKey);
                    }
                }

                // -------------------------------------------------------------
                // 6-  Fallback for views that are still bound to the old file:
                //     assign GenerativeBehavior.Document explicitly.
                // -------------------------------------------------------------
                var remainingItems = new List<DrawingWorkItem>();
                foreach (DrawingWorkItem item in workList)
                {
                    string expected = NormalizePath(item.NewPath);
                    string current = GetReferencePath(item.View as object);
                    if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        item.ReplacementApplied = true;
                        ConsoleWriteLine($"    View on sheet \"{item.SheetName}\" now points to the new file.");
                    }
                    else
                    {
                        remainingItems.Add(item);
                    }
                }

                if (remainingItems.Count > 0)
                {
                    newDocs = LoadReplacementDocuments(remainingItems);
                    foreach (string newPath in newDocs.Keys)
                    {
                        documentsToClose.Add(NormalizePath(newPath));
                    }
                    ApplyDrawingReplacements(remainingItems, newDocs, convertedItems);
                }

                if (redirected == 0)
                {
                    bool anyViewApplied = false;
                    foreach (DrawingWorkItem item in workList)
                    {
                        if (item.ReplacementApplied) { anyViewApplied = true; break; }
                    }
                    if (!anyViewApplied)
                        throw new Exception("None of the drawing links could be redirected to the new files.");
                }

                // -------------------------------------------------------------
                // 7-  Regenerate the views and persist the new link paths.
                // -------------------------------------------------------------
                try
                {
                    drawingDoc.Update();
                }
                catch (Exception ex)
                {
                    // Only the geometry regeneration failed - the links are
                    // still redirected and will be saved.
                    ConsoleWriteLine($"    Drawing update failed (non fatal): {ex.Message}");
                }

                if (!SaveDrawing(drawingDoc, drawingPath))
                {
                    // LAST RESORT: perform the whole redirect INSIDE the CATIA
                    // process (equivalent to a macro).  In-process script
                    // execution is not affected by the out-of-process COM
                    // E_FAIL failures.
                    ConsoleWriteLine("    Falling back to the in-process CATIA script redirect...");
                    ReleaseComObject(drawingDoc);
                    drawingDoc = null;

                    // Everything is retried here, including the fixes whose
                    // SaveAs already succeeded: their redirect was never
                    // persisted, because saving the drawing is what writes the
                    // new link path into the file.  SaveAs leaves the staged
                    // copy on disk, so the script can redo the whole operation.
                    bool scripted = false;
                    foreach (DrawingLinkFix fix in openableFixes)
                    {
                        if (!System.IO.File.Exists(fix.StagedPath))
                        {
                            ConsoleWriteLine($"    Cannot retry via script - the staged copy is gone: {fix.StagedPath}");
                            continue;
                        }

                        // The staged copy must not be held by the session while
                        // the script re-opens it.
                        CloseDocumentByFullPath(drawingPath, "drawing (before the script retry)");
                        CloseDocumentByFullPath(fix.StagedPath, "staged document (before the script retry)");

                        if (RedirectViaCatiaScript(drawingPath, fix.StagedPath, fix.NewPath))
                        {
                            fix.Redirected = true;
                            scripted = true;
                            documentsToClose.Add(NormalizePath(fix.StagedPath));
                            documentsToClose.Add(NormalizePath(fix.NewPath));
                        }
                        else
                        {
                            fix.Redirected = false;
                        }
                    }

                    if (!scripted)
                        throw new Exception($"Failed to save the drawing '{drawingPath}' after redirecting its links.");
                }

            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"  ERROR while processing drawing '{drawingPath}': {ex.Message}");
                throw;
            }
            finally
            {
                // Release the view proxies before closing the documents.
                foreach (DrawingWorkItem item in workList)
                {
                    ReleaseComObject(item.View);
                    item.View = null;
                }
                workList.Clear();
                ReleaseDictionaryComObjects(newDocs);
                ReleaseComObject(drawingDoc);
                drawingDoc = null;

                // Close the DRAWING FIRST - CATIA silently refuses to close a
                // part that is still referenced by an open drawing.
                CloseDocumentByFullPath(drawingPath, "drawing");

                foreach (string path in documentsToClose.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(path, normalizedDrawingPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    CloseDocumentByFullPath(path, "staged/replacement document");
                }
                documentsToClose.Clear();

                // Remove the staged copies and put every real old file back.
                DeletePlaceholders(stagedFiles);
                RestoreMovedOriginals(movedOriginals);
            }

            // -----------------------------------------------------------------
            // 8-  Verify the RESULT: the staged copies are gone, so re-opening
            //     the drawing forces CATIA to resolve its links from the paths
            //     that were actually persisted.  A view that reports the new
            //     path is proof that the relink worked.
            // -----------------------------------------------------------------
            VerifyDrawingLinksInSession(drawingPath, fixes);

            newAssembliesFound.Add(drawingPath);
        }

        /// <summary>
        /// Re-opens the saved drawing (with no staged copies on disk) and reports
        /// which new file each view now resolves to.  This is the end-to-end
        /// proof that the new link paths were persisted, and it is the only
        /// check that works for every CATIA file format.
        /// </summary>
        private static void VerifyDrawingLinksInSession(
            string drawingPath,
            List<DrawingLinkFix> fixes)
        {
            // Only the links we actually redirected can be verified - the
            // failures have already been reported in detail.
            var redirectedFixes = new List<DrawingLinkFix>();
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DrawingLinkFix fix in fixes)
            {
                if (!fix.Redirected) continue;
                redirectedFixes.Add(fix);
                expectedPaths.Add(NormalizePath(fix.NewPath));
            }
            if (expectedPaths.Count == 0) return;

            var openedForVerification = new List<string>();
            dynamic drawingDoc = null;
            try
            {
                // The new documents must be loaded, otherwise
                // GenerativeBehavior.Document cannot be read at all.
                foreach (DrawingLinkFix fix in redirectedFixes)
                {
                    if (!System.IO.File.Exists(fix.NewPath)) continue;
                    if (IsDocumentOpen(NormalizePath(fix.NewPath))) continue;
                    if (OpenStagedDocument(fix.NewPath))
                        openedForVerification.Add(NormalizePath(fix.NewPath));
                }

                drawingDoc = OpenDrawingDocument(drawingPath);
                if (drawingDoc == null)
                {
                    ConsoleWriteLine("  WARNING: could not re-open the drawing to verify the result.");
                    return;
                }

                var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                object sheets = GetComProperty(drawingDoc, "Sheets");
                int sheetCount = GetComCount(sheets);

                for (int s = 1; s <= sheetCount; s++)
                {
                    object sheet = GetComItem(sheets, s);
                    object views = GetComProperty(sheet, "Views");
                    int viewCount = GetComCount(views);

                    for (int v = 1; v <= viewCount; v++)
                    {
                        object view = GetComItem(views, v);
                        string referencePath = GetReferencePath(view);
                        if (!string.IsNullOrEmpty(referencePath))
                            resolved.Add(referencePath);
                        ReleaseComObject(view);
                    }

                    ReleaseComObject(views);
                    ReleaseComObject(sheet);
                }
                ReleaseComObject(sheets);

                foreach (string expected in expectedPaths)
                {
                    if (resolved.Contains(expected))
                        ConsoleWriteLine($"  VERIFIED: the drawing now references {expected}.");
                    else
                        ConsoleWriteLine($"  WARNING: after re-opening, no view references {expected} - the relink was not persisted.");
                }
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"  WARNING: verification of '{drawingPath}' failed: {ex.Message}");
            }
            finally
            {
                ReleaseComObject(drawingDoc);
                drawingDoc = null;

                CloseDocumentByFullPath(drawingPath, "drawing (after verification)");
                foreach (string path in openedForVerification)
                {
                    CloseDocumentByFullPath(path, "replacement document (after verification)");
                }
            }
        }

        /// <summary>
        /// One rename-map entry that this drawing references: the original file
        /// name CATIA looks for, the real new location and the temporary copies
        /// that were staged on disk to make CATIA resolve the link.
        /// </summary>
        private class DrawingLinkFix
        {
            public string MapKey;               // normalised rename-map key
            public string OldFileName;          // e.g. F20435V1.CATPart
            public string NewPath;              // real new full path
            public List<string> StoredPaths = new List<string>();   // link paths found inside the drawing
            public List<string> StagingLocations = new List<string>();
            public string StagedPath;           // the staged copy opened in the session
            public bool Redirected;
        }

        /// <summary>
        /// Builds the list of rename-map entries this drawing points at.
        ///
        /// The primary source of truth is the CATDrawing file itself: the stored
        /// link paths are extracted from it, so an entry matches no matter which
        /// old address the drawing recorded.  The historical naming convention
        /// (drawing "E1031F-F20435V1-1-R1-1" references part "F20435V1") is used
        /// as a fallback when the file cannot be parsed.
        /// </summary>
        private static List<DrawingLinkFix> BuildDrawingLinkFixes(
            string drawingPath,
            Dictionary<string, string> renameMap)
        {
            var fixes = new List<DrawingLinkFix>();

            string drawingDirectory = Path.GetDirectoryName(drawingPath);
            string drawingBaseName = Path.GetFileNameWithoutExtension(drawingPath);
            string normalizedDrawingPath = NormalizePath(drawingPath);

            List<string> storedReferences = ExtractLinkedDocumentPathsFromFile(drawingPath);
            if (storedReferences.Count > 0)
            {
                ConsoleWriteLine($"  {storedReferences.Count} 3D reference(s) stored in the drawing:");
                foreach (string reference in storedReferences)
                {
                    ConsoleWriteLine($"    {reference}");
                }
            }
            else
            {
                // Recent CATIA containers (header "V5_CFV2") do not store the
                // link paths as plain text, so this is the normal case: the
                // file-name convention plus the drawing's own folder is used to
                // decide where to stage the replacement.
                ConsoleWriteLine("  No stored 3D reference is readable from the drawing file - using the file-name convention.");
            }

            foreach (var kvp in renameMap)
            {
                string mapKey = kvp.Key;
                string newPath = kvp.Value;

                string oldFileName = GetFileNameSafe(mapKey);
                string oldBaseName = Path.GetFileNameWithoutExtension(oldFileName);
                if (string.IsNullOrWhiteSpace(oldBaseName)) continue;

                // a) the drawing really stores a link to this file name
                var storedPaths = new List<string>();
                foreach (string reference in storedReferences)
                {
                    string referenceName = GetFileNameSafe(reference);
                    if (string.IsNullOrEmpty(referenceName)) continue;

                    bool sameName = string.Equals(referenceName, oldFileName, StringComparison.OrdinalIgnoreCase);
                    if (!sameName)
                    {
                        sameName = string.Equals(
                            Path.GetFileNameWithoutExtension(referenceName),
                            oldBaseName,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    if (sameName)
                        storedPaths.Add(reference);
                }

                // b) file-name convention fallback
                bool matchesConvention =
                    drawingBaseName.IndexOf(oldBaseName + "-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(drawingBaseName, oldBaseName, StringComparison.OrdinalIgnoreCase);

                if (storedPaths.Count == 0 && !matchesConvention) continue;

                if (!System.IO.File.Exists(newPath))
                {
                    ConsoleWriteLine($"    ERROR: new file not found - cannot fix link for '{oldFileName}': {newPath}");
                    continue;
                }

                string realNewPath = GetExistingPathWithRealCase(newPath);
                if (string.Equals(NormalizePath(realNewPath), normalizedDrawingPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fix = new DrawingLinkFix
                {
                    MapKey = mapKey,
                    OldFileName = oldFileName,
                    NewPath = realNewPath
                };
                fix.StoredPaths.AddRange(storedPaths);

                // Staging locations, most likely first: the folders recorded in
                // the drawing, then the drawing's own folder (CATIA also looks
                // next to the pointing document), then the map-key folder.
                var locations = new List<string>();
                foreach (string reference in storedPaths)
                {
                    if (!IsRootedSafe(reference)) continue;
                    string directory = GetDirectoryNameSafe(reference);
                    if (!string.IsNullOrEmpty(directory))
                        AddStagingLocation(locations, directory, GetFileNameSafe(reference), realNewPath);
                }
                AddStagingLocation(locations, drawingDirectory, oldFileName, realNewPath);
                if (IsRootedSafe(mapKey))
                    AddStagingLocation(locations, GetDirectoryNameSafe(mapKey), oldFileName, realNewPath);

                if (locations.Count == 0)
                {
                    ConsoleWriteLine($"    ERROR: no staging location could be determined for '{oldFileName}'.");
                    continue;
                }

                fix.StagingLocations.AddRange(locations);
                fixes.Add(fix);

                ConsoleWriteLine($"  Link to fix: {oldFileName}  ->  {realNewPath}");
            }

            return fixes;
        }

        private static void AddStagingLocation(
            List<string> locations,
            string directory,
            string fileName,
            string newPath)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                return;

            string candidate;
            try { candidate = Path.Combine(directory, fileName); }
            catch { return; }

            // Never stage on top of the new file itself.
            if (string.Equals(NormalizePath(candidate), NormalizePath(newPath), StringComparison.OrdinalIgnoreCase))
                return;

            foreach (string existing in locations)
            {
                if (string.Equals(NormalizePath(existing), NormalizePath(candidate), StringComparison.OrdinalIgnoreCase))
                    return;
            }

            locations.Add(candidate);
        }

        /// <summary>
        /// Copies the new file under the ORIGINAL file name to every staging
        /// location.  A real old file found at such a location is moved aside
        /// first (and restored during cleanup) so that the SaveAs redirect can
        /// never write old content over the new file.
        /// </summary>
        private static void StageReplacementFile(
            DrawingLinkFix fix,
            List<string> stagedFiles,
            Dictionary<string, string> movedOriginals)
        {
            foreach (string location in fix.StagingLocations)
            {
                try
                {
                    string directory = GetDirectoryNameSafe(location);
                    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    {
                        ConsoleWriteLine($"    Skipping staging location (folder does not exist): {location}");
                        continue;
                    }

                    if (System.IO.File.Exists(location))
                    {
                        string backupPath = location + ".relink_orig";
                        try
                        {
                            ClearReadOnly(location);
                            if (System.IO.File.Exists(backupPath))
                                System.IO.File.Delete(backupPath);
                            System.IO.File.Move(location, backupPath);
                            movedOriginals[backupPath] = location;
                            ConsoleWriteLine($"  Existing file moved aside (restored later): {location}");
                        }
                        catch (Exception ex)
                        {
                            ConsoleWriteLine($"    Could not move the existing file aside ({ex.Message}) - skipping: {location}");
                            continue;
                        }
                    }

                    System.IO.File.Copy(fix.NewPath, location, overwrite: true);
                    ClearReadOnly(location);
                    stagedFiles.Add(location);
                    ConsoleWriteLine($"  Staged replacement under its original name: {location}");

                    if (string.IsNullOrEmpty(fix.StagedPath))
                        fix.StagedPath = location;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    ERROR: could not stage '{location}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Opens a staged file and verifies that CATIA really loaded THAT file
        /// (a same-named document from another folder would be returned
        /// silently).
        /// </summary>
        private static bool OpenStagedDocument(string stagedPath)
        {
            string expected = NormalizePath(stagedPath);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                dynamic doc = null;
                try
                {
                    doc = catiaApp.Documents.Open(stagedPath);
                    string loaded = GetDocumentFullPathWithRetry(doc);
                    if (string.IsNullOrEmpty(loaded) ||
                        string.Equals(loaded, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleWriteLine($"  Opened staged document: {stagedPath}");
                        return true;
                    }

                    ConsoleWriteLine($"    ERROR: CATIA returned '{loaded}' instead of the staged file '{stagedPath}'.");
                    return false;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    Open attempt {attempt}/3 failed for '{stagedPath}': {ex.Message}");
                    if (IsDocumentOpen(expected))
                        return true;
                    if (attempt < 3)
                        Thread.Sleep(400);
                }
                finally
                {
                    ReleaseComObject(doc);
                }
            }

            return false;
        }

        /// <summary>
        /// Rename map used for the view inspection phase: stagedPath -&gt; newPath.
        /// </summary>
        private static Dictionary<string, string> BuildStagedRenameMap(List<DrawingLinkFix> fixes)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DrawingLinkFix fix in fixes)
            {
                map[NormalizePath(fix.StagedPath)] = NormalizePath(fix.NewPath);
            }
            return map;
        }

        /// <summary>
        /// THE working relink mechanism: SaveAs the pointed (staged) document to
        /// its real new location.  CATIA rebinds the links of every open
        /// pointing document, exactly like the interactive File &gt; Save As.
        ///
        /// SaveAs returns E_FAIL when the target already exists (the suppressed
        /// "overwrite?" alert is auto-answered with "no"), so the existing
        /// target is moved aside first and restored when SaveAs fails.
        /// </summary>
        private static bool SaveAsRedirect(string pointedDocumentPath, string newPath)
        {
            dynamic pointedDoc = null;
            string backupPath = null;
            bool savedAs = false;

            try
            {
                pointedDoc = FindOpenDocumentByFullPath(catiaApp.Documents, NormalizePath(pointedDocumentPath));
                if (pointedDoc == null)
                {
                    ConsoleWriteLine($"    Pointed document is not open - cannot redirect via SaveAs: {pointedDocumentPath}");
                    return false;
                }

                // A session document already holding the target path blocks SaveAs.
                CloseDocumentByFullPath(newPath, "session document blocking the SaveAs target");

                if (System.IO.File.Exists(newPath))
                {
                    ClearReadOnly(newPath);
                    backupPath = newPath + ".relink_bak";
                    try
                    {
                        if (System.IO.File.Exists(backupPath))
                            System.IO.File.Delete(backupPath);
                        System.IO.File.Move(newPath, backupPath);
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"    Could not move the existing target aside ({ex.Message}) - SaveAs will try to overwrite.");
                        backupPath = null;
                    }
                }

                ConsoleWriteLine("  Redirecting the drawing links via SaveAs:");
                ConsoleWriteLine($"    Pointed document: {pointedDocumentPath}");
                ConsoleWriteLine($"    New Path        : {newPath}");

                try
                {
                    ExecuteComActionWithRetry(
                        () => pointedDoc.SaveAs(newPath),
                        $"SaveAs the pointed document to '{newPath}'",
                        2,
                        250);
                    savedAs = true;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    SaveAs redirect failed: {ex.Message}");
                    ReportSaveAsCapability();
                    return false;
                }

                // CATIA can report a successful SaveAs without writing the file.
                if (!System.IO.File.Exists(newPath))
                {
                    ConsoleWriteLine("    SaveAs reported success but wrote no file - the link was NOT redirected.");
                    savedAs = false;
                    ReportSaveAsCapability();
                    return false;
                }

                string sessionPathAfter = GetDocumentFullPathWithRetry(pointedDoc);
                if (!string.IsNullOrEmpty(sessionPathAfter) &&
                    !string.Equals(sessionPathAfter, NormalizePath(newPath), StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleWriteLine($"    WARNING: after SaveAs the session document reports '{sessionPathAfter}'.");
                }

                return true;
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    SaveAs redirect error for '{pointedDocumentPath}': {ex.Message}");
                return false;
            }
            finally
            {
                if (backupPath != null)
                {
                    try
                    {
                        if (savedAs && System.IO.File.Exists(newPath))
                        {
                            System.IO.File.Delete(backupPath);
                        }
                        else if (!System.IO.File.Exists(newPath))
                        {
                            System.IO.File.Move(backupPath, newPath);
                            ConsoleWriteLine($"    Restored the original target file after the failed SaveAs: {newPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"    WARNING: backup cleanup failed for '{backupPath}': {ex.Message}");
                    }
                }

                ReleaseComObject(pointedDoc);
            }
        }

        /// <summary>
        /// Diagnoses a failing SaveAs once per session.
        ///
        /// Relinking a CATDrawing is IMPOSSIBLE without a working SaveAs, and a
        /// CATIA installation whose write operations are disabled reports the
        /// very same E_FAIL as a genuine, fixable problem (name conflict,
        /// existing target, hidden dialog).  The two cases are told apart by
        /// asking CATIA to SaveAs a brand-new, empty part into the temp folder:
        /// when even that fails, no automation code can help.
        /// </summary>
        private static void ReportSaveAsCapability()
        {
            if (saveAsCapabilityReported) return;
            saveAsCapabilityReported = true;

            string probePath = Path.Combine(
                Path.GetTempPath(),
                "CatiaSaveAsProbe_" + Guid.NewGuid().ToString("N") + ".CATPart");

            dynamic probeDoc = null;
            try
            {
                probeDoc = catiaApp.Documents.Add("Part");
                probeDoc.SaveAs(probePath);

                ConsoleWriteLine("    DIAGNOSIS: SaveAs works in general - the failure above is specific to this file " +
                                 "(name conflict, locked target or a hidden CATIA dialog).");
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                ConsoleWriteLine("    DIAGNOSIS: this CATIA session cannot SaveAs ANY document - even a new, empty part " +
                                 $"into '{Path.GetTempPath()}' fails ({inner.Message}).");
                ConsoleWriteLine("               Relinking a CATDrawing is impossible without SaveAs. Check the CATIA " +
                                 "licence (a missing/invalid licence disables all write operations), then verify that " +
                                 "File > Save As works INTERACTIVELY in CATIA before running this tool again.");
            }
            finally
            {
                try
                {
                    if (probeDoc != null)
                        probeDoc.Close();
                }
                catch { /* best effort */ }
                ReleaseComObject(probeDoc);

                try
                {
                    if (System.IO.File.Exists(probePath))
                        System.IO.File.Delete(probePath);
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Saves the drawing, falling back to SaveAs (through a temporary file,
        /// because SaveAs onto an existing path fails with E_FAIL).
        /// </summary>
        private static bool SaveDrawing(dynamic drawingDoc, string drawingPath)
        {
            DateTime writeTimeBefore = GetLastWriteTimeSafe(drawingPath);

            try
            {
                ExecuteComActionWithRetry(() => drawingDoc.Save(), "save drawing", 3, 400);

                // CATIA can report a successful Save WITHOUT writing anything to
                // disk (installations whose write operations are disabled do
                // exactly that), so trust the file system, not the return value.
                DateTime writeTimeAfter = GetLastWriteTimeSafe(drawingPath);
                if (writeTimeAfter > writeTimeBefore)
                {
                    ConsoleWriteLine("Drawing saved successfully.");
                    return true;
                }

                ConsoleWriteLine("    Save reported success but the file on disk did not change - trying the SaveAs fallback.");
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    Save failed: {ex.Message}");
            }

            string tempPath = null;
            try
            {
                string directory = Path.GetDirectoryName(drawingPath);
                tempPath = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(drawingPath) + ".relink_tmp" + Path.GetExtension(drawingPath));

                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);

                ExecuteComActionWithRetry(() => drawingDoc.SaveAs(tempPath), "save drawing as", 2, 400);

                if (!System.IO.File.Exists(tempPath))
                {
                    ConsoleWriteLine("    SaveAs reported success but wrote no file.");
                    ReportSaveAsCapability();
                    return false;
                }

                ClearReadOnly(drawingPath);
                System.IO.File.Copy(tempPath, drawingPath, overwrite: true);
                ConsoleWriteLine("Drawing saved successfully using the SaveAs fallback.");
                return true;
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    SaveAs fallback failed: {ex.Message}");
                ReportSaveAsCapability();
                return false;
            }
            finally
            {
                try
                {
                    if (tempPath != null && System.IO.File.Exists(tempPath))
                    {
                        // The session document now lives at tempPath - close it
                        // before deleting the file.
                        CloseDocumentByFullPath(tempPath, "temporary drawing copy");
                        System.IO.File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    WARNING: could not remove the temporary drawing copy: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Performs the SaveAs redirect INSIDE the CATIA process using
        /// SystemService.Evaluate (equivalent to a CATIA macro).  Used as a last
        /// resort when out-of-process COM calls keep returning E_FAIL.
        /// </summary>
        private static bool RedirectViaCatiaScript(string drawingPath, string stagedPath, string newPath)
        {
            const string script = @"
Function RedirectLinks(drawingPath, stagedPath, newPath)
    RedirectLinks = 0

    Dim staged
    Set staged = CATIA.Documents.Open(stagedPath)
    If staged Is Nothing Then Exit Function

    Dim drw
    Set drw = CATIA.Documents.Open(drawingPath)
    If drw Is Nothing Then Exit Function

    staged.SaveAs newPath
    drw.Update
    drw.Save

    RedirectLinks = 1
End Function";

            string backupPath = null;
            try
            {
                CloseDocumentByFullPath(newPath, "session document blocking the SaveAs target");

                if (System.IO.File.Exists(newPath))
                {
                    ClearReadOnly(newPath);
                    backupPath = newPath + ".relink_bak";
                    if (System.IO.File.Exists(backupPath))
                        System.IO.File.Delete(backupPath);
                    System.IO.File.Move(newPath, backupPath);
                }

                dynamic systemService = catiaApp.SystemService;
                object[] args = new object[] { drawingPath, stagedPath, newPath };

                // 1 = catVBScriptLanguage
                object result = systemService.Evaluate(script, 1, "RedirectLinks", args);

                bool succeeded = System.IO.File.Exists(newPath);
                try { succeeded = succeeded && Convert.ToInt32(result) != 0; }
                catch { /* the return value is not always marshalled */ }

                if (succeeded)
                    ConsoleWriteLine($"  Links redirected and drawing saved by the in-process CATIA script: {newPath}");
                else
                    ConsoleWriteLine("    The in-process CATIA script did not redirect the links.");

                return succeeded;
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    In-process CATIA script failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (backupPath != null)
                {
                    try
                    {
                        if (System.IO.File.Exists(newPath))
                            System.IO.File.Delete(backupPath);
                        else
                            System.IO.File.Move(backupPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"    WARNING: backup cleanup failed for '{backupPath}': {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Extracts the stored 3D link paths (CATPart / CATProduct) from a CATIA
        /// document file.  CATIA stores them as plain ASCII and/or UTF-16
        /// strings, so scanning the file is the only way to learn the ORIGINAL
        /// addresses of links that can no longer be resolved.
        /// </summary>
        private static List<string> ExtractLinkedDocumentPathsFromFile(string filePath)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(filePath);

                var candidates = new List<string>();
                CollectPrintableStrings(bytes, 1, 0, candidates);   // ASCII
                CollectPrintableStrings(bytes, 2, 0, candidates);   // UTF-16LE (even offset)
                CollectPrintableStrings(bytes, 2, 1, candidates);   // UTF-16LE (odd offset)

                // GREEDY on purpose: the directory part of the stored link must
                // be captured, because it is the first place CATIA looks for the
                // referenced document.
                var pattern = new Regex(
                    @"(?:[A-Za-z]:\\|\\\\[^\\/:*?""<>|]+\\)?(?:[^\\/:*?""<>|]+\\)*[^\\/:*?""<>|]+\.(?:CATPart|CATProduct)",
                    RegexOptions.IgnoreCase);

                string ownName = Path.GetFileName(filePath);

                foreach (string candidate in candidates)
                {
                    // Cheap pre-filter - the vast majority of the extracted
                    // strings are not link paths at all.
                    if (candidate.IndexOf(".CATPart", StringComparison.OrdinalIgnoreCase) < 0 &&
                        candidate.IndexOf(".CATProduct", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    foreach (Match match in pattern.Matches(candidate))
                    {
                        string value = match.Value.Trim();

                        // Without a drive/UNC root the leading characters may be
                        // unrelated binary text glued to the name, so keep only
                        // the last segment (the file name) in that case.
                        if (!IsRootedSafe(value))
                        {
                            int separator = value.LastIndexOfAny(new[] { '\\', '/' });
                            if (separator >= 0)
                                value = value.Substring(separator + 1);
                        }

                        if (value.Length < 5) continue;
                        if (string.Equals(value, ownName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(value))
                            results.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    WARNING: could not read the link paths from '{filePath}': {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Collects printable character runs from a byte buffer.
        /// <paramref name="stride"/> 1 scans ASCII, 2 scans UTF-16LE.
        /// </summary>
        private static void CollectPrintableStrings(
            byte[] bytes,
            int stride,
            int startOffset,
            List<string> sink)
        {
            const int minimumLength = 5;
            var builder = new StringBuilder();

            for (int i = startOffset; i + stride - 1 < bytes.Length; i += stride)
            {
                byte value = bytes[i];
                bool printable = value >= 0x20 && value < 0x7F;
                if (printable && stride == 2 && bytes[i + 1] != 0)
                    printable = false;

                if (printable)
                {
                    builder.Append((char)value);
                }
                else
                {
                    if (builder.Length >= minimumLength)
                        sink.Add(builder.ToString());
                    builder.Length = 0;
                }
            }

            if (builder.Length >= minimumLength)
                sink.Add(builder.ToString());
        }

        /// <summary>
        /// Moves every file that was temporarily renamed back to its original
        /// name (backupPath -&gt; originalPath).
        /// </summary>
        private static void RestoreMovedOriginals(Dictionary<string, string> movedOriginals)
        {
            foreach (var pair in movedOriginals)
            {
                try
                {
                    if (!System.IO.File.Exists(pair.Key)) continue;

                    if (System.IO.File.Exists(pair.Value))
                    {
                        ClearReadOnly(pair.Value);
                        System.IO.File.Delete(pair.Value);
                    }

                    System.IO.File.Move(pair.Key, pair.Value);
                    ConsoleWriteLine($"  Restored the original file: {pair.Value}");
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    WARNING: could not restore '{pair.Value}' from '{pair.Key}': {ex.Message}");
                }
            }

            movedOriginals.Clear();
        }

        /// <summary>
        /// Last write time of a file, or <see cref="DateTime.MinValue"/> when it
        /// cannot be read.  Used to prove that a Save really hit the disk.
        /// </summary>
        private static DateTime GetLastWriteTimeSafe(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                    return System.IO.File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                // fall through
            }
            return DateTime.MinValue;
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;
                var attributes = System.IO.File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    System.IO.File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    WARNING: could not clear the read-only flag on '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the path with the casing it really has on disk.
        ///
        /// The rename map is upper-cased by <see cref="NormalizePath"/>, and an
        /// upper-cased path must never be handed to SaveAs: CATIA would store
        /// that exact string in the pointing documents, so every later
        /// comparison and every log line would show a name that does not match
        /// the real file.
        /// </summary>
        private static string GetExistingPathWithRealCase(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path) && !Directory.Exists(path))
                    return path;

                // GetFullPath + FileSystemInfo resolves the casing segment by
                // segment, starting from the (case-exact) drive root.
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                string remainder = full.Substring(root.Length);
                if (string.IsNullOrEmpty(remainder))
                    return full;

                string current = root.ToUpperInvariant();
                foreach (string segment in remainder.Split(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    if (string.IsNullOrEmpty(segment)) continue;

                    string resolved = segment;
                    try
                    {
                        var parent = new DirectoryInfo(current);
                        FileSystemInfo[] matches = parent.GetFileSystemInfos(segment);
                        if (matches.Length > 0)
                            resolved = matches[0].Name;
                    }
                    catch
                    {
                        // keep the segment as-is
                    }

                    current = Path.Combine(current, resolved);
                }

                return current;
            }
            catch
            {
                // fall through - the normalised path is good enough
            }
            return path;
        }

        private static string GetFileNameSafe(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return string.Empty; }
        }

        private static string GetDirectoryNameSafe(string path)
        {
            try { return Path.GetDirectoryName(path); }
            catch { return string.Empty; }
        }

        private static bool IsRootedSafe(string path)
        {
            try { return Path.IsPathRooted(path); }
            catch { return false; }
        }

        /// <summary>
        /// Returns the already-open session document whose FullName matches
        /// the given path, or null when it is not loaded.
        /// </summary>
        private static dynamic GetOpenDocumentByPath(string fullPath)
        {
            string wanted = NormalizePath(fullPath);
            dynamic docs = catiaApp.Documents;
            int count = docs.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic doc = null;
                try
                {
                    doc = docs.Item(i);
                    string docPath = GetDocumentFullPathWithRetry(doc);
                    if (!string.IsNullOrEmpty(docPath) &&
                        string.Equals(docPath, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return doc;
                    }
                }
                catch
                {
                    // skip unreadable session documents
                }
                if (doc != null)
                    ReleaseComObject(doc);
            }
            return null;
        }

        private static void ProcessDrawingLegacy(
            string drawingPath,
            Dictionary<string, string> renameMap,
            List<string> convertedItems,
            List<string> newAssembliesFound)
        {
            ConsoleWriteLine($"\nOpening drawing: {drawingPath}");

            // -----------------------------------------------------------------
            // 0️⃣  If the old files have already been moved, CATIA cannot load
            //     the referenced documents anymore – every view link is broken
            //     and GenerativeBehavior.Document throws.  Therefore we
            //     materialise a *temporary* copy of each moved file at its old
            //     location so that CATIA can resolve the stored links.  The
            //     copies are deleted again in the finally‑block below.
            //     We only create placeholders for entries whose file name
            //     matches the drawing's base name (common convention).
            // -----------------------------------------------------------------
            var placeholders = CreatePlaceholdersForMissingLinks(renameMap, drawingPath);
            var createdPlaceholders = placeholders.Item1;
            var unavailableKeys = placeholders.Item2;
            List<string> newFilesOppendToBeClosed = new List<string>();

            // -----------------------------------------------------------------
            // 1️⃣  GenerativeBehavior.Document only works when the pointed
            //     document is actually LOADED in the session (“The method
            //     Document failed” otherwise).  We pre-load only the relevant
            //     old documents (matching the drawing's base name) so the
            //     drawing binds its links to these session documents.
            // -----------------------------------------------------------------
            List<string> preloadedPaths = new List<string>();
            preloadedPaths = PreloadOldDocuments(renameMap, unavailableKeys, createdPlaceholders, drawingPath);

            dynamic drawingDoc = null; // we use dynamic to avoid pulling in a second interop assembly
            try
            {
                drawingDoc = OpenDrawingDocument(drawingPath);

                if (drawingDoc == null)
                    throw new Exception("DrawingDocument returned null after opening.");

                // Safety: CATIA silently returns an already-open document with
                // the same FILE NAME (e.g. a preloaded part or placeholder).
                // Verify we really got the drawing we asked for.
                string loadedDrawingPath = GetDocumentFullPathWithRetry(drawingDoc);
                if (!string.IsNullOrEmpty(loadedDrawingPath) &&
                    !string.Equals(loadedDrawingPath, NormalizePath(drawingPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        $"Name conflict: CATIA returned '{loadedDrawingPath}' instead of the drawing " +
                        $"'{drawingPath}'. Close the conflicting document in CATIA and retry.");
                }
                if (string.IsNullOrEmpty(loadedDrawingPath))
                {
                    ConsoleWriteLine("  WARNING: could not verify opened drawing full path (CATIA transient COM state). Continuing.");
                }

                // -------------------------------------------------------------
                // 2️⃣  Read phase – walk every sheet → every view and collect the
                //     work items (view + old path + new path).  Nothing is
                //     modified yet.
                // -------------------------------------------------------------
                var workList = CollectDrawingWorkItems(drawingDoc, renameMap, unavailableKeys);
                foreach (var work in workList)
                {
                    newFilesOppendToBeClosed.Add(work.NewPath);
                }

                // -------------------------------------------------------------
                // 3️⃣  PRIMARY strategy: redirect links via SaveAs on the OLD
                //     (placeholder) document.  Setting
                //     GenerativeBehavior.Document on an EXISTING view is
                //     silently ignored by CATIA (verified), but calling
                //     SaveAs(newPath) on the pointed document makes CATIA
                //     rebind every in-session pointing document (the drawing)
                //     to the new path – identical to interactive File > Save As.
                //     The placeholder is a byte-copy of the new file, so
                //     overwriting the new path with it is content-safe.
                // -------------------------------------------------------------
                RedirectLinksViaSaveAs(workList, convertedItems);

                // -------------------------------------------------------------
                // 4️⃣  FALLBACK for any view that is still not relinked:
                //     open the replacement documents and try assigning
                //     GenerativeBehavior.Document directly.
                // -------------------------------------------------------------
                var newDocs = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
                var remainingItems = new List<DrawingWorkItem>();
                foreach (DrawingWorkItem item in workList)
                {
                    if (!item.ReplacementApplied)
                        remainingItems.Add(item);
                }
                if (remainingItems.Count > 0)
                {
                    newDocs = LoadReplacementDocuments(remainingItems);
                    ApplyDrawingReplacements(remainingItems, newDocs, convertedItems);
                }

                // Close the old placeholder documents before save so CATIA can
                // persist the new reference path instead of keeping the old session
                // document bound to the drawing.
                bool anyReplacementApplied = false;
                foreach (DrawingWorkItem item in workList)
                {
                    if (item.ReplacementApplied)
                    {
                        anyReplacementApplied = true;
                        break;
                    }
                }

                if (anyReplacementApplied)
                {
                    var oldPaths = new List<string>();
                    foreach (DrawingWorkItem item in workList)
                    {
                        oldPaths.Add(item.OldPath);
                    }

                    CloseDocumentsByFullPath(
                        oldPaths.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                // -------------------------------------------------------------
                // 5️⃣  Regenerate the views from their new 3D documents & save.
                // -------------------------------------------------------------
                try
                {
                    drawingDoc.Update();
                }
                catch (Exception ex)
                {
                    // The links themselves are still valid and will be saved –
                    // a failed update only means the geometry was not regenerated.
                    ConsoleWriteLine($"    Drawing update failed: {ex.Message}");
                }

                bool saved = false;
                Exception lastSaveError = null;

                try
                {
                    ExecuteComActionWithRetry(
                        () => drawingDoc.Save(),
                        "save drawing",
                        2,
                        250);
                    ConsoleWriteLine("Drawing saved successfully.");
                    saved = true;
                }
                catch (Exception ex)
                {
                    lastSaveError = ex;
                }

                if (!saved)
                {
                    try
                    {
                        ConsoleWriteLine($"    Save failed, trying SaveAs fallback: {lastSaveError?.Message}");
                        ExecuteComActionWithRetry(
                            () => drawingDoc.SaveAs(drawingPath),
                            "save drawing as",
                            2,
                            250);
                        ConsoleWriteLine("Drawing saved successfully using SaveAs.");
                        saved = true;
                    }
                    catch (Exception ex)
                    {
                        lastSaveError = ex;
                    }
                }

                if (!saved)
                {
                    // LAST RESORT: run the relink + save INSIDE the CATIA
                    // process via SystemService.Evaluate (equivalent to a
                    // CATIA macro).  Out-of-process COM calls have been
                    // failing with E_FAIL in this session, but in-process
                    // script execution is not affected by that marshaling.
                    try
                    {
                        ConsoleWriteLine("    SaveAs failed too – trying in-process CATIA script fallback...");
                        var uniqueNewPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DrawingWorkItem item in workList)
                        {
                            uniqueNewPaths.Add(item.NewPath);
                        }

                        foreach (string newPath in uniqueNewPaths)
                        {
                            RelinkDrawingViaCatiaScript(drawingPath, newPath);
                        }

                        ConsoleWriteLine("Drawing relinked and saved successfully via CATIA script.");
                        saved = true;
                    }
                    catch (Exception ex)
                    {
                        lastSaveError = ex;
                        ConsoleWriteLine($"    CATIA script fallback failed: {ex.Message}");
                    }
                }

                ReleaseDictionaryComObjects(newDocs);

                foreach (var item in workList)
                {
                    ReleaseComObject(item.View);
                    item.View = null;
                }

                workList.Clear();

                if (!saved)
                    throw new Exception($"Failed to save drawing: {lastSaveError?.Message}", lastSaveError);
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"  ERROR while processing drawing '{drawingPath}': {ex.Message}");
                throw;
            }
            finally
            {
                // 6-  Cleanup: close the drawing, close every old document that
                //     we pre‑loaded (otherwise the placeholder files stay locked)
                //     and finally remove the temporary placeholder files.


                //try { drawingDoc?.Close(); } catch(Exception ex) { ConsoleWriteLine($"    Closing document failed: {ex.Message}"); }

                // Close the DRAWING FIRST.  CATIA silently refuses to close a
                // part/product that is still referenced by an open drawing
                // ("Close success but document is still open"), so the part
                // documents can only really be closed after the drawing.
                CloseDocumentByFullPath(
                    drawingPath,
                    "drawing");

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

                foreach (string path in
                    newFilesOppendToBeClosed.Distinct(
                        StringComparer.OrdinalIgnoreCase))
                {
                    CloseDocumentByFullPath(
                        path,
                        "replacement document");
                }
                newFilesOppendToBeClosed.Clear();

                // Now the COM reference can safely be released.
                ReleaseComObject(drawingDoc);
                drawingDoc = null;
                DeletePlaceholders(createdPlaceholders);
            }

            newAssembliesFound.Add(drawingPath);
        }

        /// <summary>
        /// Runs the view relink + save INSIDE the CATIA process using
        /// SystemService.Evaluate (VBScript). This is equivalent to running a
        /// CATIA macro and avoids the out-of-process COM E_FAIL errors seen
        /// with both dynamic dispatch and reflection-based invocation.
        /// </summary>
        private static void RelinkDrawingViaCatiaScript(string drawingPath, string newPartPath)
        {
            const string script = @"
Sub RelinkViews(drawingPath, newPartPath)
    On Error Resume Next

    Dim drw
    Set drw = CATIA.Documents.Open(drawingPath)
    If drw Is Nothing Then Exit Sub

    Dim newDoc
    Set newDoc = CATIA.Documents.Open(newPartPath)
    If newDoc Is Nothing Then Exit Sub

    Dim s, v
    Dim sheet, view
    For s = 1 To drw.Sheets.Count
        Set sheet = drw.Sheets.Item(s)
        For v = 1 To sheet.Views.Count
            Set view = sheet.Views.Item(v)
            Err.Clear
            view.GenerativeBehavior.Document = newDoc
        Next
    Next

    Err.Clear
    drw.Update
    Err.Clear
    drw.Save
End Sub";

            dynamic systemService = catiaApp.SystemService;
            object[] args = new object[] { drawingPath, newPartPath };

            // 1 = catVBScriptLanguage
            systemService.Evaluate(script, 1, "RelinkViews", args);
        }

        /// <summary>
        /// Opens a CATDrawing robustly.  Documents.Open raises E_FAIL
        /// (HRESULT 0x80004005) in these typical situations:
        ///   1. The file does not exist on disk.
        ///   2. A document with the same FILE NAME (but a different folder) is
        ///      already loaded in the session – CATIA cannot hold two documents
        ///      with the same name and fails instead of opening the file.
        ///   3. The drawing is already open – then the open document is reused.
        ///   4. CATIA is still busy from a previous Open/Close – a short wait
        ///      and retry usually succeeds.
        /// </summary>
        private static dynamic OpenDrawingDocument(string drawingPath)
        {
            if (!System.IO.File.Exists(drawingPath))
                throw new Exception($"Drawing not found on disk: {drawingPath}");

            // -----------------------------------------------------------------
            // Pre-flight checks – these are the most common reasons CATIA
            // answers Documents.Open with E_FAIL (0x80004005):
            // -----------------------------------------------------------------

            // 1-  A leftover CATIA lock file (~$name.CATDrawing) means another
            //     session (or a crashed one) still owns the file.
            string dir = Path.GetDirectoryName(drawingPath);
            string lockFile = Path.Combine(dir, "~$" + Path.GetFileName(drawingPath));
            if (System.IO.File.Exists(lockFile))
            {
                ConsoleWriteLine($"  WARNING: CATIA lock file found: {lockFile}");
                ConsoleWriteLine("           The drawing is open in another CATIA session or a previous session crashed.");
                ConsoleWriteLine("           Attempting to delete the stale lock file...");
                try { System.IO.File.Delete(lockFile); ConsoleWriteLine("           Lock file deleted."); }
                catch (Exception ex) { ConsoleWriteLine($"           Could not delete lock file: {ex.Message}"); }
            }

            // 2-  A read-only file cannot be opened for editing by CATIA.
            var attrs = System.IO.File.GetAttributes(drawingPath);
            if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                ConsoleWriteLine($"  WARNING: drawing file is read-only – clearing the flag: {drawingPath}");
                try { System.IO.File.SetAttributes(drawingPath, attrs & ~FileAttributes.ReadOnly); }
                catch (Exception ex) { ConsoleWriteLine($"           Could not clear read-only flag: {ex.Message}"); }
            }

            string targetPath = NormalizePath(drawingPath);
            string targetFileName = Path.GetFileName(drawingPath);

            // 1-  If the drawing is already open in the session, CLOSE it and
            //     open it again.  CATIA resolves external links at document
            //     open time – a drawing that was loaded while its referenced
            //     parts were missing keeps its broken links forever, even
            //     after the placeholder files have been created.  Reusing
            //     such a session document makes GenerativeBehavior.Document
            //     throw and later causes Update/Save to fail with E_FAIL.
            dynamic existing = FindOpenDocumentByFullPath(catiaApp.Documents, targetPath);
            if (existing != null)
            {
                ConsoleWriteLine($"  Drawing is already open – closing it to force a fresh open with resolved links: {targetPath}");
                ReleaseComObject(existing);
                existing = null;
                if (!CloseDocumentByFullPath(drawingPath, "stale drawing session document"))
                {
                    ConsoleWriteLine("  WARNING: could not close the already-open drawing – links may stay broken.");
                }
            }

            // 2-  Close any same-named document from a DIFFERENT folder – this
            //     name conflict is the most common reason for E_FAIL on Open.
            CloseConflictingDocumentsByName(targetFileName, targetPath);

            // 3-  Try to open, retrying transient COM failures (E_FAIL etc.).
            const int maxAttempts = 3;
            Exception lastError = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return catiaApp.Documents.Open(drawingPath);
                }
                catch (COMException ex)
                {
                    lastError = ex;
                    ConsoleWriteLine(
                        $"    Open attempt {attempt}/{maxAttempts} failed for '{drawingPath}': " +
                        $"HRESULT=0x{ex.HResult:X8}, {ex.Message}");
                    try { ConsoleWriteLine(ex.StackTrace); } catch { }

                    // The document may have been opened despite the error –
                    // check the session before retrying.
                    dynamic opened = FindOpenDocumentByFullPath(catiaApp.Documents, targetPath);
                    if (opened != null)
                    {
                        ConsoleWriteLine($"  Drawing was opened despite the COM error: {targetPath}");
                        return opened;
                    }

                    if (attempt < maxAttempts)
                        Thread.Sleep(500);
                }
            }

            throw new Exception($"Failed to open drawing: {lastError?.Message}", lastError);
        }

        /// <summary>
        /// Closes every open session document that has the same FILE NAME as
        /// <paramref name="targetFileName"/> but lives in a different folder
        /// than <paramref name="targetPath"/>.  CATIA refuses to open a file
        /// when a same-named document is already loaded (E_FAIL).
        /// </summary>
        private static void CloseConflictingDocumentsByName(string targetFileName, string targetPath)
        {
            dynamic documents = null;
            INFITF.Documents typedDocuments = null;
            try
            {
                documents = catiaApp.Documents;
                typedDocuments = documents as INFITF.Documents;
                if (typedDocuments == null)
                    typedDocuments = (INFITF.Documents)documents;

                int count = typedDocuments.Count;
                for (int i = count; i >= 1; i--)   // backwards – Close() shrinks the collection
                {
                    dynamic doc = null;
                    try
                    {
                        doc = typedDocuments.Item(i);
                        string fullName = GetDocumentFullPath(doc);
                        if (string.IsNullOrEmpty(fullName)) continue;
                        if (string.Equals(fullName, targetPath, StringComparison.OrdinalIgnoreCase)) continue;

                        if (string.Equals(Path.GetFileName(fullName), targetFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleWriteLine($"  Closing conflicting same-named document: {fullName}");
                            ((INFITF.Document)doc).Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"    Could not close conflicting document: {ex.Message}");
                    }
                    finally
                    {
                        ReleaseComObject(doc);
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    Failed to enumerate session documents: {ex.Message}");
            }
            finally
            {
                ReleaseComObject(documents);
            }
        }
        #endregion ------------------------------------------------------------

        #region ──  Misc helpers  ────────────────────────────────────────────────

        private static string StripInstanceSuffix(string partNumber)
        {
            // CATIA adds ".1", ".2", … to duplicated instances.
            // If the part name already contains a dot we only remove the LAST segment.
            if (string.IsNullOrEmpty(partNumber)) return partNumber;

            var segments = partNumber.Split('.');
            if (segments.Length > 1 && int.TryParse(segments.Last(), out _))
            {
                return string.Join(".", segments.Take(segments.Length - 1));
            }
            return partNumber;
        }

        private static string FindMatchingKeyInMap(string partNumber, Dictionary<string, string> renameMap)
        {
            // The map key is a *full path* (e.g. C:\Repo\Part1.CATPart)
            // We only compare the file name without extension.
            foreach (var key in renameMap.Keys)
            {
                string fileName = Path.GetFileNameWithoutExtension(key);
                if (string.Equals(fileName, partNumber, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return null;
        }

        private static string NormalizePath(string path, string baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            try
            {
                // If the path is relative, resolve it against baseDirectory (if provided)
                // instead of the current working directory.  This prevents relative
                // map entries from being resolved to the exe folder.
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(baseDirectory))
                {
                    path = Path.Combine(baseDirectory, path);
                }

                // Normalize case, separators, and trim trailing separators.
                // We intentionally do NOT call Path.GetFullPath here because it would
                // resolve relative paths against the current working directory.
                string normalized = path.Replace('/', Path.DirectorySeparatorChar)
                                         .Replace('\\', Path.DirectorySeparatorChar)
                                         .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return normalized.ToUpperInvariant();
            }
            catch
            {
                return path.Trim().ToUpperInvariant();
            }
        }

        private static string NormalizePath(string path)
        {
            // Backward-compatible overload – resolves relative paths against CWD.
            // Call the overload with baseDirectory when you know the correct base.
            return NormalizePath(path, baseDirectory: null);
        }

        public static void DoCleanUp()
        {
            // No explicit cleanup – the caller decides whether to quit CATIA or keep it alive.
        }

        #endregion ------------------------------------------------------------

        #region ──  Document management helpers  ─────────────────────────────────

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
        /// Opens every old document of the rename map that exists on disk AND
        /// whose file name matches the drawing's base name, so the view links
        /// of the drawing bind to loaded session documents.
        /// Returns the (normalised) paths that were pre‑loaded.
        /// </summary>
        private static List<string> PreloadOldDocuments(
            Dictionary<string, string> renameMap,
            HashSet<string> unavailableKeys,
            List<string> newDocuments,
            string drawingPath)
        {
            var preloaded = new List<string>();
            string drawingBaseName = Path.GetFileNameWithoutExtension(drawingPath);

            foreach (var kvp in renameMap)
            {

                string oldPath = kvp.Key;
                if (unavailableKeys.Contains(oldPath)) continue;
                string oldBaseName = Path.GetFileNameWithoutExtension(oldPath);

                // Preload the old document when it is relevant to this drawing:
                //   a) we created a placeholder for it, or
                //   b) its base name is part of the drawing's base name
                //      (naming convention: E1031F-F20435V1-1-R1-1 contains "F20435V1-").
                bool isPlaceholder = newDocuments.Contains(oldPath, StringComparer.OrdinalIgnoreCase);
                bool matchesDrawingName = drawingBaseName.IndexOf(oldBaseName + "-", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isPlaceholder && !matchesDrawingName) continue;
                if (!System.IO.File.Exists(oldPath)) continue;

                try
                {
                    catiaApp.Documents.Open(oldPath);
                    preloaded.Add(oldPath);
                    ConsoleWriteLine($"  Pre-loaded linked document: {oldPath}");
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    Could not pre-load '{oldPath}': {ex.Message}");
                }
            }
            return preloaded;
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
        /// Helps closing any other document
        /// </summary>
        /// <param name="mainDocumentPath"></param>
        private static void CloseAllDocumentsExcept(string mainDocumentPath)
        {
            try
            {
                dynamic documents = catiaApp.Documents;
                int count = documents.Count;
                for (int i = count; i >= 1; i--)   // backwards – Close() shrinks the collection
                {
                    try
                    {
                        dynamic doc = documents.Item(i);
                        string fullName = NormalizePath((string)doc.FullName);
                        if (!string.Equals(fullName, mainDocumentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            doc.Close();
                            ConsoleWriteLine($"  Closed session document: {fullName}");
                        }
                    }
                    catch { /* dead / unnamed document – ignore */ }
                }
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"Failed to close documents: {ex.Message}");
            }
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

            // Defensive: ensure we have a valid COM documents collection.
            try
            {
                if (!Marshal.IsComObject(documents))
                {
                    ConsoleWriteLine("    WARNING: CATIA.Documents is not a COM object.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    WARNING: failed to validate CATIA.Documents: {ex.Message}");
                return null;
            }

            string normalizedTarget =
                NormalizePath(targetPath);

            INFITF.Documents typedDocuments = null;
            try
            {
                typedDocuments = documents as INFITF.Documents;
                if (typedDocuments == null)
                    typedDocuments = (INFITF.Documents)documents;
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"    WARNING: failed to cast CATIA.Documents to interop type: {ex.Message}");
                return null;
            }

            int count = 0;
            const int maxCountAttempts = 3;
            bool countResolved = false;

            for (int attempt = 1; attempt <= maxCountAttempts; attempt++)
            {
                try
                {
                    count = typedDocuments.Count;
                    countResolved = true;
                    break;
                }
                catch (COMException ex)
                {
                    ConsoleWriteLine(
                        $"    ERROR: cannot enumerate CATIA.Documents " +
                        $"(attempt {attempt}/{maxCountAttempts}): " +
                        $"HRESULT=0x{ex.HResult:X8}, {ex.Message}");

                    if (attempt < maxCountAttempts)
                    {
                        Thread.Sleep(200);
                        continue;
                    }

                    try { ConsoleWriteLine(ex.StackTrace); } catch { }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine(
                        $"    ERROR: cannot enumerate CATIA.Documents " +
                        $"(attempt {attempt}/{maxCountAttempts}): {ex.Message}");

                    if (attempt < maxCountAttempts)
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                }
            }

            if (!countResolved)
                return null;

            for (int i = 1; i <= count; i++)
            {
                dynamic doc = null;

                try
                {
                    doc =
                        typedDocuments.Item(i);

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
                string fullName = null;

                var typedDoc = doc as INFITF.Document;
                if (typedDoc != null)
                {
                    fullName = typedDoc.FullName;
                }
                else
                {
                    fullName = doc.FullName as string;
                }

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

        private static string GetDocumentFullPathWithRetry(
            dynamic doc,
            int maxAttempts = 3,
            int delayMs = 100)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                string fullPath = GetDocumentFullPath(doc);
                if (!string.IsNullOrEmpty(fullPath))
                    return fullPath;

                if (attempt < maxAttempts)
                    Thread.Sleep(delayMs);
            }

            return string.Empty;
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
        /// One pending view replacement: the view object plus its current
        /// (old) reference path and the new path it must point to.
        /// </summary>
        private class DrawingWorkItem
        {
            public dynamic View;
            public string SheetName;
            public string OldPath;
            public string NewPath;
            public bool ReplacementApplied;
        }

        /// <summary>
        /// Walks every sheet → every view of the drawing and collects the views
        /// whose current 3D reference matches a key of <paramref name="renameMap"/>.
        /// Nothing is modified at this stage.
        /// </summary>
        private static List<DrawingWorkItem> CollectDrawingWorkItems(
            dynamic drawingDoc,
            Dictionary<string, string> renameMap,
            HashSet<string> unavailableKeys)
        {
            var workList = new List<DrawingWorkItem>();
            var knownNewPaths = new HashSet<string>(renameMap.Values, StringComparer.OrdinalIgnoreCase);

            object sheets = null;
            COMException lastSheetsComEx = null;
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException lastSheetsBinderEx = null;
            const int maxSheetsAttempts = 5;

            for (int attempt = 1; attempt <= maxSheetsAttempts; attempt++)
            {
                try
                {
                    sheets = GetComProperty(drawingDoc, "Sheets");
                    if (sheets != null)
                        break;
                }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
                {
                    lastSheetsBinderEx = ex;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException comEx)
                {
                    lastSheetsComEx = comEx;
                }
                catch (COMException ex)
                {
                    lastSheetsComEx = ex;
                }

                if (attempt < maxSheetsAttempts)
                {
                    ConsoleWriteLine($"    WARNING: CATIA drawing not ready to return Sheets (attempt {attempt}/{maxSheetsAttempts}). Retrying...");
                    Thread.Sleep(250);
                }
            }

            if (sheets == null)
            {
                if (lastSheetsComEx != null)
                {
                    throw new Exception(
                        $"CATIA failed to return the Sheets collection " +
                        $"(HRESULT=0x{lastSheetsComEx.HResult:X8}): {lastSheetsComEx.Message}",
                        lastSheetsComEx);
                }

                if (lastSheetsBinderEx != null)
                {
                    throw new Exception(
                        "The opened document is not a CATDrawing (no 'Sheets' collection). " +
                        "A same-named non-drawing document may be open in the session. " +
                        $"Details: {lastSheetsBinderEx.Message}",
                        lastSheetsBinderEx);
                }

                throw new Exception("The drawing document exposes no Sheets collection.");
            }

            int sheetCount = 0;
            COMException lastSheetCountComEx = null;
            const int maxSheetCountAttempts = 3;
            bool sheetCountResolved = false;

            for (int attempt = 1; attempt <= maxSheetCountAttempts; attempt++)
            {
                try
                {
                    sheetCount = GetComCount(sheets);
                    sheetCountResolved = true;
                    break;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException comEx)
                {
                    lastSheetCountComEx = comEx;
                    if (attempt < maxSheetCountAttempts)
                        Thread.Sleep(200);
                }
                catch (COMException ex)
                {
                    lastSheetCountComEx = ex;
                    if (attempt < maxSheetCountAttempts)
                        Thread.Sleep(200);
                }
            }

            if (!sheetCountResolved)
            {
                throw new Exception(
                    $"CATIA failed to enumerate drawing sheets " +
                    $"(HRESULT=0x{lastSheetCountComEx?.HResult:X8}): {lastSheetCountComEx?.Message}",
                    lastSheetCountComEx);
            }

            for (int s = 1; s <= sheetCount; s++)
            {
                object sheet = GetComItem(sheets, s);
                object views = GetComProperty(sheet, "Views");
                int viewCount = GetComCount(views);
                string sheetName = GetComStringPropertySafe(sheet, "Name");

                for (int v = 1; v <= viewCount; v++)
                {
                    dynamic view = GetComItem(views, v);
                    try
                    {
                        // -----------------------------------------------------
                        // 1️⃣  Get the current reference path (if any)
                        // -----------------------------------------------------
                        string oldPath = GetReferencePath(view);
                        string referenceName = string.Empty;

                        if (string.IsNullOrWhiteSpace(oldPath))
                        {
                            referenceName = GetReferenceName(view);
                            if (string.IsNullOrWhiteSpace(referenceName))
                            {
                                // Main/Background views and pure 2D views have no 3D
                                // link – for generative views this means the pointed
                                // document could not be loaded.
                                ConsoleWriteLine($"    View \"{GetViewNameSafe(view)}\" on sheet \"{sheetName}\" has no readable 3D link - skipped.");
                                continue;
                            }
                        }

                        // -----------------------------------------------------
                        // 2️⃣  The view may already point at one of the *new*
                        //     files (e.g. CATIA resolved the link against a
                        //     document that was already open in the session).
                        // -----------------------------------------------------
                        if (!string.IsNullOrWhiteSpace(oldPath) && knownNewPaths.Contains(oldPath))
                        {
                            ConsoleWriteLine($"  View on sheet \"{sheetName}\" is already up-to-date: {oldPath}");
                            continue;
                        }

                        // -----------------------------------------------------
                        // 3️⃣  See whether we have a replacement for it
                        // -----------------------------------------------------
                        string matchingKey = null;
                        if (!string.IsNullOrWhiteSpace(oldPath) && renameMap.ContainsKey(oldPath))
                        {
                            matchingKey = oldPath;
                        }
                        else
                        {
                            string lookupName = !string.IsNullOrWhiteSpace(oldPath)
                                ? Path.GetFileNameWithoutExtension(oldPath)
                                : referenceName;

                            matchingKey = FindMatchingKeyInMap(lookupName, renameMap);
                        }

                        if (matchingKey == null) continue;                    // no entry for this view
                        if (unavailableKeys.Contains(matchingKey)) continue;  // placeholder could not be created

                        string newPath = renameMap[matchingKey];
                        string effectiveOldPath = string.IsNullOrWhiteSpace(oldPath) ? matchingKey : oldPath;

                        if (string.Equals(effectiveOldPath, newPath, StringComparison.OrdinalIgnoreCase)) continue;

                        workList.Add(new DrawingWorkItem
                        {
                            View = view,
                            SheetName = sheetName,
                            OldPath = effectiveOldPath,
                            NewPath = newPath
                        });
                    }
                    catch (COMException comEx)
                    {
                        ConsoleWriteLine($"    COM issue on view {v}: {comEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"    Unexpected issue on view {v}: {ex.Message}");
                    }
                }
            }
            return workList;
        }

        /// <summary>
        /// Returns the name of a drawing view without ever throwing.
        /// </summary>
        private static string GetViewNameSafe(dynamic view)
        {
            return GetComStringPropertySafe(view as object, "Name");
        }

        private static object GetComProperty(object comObject, string propertyName)
        {
            if (comObject == null)
                return null;

            return comObject.GetType().InvokeMember(
                propertyName,
                BindingFlags.GetProperty,
                null,
                comObject,
                null);
        }

        private static void SetComProperty(object comObject, string propertyName, object value)
        {
            if (comObject == null)
                return;

            comObject.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty,
                null,
                comObject,
                new object[] { value });
        }

        private static bool TrySetComProperty(object comObject, string propertyName, object value)
        {
            try
            {
                SetComProperty(comObject, propertyName, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object InvokeComMethod(object comObject, string methodName, params object[] args)
        {
            if (comObject == null)
                return null;

            return comObject.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod,
                null,
                comObject,
                args);
        }

        private static void ExecuteComActionWithRetry(
            Action action,
            string actionName,
            int maxAttempts,
            int delayMs)
        {
            COMException lastComEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException comEx)
                {
                    lastComEx = comEx;
                }
                catch (COMException ex)
                {
                    lastComEx = ex;
                }

                if (attempt < maxAttempts)
                {
                    ConsoleWriteLine($"    WARNING: CATIA failed to {actionName} (attempt {attempt}/{maxAttempts}). Retrying...");
                    Thread.Sleep(delayMs);
                }
            }

            throw new COMException(
                $"CATIA failed to {actionName} after {maxAttempts} attempts: {lastComEx?.Message}",
                lastComEx);
        }

        private static int GetComCount(object comCollection)
        {
            object count = GetComProperty(comCollection, "Count");
            return count == null ? 0 : Convert.ToInt32(count);
        }

        private static object GetComItem(object comCollection, int index)
        {
            if (comCollection == null)
                return null;

            return comCollection.GetType().InvokeMember(
                "Item",
                BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                null,
                comCollection,
                new object[] { index });
        }

        private static string GetComStringPropertySafe(object comObject, string propertyName)
        {
            try
            {
                return GetComProperty(comObject, propertyName) as string ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// Closes the old documents and opens the new ones in the CATIA session.
        /// CATIA does not allow two documents with the same file name in one
        /// session, so the order (close old → open new) is essential.
        /// Returns a lookup of newPath → opened document.
        /// </summary>
        private static Dictionary<string, dynamic> LoadReplacementDocuments(
            List<DrawingWorkItem> workList)
        {
            var newDocs = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (workList.Count == 0) return newDocs;

            // 1-  Open the new documents.
            foreach (string newPath in workList.Select(w => w.NewPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    dynamic doc = GetOrOpenDocument(newPath);

                    // Safety: if the old document could not be opened, CATIA
                    // silently returns it again (name conflict) – detect that.
                    string loadedPath = GetDocumentFullPathWithRetry(doc);
                    if (!string.IsNullOrEmpty(loadedPath) &&
                        !string.Equals(loadedPath, NormalizePath(newPath), StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleWriteLine($"    ERROR: name conflict – the session returned '{loadedPath}' instead of '{newPath}'. Close the old document in CATIA and retry.");
                        ReleaseComObject(doc);
                        continue;
                    }
                    if (string.IsNullOrEmpty(loadedPath))
                    {
                        ConsoleWriteLine($"    WARNING: could not verify full path for opened document '{newPath}' (CATIA transient COM state). Continuing.");
                    }
                    newDocs[newPath] = doc;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    Failed to open '{newPath}': {ex.Message}");
                }
            }
            return newDocs;
        }

        /// <summary>
        /// PRIMARY relink strategy.
        ///
        /// CATIA redirects the links of every open pointing document (our
        /// drawing) when the pointed document is saved under a new name –
        /// exactly what interactive File > Save As does.  Because
        /// GenerativeBehavior.Document cannot be re-assigned on an existing
        /// view (CATIA silently ignores the property-put – verified), this is
        /// the only reliable automation path:
        ///
        ///   1. The old (placeholder) part is open in the session and the
        ///      drawing's views point at it.
        ///   2. oldDoc.SaveAs(newPath) – the session document itself becomes
        ///      the new file and CATIA rebinds the drawing's links to it.
        ///   3. Saving the drawing afterwards persists the new path.
        ///
        /// The placeholder file is a byte-copy of the new file, so
        /// overwriting the new path via SaveAs is content-safe.
        /// Every redirect is VERIFIED by reading the view link back.
        /// </summary>
        private static void RedirectLinksViaSaveAs(
            List<DrawingWorkItem> workList,
            List<string> convertedItems)
        {
            if (workList.Count == 0) return;

            // Group items – several views usually point at the same old file.
            var groups = workList
                .GroupBy(w => w.OldPath, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                string oldPath = group.Key;
                string newPath = group.First().NewPath;

                dynamic oldDoc = null;
                string backupPath = null;
                bool savedAs = false;
                try
                {
                    oldDoc = FindOpenDocumentByFullPath(catiaApp.Documents, NormalizePath(oldPath));
                    if (oldDoc == null)
                    {
                        ConsoleWriteLine($"    Old document is not open in the session – cannot redirect via SaveAs: {oldPath}");
                        continue;
                    }

                    // A session document already holding the target path would
                    // make SaveAs fail – close it first.
                    CloseDocumentByFullPath(newPath, "session document blocking SaveAs target");

                    // With DisplayFileAlerts=false CATIA auto-answers the
                    // "file already exists – overwrite?" alert with the
                    // DEFAULT action (do NOT overwrite) and returns E_FAIL.
                    // The placeholder is a byte-copy of the new file, so we
                    // can safely move the existing target aside and let
                    // SaveAs write a fresh file.  Restored on failure.
                    if (System.IO.File.Exists(newPath))
                    {
                        try
                        {
                            var newAttrs = System.IO.File.GetAttributes(newPath);
                            if ((newAttrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                System.IO.File.SetAttributes(newPath, newAttrs & ~FileAttributes.ReadOnly);
                        }
                        catch { /* best effort */ }

                        backupPath = newPath + ".relink_bak";
                        try
                        {
                            if (System.IO.File.Exists(backupPath))
                                System.IO.File.Delete(backupPath);
                            System.IO.File.Move(newPath, backupPath);
                            ConsoleWriteLine($"    Existing target moved aside to avoid the suppressed overwrite alert: {backupPath}");
                        }
                        catch (Exception ex)
                        {
                            ConsoleWriteLine($"    Could not move existing target aside ({ex.Message}) – SaveAs will try to overwrite.");
                            backupPath = null;
                        }
                    }

                    ConsoleWriteLine($"  Redirecting link via SaveAs:");
                    ConsoleWriteLine($"    Old Path: {oldPath}");
                    ConsoleWriteLine($"    New Path: {newPath}");

                    try
                    {
                        ExecuteComActionWithRetry(
                            () => oldDoc.SaveAs(newPath),
                            $"SaveAs pointed document to '{newPath}'",
                            2,
                            250);
                        savedAs = true;
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"    SaveAs redirect failed: {ex.Message}");
                        continue;
                    }

                    // Sanity: the session document must now live at newPath.
                    string sessionPathAfter = GetDocumentFullPathWithRetry(oldDoc);
                    if (!string.IsNullOrEmpty(sessionPathAfter) &&
                        !string.Equals(sessionPathAfter, NormalizePath(newPath), StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleWriteLine($"    WARNING: after SaveAs the session document reports '{sessionPathAfter}'.");
                    }

                    // VERIFY per view: the drawing link must now point at newPath.
                    string expected = NormalizePath(newPath);
                    foreach (var item in group)
                    {
                        string currentPath = GetReferencePath(item.View as object);
                        if (string.Equals(currentPath, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleWriteLine($"    View on sheet \"{item.SheetName}\" now points to the new file.");
                            item.ReplacementApplied = true;
                            convertedItems.Add(item.OldPath);
                        }
                        else
                        {
                            ConsoleWriteLine(
                                $"    View on sheet \"{item.SheetName}\" still points to " +
                                $"'{(string.IsNullOrEmpty(currentPath) ? "<unreadable>" : currentPath)}' – will try fallback.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    SaveAs redirect error for '{oldPath}': {ex.Message}");
                }
                finally
                {
                    // Backup handling: delete on success, restore on failure.
                    if (backupPath != null)
                    {
                        try
                        {
                            if (savedAs && System.IO.File.Exists(newPath))
                            {
                                System.IO.File.Delete(backupPath);
                            }
                            else if (!System.IO.File.Exists(newPath))
                            {
                                System.IO.File.Move(backupPath, newPath);
                                ConsoleWriteLine($"    Restored original target file after failed SaveAs: {newPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            ConsoleWriteLine($"    WARNING: backup cleanup failed for '{backupPath}': {ex.Message}");
                        }
                    }

                    ReleaseComObject(oldDoc);
                }
            }
        }

        /// <summary>
        /// Points every collected view at its new (already loaded) document.
        ///
        /// IMPORTANT (CATIA V5 automation):
        /// DrawingViewGenerativeBehavior.Document must be assigned the 3D
        /// OBJECT of the new document – i.e. PartDocument.Product /
        /// ProductDocument.Product – NOT the Document COM object itself.
        /// Assigning the Document either throws E_FAIL or is silently
        /// ignored, leaving the view bound to the old file (which then makes
        /// the old document impossible to close and Update/Save fail).
        /// Official doc example:
        ///   MyView.GenerativeBehavior.Document = someDoc.Product
        /// Every assignment is VERIFIED by reading the link back.
        /// </summary>
        private static void ApplyDrawingReplacements(
            List<DrawingWorkItem> workList,
            Dictionary<string, dynamic> newDocs,
            List<string> convertedItems)
        {
            int appliedCount = 0;

            foreach (var item in workList)
            {
                try
                {
                    if (!newDocs.TryGetValue(item.NewPath, out dynamic newDoc))
                    {
                        ConsoleWriteLine($"    Skipped – new document not loaded: {item.NewPath}");
                        continue;
                    }

                    ConsoleWriteLine($"  Replacing view reference on sheet \"{item.SheetName}\":");
                    ConsoleWriteLine($"    Old Path: {item.OldPath}");
                    ConsoleWriteLine($"    New Path: {item.NewPath}");

                    object view = item.View as object;
                    if (view == null)
                    {
                        ConsoleWriteLine("    Skipped – drawing view COM object is not available.");
                        continue;
                    }

                    object generativeBehavior = null;
                    try { generativeBehavior = GetComProperty(view, "GenerativeBehavior"); }
                    catch { /* handled below */ }

                    if (generativeBehavior == null)
                    {
                        ConsoleWriteLine("    Failed – view exposes no GenerativeBehavior (not a generative view).");
                        continue;
                    }

                    // Build the candidate 3D objects to assign, best first.
                    var candidates = new List<KeyValuePair<string, object>>();
                    AddComPropertyCandidate(candidates, newDoc, "Product");
                    AddComPropertyCandidate(candidates, newDoc, "Part");
                    candidates.Add(new KeyValuePair<string, object>("Document (itself)", (object)newDoc));

                    string expectedNewPath = NormalizePath(item.NewPath);
                    bool replaced = false;

                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            SetComProperty(generativeBehavior, "Document", candidate.Value);
                        }
                        catch (Exception ex)
                        {
                            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                            ConsoleWriteLine($"    Assigning {candidate.Key} failed: {inner.Message}");
                            continue;
                        }

                        // VERIFY: read the link back – CATIA sometimes accepts
                        // the property-put without actually relinking.
                        string currentPath = GetReferencePath(view);
                        if (string.Equals(currentPath, expectedNewPath, StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleWriteLine($"    Relinked successfully via {candidate.Key}.");
                            replaced = true;
                            break;
                        }

                        ConsoleWriteLine(
                            $"    Assignment of {candidate.Key} was accepted but the view still points to " +
                            $"'{(string.IsNullOrEmpty(currentPath) ? "<unreadable>" : currentPath)}' – trying next candidate.");
                    }

                    if (replaced)
                    {
                        item.ReplacementApplied = true;
                        appliedCount++;
                        convertedItems.Add(item.OldPath);
                    }
                    else
                    {
                        ConsoleWriteLine("    FAILED to relink this view – it still references the old document.");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    Failed to replace drawing view reference: {ex.Message}");
                }
            }

            if (workList.Count > 0)
            {
                ConsoleWriteLine($"  Relink summary: {appliedCount} of {workList.Count} view(s) relinked.");
            }
        }

        /// <summary>
        /// Reads a COM property and, when it exists and is non-null, adds it
        /// to the candidate list.
        /// </summary>
        private static void AddComPropertyCandidate(
            List<KeyValuePair<string, object>> candidates,
            object comObject,
            string propertyName)
        {
            try
            {
                object value = GetComProperty(comObject, propertyName);
                if (value != null)
                    candidates.Add(new KeyValuePair<string, object>(propertyName, value));
            }
            catch
            {
                // Property does not exist on this document type – skip.
            }
        }

        /// <summary>
        /// For every map entry whose old file no longer exists AND whose file name
        /// matches the drawing's base name (common convention: Part1.CATDrawing
        /// references Part1.CATPart), copies the new file to the old location so
        /// CATIA can resolve the stored link while opening the drawing.
        /// Returns the list of created placeholder files plus the set of map keys
        /// that could NOT be materialised.
        /// </summary>
        private static Tuple<List<string>, HashSet<string>> CreatePlaceholdersForMissingLinks(
            Dictionary<string, string> renameMap,
            string drawingPath)
        {
            var created = new List<string>();
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string drawingBaseName = Path.GetFileNameWithoutExtension(drawingPath);


            foreach (var kvp in renameMap)
            {
                string oldPath = kvp.Key;
                string newPath = kvp.Value;
                string oldFileName = Path.GetFileNameWithoutExtension(oldPath);
                // Only create placeholder if the old file is missing AND the
                // file name matches the drawing (common case: same base name).
                // This avoids copying hundreds of unrelated parts.
                string oldBaseName = Path.GetFileNameWithoutExtension(oldFileName);
                if (!drawingBaseName.ToLower().Contains(oldBaseName.ToLower() + "-"))
                    continue; // not relevant to this drawing

                if (!System.IO.File.Exists(newPath))
                {
                    failed.Add(oldPath);
                    ConsoleWriteLine($"    ERROR: new file not found – cannot fix link: {newPath}");
                    continue;
                }

                try
                {
                    string dir = Path.GetDirectoryName(oldPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    if (!System.IO.File.Exists(oldPath))
                    {
                        System.IO.File.Copy(newPath, oldPath, overwrite: false);
                        ConsoleWriteLine($"  Temporary placeholder created: {oldPath}");
                    }
                    else
                    {
                        ConsoleWriteLine($"  Temporary placeholder already exists: {oldPath}");
                    }

                    // File.Copy COPIES the read-only attribute from the source.
                    // A read-only placeholder is opened read-only by CATIA and
                    // then SaveAs on it fails with E_FAIL.  Always clear it.
                    try
                    {
                        var attrs = System.IO.File.GetAttributes(oldPath);
                        if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            System.IO.File.SetAttributes(oldPath, attrs & ~FileAttributes.ReadOnly);
                            ConsoleWriteLine($"  Cleared read-only attribute on placeholder: {oldPath}");
                        }
                    }
                    catch (Exception attrEx)
                    {
                        ConsoleWriteLine($"    WARNING: could not clear read-only on placeholder: {attrEx.Message}");
                    }

                    created.Add(oldPath);

                }
                catch (Exception ex)
                {
                    failed.Add(oldPath);
                    ConsoleWriteLine($"    ERROR: could not create placeholder '{oldPath}': {ex.Message}");
                }
            }
            return Tuple.Create(created, failed);
        }

        /// <summary>
        /// Deletes the temporary placeholder files again.  Only files that were
        /// actually created by <see cref="CreatePlaceholdersForMissingLinks"/>
        /// are removed.
        /// </summary>
        private static void DeletePlaceholders(List<string> createdPlaceholders)
        {
            foreach (string path in createdPlaceholders)
            {
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                        ConsoleWriteLine($"  Temporary placeholder removed: {path}");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"    WARNING: could not delete temporary placeholder '{path}' – please remove it manually. ({ex.Message})");
                }
            }
        }

        /// <summary>
        /// Closes every open session document whose full path matches one of
        /// <paramref name="fullPaths"/> (comparison is case-insensitive on the
        /// normalised path, so same-named documents from OTHER folders are
        /// never touched).
        /// </summary>
        private static void CloseDocumentsByFullPath(IEnumerable<string> fullPaths)
        {
            var wanted = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return;

            foreach (string path in wanted)
            {
                // CloseDocumentByFullPath is fully guarded: it re-acquires
                // fresh COM references, retries, and NEVER throws.  The old
                // implementation here read doc.FullName outside a try block,
                // which threw a COMException for unsaved/dead session
                // documents and aborted the whole drawing process before
                // Update/Save could run.
                CloseDocumentByFullPath(path, "old linked document");
            }
        }

        /// <summary>
        /// Returns the full path of the 3D document (part/product) that a drawing
        /// view represents.  A drawing view has NO “Reference” property – CATIA
        /// exposes the pointed document through
        /// <c>DrawingView.GenerativeBehavior.Document</c> instead.
        /// </summary>
        private static string GetReferencePath(object view)
        {
            try
            {
                object generativeBehavior = GetComProperty(view, "GenerativeBehavior");
                if (generativeBehavior == null) return string.Empty;

                // The pointed object is usually a Document (part/product), but
                // for views linked to a single body it can be a sub‑object –
                // then we walk up the parent chain to the owning document.
                object pointedObject = null;
                try { pointedObject = GetComProperty(generativeBehavior, "Document"); }
                catch { return string.Empty; }  // no 3D link (2D‑only / background view) or broken link

                string path = GetFullNameFromObject(pointedObject);
                if (!string.IsNullOrWhiteSpace(path)) return NormalizePath(path);
            }
            catch
            {
                // Not a generative view (2D‑only view, background view, …) or the
                // referenced document could not be loaded – treat as “no reference”.
            }
            return string.Empty;
        }

        private static string GetReferenceName(object view)
        {
            object generativeBehavior = null;
            object pointedObject = null;

            try
            {
                generativeBehavior = GetComProperty(view, "GenerativeBehavior");
                if (generativeBehavior == null)
                    return string.Empty;

                try
                {
                    pointedObject = GetComProperty(generativeBehavior, "Document");
                }
                catch
                {
                    return string.Empty;
                }

                if (pointedObject == null)
                    return string.Empty;

                string fullName = GetFullNameFromObject(pointedObject);
                if (!string.IsNullOrWhiteSpace(fullName))
                    return Path.GetFileNameWithoutExtension(fullName);

                string name = GetComStringPropertySafe(pointedObject, "Name");
                if (!string.IsNullOrWhiteSpace(name) && name != "?")
                    return StripInstanceSuffix(Path.GetFileNameWithoutExtension(name));
            }
            catch
            {
            }
            finally
            {
                ReleaseComObject(pointedObject);
                ReleaseComObject(generativeBehavior);
            }

            return string.Empty;
        }

        /// <summary>
        /// Tries to read the .FullName of a CATIA object.  If the object is not a
        /// Document (e.g. a Body), walks up its .Parent chain until the owning
        /// document is reached.
        /// </summary>
        private static string GetFullNameFromObject(object catiaObject)
        {
            object current = catiaObject;
            for (int depth = 0; depth < 6 && current != null; depth++)
            {
                try
                {
                    string fullName = GetComProperty(current, "FullName") as string;
                    if (!string.IsNullOrWhiteSpace(fullName)) return fullName;
                }
                catch { /* this object has no FullName – climb one level up */ }

                try { current = GetComProperty(current, "Parent"); }
                catch { break; }
            }
            return string.Empty;
        }

        /// <summary>
        /// Returns an already open document or opens it – the drawing API only
        /// accepts documents that are loaded in the current CATIA session.
        /// </summary>
        private static dynamic GetOrOpenDocument(string path)
        {
            // Prefer an exact full-path match from the session before opening.
            // CATIA can otherwise return a same-named document from another folder.
            dynamic existing = FindOpenDocumentByFullPath(catiaApp.Documents, NormalizePath(path));
            if (existing != null)
                return existing;

            try
            {
                return catiaApp.Documents.Open(path);
            }
            catch
            {
                // Fallback: look the document up by file name in the collection
                // of currently open documents.
                return catiaApp.Documents.Item(Path.GetFileName(path));
            }
        }

        #endregion ------------------------------------------------------------
    }
}
