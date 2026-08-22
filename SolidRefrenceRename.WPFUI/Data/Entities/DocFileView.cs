using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Data.Entities
{
    public class DocFileView
    {
        public int ID { get; set; }
        public int DocId { get; set; }
        public string FileName { get; set; }
        public int AssortmentId { get; set; }
        public string DocClass { get; set; }
        public string DestinationFolderAddress { get; set; }
        public string CODE { get; set; }
       


        public string SourceFileAddress { get; set; }
        public string NAME { get; set; }
        public string DESCRIBE { get; set; }
        public string DOCNO { get; set; }
        public string EXTENSION { get; set; }
        public string D1 { get; set; }
        public int D2 { get; set; }
        public DateTime DATE { get; set; }
        public DateTime VALIDATE { get; set; }
        public string NEVISANDE { get; set; }
        public string NASHER { get; set; }
        public string USER { get; set; }
        public string KEYWORD { get; set; }
        public int DOCINDEX { get; set; }
        public int? REDOCINDEX { get; set; }
        public decimal REVISION { get; set; }
        public decimal STATUSE { get; set; }
        public decimal VALIDSTATE { get; set; }
        public decimal DOCPATH { get; set; }
        public DateTime UPDATETIME { get; set; }
        public decimal Validlogic { get; set; }
        public int PROJNO { get; set; }
        public string noPath { get; set; }
        public string PathType { get; set; }

        public string SourceFolderAddress { get; set; }
        public string SourceFileName { get; set; }
        public string ExtensionClassCode { get; set; }
        public string REVISION_STR { get; set; }
        public int? PartNumber { get; set; }
        public string DestinationFileAddress { get; set; }
        public int AssortmentRevision { get; set; }
        public DateTime? AssortmentRevisionDate { get; set; }
        public string FileTypeInIFS { get; set; }
        public int SheetNumber { get; set; }

        public string SourceDbCode { get; set; }

    }
}
