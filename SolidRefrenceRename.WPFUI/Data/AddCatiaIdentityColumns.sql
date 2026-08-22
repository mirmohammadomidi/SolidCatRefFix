-- =====================================================================
-- Add CatiaUuid and CatiaPartDefinition columns to support fast
-- relevance matching without reading every part file from disk.
--
-- vw_docFiles is a VIEW, so the columns must be added to the UNDERLYING
-- TABLE that the view selects from.  Replace <TableName> below with the
-- actual table name behind vw_docFiles.
--
-- After running this script, start the WPF app and run StartFixing once:
-- the code will automatically backfill the new columns by scanning the
-- CATPart files and extracting their UUID + part definition.
-- =====================================================================

-- Step 1: Add the columns to the underlying table.
-- Replace <TableName> with the real table name behind vw_docFiles.
ALTER TABLE <TableName>
    ADD CatiaUuid NVARCHAR(50) NULL,
        CatiaPartDefinition NVARCHAR(100) NULL;

-- Step 2: Recreate the view to include the new columns.
-- Replace <TableName> and adjust the column list to match the existing view.
-- IMPORTANT: review the existing view definition first (sp_helptext vw_docFiles)
-- and add the two new columns to the SELECT list.
--
-- Example (adjust as needed):
--
-- ALTER VIEW vw_docFiles AS
-- SELECT
--     ID, DocId, FileName, AssortmentId, DocClass,
--     DestinationFolderAddress, CODE, SourceFileAddress,
--     NAME, DESCRIBE, DOCNO, EXTENSION, D1, D2,
--     DATE, VALIDATE, NEVISANDE, NASHER, [USER],
--     KEYWORD, DOCINDEX, REDOCINDEX, REVISION, STATUSE,
--     VALIDSTATE, DOCPATH, UPDATETIME, Validlogic, PROJNO,
--     noPath, PathType, SourceFolderAddress, SourceFileName,
--     ExtensionClassCode, REVISION_STR, PartNumber,
--     DestinationFileAddress, AssortmentRevision,
--     AssortmentRevisionDate, FileTypeInIFS, SheetNumber,
--     SourceDbCode,
--     CatiaUuid,              -- NEW
--     CatiaPartDefinition     -- NEW
-- FROM <TableName>;

-- Step 3: Verify the columns are visible through the view.
-- SELECT TOP 1 CatiaUuid, CatiaPartDefinition FROM vw_docFiles;
