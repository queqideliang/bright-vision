using System;
using System.Collections.Generic;

// Small, deliberately separate API fixture. The production resolver is
// compiled against these types; no Revit or vendor assembly is loaded.
namespace Autodesk.Revit.DB
{
    public sealed class ElementId
    {
        public int IntegerValue { get; private set; }
        public ElementId(int value) { IntegerValue = value; }
    }

    public class Element
    {
        private string uniqueId;
        public bool IsValidObject { get; set; }
        public Document Document { get; set; }
        public ElementId Id { get; set; }
        public string UniqueId
        {
            get
            {
                if (!IsValidObject) throw new InvalidOperationException("Attempted to read an invalid element.");
                return uniqueId;
            }
            set { uniqueId = value; }
        }
        public Element() { IsValidObject = true; }
    }

    public class View : Element
    {
        public bool IsTemplate { get; set; }
    }

    public class View3D : View { }

    public sealed class ModelIdentity
    {
        public readonly Dictionary<string, Element> Elements = new Dictionary<string, Element>(StringComparer.Ordinal);
    }

    public class Document
    {
        private readonly ModelIdentity model;
        public bool IsValidObject { get; set; }
        public int UniqueIdLookups { get; private set; }
        public int IntegerIdLookups { get; private set; }
        public string LastUniqueId { get; private set; }
        public Document(ModelIdentity value) { model = value; IsValidObject = true; }
        public override bool Equals(object value)
        {
            Document other = value as Document;
            return other != null && Object.ReferenceEquals(model, other.model);
        }
        public override int GetHashCode() { return model.GetHashCode(); }
        public Element GetElement(string uniqueId)
        {
            if (!IsValidObject) throw new InvalidOperationException("Attempted to query an invalid document.");
            UniqueIdLookups++;
            LastUniqueId = uniqueId;
            Element value;
            return model.Elements.TryGetValue(uniqueId, out value) ? value : null;
        }
        public Element GetElement(ElementId id)
        {
            IntegerIdLookups++;
            throw new InvalidOperationException("An integer ID cannot establish model ownership.");
        }
    }
}
