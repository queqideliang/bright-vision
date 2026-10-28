using System;
using System.IO;
using Autodesk.Revit.DB;
using BrightVision.Revit;

internal static class SelectedViewFixture
{
    private static int assertions;

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("ASSERT: " + message);
    }

    private static void Rejects(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, message);
    }

    private static View3D View(Document document, string uniqueId, int integerId)
    {
        return new View3D { Document = document, UniqueId = uniqueId, Id = new ElementId(integerId) };
    }

    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.WriteLine("FAIL: " + error.GetType().Name + ": " + error.Message); return 1; }
    }

    private static void Run()
    {
        ModelIdentity model = new ModelIdentity();
        Document captured = new Document(model);
        Document anotherWrapper = new Document(model);
        View3D selected = View(anotherWrapper, "model-a-view-17", 17);
        View3D current = View(captured, "model-a-view-17", 17);
        model.Elements.Add(current.UniqueId, current);

        Assert(!Object.ReferenceEquals(captured, selected.Document), "The fixture uses distinct managed Document wrappers.");
        Assert(captured.Equals(selected.Document), "Distinct wrappers identify the same native model.");
        View3D resolved = SelectedViewResolver.Resolve(captured, selected);
        Assert(Object.ReferenceEquals(current, resolved), "The captured document supplies the current view wrapper.");
        Assert(!Object.ReferenceEquals(selected, resolved), "The stale selection wrapper is not returned.");
        Assert(captured.UniqueIdLookups == 1 && captured.LastUniqueId == selected.UniqueId, "Resolve queries the captured document using the selected UniqueId.");
        Assert(captured.IntegerIdLookups == 0, "Resolve never uses a model-local integer ElementId.");

        ModelIdentity otherModel = new ModelIdentity();
        Document otherDocument = new Document(otherModel);
        View3D otherSelection = View(otherDocument, "model-b-view-17", 17);
        otherModel.Elements.Add(otherSelection.UniqueId, otherSelection);
        int beforeForeignLookup = captured.UniqueIdLookups;
        Rejects(delegate { SelectedViewResolver.Resolve(captured, otherSelection); }, "A different model's view with the same integer ID is rejected.");
        Assert(captured.UniqueIdLookups == beforeForeignLookup && captured.IntegerIdLookups == 0, "Cross-model rejection occurs before querying the wrong model.");

        View3D clonedSelection = View(otherDocument, current.UniqueId, 17);
        Rejects(delegate { SelectedViewResolver.Resolve(captured, clonedSelection); }, "A cloned model sharing the same UniqueId is rejected by native document identity.");
        Assert(captured.UniqueIdLookups == beforeForeignLookup, "A cloned-model selection does not query the captured model.");

        Rejects(delegate { SelectedViewResolver.Resolve(captured, View(anotherWrapper, "missing-view", 88)); }, "A missing or removed view is rejected.");

        View3D deleted = View(captured, "deleted-view", 89);
        deleted.IsValidObject = false;
        model.Elements.Add("deleted-view", deleted);
        Rejects(delegate { SelectedViewResolver.Resolve(captured, View(anotherWrapper, "deleted-view", 89)); }, "An invalid current view is rejected after resolution.");

        View3D template = View(captured, "template-view", 90);
        template.IsTemplate = true;
        model.Elements.Add(template.UniqueId, template);
        Rejects(delegate { SelectedViewResolver.Resolve(captured, View(anotherWrapper, "template-view", 90)); }, "A resolved template is rejected.");

        model.Elements.Add("plan-view", new Autodesk.Revit.DB.View { Document = captured, UniqueId = "plan-view", Id = new ElementId(91) });
        Rejects(delegate { SelectedViewResolver.Resolve(captured, View(anotherWrapper, "plan-view", 91)); }, "A resolved non-three-dimensional view is rejected.");

        Rejects(delegate { SelectedViewResolver.Resolve(null, selected); }, "A null captured document is rejected.");
        Rejects(delegate { SelectedViewResolver.Resolve(captured, null); }, "A null selection is rejected.");

        View3D invalidSelection = View(anotherWrapper, "invalid-selection", 92);
        invalidSelection.IsValidObject = false;
        int beforeLookup = captured.UniqueIdLookups;
        Rejects(delegate { SelectedViewResolver.Resolve(captured, invalidSelection); }, "An invalid selection is rejected.");
        Assert(captured.UniqueIdLookups == beforeLookup, "Invalid selection never queries the captured document.");

        Document closedDocument = new Document(model) { IsValidObject = false };
        Rejects(delegate { SelectedViewResolver.Resolve(closedDocument, selected); }, "A closed or invalid document is rejected.");
        Assert(closedDocument.UniqueIdLookups == 0 && closedDocument.IntegerIdLookups == 0, "An invalid document is not queried.");

        View3D withoutDocument = View(null, current.UniqueId, 17);
        Rejects(delegate { SelectedViewResolver.Resolve(captured, withoutDocument); }, "A selection without a source document is rejected.");
        View3D closedSource = View(new Document(model) { IsValidObject = false }, current.UniqueId, 17);
        Rejects(delegate { SelectedViewResolver.Resolve(captured, closedSource); }, "A selection from an invalid source-document wrapper is rejected.");

        Rejects(delegate { SelectedViewResolver.Resolve(captured, View(anotherWrapper, "", 93)); }, "An empty UniqueId is rejected.");
        Rejects(delegate { SelectedViewResolver.Resolve(captured, View(anotherWrapper, null, 94)); }, "A null UniqueId is rejected.");

        Console.WriteLine("Passed " + assertions + " selected-view identity assertions; no Revit assemblies loaded.");
    }
}
