using INFITF;
using ProductStructureTypeLib;
using SolidRefrenceRename.WPFUI.Lib;
using SolidRefrenceRename.WPFUI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace CatiaReferenceRename.WPFUI.Lib
{
    public class CatiaUtils
    {
        private static INFITF.Application catiaApp;
        private static ProductDocument productDoc;

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
                    return true;
                }
                catch (Exception ex)
                {
                    ConsoleWriteLine($"Failed to start CATIA: {ex.Message}");
                    return false;
                }
            }
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

        private static void ProcessDrawing(
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
            List<string> newFilesOppendToBeClosed=new List<string>();

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
