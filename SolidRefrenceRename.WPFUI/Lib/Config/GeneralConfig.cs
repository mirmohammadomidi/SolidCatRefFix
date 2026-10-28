using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Lib
{
    public class GeneralConfig
    {
        public string ManagementConnectionString { get; set; }

        /// <summary>
        /// When &gt; 0, every selection from vw_docFiles returns only rows
        /// with this AssortmentGroupId.  0 (the default, also used when the
        /// entry is missing from generalConfig.json) means no filtering.
        /// </summary>
        public int AssortmentGroupId { get; set; }
    }
}
