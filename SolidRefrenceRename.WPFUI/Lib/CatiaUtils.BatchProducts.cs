using INFITF;
using ProductStructureTypeLib;
using SolidRefrenceRename.WPFUI.Lib;
using SolidRefrenceRename.WPFUI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CatiaReferenceRename.WPFUI.Lib
{
    /// <summary>
    /// Batch processing of CATProduct files (partial part of <see cref="CatiaUtils"/>).
    ///
    /// The single-file <see cref="CatiaUtils.ProcessProduct"/> flow stages the
    /// replacement files, opens the product, relinks, saves and then closes
    /// every document and deletes every staged copy - PER PRODUCT.  When many
    /// products share the same parts, every product pays the full staging,
    /// document-open and cleanup cost again.
    ///
    /// This batch flow restructures the same building blocks into phases:
    ///
    ///   1. Close every target product that is already open (links resolve
    ///      only at open time).
    ///   2. STAGE PHASE   - stage the replacement files of ALL products up
    ///      front.  A file needed by several products is staged ONCE
    ///      (<see cref="StageProductComponent"/> skips locations that already
    ///      hold the staged copy).
    ///   3. PRE-OPEN PHASE- open every staged file and KEEP it loaded in the
    ///      session, so every product of the batch resolves its components
    ///      against the already loaded documents.
    ///   4. PROCESS PHASE - per product: open, relink the components
    ///      (<see cref="ReplaceReferencesInProduct"/>, pruning disabled
    ///      because staged copies are shared), save and close ONLY the
    ///      product itself.
    ///   5. CLEANUP PHASE - after the whole batch: close the loaded
    ///      component documents, delete the staged copies and restore any
    ///      moved original (runs in finally, also on failure).
    /// </summary>
    public partial class CatiaUtils
    {
        #region ──  Batch CATProduct processing  ──────────────────────────────

        /// <summary>
        /// Raised on the batch worker (STA) thread before a product is
        /// processed: (1-based index, product path).  The receiver must
        /// marshal to the UI thread itself.
        /// </summary>
        public static event EventHandler<(int index, string productPath)> BatchProductProgress;

        /// <summary>
        /// Relinks the components of MANY CATProduct files in one CATIA
        /// session.  All staging happens once up front, the staged documents
        /// stay loaded for the whole batch, and the staged files are removed
        /// only after the last product has been processed.
        /// </summary>
        /// <param name="productPaths">The CATProduct files to process.</param>
        /// <param name="newPartAddressMap">The rename map (old name -&gt; new
        /// full path), shared by all products.</param>
        public static PartChangingOutput ChangePartAddressesBatch(
            List<string> productPaths,
            List<PartNewRef> newPartAddressMap)
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
                            result = ChangePartAddressesBatchInternal(productPaths, newPartAddressMap);
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

            return ChangePartAddressesBatchInternal(productPaths, newPartAddressMap);
        }

        private static PartChangingOutput ChangePartAddressesBatchInternal(
            List<string> productPaths,
            List<PartNewRef> newPartAddressMap)
        {
            var errorsList = new List<ProcessOutputDetails>();
            var processedProducts = new List<string>();
            var convertedItems = new List<string>();

            // Batch-wide state.  Everything created/loaded here survives until
            // the final cleanup phase - that is the whole point of the batch.
            var globalStagedFiles = new List<string>();
            var movedOriginals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var loadedDocuments = new List<string>();
            var productsToProcess = new List<string>();

            try
            {
                // -------------------------------------------------------------
                // 1-  Initialise / attach to a CATIA session.
                // -------------------------------------------------------------
                if (!TryInitCatia())
                {
                    return Fail("Failed to initialise CATIA.", null);
                }

                // -------------------------------------------------------------
                // 2-  Normalise the rename map.
                // -------------------------------------------------------------
                if (newPartAddressMap == null)
                    newPartAddressMap = new List<PartNewRef>();

                var renameMap = NormaliseRenameMap(newPartAddressMap, null);
                if (renameMap.Count == 0)
                    ConsoleWriteLine("WARNING: the rename map is empty - no component can be relinked.");

                // -------------------------------------------------------------
                // 3-  Validate and de-duplicate the product list.
                // -------------------------------------------------------------
                var seenProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string productPath in productPaths ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(productPath)) continue;

                    if (!System.IO.File.Exists(productPath))
                    {
                        string message = $"Product not found on disk: {productPath}";
                        ConsoleWriteLine(message);
                        errorsList.Add(new ProcessOutputDetails(message, productPath));
                        continue;
                    }

                    if (seenProducts.Add(NormalizePath(productPath)))
                        productsToProcess.Add(productPath);
                }

                if (productsToProcess.Count == 0)
                {
                    return Fail("No CATProduct file to process.", null);
                }

                ConsoleWriteLine($"\n=== BATCH: {productsToProcess.Count} CATProduct file(s), {renameMap.Count} rename-map entry(ies) ===");

                // -------------------------------------------------------------
                // 4-  Close every target product that is already open.  CATIA
                //     resolves external links only while a document is being
                //     opened, so a pre-loaded product keeps its unresolved
                //     components; it must be re-opened after staging.
                // -------------------------------------------------------------
                foreach (string productPath in productsToProcess)
                {
                    if (IsDocumentOpen(NormalizePath(productPath)))
                    {
                        ConsoleWriteLine($"  Assembly is already open - closing it so CATIA re-resolves its links on open: {productPath}");
                        CloseDocumentByFullPath(productPath, "assembly (force fresh link resolution)");
                    }
                }

                // -------------------------------------------------------------
                // 5-  STAGE PHASE: stage the replacement files of ALL products
                //     up front, under their ORIGINAL names in each product's
                //     folder, so every component can resolve on open.
                //     A location that was already staged for a previous
                //     product (or that holds a real old file) is skipped by
                //     <see cref="StageProductComponent"/>, so shared parts are
                //     staged exactly once per folder.
                // -------------------------------------------------------------
                ConsoleWriteLine("\n=== STAGE PHASE ===");
                foreach (string productPath in productsToProcess)
                {
                    var relevantEntries = FindRelevantRenameMapEntries(productPath, renameMap);
                    if (relevantEntries.Count == 0)
                    {
                        ConsoleWriteLine($"  No relevance match for '{GetFileNameSafe(productPath)}' - staging all map entries (unused copies are removed in the cleanup phase).");
                        foreach (var kvp in renameMap)
                            relevantEntries.Add(new KeyValuePair<string, string>(kvp.PartName, kvp.NewAddress));
                    }

                    foreach (var kvp in relevantEntries)
                    {
                        StageProductComponent(productPath, kvp.Key, kvp.Value, globalStagedFiles, movedOriginals);
                    }
                }
                ConsoleWriteLine($"=== STAGE PHASE complete: {globalStagedFiles.Count} file(s) staged for the whole batch ===");

                // -------------------------------------------------------------
                // 6-  PRE-OPEN PHASE: open every staged file NOW and keep it
                //     loaded, so each product of the batch binds its
                //     components to the already loaded session documents
                //     instead of re-loading the same parts again and again.
                //     A file that cannot be pre-opened is not fatal - the
                //     product open will load it from disk.
                // -------------------------------------------------------------
                ConsoleWriteLine($"\n=== PRE-OPEN PHASE: loading {globalStagedFiles.Count} staged document(s) ===");
                var preOpenedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string stagedPath in globalStagedFiles)
                {
                    string stagedName = GetFileNameSafe(stagedPath);

                    // CATIA can hold only ONE document per file name.  When
                    // several products (in different folders) staged the same
                    // old file name, the first copy is opened and the rest are
                    // skipped: the products bind to the loaded copy, whose
                    // content is identical (every staged copy is a copy of the
                    // same new file).
                    if (!preOpenedNames.Add(stagedName))
                        continue;

                    CloseConflictingDocumentsByName(stagedName, NormalizePath(stagedPath));

                    if (OpenStagedDocument(stagedPath))
                        loadedDocuments.Add(NormalizePath(stagedPath));
                    else
                        ConsoleWriteLine($"  WARNING: could not pre-open '{stagedPath}' - the products will load it from disk.");
                }

                // -------------------------------------------------------------
                // 7-  PROCESS PHASE: fix the products one by one.  A failure
                //     of one product is recorded and the batch continues with
                //     the next one.
                // -------------------------------------------------------------
                ConsoleWriteLine($"\n=== PROCESS PHASE: fixing {productsToProcess.Count} product(s) ===");
                int index = 0;
                foreach (string productPath in productsToProcess)
                {
                    index++;
                    BatchProductProgress?.Invoke(null, (index, productPath));

                    ConsoleWriteLine($"\n[{index}/{productsToProcess.Count}] Processing assembly: {productPath}");
                    try
                    {
                        ProcessSingleProductInBatch(productPath, renameMap, convertedItems, errorsList, loadedDocuments);
                        processedProducts.Add(productPath);
                    }
                    catch (Exception ex)
                    {
                        ConsoleWriteLine($"  ERROR while processing assembly '{productPath}': {ex.Message}");
                        errorsList.Add(new ProcessOutputDetails(ex.Message, productPath));
                    }
                }

                ConsoleWriteLine($"\n=== BATCH complete: {processedProducts.Count} of {productsToProcess.Count} product(s) processed, {convertedItems.Count} distinct reference(s) relinked ===");

                // -------------------------------------------------------------
                // 8-  Result.  The staged files are still on disk and the
                //     documents are still open - the cleanup phase below owns
                //     their removal.
                // -------------------------------------------------------------
                return new PartChangingOutput
                {
                    Succeed = errorsList.Count == 0,
                    Errors = errorsList,
                    NewAssembliesFound = processedProducts
                };
            }
            catch (Exception ex)
            {
                errorsList.Add(new ProcessOutputDetails($"Unexpected batch error: {ex.Message}"));
                return new PartChangingOutput
                {
                    Succeed = false,
                    Errors = errorsList,
                    NewAssembliesFound = processedProducts
                };
            }
            finally
            {
                // -------------------------------------------------------------
                // 9-  CLEANUP PHASE: runs after the LAST product (and also on
                //     failure).  This is the only place where the shared
                //     staged files may be removed.
                // -------------------------------------------------------------
                CleanupBatch(productsToProcess, loadedDocuments, globalStagedFiles, movedOriginals);
            }
        }

        /// <summary>
        /// Opens, relinks, saves and closes ONE product of the batch.  The
        /// shared component documents (staged copies, new files) are NOT
        /// touched here - they stay open for the remaining products and are
        /// closed by <see cref="CleanupBatch"/>.
        /// </summary>
        private static void ProcessSingleProductInBatch(
            string productPath,
            List<PartNewRef> renameMap,
            List<string> convertedItems,
            List<ProcessOutputDetails> errorsList,
            List<string> loadedDocuments)
        {
            string normalizedProductPath = NormalizePath(productPath);
            ProductDocument productDoc = null;
            try
            {
                // A product that is already in the session (open before the
                // batch, or loaded as a COMPONENT of an earlier batch product)
                // keeps the links it resolved at THAT time.  Close it so this
                // fresh open re-resolves its links against the staged files.
                if (IsDocumentOpen(normalizedProductPath))
                {
                    ConsoleWriteLine("  Assembly is already open - closing it so CATIA re-resolves its links on open.");
                    CloseDocumentByFullPath(productPath, "assembly (force fresh link resolution)");
                }

                // CATIA cannot hold two documents with the same file name.
                CloseConflictingDocumentsByName(GetFileNameSafe(productPath), normalizedProductPath);

                ConsoleWriteLine("  Opening the assembly in CATIA (large assemblies can take a while)...");
                productDoc = (ProductDocument)catiaApp.Documents.Open(productPath);
                if (productDoc == null)
                    throw new Exception("ProductDocument returned null after opening.");

                // Documents.Open can silently return a same-named document
                // from another folder - verify we really got THIS file.
                string loadedPath = GetDocumentFullPathWithRetry(productDoc);
                if (!string.IsNullOrEmpty(loadedPath) &&
                    !string.Equals(loadedPath, normalizedProductPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        $"Name conflict: CATIA returned '{loadedPath}' instead of the assembly " +
                        $"'{productPath}'. Close the conflicting document in CATIA and retry.");
                }

                var rootProduct = productDoc.Product;
                var alreadyReplaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var outcome = new ProductReplaceOutcome();

                // stagedFiles is passed as NULL on purpose: it disables the
                // per-product pruning of staged copies.  In a batch the staged
                // files are shared between products and may only be deleted
                // after the whole batch is done.
                ReplaceReferencesInProduct(
                    rootProduct, renameMap, alreadyReplaced, convertedItems, loadedDocuments, outcome, null);

                // Nothing to relink is SUCCESS, not failure: the assembly may
                // already point at the new files (re-run) or contain no
                // component covered by the rename map.
                if (outcome.Replaced == 0)
                {
                    if (outcome.Failed > 0)
                    {
                        throw new Exception(
                            $"{outcome.Failed} component reference(s) of this assembly could not be " +
                            "relinked to the new files.");
                    }

                    ConsoleWriteLine(
                        outcome.AlreadyUpToDate > 0
                            ? $"  Nothing to do - {outcome.AlreadyUpToDate} component(s) already point at the new files."
                            : "  Nothing to do - no component of this assembly matches the rename map.");
                }
                else
                {
                    // CATIA can report a successful Save without writing
                    // anything, so the file system is the judge.
                    DateTime writeTimeBefore = GetLastWriteTimeSafe(productPath);
                    ExecuteComActionWithRetry(() => ComSave(productDoc), "save assembly", 3, 400);

                    if (GetLastWriteTimeSafe(productPath) <= writeTimeBefore)
                    {
                        ReportSaveAsCapability();
                        throw new Exception(
                            $"Save reported success but '{productPath}' did not change on disk - " +
                            "the new references were NOT persisted.");
                    }

                    ConsoleWriteLine(
                        $"Assembly saved successfully ({outcome.Replaced} component reference(s) relinked" +
                        (outcome.Failed > 0 ? $", {outcome.Failed} failed" : "") + ").");

                    if (outcome.Failed > 0)
                    {
                        errorsList.Add(new ProcessOutputDetails(
                            $"{outcome.Failed} component reference(s) could not be relinked.", productPath)
                        {
                            Level = "warning"
                        });
                    }
                }
            }
            finally
            {
                ReleaseComObject(productDoc);
                productDoc = null;

                // Close ONLY the product.  The loaded component documents stay
                // in the session for the remaining products of the batch.
                CloseDocumentByFullPath(productPath, "assembly (batch)");
            }
        }

        /// <summary>
        /// Final cleanup of the whole batch: close the products, close every
        /// document the batch loaded (staged copies, new files, resolved old
        /// files), then delete the staged copies and restore any moved
        /// original.  The products are closed FIRST - CATIA refuses to close
        /// a document that is still referenced by an open product.
        /// </summary>
        private static void CleanupBatch(
            List<string> productsToProcess,
            List<string> loadedDocuments,
            List<string> stagedFiles,
            Dictionary<string, string> movedOriginals)
        {
            ConsoleWriteLine("\n=== CLEANUP PHASE ===");

            try
            {
                foreach (string productPath in productsToProcess)
                {
                    CloseDocumentByFullPath(productPath, "assembly (batch cleanup)");
                }

                foreach (string path in loadedDocuments.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    CloseDocumentByFullPath(path, "batch component document");
                }
                loadedDocuments.Clear();

                foreach (string path in stagedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    CloseDocumentByFullPath(path, "staged component");
                }

                // Remove the staged copies and restore any real file we moved.
                DeletePlaceholders(stagedFiles);
                RestoreMovedOriginals(movedOriginals);

                ConsoleWriteLine("=== CLEANUP complete ===");
            }
            catch (Exception ex)
            {
                ConsoleWriteLine($"  ERROR during batch cleanup: {ex.Message}");
            }
        }

        #endregion ------------------------------------------------------------
    }
}
