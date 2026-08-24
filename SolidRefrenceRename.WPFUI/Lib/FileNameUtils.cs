using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Lib
{
    public class FileNameUtils
    {
        public static string GetMainFileName(string drawingFileName)
        {
            // Extract the file name from the path (handles both UNC and local paths)
            string fileName = System.IO.Path.GetFileName(drawingFileName);

            // Remove the file extension
            string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(fileName);

            // Split by '-' and get the second part (index 1)
            string[] parts = fileNameWithoutExtension.Split('-');

            // Return the second part if there are at least 2 parts
            if (parts.Length >= 2)
            {
                return parts[1];
            }

            // Return empty string if we can't extract the expected part
            return string.Empty;
        }
    }

}
