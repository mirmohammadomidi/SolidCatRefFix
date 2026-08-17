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
    }
}
