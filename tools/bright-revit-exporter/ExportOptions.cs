using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BrightVision.Revit
{
    // These switches describe work performed by the independent exporter. They
    // are never forwarded to another add-in or used to load a vendor assembly.
    internal sealed class ExportOptions
    {
        internal string ModelName;
        internal string OutputPath;
        internal View3D View;
        internal bool IncludeTextures = true;
        internal bool IncludeSystemTree = true;
        internal bool IncludeSpaceTree = true;
        internal bool IncludeDisciplineTree = true;
        internal bool IncludeCadLinks = true;
        internal bool IncludeGrids;
        internal bool Include2DViews;
        internal IList<ElementId> Views2D = new List<ElementId>();
    }
}
