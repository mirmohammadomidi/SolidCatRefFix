using SolidRefrenceRename.WPFUI.Data.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.ModelConfiguration.Conventions;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Data
{
    public class IFSCodeCleansingDBContext : DbContext
    {
        // Constructor
        public IFSCodeCleansingDBContext() : base("name=IFSCC_ConnectionString")
        {
            // Optional: Enable lazy loading
            this.Configuration.LazyLoadingEnabled = true;
        }
        public IFSCodeCleansingDBContext(string nameOrConnectionString) : base(nameOrConnectionString)
        {

        }

        // DbSet for your view
        public DbSet<DocFileView> DocFileViews { get; set; }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            // Configure the view mapping
            modelBuilder.Entity<DocFileView>()
                .ToTable("vw_docFiles"); // Your view name

            // Remove convention that would try to create tables
            modelBuilder.Conventions.Remove<PluralizingTableNameConvention>();
        }

        /// <summary>
        /// Batch-updates the CatiaUuid column for a set of rows.
        ///
        /// Each item in <paramref name="uuids"/> is a (ID, UUID) pair.  The
        /// update is done via raw SQL because vw_docFiles is a view and EF
        /// cannot generate UPDATE statements against it.
        ///
        /// Call this from <see cref="ViewModel.SolidFixViewModel.FillSiteUUID"/>
        /// after extracting UUIDs from the part files.
        /// </summary>
        public void UpdateUUIDs(List<KeyValuePair<int, string>> uuids)
        {
            if (uuids == null || uuids.Count == 0) return;

            using (var tx = Database.BeginTransaction())
            {
                try
                {
                    foreach (var item in uuids)
                    {
                        Database.ExecuteSqlCommand(
                            "UPDATE vw_docFiles SET Uuid = {0} WHERE ID = {1}",
                            item.Value ?? (object)DBNull.Value,
                            item.Key);
                    }
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }
    }
}
