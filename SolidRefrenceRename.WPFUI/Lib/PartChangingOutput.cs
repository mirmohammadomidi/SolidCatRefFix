using SolidRefrenceRename.WPFUI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Lib
{
    // (bool succeed, List<ProcessOutputDetails> errors, List<string> newAssembliesFound)
    public class PartChangingOutput
    {
        public bool Succeed { get; set; }
        public List<ProcessOutputDetails> Errors  { get; set; }
        public List<string> NewAssembliesFound { get; set; }
    }
}
