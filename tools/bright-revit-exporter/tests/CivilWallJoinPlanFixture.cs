// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BrightVision.Civil;

internal static class CivilWallJoinPlanFixture
{
    private static int checks;
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
    private static void Reject(Action action, string reason) { try { action(); } catch (InvalidDataException) { checks++; return; } catch (ArgumentNullException) { checks++; return; } throw new Exception(reason); }
    private static CivilWallJoinBinding Binding(string output)
    { return new CivilWallJoinBinding { DocumentScope = "root-document", SourcePath = Path.Combine(output,"model.rvt"), PhaseId = "phase", GeometryViewUniqueId = "view", SourceRevision = Int64.MaxValue - 3, GeometryDependencyFingerprint = new String('a',64) }; }
    private static byte[] Utf8(string text) { return new UTF8Encoding(false,true).GetBytes(text); }
    private static CivilWallJoinPlan Mutated(CivilWallJoinPlan original, Action<CivilWallJoinPlan> change) { var result = CivilWallJoinPlans.Clone(original); change(result); return result; }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output); var binding = Binding(output); string[] ids = {"B","A","C"};
        var plan = CivilWallJoinPlans.Create(binding, ids,"墙体组件优先顺序"); string fingerprint = CivilWallJoinPlans.Fingerprint(plan);
        Check(plan.Priorities.Select(item=>item.ElementUniqueId).SequenceEqual(ids),"Explicit caller order is preserved, not sorted by UID");
        Check(plan.Priorities.Select(item=>item.Rank).SequenceEqual(new[]{1,2,3}),"Ranks are contiguous and rank1 receives priority");
        Check(plan.Binding.SourceRevision==Int64.MaxValue-3,"Full Int64 revision is retained");
        binding.SourceRevision--;Check(plan.Binding.SourceRevision!=binding.SourceRevision,"Create detaches binding");binding.SourceRevision++;
        ids[0]="caller-mutated";Check(plan.Priorities[0].ElementUniqueId=="B","Create detaches caller order");ids[0]="B";
        var cloned=CivilWallJoinPlans.Clone(plan);cloned.Binding.PhaseId="other";cloned.Priorities[0].Rank=2;
        Check(plan.Binding.PhaseId=="phase"&&plan.Priorities[0].Rank==1,"Deep clone isolates binding and entries");
        CivilWallJoinPlans.ValidateFor(plan,binding,new[]{"A","C","B"});Check(true,"Complete component membership need not arrive in priority order");
        Reject(delegate{CivilWallJoinPlans.ValidateFor(plan,binding,new[]{"B","A"});},"Missing unrequested dependency is refused");
        Reject(delegate{CivilWallJoinPlans.ValidateFor(plan,binding,new[]{"B","A","C","D"});},"Extra component member is refused");
        Reject(delegate{CivilWallJoinPlans.ValidateFor(plan,binding,new[]{"B","A","A"});},"Duplicate component member is refused");
        Reject(delegate{CivilWallJoinPlans.ValidateFor(plan,binding,null);},"Missing component is refused");
        foreach(Action<CivilWallJoinBinding> change in new Action<CivilWallJoinBinding>[] {
            value=>value.DocumentScope="foreign-document",value=>value.SourcePath=Path.Combine(output,"other.rvt"),value=>value.PhaseId="phase2",value=>value.GeometryViewUniqueId="view2",value=>value.SourceRevision++,value=>value.GeometryDependencyFingerprint=new String('b',64)})
        {var changed=CivilWallJoinPlans.CloneBinding(binding);change(changed);Reject(delegate{CivilWallJoinPlans.ValidateFor(plan,changed,ids);},"Changed binding is refused");}
        var normalized=CivilWallJoinPlans.CloneBinding(binding);normalized.SourcePath=Path.Combine(output,"sub","..","model.rvt").ToUpperInvariant();
        CivilWallJoinPlans.ValidateFor(plan,normalized,ids);Check(true,"Equivalent normalized Windows source path is accepted");
        var pathCase=CivilWallJoinPlans.Clone(plan);pathCase.Binding.SourcePath=normalized.SourcePath;
        Check(CivilWallJoinPlans.Fingerprint(pathCase)==fingerprint,"Fingerprint normalizes source path case and segments");
        var reorder=CivilWallJoinPlans.Clone(plan);reorder.Priorities.Reverse();Check(CivilWallJoinPlans.Fingerprint(reorder)==fingerprint,"Serialized array order cannot alter explicit rank meaning");
        var swap=CivilWallJoinPlans.Clone(plan);swap.Priorities[0].Rank=2;swap.Priorities[1].Rank=1;
        Check(CivilWallJoinPlans.Fingerprint(swap)!=fingerprint,"Changing business priority changes fingerprint");
        var previousCulture=CultureInfo.CurrentCulture;try{foreach(string culture in new[]{"en-US","zh-CN","fr-FR","ar-SA"}){System.Threading.Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo(culture);Check(CivilWallJoinPlans.Fingerprint(plan)==fingerprint,"Plan fingerprint is invariant under "+culture);}}finally{System.Threading.Thread.CurrentThread.CurrentCulture=previousCulture;}
        foreach(Action<CivilWallJoinPlan> change in new Action<CivilWallJoinPlan>[] {
            value=>value.SchemaVersion=2,value=>value.PlanId=Guid.Empty.ToString(),value=>value.PlanId="not-guid",value=>value.Name=" ",value=>value.Name="bad\0name",value=>value.Name="\ud800",value=>value.Revision=0,value=>value.MethodId="unimplemented",value=>value.Binding=null,value=>value.Priorities=null,value=>value.Priorities.Clear(),value=>value.Priorities[0]=null,value=>value.Priorities[0].ElementUniqueId="",value=>value.Priorities[0].ElementUniqueId="A",value=>value.Priorities[0].Rank=0,value=>value.Priorities[0].Rank=4,value=>value.Priorities[0].Rank=2})
        { var changed=Mutated(plan,change);Reject(delegate{CivilWallJoinPlans.Validate(changed);},"Invalid plan fact refused"); }
        foreach(Action<CivilWallJoinBinding> change in new Action<CivilWallJoinBinding>[] {
            value=>value.DocumentScope="",value=>value.SourcePath="relative.rvt",value=>value.SourcePath="C:\\file.rvt:secret",value=>value.SourcePath="\\\\?\\C:\\model.rvt",value=>value.SourcePath="bad\0path",value=>value.PhaseId="",value=>value.GeometryViewUniqueId="",value=>value.SourceRevision=-1,value=>value.GeometryDependencyFingerprint=new String('A',64),value=>value.GeometryDependencyFingerprint="abc"})
        {var changed=CivilWallJoinPlans.CloneBinding(binding);change(changed);Reject(delegate{CivilWallJoinPlans.ValidateBinding(changed);},"Invalid source binding refused");}
        var unsaved=CivilWallJoinPlans.CloneBinding(binding);unsaved.SourcePath="";Check(CivilWallJoinPlans.Create(unsaved,ids,"Unsaved project").Binding.SourcePath=="","Unsaved models bind document scope without invented disk path");
        var sameSession=CivilWallJoinPlans.AdoptForUnchangedGeometry(plan,binding,ids);Check(CivilWallJoinPlans.Fingerprint(sameSession)==fingerprint&&!Object.ReferenceEquals(plan,sameSession),"Unchanged source counter returns a detached same-revision plan");
        var reopened=CivilWallJoinPlans.CloneBinding(binding);reopened.SourceRevision=0;var adopted=CivilWallJoinPlans.AdoptForUnchangedGeometry(plan,reopened,ids);
        Check(adopted.PlanId==plan.PlanId&&adopted.Revision==plan.Revision+1&&adopted.Binding.SourceRevision==0,"Cross-session counter reset rebinds the same explicit order with a new plan revision");
        Check(adopted.Priorities.Select(entry=>entry.ElementUniqueId).SequenceEqual(plan.Priorities.Select(entry=>entry.ElementUniqueId)),"Rebinding reuses only UID order, never inserts a business default");
        Check(CivilWallJoinPlans.Fingerprint(plan)==fingerprint&&binding.SourceRevision==Int64.MaxValue-3,"Rebinding does not mutate prior plan or either caller binding");
        CivilWallJoinPlans.ValidateFor(adopted,reopened,ids);Check(true,"Adopted plan strictly validates against current process revision");
        Reject(delegate{CivilWallJoinPlans.ValidateFor(plan,reopened,ids);},"Original plan remains stale under strict ValidateFor");
        var counterChanged=CivilWallJoinPlans.CloneBinding(binding);counterChanged.SourceRevision=Int64.MaxValue;var maximumCounter=CivilWallJoinPlans.AdoptForUnchangedGeometry(adopted,counterChanged,ids);
        Check(maximumCounter.Binding.SourceRevision==Int64.MaxValue&&maximumCounter.Revision==adopted.Revision+1,"Adoption preserves full Int64 process counters without truncation");
        foreach(Action<CivilWallJoinBinding> change in new Action<CivilWallJoinBinding>[] {value=>value.DocumentScope="foreign",value=>value.SourcePath=Path.Combine(output,"foreign.rvt"),value=>value.PhaseId="foreign",value=>value.GeometryViewUniqueId="foreign",value=>value.GeometryDependencyFingerprint=new String('b',64)})
        {var changed=CivilWallJoinPlans.CloneBinding(reopened);change(changed);Reject(delegate{CivilWallJoinPlans.AdoptForUnchangedGeometry(plan,changed,ids);},"Persistent source or geometry change cannot adopt old order");}
        Reject(delegate{CivilWallJoinPlans.AdoptForUnchangedGeometry(plan,reopened,new[]{"B","A"});},"Adoption refuses a missing outside dependency");
        Reject(delegate{CivilWallJoinPlans.AdoptForUnchangedGeometry(plan,reopened,new[]{"B","A","C","D"});},"Adoption refuses new geometry participants");
        Reject(delegate{CivilWallJoinPlans.AdoptForUnchangedGeometry(plan,reopened,new[]{"B","A","A"});},"Adoption refuses duplicate membership");
        var ceilingPlan=CivilWallJoinPlans.Clone(plan);ceilingPlan.Revision=Int32.MaxValue;Check(CivilWallJoinPlans.AdoptForUnchangedGeometry(ceilingPlan,binding,ids).Revision==Int32.MaxValue,"Unchanged maximum plan revision can still be loaded");
        Reject(delegate{CivilWallJoinPlans.AdoptForUnchangedGeometry(ceilingPlan,reopened,ids);},"Cross-session revision increment never overflows");
        var maximum=CivilWallJoinPlans.Create(binding,Enumerable.Range(0,64).Select(i=>"uid-"+i),"64 participants");Check(maximum.Priorities.Count==64,"Complete64-participant component accepted");
        Reject(delegate{CivilWallJoinPlans.Create(binding,Enumerable.Range(0,65).Select(i=>"uid-"+i),"Over limit");},"65th participant is not truncated");
        Reject(delegate{CivilWallJoinPlans.Create(binding,new[]{"A","A"},"Duplicate");},"Creation refuses duplicate UID");
        string file=Path.Combine(output,"component.bvwalljoins.json");CivilWallJoinPlans.Save(file,plan);
        Check(File.Exists(file),"Plan file written");var loaded=CivilWallJoinPlans.Load(file);Check(CivilWallJoinPlans.Fingerprint(loaded)==fingerprint,"Strict JSON roundtrip retains every field");
        string json=File.ReadAllText(file,Encoding.UTF8);Check(json.Contains((Int64.MaxValue-3).ToString(CultureInfo.InvariantCulture)),"JSON keeps full Int64 revision digits");
        string[] invalidJson={"[]","null","1","{}",json.Replace("\"SchemaVersion\":1","\"SchemaVersion\":1,\"FutureOption\":true"),json.Replace("\"SchemaVersion\":1","\"SchemaVersion\":1,\"SchemaVersion\":1"),json.Replace("\"Binding\":{","\"Binding\":{\"Future\":1,"),json.Replace("\"DocumentScope\":\"root-document\"","\"DocumentScope\":\"root-document\",\"DocumentScope\":\"root-document\""),json.Replace("\"Rank\":1","\"Rank\":1,\"Rank\":1"),json.Replace("\"Rank\":1","\"Rank\":1,\"Future\":1"),json.Replace("\"Rank\":1","\"Rank\":\"1\""),json.Replace("\"Rank\":1","\"Rank\":1.5"),json.Replace("\"Rank\":1","\"Rank\":1e0"),json.Replace("\"Rank\":1","\"Rank\":2147483648"),json.Replace("\"Name\":\"墙体组件优先顺序\"","\"Name\":4"),json.Replace("\"Binding\":{","\"Binding\":[{"),json.Replace("\"Priorities\":[","\"Priorities\":null,\"Discard\":["),json.Replace((Int64.MaxValue-3).ToString(CultureInfo.InvariantCulture),"9223372036854775808"),json.Replace("\"Revision\":1,","")};
        for(int index=0;index<invalidJson.Length;index++){string candidate=invalidJson[index];Check(candidate!=json,"JSON negative case actually modifies valid input "+index);Reject(delegate{CivilWallJoinPlans.Parse(Utf8(candidate));},"Invalid JSON contract rejected "+index);}
        Reject(delegate{CivilWallJoinPlans.Parse(new byte[]{0xc3,0x28});},"Invalid UTF8 refused");Reject(delegate{CivilWallJoinPlans.Parse(new byte[0]);},"Empty file refused");Reject(delegate{CivilWallJoinPlans.Parse(new byte[CivilWallJoinPlans.MaximumBytes+1]);},"Oversized file refused before parsing");
        Reject(delegate{CivilWallJoinPlans.Load("relative.bvwalljoins.json");},"Relative store path refused");Reject(delegate{CivilWallJoinPlans.Save(Path.Combine(output,"wrong.json"),plan);},"Wrong extension refused");
        byte[] original=File.ReadAllBytes(file);
        Reject(delegate{CivilWallJoinPlans.Save(file,Mutated(plan,value=>value.Name="changed without revision"));},"Changed same revision cannot replace saved file");
        Reject(delegate{CivilWallJoinPlans.Save(file,Mutated(plan,value=>value.PlanId=Guid.NewGuid().ToString("D")));},"Foreign plan cannot replace file");
        Reject(delegate{CivilWallJoinPlans.Save(file,Mutated(plan,value=>{value.Revision++;value.Binding.PhaseId="other";}));},"Foreign binding cannot overwrite same plan identity");
        Check(File.ReadAllBytes(file).SequenceEqual(original),"Refused saves preserve original bytes");
        var updated=Mutated(plan,value=>{value.Name="Confirmed new order";value.Revision=2;int first=value.Priorities[0].Rank;value.Priorities[0].Rank=value.Priorities[1].Rank;value.Priorities[1].Rank=first;});
        CivilWallJoinPlans.Save(file,updated);Check(CivilWallJoinPlans.Load(file).Revision==2,"Atomic replacement advances revision");
        byte[] latest=File.ReadAllBytes(file);Reject(delegate{CivilWallJoinPlans.Save(file,plan);},"Older plan revision refused");Check(File.ReadAllBytes(file).SequenceEqual(latest),"Older revision refusal preserves latest bytes");
        string corrupt=Path.Combine(output,"corrupt.bvwalljoins.json");File.WriteAllText(corrupt,"{broken");Reject(delegate{CivilWallJoinPlans.Save(corrupt,plan);},"Damaged existing file is never silently replaced");Check(File.ReadAllText(corrupt)=="{broken","Damaged user file preserved");
        string adoptionFile=Path.Combine(output,"reopened.bvwalljoins.json");CivilWallJoinPlans.Save(adoptionFile,plan);CivilWallJoinPlans.Save(adoptionFile,adopted);var reloaded=CivilWallJoinPlans.Load(adoptionFile);Check(reloaded.Binding.SourceRevision==0&&reloaded.Revision==2&&CivilWallJoinPlans.Fingerprint(reloaded)==CivilWallJoinPlans.Fingerprint(adopted),"Saving adopted order retains the new binding and plan revision atomically");
        Reject(delegate{CivilWallJoinPlans.Save(adoptionFile,plan);},"Original previous-process revision cannot overwrite newly adopted plan");
        Check(Directory.GetFiles(output,".bv-wall-joins-*.tmp").Length==0,"Successful or refused writes leave no temp files");
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks+",\"scope\":\"Own managed source-bound join-plan storage DLL only; no Revit, native geometry, shown UI or desktop input\",\"hostExecuted\":false}",new UTF8Encoding(false));
        Console.WriteLine("PASS "+checks+" join plan assertions.");
    }
    private static int Main(string[] args) {Run(args[0]);return 0;}
}
