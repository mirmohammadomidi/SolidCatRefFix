using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.Lib.Config
{
    public class ConfigService 
    {
        public void SaveToFile(GeneralConfig workspace, string fileAdddress)
        {

            TextWriter writer = null;
            try
            {
                var contentsToWriteToFile = JsonConvert.SerializeObject(workspace, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings()
                {
                    PreserveReferencesHandling = PreserveReferencesHandling.Objects
                }
            );
                writer = new StreamWriter(fileAdddress, false);
                writer.Write(contentsToWriteToFile);
            }
            finally
            {
                if (writer != null)
                    writer.Close();
            }
        }

        public GeneralConfig ReadFromFile(string fileAdddress)
        {
            TextReader reader = null;
            try
            {
                if (!File.Exists(fileAdddress))
                {
                    return new GeneralConfig()
                    {

                    };
                }
                reader = new StreamReader(fileAdddress);
                var fileContents = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<GeneralConfig>(fileContents);
            }
            finally
            {
                if (reader != null)
                    reader.Close();
            }
        }
    }
}
