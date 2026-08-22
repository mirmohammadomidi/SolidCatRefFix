using System;

namespace SolidRefrenceRename.WPFUI.Lib
{
    [Flags]
    public enum FileExtensionTypes
    {
        None = 0,
        Assembly = 1,
        Part = 2,
        Drawing = 4,
        All = Assembly | Part | Drawing
    }
}
