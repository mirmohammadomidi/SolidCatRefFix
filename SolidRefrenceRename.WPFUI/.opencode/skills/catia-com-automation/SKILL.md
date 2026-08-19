---
name: catia-com-automation
description: Use when working on CATIA V5 COM interop code in this repo, especially Lib/CatiaUtils.cs and Lib/CatiaUtils2.cs - CATDrawing/CATProduct/CATPart reference renaming, ProcessDrawing, ProcessProduct, E_FAIL (0x80004005) errors, TargetInvocationException on views, Documents.Open failures, Save/SaveAs failures, or broken 3D links in drawing views.
---

# CATIA V5 COM Automation (SolidRefrenceRename)

Domain knowledge for the CATIA reference-renaming code in this project.
Main files: `Lib/CatiaUtils.cs` (active), `Lib/CatiaUtils2.cs` (alternate),
`Lib/Versions/*.txt` (historical snapshots - do NOT edit these).

## What the code does

`CatiaUtils.ChangePartAddress(assemblyPath, renameMap)` fixes broken external
references in CATIA documents:

- `renameMap` keys = OLD full paths (or old locations) of CATPart/CATProduct
  files; values = NEW full paths. Matching is usually done by **file name
  without extension**, case-insensitive (see `FindMatchingKeyInMap`).
- `.CATProduct` -> `ProcessProduct`: walks the Product tree and calls
  `Products.ReplaceComponent(child, newPath, true)`.
- `.CATDrawing` -> `ProcessDrawing`: relinks each drawing view via
  `DrawingView.GenerativeBehavior.Document = <loaded new document>`.

File naming convention in this domain: a drawing named
`E1031F-F20435V1-1-R1-1.CATDrawing` references part `F20435V1.CATPart`.
The part's base name appears inside the drawing's base name followed by `-`
(`drawingBaseName.Contains(oldBaseName + "-")`). Keep this convention in mind
whenever matching drawings to parts.

## Critical CATIA COM facts (hard-won, do not regress)

1. **Links resolve only at document-open time.** If a drawing is opened while
   its referenced parts are missing on disk, its links stay broken for the
   whole session - even if the files appear later. Never "reuse" an
   already-open drawing when placeholders were just created; close it and
   reopen it so CATIA re-resolves links. (This was the root cause of the
   "has no readable 3D link - skipped" + E_FAIL save failure bug.)

2. **`GenerativeBehavior.Document` throws unless the pointed document is
   LOADED in the session.** That's why `ProcessDrawing` pre-loads old
   documents (`PreloadOldDocuments`) and creates temporary placeholder copies
   of missing files at their old paths (`CreatePlaceholdersForMissingLinks`)
   before opening the drawing. Placeholders are deleted in the finally block.

2b. **`GenerativeBehavior.Document` CANNOT re-link an EXISTING view.**
   Empirically verified in this project: the property-put is accepted by
   IDispatch without error for ALL candidates (`doc.Product`, `doc.Part`,
   the Document itself), but reading the link back shows the view still
   points at the old file. The property only works when DEFINING a new
   generative view. Do not waste time on it as a primary mechanism.
   Symptom chain when relink silently fails: old document cannot be closed
   ("Close success but document is still open") -> Update/Save fail with
   E_FAIL. ALWAYS verify a relink by reading the link back
   (`GetReferencePath(view)` must equal the new path). `ReferenceProduct` /
   `ReferenceDocument` do not exist on DrawingView (IDispatch may fake
   success).

2c. **The WORKING relink mechanism: `oldDoc.SaveAs(newPath)`.**
   When the pointed (old/placeholder) document is open in the session
   together with the drawing, calling `SaveAs(newPath)` on the pointed
   document makes CATIA rebind every in-session pointing document's links to
   the new path - identical to interactive File > Save As. Then save the
   drawing to persist the redirected links. Since the placeholder is a
   byte-copy of the new file, overwriting the new path via SaveAs is
   content-safe. See `RedirectLinksViaSaveAs` in CatiaUtils.cs.

2d. **`SaveAs` returns E_FAIL when the target file already EXISTS.**
   With `DisplayFileAlerts = false` CATIA auto-answers the "file already
   exists - overwrite?" alert with the DEFAULT (do NOT overwrite) and the
   call fails with E_FAIL. Before SaveAs: (1) close any session document
   already holding the target path, (2) clear the read-only attribute,
   (3) MOVE the existing target file aside (e.g. `<name>.relink_bak`), then
   SaveAs into the free path; delete the backup on success, restore it on
   failure. This is implemented in `RedirectLinksViaSaveAs`.

3. **CATIA cannot hold two documents with the same FILE NAME** (even from
   different folders). `Documents.Open` then fails with E_FAIL. Always close
   same-named conflicting documents first (`CloseConflictingDocumentsByName`)
   and close old documents before opening new same-named ones (order:
   close old -> open new).

