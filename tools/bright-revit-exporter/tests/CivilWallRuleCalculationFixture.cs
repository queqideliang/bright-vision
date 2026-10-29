// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrightVision.Civil;

// Independent dimensional expectations for the rule-to-calculation boundary.
// Facts are explicit controlled inputs, not evidence of native IFC/CSG results.
internal static class CivilWallRuleCalculationFixture
{
    private static int checks;
    private static readonly List<string> cases = new List<string>();
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    private static void Near(double? actual,double expected,string message){Check(actual.HasValue&&Math.Abs(actual.Value-expected)<=1e-10,message);}
    private static CivilMetric Length(double value,string source){return new CivilMetric{Status="available",Unit="m",Value=value,Source=source};}
    private static CivilWallSnapshot Input(CivilWallRuleTemplate rule)
    {
        var native = new CivilSnapshot{ModelTitle="independent ten-by-three wall",DocumentScope="domain-rule-fixture",SourcePath="H:\\fixture\\rules.rvt",Revision=17};
        var record = new CivilElementRecord{ElementId="10",UniqueId="wall-10",Name="native unchanged",KindCode="wall",Supported=true,
            Volume=new CivilMetric{Status="available",Unit="m3",Value=123,Source="HOST_VOLUME_COMPUTED"},
            Area=new CivilMetric{Status="available",Unit="m2",Value=456,Source="HOST_AREA_COMPUTED"}};
        native.Elements.Add(record);
        var wall = new CivilWallFact{ElementId="10",UniqueId="wall-10",TypeUniqueId="type-1",TypeName="200 mm",Name="wall",Status="verified",GeometryVerified=true,InterwallOverlapCheckComplete=true,
            ParameterLength=Length(10,"LocationCurve.Curve.Length"),ParameterHeight=Length(3,"WALL_USER_HEIGHT_PARAM"),ParameterWidth=Length(0.2,"WallType.Width"),
            GeometryVolumeMetres3=6,SymmetricDifferenceMetres3=0,GeometryToleranceMetres3=1e-8,NativeVolume=record.Volume,NativeArea=record.Area};
        return new CivilWallSnapshot{NativeSnapshot=native,RuleTemplate=rule,Walls=new List<CivilWallFact>{wall}};
    }
    private static CivilWallInsert Hole(string id,string method,double x0,double x1,double z0,double z1)
    {
        return new CivilWallInsert{ElementId=id,UniqueId="insert-"+id,HostUniqueId="wall-10",Kind="rect-opening",Status="recognized",CutoutMethod=method,
            Source="controlled exact local rectangle; no nominal dimensions",RectX0=x0,RectX1=x1,RectZ0=z0,RectZ1=z1};
    }
    private static CivilWallCalculationBatch Calculate(CivilWallSnapshot input,string name)
    {
        string nativeBefore=CivilWallBatchIntegrity.SnapshotFingerprint(input.NativeSnapshot);
        string ruleBefore=input.RuleTemplate==null?null:CivilWallRuleTemplates.Fingerprint(input.RuleTemplate);
        CivilWallFact fact=input.Walls.Single();CivilMetric native=fact.NativeVolume;List<CivilWallInsert> inserts=fact.Inserts;
        double? tolerance=fact.GeometryToleranceMetres3,geometryVolume=fact.GeometryVolumeMetres3;
        string[] methods=fact.Inserts.Select(insert=>insert.CutoutMethod).ToArray();
        CivilWallCalculationBatch batch=CivilWallCalculator.Calculate(input);
        Check(CivilWallBatchIntegrity.SnapshotFingerprint(input.NativeSnapshot)==nativeBefore,name+" leaves all native snapshot fields unchanged");
        Check((input.RuleTemplate==null&&ruleBefore==null)||(input.RuleTemplate!=null&&CivilWallRuleTemplates.Fingerprint(input.RuleTemplate)==ruleBefore),name+" does not replace or alter the selected input rule");
        Check(Object.ReferenceEquals(native,fact.NativeVolume)&&native.Value==123&&Object.ReferenceEquals(inserts,fact.Inserts)&&methods.SequenceEqual(fact.Inserts.Select(insert=>insert.CutoutMethod))&&fact.GeometryToleranceMetres3==tolerance&&fact.GeometryVolumeMetres3==geometryVolume,
            name+" does not rewrite native references, insert methods or proof values");
        CivilWallBatchIntegrity.Validate(batch);Check(!String.IsNullOrEmpty(batch.RuleFingerprint)&&!String.IsNullOrEmpty(batch.BatchId),name+" returns a fully sealed batch");
        cases.Add(name);return batch;
    }
    private static void Accepted(CivilWallCalculationBatch batch,double volume,double area,string message)
    {
        CivilWallResult row=batch.Results.Single();Check(row.Status=="calculated",message);
        Near(row.BasisVolumeMetres3,6,message+" independent rectangular basis");Near(row.ResultVolumeMetres3,volume,message+" independent net volume");Near(row.ResultAreaMetres2,area,message+" independent single-sided projection");
        Near(row.Ledger.Sum(entry=>entry.DeltaVolumeMetres3),volume,message+" signed volume ledger");Near(row.Ledger.Sum(entry=>entry.DeltaAreaMetres2),area,message+" signed area ledger");
        Check(!row.Ledger.Any(entry=>entry.Formula.Contains("HOST_VOLUME_COMPUTED")||entry.AreaFormula.Contains("HOST_AREA_COMPUTED")),message+" native net metrics never become gross inputs");
    }
    private static void Refused(CivilWallCalculationBatch batch,string message)
    {
        CivilWallResult row=batch.Results.Single();
        Check(row.Status=="unsupported"&&!row.BasisVolumeMetres3.HasValue&&!row.DeductionVolumeMetres3.HasValue&&!row.ResultVolumeMetres3.HasValue&&!row.BasisAreaMetres2.HasValue&&!row.ResultAreaMetres2.HasValue&&row.Ledger.Count==0,message);
    }
    private static void InvalidBeforeRows(Action<CivilWallRuleTemplate> change,string name,string expected)
    {
        CivilWallRuleTemplate rule=CivilWallRuleTemplates.Default();change(rule);
        var input=Input(rule);input.Walls[0]=null;CivilWallCalculationBatch candidate=null;
        string nativeBefore=CivilWallBatchIntegrity.SnapshotFingerprint(input.NativeSnapshot);
        try{candidate=CivilWallCalculator.Calculate(input);throw new Exception("Invalid rule accepted before rows: "+name);}
        catch(InvalidDataException error){Check(error.Message.Contains(expected),name+" rule is rejected before deliberately invalid wall row");}
        Check(candidate==null&&input.Walls[0]==null&&CivilWallBatchIntegrity.SnapshotFingerprint(input.NativeSnapshot)==nativeBefore,name+" returns no partial batch and does not alter source");cases.Add(name);
    }
    private static int Main(string[] args)
    {
        Check(new CivilWallInsert().CutoutMethod=="native-opening","Legacy construction explicitly defaults to native-opening");
        var input=Input(null);var batch=Calculate(input,"null-rule-uses-default-without-writing-input");Accepted(batch,6,30,"No-hole wall");Check(input.RuleTemplate==null&&batch.RuleTemplate.OpeningPolicy==CivilWallRuleTemplates.VerifiedRectCutout,"Default rule captured without silently assigning source rule");
        input=Input(CivilWallRuleTemplates.LegacyExplicitOpeningOnly());batch=Calculate(input,"legacy-no-hole");Accepted(batch,6,30,"Legacy no-hole wall");
        input=Input(CivilWallRuleTemplates.Default());input.Walls[0].Inserts.Add(Hole("A","native-opening",1,3,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.6;
        batch=Calculate(input,"default-native-opening");Accepted(batch,5.6,28,"Two-square-metre native opening");
        input=Input(CivilWallRuleTemplates.LegacyExplicitOpeningOnly());input.Walls[0].Inserts.Add(Hole("A","native-opening",1,3,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.6;
        batch=Calculate(input,"legacy-native-opening");Accepted(batch,5.6,28,"Legacy native opening remains supported");
        input=Input(CivilWallRuleTemplates.Default());input.Walls[0].Inserts.Add(Hole("A","family-rect-cutout",1,3,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.6;
        batch=Calculate(input,"verified-policy-family-cutout");Accepted(batch,5.6,28,"Verified rectangular family cutout");Check(batch.RuleTemplate.OpeningPolicy==CivilWallRuleTemplates.VerifiedRectCutout,"Allowed family calculation records selected policy");
        input=Input(CivilWallRuleTemplates.LegacyExplicitOpeningOnly());input.Walls[0].Inserts.Add(Hole("A","family-rect-cutout",1,3,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.6;
        batch=Calculate(input,"native-only-policy-refuses-family-cutout");Refused(batch,"Legacy policy refuses family cutout despite valid rectangle, proof and numeric geometry");Check(batch.RuleTemplate.OpeningPolicy==CivilWallRuleTemplates.NativeOpeningOnly,"Refusal records the actual selected policy");
        input=Input(CivilWallRuleTemplates.Default());input.Walls[0].Inserts.Add(Hole("A","native-opening",1,3,0.5,1.5));input.Walls[0].Inserts.Add(Hole("B","family-rect-cutout",2,4,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.4;
        batch=Calculate(input,"mixed-native-family-overlap-union");Accepted(batch,5.4,27,"Two 2m2 holes overlapping by 1m2");Near(batch.Results[0].OpeningUnionAreaMetres2,3,"Independent overlap inclusion-exclusion area");Near(batch.Results[0].Ledger.Last().DeltaVolumeMetres3,-0.2,"Second hole deducts one newly covered square metre only");
        input=Input(CivilWallRuleTemplates.LegacyExplicitOpeningOnly());input.Walls[0].Inserts.Add(Hole("A","native-opening",1,3,0.5,1.5));input.Walls[0].Inserts.Add(Hole("B","family-rect-cutout",2,4,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.4;
        Refused(Calculate(input,"mixed-holes-native-only-refuses-entire-wall"),"Legacy policy cannot silently ignore family hole and emit a partial native-only quantity");
        foreach(string method in new[]{"unknown-void","",null}){
            input=Input(CivilWallRuleTemplates.Default());var hole=Hole("A",method,1,3,0.5,1.5);hole.Source="Opening.BoundaryRect pretended source text";input.Walls[0].Inserts.Add(hole);input.Walls[0].GeometryVolumeMetres3=5.6;
            Refused(Calculate(input,"unknown-cutout-method-"+(method??"null")),"Unknown method cannot become native-opening through source text or default policy");
        }
        input=Input(CivilWallRuleTemplates.LegacyExplicitOpeningOnly());input.Walls[0].Inserts.Add(new CivilWallInsert{CutoutMethod="family-rect-cutout",Status="excluded"});Accepted(Calculate(input,"excluded-family-is-not-an-active-deduction"),6,30,"Explicit excluded family fact");
        input=Input(CivilWallRuleTemplates.Default());input.Walls[0].GeometryToleranceMetres3=6e-8;Accepted(Calculate(input,"default-template-upper-proof-tolerance"),6,30,"Default relative tolerance at 6m3 basis");
        CivilWallRuleTemplate strict=CivilWallRuleTemplates.Copy(CivilWallRuleTemplates.Default(),"strict numeric proof");strict.AbsoluteToleranceMetres3=1e-11;strict.RelativeTolerance=1e-11;
        input=Input(strict);input.Walls[0].GeometryToleranceMetres3=6e-8;Refused(Calculate(input,"strict-template-refuses-looser-adapter-proof"),"Selecting stricter template changes calculation acceptance, not only its fingerprint");
        input=Input(strict);input.Walls[0].GeometryToleranceMetres3=6e-11;Accepted(Calculate(input,"strict-template-matching-proof"),6,30,"Matching strict proof");
        input=Input(strict);input.Walls[0].GeometryToleranceMetres3=6e-11*(1+0.5e-12);Accepted(Calculate(input,"strict-template-floating-point-comparison-buffer"),6,30,"Only a relative floating-point comparison buffer is accepted");
        input=Input(strict);input.Walls[0].GeometryToleranceMetres3=6e-11;input.Walls[0].SymmetricDifferenceMetres3=1e-10;Refused(Calculate(input,"strict-template-refuses-shape-residual"),"Strict shape residual is rejected even when arithmetic volume matches");
        input=Input(strict);input.Walls[0].GeometryToleranceMetres3=6e-11*(1+1e-7);Refused(Calculate(input,"relative-buffer-cannot-enlarge-tolerance"),"1e-7 relative excess is larger than numeric-comparison buffer");
        input=Input(CivilWallRuleTemplates.Default());input.Walls[0].GeometryToleranceMetres3=1e-7;Refused(Calculate(input,"default-rule-refuses-old-generic-cap-only-proof"),"Old generic 1e-6 cap cannot override selected default max(abs,basis*rel)");
        InvalidBeforeRows(rule=>rule.MethodId="regional-unimplemented","invalid-volume-method-before-rows","方法");
        InvalidBeforeRows(rule=>rule.OpeningPolicy="accept-width-times-height","invalid-policy-before-rows","切洞策略");
        InvalidBeforeRows(rule=>rule.AbsoluteToleranceMetres3=1,"invalid-tolerance-before-rows","容差");
        InvalidBeforeRows(rule=>rule.JoinPolicy="implicit-priority","invalid-join-rule-before-rows","交接");
        input=Input(CivilWallRuleTemplates.Default());input.Walls[0].Inserts.Add(Hole("A","family-rect-cutout",1,3,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.6;batch=Calculate(input,"cutout-method-is-in-sealed-facts");
        input.Walls[0].Inserts[0].CutoutMethod="native-opening";try{CivilWallBatchIntegrity.Validate(batch);throw new Exception("Post-seal method change accepted");}catch(InvalidDataException){checks++;}
        input.Walls[0].Inserts[0].CutoutMethod="family-rect-cutout";CivilWallBatchIntegrity.Validate(batch);Check(true,"Restoring the original method restores original batch identity without resealing");
        CivilWallFact plain=Input(null).Walls[0];plain.Inserts.Add(Hole("A","family-rect-cutout",1,3,0.5,1.5));plain.GeometryVolumeMetres3=5.6;var direct=CivilWallCalculator.CalculateWall(plain);Check(direct.Status=="calculated","Original CalculateWall API remains usable with explicit default rule behavior");Near(direct.ResultVolumeMetres3,5.6,"Original entry applies independent rectangle quantity");
        string directory=Path.Combine(args[1],"rule-templates");var imported=new List<string>();
        foreach(CivilWallRuleTemplate catalog in CivilWallRuleTemplates.Catalog()){
            string name=catalog.OpeningPolicy==CivilWallRuleTemplates.VerifiedRectCutout?"project-geometry.bvwallrules.json":"native-opening-only.bvwallrules.json";
            CivilWallRuleTemplate loaded=CivilWallRuleTemplates.Load(Path.Combine(directory,name));
            string actualFingerprint=CivilWallRuleTemplates.Fingerprint(loaded),expectedFingerprint=CivilWallRuleTemplates.Fingerprint(catalog);
            Check(actualFingerprint==expectedFingerprint&&loaded.TemplateId==catalog.TemplateId&&loaded.Revision==catalog.Revision&&loaded.Name==catalog.Name,"Shipped BV JSON imports into the complete matching catalog rule: "+name);
            input=Input(loaded);input.Walls[0].Inserts.Add(Hole("A","family-rect-cutout",1,3,0.5,1.5));input.Walls[0].GeometryVolumeMetres3=5.6;
            batch=Calculate(input,"shipped-template-actual-import-"+loaded.OpeningPolicy);
            if(loaded.OpeningPolicy==CivilWallRuleTemplates.VerifiedRectCutout)Accepted(batch,5.6,28,"Imported project geometry rule executes family-cutout policy");
            else Refused(batch,"Imported native-only rule refuses family cutout numerically valid for project geometry");
            Check(batch.RuleFingerprint==actualFingerprint,"Executed imported template fingerprint is preserved in sealed batch");imported.Add(name+"\t"+actualFingerprint);
        }
        File.WriteAllLines(Path.Combine(args[0],"imported-template-fingerprints.tsv"),imported);
        File.WriteAllLines(Path.Combine(args[0],"cases.txt"),cases);Console.WriteLine("PASS "+checks+" selected-rule calculation assertions; independent dimensional expectations, controlled SI facts, no Autodesk API or geometry engine.");return checks;
    }
}
