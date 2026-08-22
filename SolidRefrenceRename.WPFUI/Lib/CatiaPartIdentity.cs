using System;
using System.Collections.Generic;

namespace CatiaReferenceRename.WPFUI.Lib
{
    /// <summary>
    /// Pre-computed CATIA identity of a part file, extracted once from the raw
    /// file bytes and cached (in the database or in memory) so that relevance
    /// matching does not have to read every part file from disk each time.
    /// </summary>
    public class CatiaPartIdentity
    {
        /// <summary>
        /// The full path of the part file this identity was extracted from
        /// (used as the cache key).
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// CATIA component UUID (e.g. "DR01AAA01"), or null/empty when the file
        /// carries none.
        /// </summary>
        public string Uuid { get; set; }

        /// <summary>
        /// CATIA part definition / part number (e.g. "F20435V1"), or null/empty
        /// when the file carries none.
        /// </summary>
        public string PartDefinition { get; set; }
    }

    /// <summary>
    /// A lookup of part-file-path → <see cref="CatiaPartIdentity"/>, built once
    /// from the database (or by scanning files) and passed to
    /// <see cref="CatiaUtils.ChangePartAddress"/> so that relevance matching
    /// uses cached identities instead of reading every part file.
    /// </summary>
    public class CatiaPartIdentityCache
    {
        private readonly Dictionary<string, CatiaPartIdentity> _lookup =
            new Dictionary<string, CatiaPartIdentity>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds or replaces an identity entry keyed by the normalised file path.
        /// </summary>
        public void Add(CatiaPartIdentity identity)
        {
            if (identity == null || string.IsNullOrEmpty(identity.FilePath)) return;
            _lookup[identity.FilePath] = identity;
        }

        /// <summary>
        /// Returns the cached identity for the given file path, or null.
        /// </summary>
        public CatiaPartIdentity Get(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            CatiaPartIdentity identity;
            _lookup.TryGetValue(filePath, out identity);
            return identity;
        }

        public int Count => _lookup.Count;

        public bool IsEmpty => _lookup.Count == 0;
    }
}
