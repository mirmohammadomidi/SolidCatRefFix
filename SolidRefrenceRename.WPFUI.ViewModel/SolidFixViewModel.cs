using CommunityToolkit.Mvvm.ComponentModel;
using SolidRefrenceRename.WPFUI.Data;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolidRefrenceRename.WPFUI.ViewModel
{
    public partial class SolidFixViewModel:ObservableObject
    {
       
        public event EventHandler<int> OnNumberOfAssembliesChanged;
        public async Task StartFixing()
        {
            using (var context = new IFSCodeCleansingDBContext())
            {
                try
                {
                    await Task.Run(async () =>
                    {
                        var allAssemblies = await context.DocFileViews.Where(uu => uu.EXTENSION.ToUpper() == "SLDASM").ToListAsync();
                        if (OnNumberOfAssembliesChanged != null)
                            OnNumberOfAssembliesChanged(this, allAssemblies.Count);
                        // NumberOfAssemblies= allAssemblies.Count;
                    });

                }
                catch (Exception ex)
                {
                    // Handle exception
                    Console.WriteLine($"Error reading employees: {ex.Message}");
                   
                }
            }
           
        }
    }
}
