// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BrightVision.Civil;

internal static class CivilWallRuleTemplateFixture
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); } catch (InvalidDataException) { refused = true; } catch (ArgumentException) { refused = true; } catch (IOException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; }
        Check(refused, message);
    }
    private static void RejectChange(Action<CivilWallRuleTemplate> change, string message)
    {
        CivilWallRuleTemplate value = CivilWallRuleTemplates.Default(); change(value);
        Refuses(delegate { CivilWallRuleTemplates.Validate(value); }, message);
        Refuses(delegate { CivilWallRuleTemplates.Fingerprint(value); }, message + " (fingerprint also refuses)");
    }
    private static void ChangesFingerprint(Action<CivilWallRuleTemplate> change, string message)
    {
        CivilWallRuleTemplate value = CivilWallRuleTemplates.Default(); string original = CivilWallRuleTemplates.Fingerprint(value); change(value);
        Check(original != CivilWallRuleTemplates.Fingerprint(value), message);
    }
    private static string Write(string folder, string name, string content)
    {
        string path = Path.Combine(folder, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path;
    }
    private static void BadJson(string folder, string name, string content, string message)
    {
        string path = Write(folder, name, content); byte[] before = File.ReadAllBytes(path);
        Refuses(delegate { CivilWallRuleTemplates.Load(path); }, message);
        Refuses(delegate { CivilWallRuleTemplates.Save(path, CivilWallRuleTemplates.Default()); }, message + " cannot be overwritten by a fresh template");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), message + " retains bytes");
    }

    private static int Main(string[] args)
    {
        string output = args[0];
        CivilWallRuleTemplate first = CivilWallRuleTemplates.Default(), second = CivilWallRuleTemplates.Default();
        CivilWallRuleTemplates.Validate(first);
        Check(first.SchemaVersion == 1 && first.Revision == 1, "Default schema/revision");
        Check(first.TemplateId == "BV.Wall.ProjectGeometry" && first.MethodId == "BV.Wall.RectSolid/1" && first.AreaMethodId == "BV.Wall.AxisProjection/1", "Default BV identity and implemented methods");
        Check(first.OpeningPolicy == "verified-rect-cutout" && first.JoinPolicy == "reject-overlap", "Actual supported policy selection");
        Check(first.AbsoluteToleranceMetres3 == 1e-8 && first.RelativeTolerance == 1e-8 && first.Priorities.Count == 0, "Default keeps the old strict tolerance and no fake priority mode");
        Check(first.Description.Contains("崛亮视觉") && first.Description.Contains("不是地区"), "BV project convention attribution");
        Check(!Object.ReferenceEquals(first, second) && !Object.ReferenceEquals(first.Priorities, second.Priorities), "Default instances and lists are independent");
        first.Name = "changed"; first.Priorities.Add(new CivilWallPriority { ElementUniqueId = "A", Rank = 1 });
        Check(second.Name != "changed" && second.Priorities.Count == 0, "Mutating one default cannot change another");
        first = CivilWallRuleTemplates.Default();
        CivilWallRuleTemplate cloned = CivilWallRuleTemplates.Clone(first);
        Check(!Object.ReferenceEquals(first, cloned) && !Object.ReferenceEquals(first.Priorities, cloned.Priorities), "Clone has a deep independent collection");
        Check(first.TemplateId == cloned.TemplateId && first.Revision == cloned.Revision && CivilWallRuleTemplates.Fingerprint(first) == CivilWallRuleTemplates.Fingerprint(cloned), "Clone preserves identity/version/all values");
        cloned.Name = "copy changed"; cloned.Priorities.Add(new CivilWallPriority { ElementUniqueId = "new", Rank = 1 });
        Check(first.Name != cloned.Name && first.Priorities.Count == 0, "Clone edits do not leak");
        CivilWallRuleTemplate copied = CivilWallRuleTemplates.Copy(first, "独立项目模板"); Guid copiedId;
        Check(Guid.TryParse(copied.TemplateId, out copiedId) && copiedId != Guid.Empty && copied.TemplateId != first.TemplateId && copied.Revision == 1, "Copy gets a new Guid and revision one");
        Check(copied.Name == "独立项目模板" && copied.OpeningPolicy == first.OpeningPolicy && copied.AbsoluteToleranceMetres3 == first.AbsoluteToleranceMetres3, "Copy preserves method configuration with new name");
        Check(CivilWallRuleTemplates.IsBuiltIn(first) && CivilWallRuleTemplates.IsBuiltIn("BV.Wall.LegacyExplicitOpeningOnly") && !CivilWallRuleTemplates.IsBuiltIn(copied) && !CivilWallRuleTemplates.IsBuiltIn((CivilWallRuleTemplate)null), "Built-in identity helper");
        var catalog = CivilWallRuleTemplates.Catalog(); var nextCatalog = CivilWallRuleTemplates.Catalog();
        Check(catalog.Count == 2 && catalog.Select(item => item.TemplateId).Distinct(StringComparer.Ordinal).Count() == 2, "Two distinct BV-owned conventions");
        Check(catalog[1].OpeningPolicy == "native-opening-only" && catalog[1].JoinPolicy == "reject-overlap", "Legacy template policy is explicit");
        Check(!Object.ReferenceEquals(catalog[0], nextCatalog[0]) && !Object.ReferenceEquals(catalog[1].Priorities, nextCatalog[1].Priorities), "Catalog instances are independent");
        Check(CivilWallRuleTemplates.Fingerprint(first).Length == 64 && CivilWallRuleTemplates.Fingerprint(first).All(character => "0123456789abcdef".Contains(character)), "Fingerprint is lower-case SHA256");
        ChangesFingerprint(value => value.TemplateId += ".copy", "Fingerprint binds template identity");
        ChangesFingerprint(value => value.Name += "二", "Fingerprint binds name");
        ChangesFingerprint(value => value.Description += "\n复核", "Fingerprint binds description");
        ChangesFingerprint(value => value.Revision++, "Fingerprint binds version");
        ChangesFingerprint(value => value.OpeningPolicy = "native-opening-only", "Fingerprint binds opening mode");
        ChangesFingerprint(value => value.AbsoluteToleranceMetres3 = 1e-9, "Fingerprint binds absolute tolerance");
        ChangesFingerprint(value => value.RelativeTolerance = 1e-9, "Fingerprint binds relative tolerance");
        string invariant = CivilWallRuleTemplates.Fingerprint(first);
        Check(invariant == "72632c65c0506db4cf9bfdfae6900e10960b84517f36068c8ddf48a763ff7332", "Canonical version one fixes the default rule identity with IEEE binary64 tolerance encoding");
        CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
        try { Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Check(CivilWallRuleTemplates.Fingerprint(first) == invariant, "Fingerprint is invariant under comma-decimal locale"); }
        finally { Thread.CurrentThread.CurrentCulture = previousCulture; }
        RejectChange(value => value.SchemaVersion = 2, "Unsupported schema");
        RejectChange(value => value.Revision = 0, "Zero revision");
        RejectChange(value => value.TemplateId = "", "Empty ID");
        RejectChange(value => value.Name = " ", "Blank name");
        RejectChange(value => value.Description = null, "Null description");
        RejectChange(value => value.Name = "bad\0name", "Control character name");
        RejectChange(value => value.Name = "bad\ud800name", "Unpaired Unicode surrogate name");
        RejectChange(value => value.Name = new String('n', 513), "Oversize name");
        RejectChange(value => value.Description = new String('x', 8193), "Oversize description");
        RejectChange(value => value.MethodId = "BV.Wall.Future/1", "Future volume method");
        RejectChange(value => value.AreaMethodId = "CN.Quota/1", "Fake regional area method");
        RejectChange(value => value.OpeningPolicy = "nominal-dimensions", "Nominal dimension deduction");
        RejectChange(value => value.JoinPolicy = "priority", "Unimplemented join method");
        RejectChange(value => value.AbsoluteToleranceMetres3 = Double.NaN, "NaN absolute tolerance");
        RejectChange(value => value.AbsoluteToleranceMetres3 = Double.PositiveInfinity, "Infinite absolute tolerance");
        RejectChange(value => value.AbsoluteToleranceMetres3 = 0, "Zero absolute tolerance");
        RejectChange(value => value.AbsoluteToleranceMetres3 = -1e-8, "Negative absolute tolerance");
        RejectChange(value => value.AbsoluteToleranceMetres3 = 1.0001e-8, "Absolute tolerance cannot relax old proof");
        RejectChange(value => value.RelativeTolerance = Double.NegativeInfinity, "Infinite relative tolerance");
        RejectChange(value => value.RelativeTolerance = Double.NaN, "NaN relative tolerance");
        RejectChange(value => value.RelativeTolerance = 0, "Zero relative tolerance");
        RejectChange(value => value.RelativeTolerance = 1.0001e-8, "Relative tolerance cannot relax old proof");
        RejectChange(value => value.Priorities = null, "Missing priorities list");
        RejectChange(value => value.Priorities.Add(null), "Null priority entry");
        RejectChange(value => value.Priorities.Add(new CivilWallPriority { ElementUniqueId = "A", Rank = 1 }), "Nonempty priorities cannot imply implemented allocation");
        RejectChange(value => value.Priorities.AddRange(new[] { new CivilWallPriority { ElementUniqueId = "A", Rank = 1 }, new CivilWallPriority { ElementUniqueId = "A", Rank = 2 } }), "Duplicate priority identities");
        RejectChange(value => value.Priorities.AddRange(new[] { new CivilWallPriority { ElementUniqueId = "A", Rank = 1 }, new CivilWallPriority { ElementUniqueId = "B", Rank = 1 } }), "Duplicate priority ranks");
        RejectChange(value => value.Priorities.Add(new CivilWallPriority { ElementUniqueId = "A", Rank = 0 }), "Invalid priority rank");
        Refuses(delegate { CivilWallRuleTemplates.Copy(first, ""); }, "Copy validates the new name");

        string path = Path.Combine(output, "default.bvwallrules.json"); CivilWallRuleTemplates.Save(path, first);
        byte[] initialBytes = File.ReadAllBytes(path); string json = File.ReadAllText(path, Encoding.UTF8);
        Check(!(initialBytes.Length >= 3 && initialBytes[0] == 239 && initialBytes[1] == 187 && initialBytes[2] == 191), "Saved JSON is UTF8 without BOM");
        Check(json.Contains("崛亮视觉") && !json.Contains("NaN") && !json.Contains("Infinity"), "Saved JSON has company attribution and finite numbers");
        CivilWallRuleTemplate loaded = CivilWallRuleTemplates.Load(path);
        Check(CivilWallRuleTemplates.Fingerprint(first) == CivilWallRuleTemplates.Fingerprint(loaded), "Save/load preserves every field and fingerprint");
        CivilWallRuleTemplates.Save(path, first); Check(initialBytes.SequenceEqual(File.ReadAllBytes(path)), "Identical revision can be saved idempotently");
        var changed = CivilWallRuleTemplates.Clone(first); changed.Name += " changed";
        Refuses(delegate { CivilWallRuleTemplates.Save(path, changed); }, "Changed same revision cannot overwrite a version");
        Check(initialBytes.SequenceEqual(File.ReadAllBytes(path)), "Rejected same-version overwrite preserves old bytes");
        changed.Revision++; CivilWallRuleTemplates.Save(path, changed); byte[] revisedBytes = File.ReadAllBytes(path);
        Check(CivilWallRuleTemplates.Load(path).Name == changed.Name && CivilWallRuleTemplates.Load(path).Revision == 2, "New revision replaces atomically");
        Refuses(delegate { CivilWallRuleTemplates.Save(path, first); }, "Old revision cannot replace newer template");
        Refuses(delegate { CivilWallRuleTemplates.Save(path, copied); }, "Different identity cannot overwrite an existing template");
        Check(revisedBytes.SequenceEqual(File.ReadAllBytes(path)), "Rejected old/foreign saves preserve new bytes");
        var invalid = CivilWallRuleTemplates.Clone(changed); invalid.AbsoluteToleranceMetres3 = Double.NaN;
        Refuses(delegate { CivilWallRuleTemplates.Save(path, invalid); }, "Invalid save never opens existing destination for writing");
        Check(revisedBytes.SequenceEqual(File.ReadAllBytes(path)), "Invalid save preserves previous file");
        var lockedChange = CivilWallRuleTemplates.Clone(changed); lockedChange.Revision++; lockedChange.Description += " updated";
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Refuses(delegate { CivilWallRuleTemplates.Save(path, lockedChange); }, "Locked existing template cannot be replaced");
        Check(revisedBytes.SequenceEqual(File.ReadAllBytes(path)), "Failed locked-file save preserves previous bytes");
        string missingParent = Path.Combine(output, "missing", "rule.json"); Refuses(delegate { CivilWallRuleTemplates.Save(missingParent, first); }, "Missing parent is not silently created");
        Refuses(delegate { CivilWallRuleTemplates.Save("relative.json", first); }, "Relative destination refused");
        Refuses(delegate { CivilWallRuleTemplates.Save(Path.Combine(output, "rules.txt"), first); }, "Non-JSON destination refused");
        Refuses(delegate { CivilWallRuleTemplates.Load(path + ":data.json"); }, "Alternate data stream refused");
        string directoryAsTarget = Path.Combine(output, "directory.json"); Directory.CreateDirectory(directoryAsTarget);
        Refuses(delegate { CivilWallRuleTemplates.Save(directoryAsTarget, first); }, "Atomic move failure cannot replace a directory");
        Check(Directory.Exists(directoryAsTarget) && Directory.GetFiles(output, ".bv-wall-rule-*.tmp").Length == 0, "Failed save cleans only its own temporary file");

        string reordered = "{\"Priorities\":[],\"RelativeTolerance\":1e-8,\"AbsoluteToleranceMetres3\":0.00000001,\"Revision\":1,\"JoinPolicy\":\"reject-overlap\",\"OpeningPolicy\":\"verified-rect-cutout\",\"AreaMethodId\":\"BV.Wall.AxisProjection/1\",\"MethodId\":\"BV.Wall.RectSolid/1\",\"Description\":\"" + first.Description + "\",\"Name\":\"" + first.Name + "\",\"TemplateId\":\"" + first.TemplateId + "\",\"SchemaVersion\":1}";
        Check(CivilWallRuleTemplates.Fingerprint(CivilWallRuleTemplates.Load(Write(output, "reordered.json", reordered))) == invariant, "JSON field order and equivalent numeric spelling do not change fingerprint");
        BadJson(output, "bad.json", "{broken", "Malformed JSON");
        BadJson(output, "empty.json", "", "Empty JSON");
        BadJson(output, "root-array.json", "[]", "Array root");
        BadJson(output, "root-null.json", "null", "Null root");
        BadJson(output, "unknown.json", json.Insert(1, "\"Execute\":\"anything\","), "Unknown top-level field");
        BadJson(output, "duplicate.json", json.Insert(1, "\"Revision\":1,"), "Duplicate known field");
        BadJson(output, "unknown-duplicate.json", json.Insert(1, "\"Future\":1,\"Future\":2,"), "Unknown duplicate field");
        BadJson(output, "missing.json", json.Replace("\"SchemaVersion\":1,", ""), "Missing required field");
        BadJson(output, "number-string.json", json.Replace("\"Revision\":1", "\"Revision\":\"1\""), "String cannot replace number");
        BadJson(output, "string-number.json", json.Replace("\"Name\":\"" + first.Name + "\"", "\"Name\":1"), "Number cannot replace string");
        BadJson(output, "schema2.json", json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"), "Unknown schema JSON");
        BadJson(output, "method.json", json.Replace("BV.Wall.RectSolid", "BV.Wall.Future"), "Unknown method JSON");
        BadJson(output, "priorities-null.json", json.Replace("\"Priorities\":[]", "\"Priorities\":null"), "Null priorities JSON");
        BadJson(output, "priority-unknown.json", json.Replace("\"Priorities\":[]", "\"Priorities\":[{\"ElementUniqueId\":\"A\",\"Rank\":1,\"Future\":true}]"), "Unknown nested priority field");
        BadJson(output, "priority-duplicate.json", json.Replace("\"Priorities\":[]", "\"Priorities\":[{\"ElementUniqueId\":\"A\",\"Rank\":1,\"Rank\":2}]"), "Duplicate nested priority field");
        BadJson(output, "priority-valid-shape.json", json.Replace("\"Priorities\":[]", "\"Priorities\":[{\"ElementUniqueId\":\"A\",\"Rank\":1}]"), "Future priority mode stays unavailable despite valid shape");
        BadJson(output, "oversize.json", new String(' ', CivilWallRuleTemplates.MaxBytes + 1), "Oversize JSON");
        File.WriteAllBytes(Path.Combine(output, "invalid-utf8.json"), new byte[] { 123, 255, 125 });
        Refuses(delegate { CivilWallRuleTemplates.Load(Path.Combine(output, "invalid-utf8.json")); }, "Malformed UTF8 refused");
        Check(Directory.GetFiles(output, ".bv-wall-rule-*.tmp").Length == 0, "No save temporary files remain");
        File.WriteAllText(Path.Combine(output, "report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks.ToString(CultureInfo.InvariantCulture) + ",\"scope\":\"BV-owned versioned rule-template validation, canonical fingerprint and bounded atomic JSON store; own managed DLL only\",\"templateSchemaVersion\":1,\"builtInCount\":2,\"revitStarted\":false,\"nativeDesktopInput\":false,\"childFixtureExecutable\":false,\"defaultFingerprint\":\"" + invariant + "\"}", new UTF8Encoding(false));
        Console.WriteLine("PASS " + checks.ToString(CultureInfo.InvariantCulture) + " wall rule-template assertions."); return 0;
    }
}
