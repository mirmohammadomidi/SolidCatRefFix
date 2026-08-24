using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Lib
{
    public class PartNewRef
    {
        public PartNewRef()
        {
            
        }
        public PartNewRef(string partName,string newAddress,string code)
        {
            PartName = partName;
            NewAddress = newAddress;
            Code = code;
        }
        public string PartName { get; set; }
        public string  NewAddress { get; set; }
        public string Code { get; set; }
    }
}