4. **Hidden modal dialogs poison the session.** A CATIA alert dialog (broken
   links, save warnings) makes every subsequent COM call return E_FAIL.
   `TryInitCatia` sets `catiaApp.DisplayFileAlerts = false` on both the
   attach path and the launch path - keep it that way.

5. **STA threading is mandatory.** All CATIA COM calls must run on an STA
   thread; `ChangePartAddress` spawns one if needed. Never call the internals
   from an MTA thread.

6. **Never call `Close()` twice on the same RCW.** If Close fails, re-acquire
   a fresh COM reference from `catiaApp.Documents` and retry
   (`CloseDocumentByFullPath` pattern). Never modify a COM collection while
   iterating it - collect first, then act; iterate backwards when closing.
   Route ALL document closing through `CloseDocumentByFullPath` - never read
   `doc.FullName` on a raw `dynamic` outside a try block (unsaved/dead
   documents throw COMException and abort the whole flow before Save).

6b. **Close order matters: drawing FIRST, then parts.** CATIA silently
   refuses to close a part/product still referenced by an open drawing
   (Close "succeeds" but the document stays in `Documents` - the
   "Close success but document is still open" log line). In cleanup, close
   the CATDrawing before closing preloaded/replacement part documents.

7. **`Documents.Open` can return the WRONG document** (a same-named one
   already in session) without any error. Always verify `doc.FullName`
   against the requested path after opening (`GetDocumentFullPathWithRetry`).

8. **Transient COM failures are normal.** CATIA is often "busy" right after
   Open/Close/Save. Wrap COM calls in retry loops with short sleeps
   (`ExecuteComActionWithRetry`, retry patterns for Sheets/Count). A
   `TargetInvocationException` wrapping a `COMException` is the common shape
   when using reflection (`InvokeMember`) - catch both.

9. **Path comparison:** always compare via `NormalizePath` (uppercase,
   backslashes, trimmed) with `StringComparison.OrdinalIgnoreCase`. CATIA
   reports paths in inconsistent casing (e.g. `C:\CATIA\...CATDRAWING`).

10. **Instance suffixes:** CATIA appends `.1`, `.2`, ... to duplicated product
    instances. Strip with `StripInstanceSuffix` before name matching.

11. **`drawingDoc.Update()` failure is non-fatal** (links are still saved;
    only geometry regeneration failed) - log and continue to Save. But a
    Save failure IS fatal; SaveAs to the same path is the fallback.

2e. **!!! NEVER call SaveAs/Save/Update through C# `dynamic` - use
   reflection. THIS WAS THE ROOT CAUSE of the long-standing E_FAIL bug.**
   Measured on a licensed CATIA V5 B29 session, same scratch part, same
   target folder, four calls back-to-back:
   ```
   reflection InvokeMember("SaveAs") -> file written      OK
   dynamic    doc.SaveAs(path)       -> E_FAIL 0x80004005 FAIL
   reflection InvokeMember("SaveAs") -> file written      OK   (session fine)
   typed      INFITF.Document.SaveAs -> file written      OK
   ```
   The C# `dynamic` binder drives CATIA's IDispatch through its own
   ITypeInfo-scanning/marshaling path, and CATIA rejects the call. It is NOT
   a licence issue, NOT a "target already exists" issue, NOT a modal dialog,
   and it does NOT poison the session. Use the `ComSaveAs` / `ComSave` /
   `ComUpdate` helpers in CatiaUtils.cs, which wrap `InvokeComMethod`
   (reflection). After this one change `ProcessDrawing` went from
   "0 of 3 views relinked" to all views relinked + saved + verified.
   Beware: a headless CATIA launched via `Activator.CreateInstance` also
   cannot SaveAs, which can masquerade as the same symptom - attach to a
   real GUI session (`Marshal.GetActiveObject`) when testing.
   `ReportSaveAsCapability()` distinguishes the two cases.

2f. **Never trust a CATIA "success" return for a write.** Every save path
   verifies the FILE SYSTEM afterwards: `SaveAsRedirect` checks
   `File.Exists(newPath)`, and `SaveDrawing` compares the drawing's
   `LastWriteTimeUtc` before/after `Save()`. Keep these checks.

2i. **`SaveAs` re-serialises the part; its byte size WILL change.** The
   redirected part went 228,411 -> 194,560 bytes. It still opens with its
   `PartBody` intact - this is CATIA rewriting the container, not data loss.
   Do not "fix" it by copying the original file over the SaveAs result: that
   would restore the old internal document name and break the new link.

