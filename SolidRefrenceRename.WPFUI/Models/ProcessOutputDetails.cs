using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Models
{
    public class ProcessOutputDetails
    {
        public ProcessOutputDetails()
        {

        }
        public ProcessOutputDetails(string message)
        {
            Message = message;
            Level = "error";
        }
        public ProcessOutputDetails(string message,string assemblyFile)
        {
            Message = message;
            AssemblyFile=assemblyFile;
            Level = "error";
        }
        public string Level { get; set; } //e.g error, warning, success
        public string AssemblyFile { get; set; }      // the *.sldasm that is being processed
        public string PartName { get; set; }      // the name that could not be found in the dictionary
        public string Message { get; set; }      // e.g. "Part not present in part dictionary"
    }
}
