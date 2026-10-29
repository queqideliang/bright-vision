// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BrightVision.Civil;

internal static class CivilWallProjectRulesFixture
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); } catch (InvalidDataException) { refused = true; } catch (ArgumentException) { refused = true; } catch (IOException) { refused = true; }
        Check(refused, message);
    }
    private static string SerializeUnchecked(CivilProjectSettings profile)
    {
        using (var memory = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(CivilProjectSettings)).WriteObject(memory, profile);
            return new UTF8Encoding(false, true).GetString(memory.ToArray());
        }
    }
    private static string Write(string output, string name, string content)
    {
        string path = Path.Combine(output, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path;
    }
    private static void InvalidFile(string output, string name, string content, CivilProjectSettings valid, string message)
    {
        string path = Write(output, name, content); byte[] before = File.ReadAllBytes(path);
        Refuses(delegate { CivilProjectStore.Load(path); }, message);
        Refuses(delegate { CivilProjectStore.Save(path, valid); }, message + " cannot be overwritten by Save");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), message + " preserves bytes");
    }

    [DataContract]
    private sealed class SchemaOneHeader
    {
        [DataMember] public int SchemaVersion { get; set; }
    }

    private static int Main(string[] args)
    {
        string output = args[0];
        var native = new CivilSnapshot { ModelTitle = "规则工程", DocumentScope = "fixture-document-A", SourcePath = Path.Combine(output, "source.rvt"), Revision = 3 };
        native.Elements.Add(new CivilElementRecord { ElementId = "10", UniqueId = "wall-uid", KindCode = "wall", LevelUniqueId = "level-uid", Name = "墙", Supported = true,
            Volume = new CivilMetric { Status = "available", Unit = "m3", Value = 5.6, Source = "HOST_VOLUME_COMPUTED" } });
        CivilProjectSettings legacy = CivilProject.Create(native, "旧格式工程");
        Check(legacy.SchemaVersion == 1 && legacy.WallRuleTemplate == null, "Create preserves the 1.8 schema-1 contract");
        legacy.QuantityLevels.Add(new CivilQuantityLevel { Id = "Q1", Name = "计量层一", ElevationMetres = 0 });
        legacy.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-uid", QuantityLevelId = "Q1" });
        legacy.Segments.Add(new CivilSegment { Id = "S1", Name = "施工段一" });
        legacy.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = "wall-uid", SegmentId = "S1" });
        string path = Path.Combine(output, "project.bvproject.json"); CivilProjectStore.Save(path, legacy);
        string originalJson = File.ReadAllText(path, Encoding.UTF8); byte[] originalBytes = File.ReadAllBytes(path);
        Check(!originalJson.Contains("WallRuleTemplate") && originalJson.Contains("\"SchemaVersion\":1"), "Schema one emits no rules field");
        CivilProjectSettings roundTrip = CivilProjectStore.Load(path);
        Check(roundTrip.SchemaVersion == 1 && roundTrip.WallRuleTemplate == null && roundTrip.ProjectId == legacy.ProjectId && roundTrip.Revision == legacy.Revision, "Schema one identity/revision round trip");
        Check(roundTrip.QuantityLevels.Single().Id == "Q1" && roundTrip.ElementSegments.Single().SegmentId == "S1", "Schema one reporting mappings round trip");
        string legacyExtension = Write(output, "legacy-extension.json", originalJson.Insert(1, "\"LegacyExtension\":{\"KeepReading\":true},"));
        Check(CivilProjectStore.Load(legacyExtension).SchemaVersion == 1, "Schema one retains historical unknown-extension loading compatibility");

        CivilProjectSettings migrated = CivilProject.Clone(legacy);
        migrated.SchemaVersion = 2; migrated.WallRuleTemplate = CivilWallRuleTemplates.Default(); CivilProject.Touch(migrated);
        string fingerprint = CivilWallRuleTemplates.Fingerprint(migrated.WallRuleTemplate);
        Check(legacy.SchemaVersion == 1 && legacy.WallRuleTemplate == null && migrated.Revision == legacy.Revision + 1, "Migration clones and increments without changing the source object");
        CivilProjectStore.Save(path, migrated); string schemaTwoJson = File.ReadAllText(path, Encoding.UTF8);
        Check(schemaTwoJson.Contains("\"SchemaVersion\":2") && schemaTwoJson.Contains("WallRuleTemplate"), "Schema two explicitly declares embedded rules");
        CivilProjectSettings loaded = CivilProjectStore.Load(path);
        Check(loaded.SchemaVersion == 2 && loaded.ProjectId == legacy.ProjectId && loaded.SourcePath == legacy.SourcePath && loaded.SourceDocumentScope == legacy.SourceDocumentScope, "Migration keeps project and model identity");
        Check(CivilWallRuleTemplates.Fingerprint(loaded.WallRuleTemplate) == fingerprint, "Strict embedded-rule round trip retains its fingerprint");
        Check(loaded.Revision == migrated.Revision && loaded.QuantityLevels.Single().Id == "Q1" && loaded.ElementSegments.Single().SegmentId == "S1", "Migration keeps mappings, assignments and new revision");
        using (var memory = new MemoryStream(Encoding.UTF8.GetBytes(schemaTwoJson)))
        {
            var oldHeader = (SchemaOneHeader)new DataContractJsonSerializer(typeof(SchemaOneHeader)).ReadObject(memory);
            Check(oldHeader.SchemaVersion == 2, "A schema-one-only reader still sees version two before rejecting unsupported schema; no implicit downgrade");
        }
        CivilProjectSettings clone = CivilProject.Clone(loaded);
        Check(!Object.ReferenceEquals(clone.WallRuleTemplate, loaded.WallRuleTemplate) && !Object.ReferenceEquals(clone.WallRuleTemplate.Priorities, loaded.WallRuleTemplate.Priorities), "Project clone deeply copies its rule and collection");
        clone.WallRuleTemplate.Name += " 修改"; clone.WallRuleTemplate.AbsoluteToleranceMetres3 = 1e-9;
        clone.QuantityLevels[0].Name += " 修改";
        Check(CivilWallRuleTemplates.Fingerprint(loaded.WallRuleTemplate) == fingerprint && loaded.QuantityLevels[0].Name == "计量层一", "Clone edits cannot alter loaded rule or mappings");
        CivilSnapshot applied = CivilProject.Apply(native, loaded);
        Check(applied.ProjectId == loaded.ProjectId && applied.ProjectRevision == loaded.Revision && applied.Elements.Single().QuantityLevelId == "Q1", "Schema-two project can annotate a matching model");
        Check(native.ProjectId == "" && native.Elements.Single().QuantityLevelId == "" && native.Elements.Single().Volume.Value == 5.6, "Apply leaves source identities, native quantities and annotations unchanged");
        var foreign = CivilProject.Clone(loaded); foreign.SourceDocumentScope = "another-doc";
        Refuses(delegate { CivilProject.Apply(native, foreign); }, "Wrong model scope is rejected");
        foreign = CivilProject.Clone(loaded); foreign.SourcePath = Path.Combine(output, "another.rvt");
        Refuses(delegate { CivilProject.Apply(native, foreign); }, "Wrong source path is rejected");
        byte[] migratedBytes = File.ReadAllBytes(path);
        Refuses(delegate { CivilProjectStore.Save(path, foreign); }, "Foreign source cannot overwrite a rules project");
        Check(migratedBytes.SequenceEqual(File.ReadAllBytes(path)), "Foreign-source save preserves the rules file");
        var downgraded = CivilProject.Clone(legacy); downgraded.Revision = migrated.Revision + 1;
        Refuses(delegate { CivilProjectStore.Save(path, downgraded); }, "Schema one cannot erase rules from an existing schema-two file");
        Check(migratedBytes.SequenceEqual(File.ReadAllBytes(path)), "Downgrade attempt preserves embedded rules");
        var changedRules = CivilProject.Clone(loaded); changedRules.WallRuleTemplate = CivilWallRuleTemplates.LegacyExplicitOpeningOnly();
        Refuses(delegate { CivilProjectStore.Save(path, changedRules); }, "Changing project rules requires a new project revision");
        Check(migratedBytes.SequenceEqual(File.ReadAllBytes(path)), "Same-revision rule change preserves the existing file");
        CivilProject.Touch(changedRules); CivilProjectStore.Save(path, changedRules);
        Check(CivilProjectStore.Load(path).WallRuleTemplate.OpeningPolicy == "native-opening-only", "New project revision persists a different effective policy");
        Check(loaded.WallRuleTemplate.OpeningPolicy == "verified-rect-cutout" && loaded.Revision == migrated.Revision, "Saving changed rules does not mutate the previously loaded object");
        var stale = CivilProject.Clone(migrated); byte[] updatedBytes = File.ReadAllBytes(path);
        Refuses(delegate { CivilProjectStore.Save(path, stale); }, "Old revision cannot replace new policy");
        Check(updatedBytes.SequenceEqual(File.ReadAllBytes(path)), "Stale save preserves new policy bytes");
        var invalid = CivilProject.Clone(loaded); invalid.SchemaVersion = 1;
        Refuses(delegate { CivilProject.Validate(invalid); }, "Schema one cannot carry a rule object");
        Refuses(delegate { CivilProject.Clone(invalid); }, "Clone refuses invalid schema-one rules");
        invalid = CivilProject.Clone(loaded); invalid.WallRuleTemplate = null;
        Refuses(delegate { CivilProject.Validate(invalid); }, "Schema two requires a rule object");
        invalid = CivilProject.Clone(loaded); invalid.WallRuleTemplate.JoinPolicy = "priority";
        Refuses(delegate { CivilProject.Validate(invalid); }, "Project cannot enable an unimplemented join rule");
        Refuses(delegate { CivilProjectStore.Save(path, invalid); }, "Invalid embedded rule cannot overwrite a complete file");
        Check(updatedBytes.SequenceEqual(File.ReadAllBytes(path)), "Invalid embedded rule save preserves previous file");
        var noRules = CivilProject.Clone(loaded); noRules.WallRuleTemplate = null;
        InvalidFile(output, "schema2-missing-rule.json", SerializeUnchecked(noRules), changedRules, "Schema two missing rules");
        InvalidFile(output, "schema2-null-rule.json", schemaTwoJson.Replace("\"WallRuleTemplate\":{", "\"WallRuleTemplate\":null,\"DiscardedRule\":{ "), changedRules, "Schema two null rule cannot hide behind an extra object");
        var stealth = CivilProject.Clone(loaded); stealth.SchemaVersion = 1;
        InvalidFile(output, "schema1-stealth-rule.json", SerializeUnchecked(stealth), changedRules, "Schema one stealth rule");
        InvalidFile(output, "unknown-rule-field.json", schemaTwoJson.Replace("\"WallRuleTemplate\":{", "\"WallRuleTemplate\":{\"FuturePolicy\":\"execute\","), changedRules, "Unknown embedded rule field");
        InvalidFile(output, "duplicate-rule-field.json", schemaTwoJson.Replace("\"WallRuleTemplate\":{", "\"WallRuleTemplate\":{\"Revision\":1,"), changedRules, "Duplicate embedded rule field");
        InvalidFile(output, "unknown-root-field.json", schemaTwoJson.Insert(1, "\"Execute\":\"anything\","), changedRules, "Unknown schema-two root field");
        InvalidFile(output, "duplicate-root-field.json", schemaTwoJson.Insert(1, "\"Name\":\"another\","), changedRules, "Duplicate schema-two root field");
        InvalidFile(output, "duplicate-schema-field.json", schemaTwoJson.Insert(1, "\"SchemaVersion\":2,"), changedRules, "Duplicate schema version");
        string ruleFile = Path.Combine(output, "rule.json"); CivilWallRuleTemplates.Save(ruleFile, loaded.WallRuleTemplate); string ruleJson = File.ReadAllText(ruleFile, Encoding.UTF8);
        InvalidFile(output, "duplicate-rule-object.json", schemaTwoJson.Insert(1, "\"WallRuleTemplate\":" + ruleJson + ","), changedRules, "Duplicate embedded rule objects");
        InvalidFile(output, "schema3.json", schemaTwoJson.Replace("\"SchemaVersion\":2", "\"SchemaVersion\":3"), changedRules, "Unsupported project schema");
        InvalidFile(output, "rule-schema2.json", schemaTwoJson.Replace("\"WallRuleTemplate\":{\"SchemaVersion\":1", "\"WallRuleTemplate\":{\"SchemaVersion\":2"), changedRules, "Unsupported embedded rule schema");
        InvalidFile(output, "rule-method.json", schemaTwoJson.Replace("BV.Wall.RectSolid", "BV.Wall.Future"), changedRules, "Unsupported embedded rule method");
        InvalidFile(output, "rule-null-priorities.json", schemaTwoJson.Replace("\"Priorities\":[]", "\"Priorities\":null"), changedRules, "Null embedded priorities");
        InvalidFile(output, "rule-unknown-priority.json", schemaTwoJson.Replace("\"Priorities\":[]", "\"Priorities\":[{\"ElementUniqueId\":\"A\",\"Rank\":1,\"Future\":true}]"), changedRules, "Unknown nested priority option");
        InvalidFile(output, "malformed.json", "{broken", changedRules, "Malformed project JSON");
        InvalidFile(output, "root-array.json", "[]", changedRules, "Array project root");
        InvalidFile(output, "empty.json", "", changedRules, "Empty project JSON");
        InvalidFile(output, "oversize.json", new String(' ', CivilProjectStore.MaxBytes + 1), changedRules, "Over-limit project JSON");

        var minimum = CivilProject.Clone(loaded); minimum.CreatedAtUtc = minimum.UpdatedAtUtc = minimum.PreferredScope = minimum.PhaseId = minimum.DesignOptionId = null;
        string minimumPath = Path.Combine(output, "optional-omitted.json"); CivilProjectStore.Save(minimumPath, minimum);
        Check(CivilWallRuleTemplates.Fingerprint(CivilProjectStore.Load(minimumPath).WallRuleTemplate) == fingerprint, "Optional project metadata may be omitted in schema two");
        CivilProjectStore.Save(minimumPath, CivilProject.Clone(minimum));
        Check(CivilProjectStore.Load(minimumPath).Revision == minimum.Revision, "Idempotent schema-two save works without optional metadata");
        var migrationWithoutTouch = CivilProject.Clone(legacy); migrationWithoutTouch.SchemaVersion = 2; migrationWithoutTouch.WallRuleTemplate = CivilWallRuleTemplates.Default();
        string noTouchPath = Write(output, "migration-needs-touch.json", originalJson);
        Refuses(delegate { CivilProjectStore.Save(noTouchPath, migrationWithoutTouch); }, "Migration explicitly advances the project revision");
        Check(originalBytes.SequenceEqual(File.ReadAllBytes(noTouchPath)), "Rejected migration keeps schema-one file intact");
        Check(Directory.GetFiles(output, ".bright-civil-*.tmp").Length == 0, "No project-save temporary files remain");
        File.WriteAllText(Path.Combine(output, "report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks.ToString(CultureInfo.InvariantCulture) + ",\"scope\":\"Own managed project schema 1/2, versioned embedded BV rules, strict JSON, source binding, deep clone and atomic migration; synthetic files only\",\"defaultRuleFingerprint\":\"" + fingerprint + "\",\"legacyHostExecution\":false,\"revitStarted\":false,\"nativeDesktopInput\":false,\"childFixtureExecutable\":false}", new UTF8Encoding(false));
        Console.WriteLine("PASS " + checks.ToString(CultureInfo.InvariantCulture) + " wall project-rule assertions."); return 0;
    }
}
