using System;

namespace SolidRefrenceRename.WPFUI.Lib
{
    [Flags]
    public enum FileExtensionTypes
    {
        None = 0,
        Assembly = 1,
        Drawing = 4,
        All = Assembly |  Drawing
    }
}