2j. **"The method Document failed" on a cold-opened drawing is NORMAL here**
   and is NOT proof that a relink failed. The pristine, untouched test
   drawing reports it too, even with the referenced part present AND open in
   the session (some views expose no readable 3D link via automation at all).
   Judge success by the log line written DURING the redirect
   (`View on sheet "..." now points to the new file`) plus the post-save
   `VERIFIED:` line, not by a cold read of `GenerativeBehavior.Document`.

2g. **Staging beats placeholders.** `ProcessDrawing` copies the NEW file
   under the OLD file name next to the drawing (`StageReplacementFile`),
   opens it, then `SaveAs`-es that staged document to its real new path.
   Any real file already sitting at a staging location is moved to
   `<name>.relink_orig` and restored in the finally block, so old content can
   never overwrite the new file. Staged copies are byte-copies of the new
   file, so the redirect is content-safe.

2h. **Link paths are NOT plain text in modern CATIA files.** Files with the
   `V5_CFV2` header (checked on a real 722 KB CATDrawing) contain zero
   occurrences of "CATPart"/"CATProduct" in ASCII, UTF-16, or any zlib
   stream. `ExtractLinkedDocumentPathsFromFile` therefore usually returns
   nothing, and matching falls back to the file-name convention. Do not
   build features that depend on reading link paths from disk.

## CATProduct component relinking (ProcessProduct)

3a. **A component's `Name` / `PartNumber` have NOTHING to do with its file
   name.** Measured on `E1031FA-FA20174V1-2-R1-1.CATProduct`: the three
   children are `Name='Cover Main Part'` / `PartNumber='DR01AAA01'`,
   `'Cover-Connector leg part 1'` / `DR01AAA02`, `'connector-leg-2.1'` /
   `DR01AAA03`, while the files are `F20427V1.CATPart`, `F20428V1.CATPart`,
   `F20429V1.CATPart`. The original code matched the rename map against
   `child.Name` via `FindMatchingKeyInMap`, so it matched NOTHING and silently
   replaced nothing. The ONLY reliable file identity is
   `child.ReferenceProduct.Parent.FullName` - see `GetComponentFilePath` and
   `FindMatchingKeyForFile`.

3b. **An UNRESOLVED component throws on almost every property.** When the
   referenced file is missing from disk, `ReferenceProduct` throws "The method
   ReferenceProduct failed" and `PartNumber` throws too (`Name` still works,
   since it is stored in the product itself). This is the detection mechanism
   for "component not resolved". Consequence: you cannot learn which file a
   broken component wants - the product file stores no plaintext path either
   (`V5_CFV2`, zero `CATPart` strings in ASCII/UTF-16/zlib). So `ProcessProduct`
   stages the replacement under the OLD file name in the product's folder
   FIRST, then opens the product; the components resolve, and the file paths
   become readable.

3c. **`Products.ReplaceComponent(child, newPath, true)` works** once the
   component is resolved - verified by reading
   `ReferenceProduct.Parent.FullName` back, and by reopening the saved product
   with nothing staged (all 3 components pointed at the new
   `C:\CatiaParts\...` paths). Unlike drawings, products need NO SaveAs
   trickery: plain `Save()` persists the new references. The referenced part
   files are left byte-identical (verified by hash).

3d. **"Nothing to relink" is SUCCESS, not failure.** Re-running on an
   already-fixed assembly must not error. `ProductReplaceOutcome` tallies
   Replaced / Failed / AlreadyUpToDate / Unresolved, and only a non-zero
   `Failed` (or a Save that does not change the file) is an error. Components
   already pointing at a rename-map VALUE are detected via `knownNewPaths` -
   matching by the old key alone would report them as "no match".

## Debugging E_FAIL (0x80004005) checklist

When a user reports E_FAIL from Open/Save/Update, check in this order:

1. Was the drawing already open in the session from before placeholders were
   created? (broken links baked in -> must close & reopen)
2. Is a hidden modal dialog open in CATIA? (`DisplayFileAlerts`)
3. Same-named document conflict in the session?
4. Stale lock file `~$<name>.CATDrawing` next to the file?
5. Read-only file attribute?
6. Views logging "has no readable 3D link - skipped" means
   `GenerativeBehavior.Document` threw -> the referenced document is not
   loaded/resolvable -> nothing gets relinked -> Update/Save then fail.

## Testing & environment notes

- This project cannot be meaningfully unit-tested without a running CATIA V5
  instance; verification is compile + manual run by the user.
- Build with MSBuild (found via
  `C:\Program Files*\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe`),
  e.g. VS 18 Enterprise on this machine. .NET Framework 4.x WPF app with
  packages.config (MVVM Toolkit source generators warn - known, ignore).
- Console output doubles as the audit trail; keep the existing
  `ConsoleWriteLine` logging style (also raises the `LogErrors` event).
