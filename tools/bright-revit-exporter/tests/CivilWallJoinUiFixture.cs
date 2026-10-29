// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrightVision.Civil;
using BrightVision.Revit;

internal static class CivilWallJoinUiFixture
{
    private static int checks;
    private static readonly string Component0=new String('a',64),Component1=new String('b',64);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    private static bool Refuses(Action action){try{action();return false;}catch(InvalidOperationException){return true;}catch(InvalidDataException){return true;}}
    private static T Field<T>(object owner,string name){return(T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
    private static object Property(object owner,string name){return owner.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(owner,null);}
    private static void Near(double value,double expected,string message){Check(!Double.IsNaN(value)&&!Double.IsInfinity(value)&&Math.Abs(value-expected)<1e-9,message);}
    private static void Click(Button value){value.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,value));}
    private static void SelectChoice(CivilWallJoinWindow owner,string field,string id){var box=Field<ComboBox>(owner,field);box.SelectedItem=box.Items.Cast<CivilChoice>().Single(value=>value.Id==id);}
    private static List<CivilChoice> Choices(params string[] ids){return ids.Select(id=>new CivilChoice{Id=id,Label=id}).ToList();}
    private static CivilMetric Metric(double value,string unit){return new CivilMetric{Value=value,Unit=unit,Status="available",Source="Independent exact-cell UI fixture"};}

    private sealed class Fixture
    {
        internal readonly string Output;
        internal long Revision=10;
        internal int Reads,ReadBoundaries;
        internal bool FailRead,WrongRule,WrongView,WrongPhase,DuplicateComponent,AllUnsupported,Overflow,SingleWalls,Empty;
        internal Action<CivilWallJoinWindow,CivilWallReadControl,int> OnRead;
        internal RevitWallJoinReadBundle LastBundle;
        internal CivilWallJoinWindow Window;
        internal Fixture(string output){Output=output;}
        internal CivilWallJoinWindow MakeWindow()
        {
            Window=new CivilWallJoinWindow(new CivilReadRequest{Scope="selection",PhaseId="P",DesignOptionId="-1"},Choices("P","P2"),Choices("-1"),Choices("V","V2"),"V",Read,()=>Revision);return Window;
        }
        private RevitWallJoinReadBundle Read(CivilReadRequest request,string view,CivilWallRuleTemplate rule,CivilWallReadControl control)
        {
            Reads++;ReadBoundaries=0;var data=Build(request,view,rule);LastBundle=data;
            try
            {
                for(int index=0;index<data.Components.Count;index++)
                {
                    if(OnRead!=null)OnRead(Window,control,index);
                    control.Checkpoint("UI fixture component read",index,data.Components.Count);ReadBoundaries++;
                }
                if(FailRead)throw new InvalidOperationException("Injected reader failure");
                return data;
            }
            catch{data.Dispose();throw;}
        }
        private RevitWallJoinReadBundle Build(CivilReadRequest request,string view,CivilWallRuleTemplate rule)
        {
            var ops=new JoinUiOps();
            var native=new CivilSnapshot{DocumentScope="UI-doc",SourcePath=Path.Combine(Output,"source.rvt"),ModelTitle="Own synthetic join UI fixture",Revision=Revision,ScopeLabel=request.Scope,PhaseLabel=request.PhaseId,DesignOptionLabel="main model",CapturedAtUtc="2026-10-29T00:00:00Z"};
            var data=new RevitWallJoinReadBundle{NativeSnapshot=native,GeometryViewUniqueId=WrongView?"wrong-view":view,GeometryViewName="fixture view",GeometryPhaseId=WrongPhase?"wrong-phase":request.PhaseId,RuleTemplate=WrongRule?CivilWallRuleTemplates.LegacyExplicitOpeningOnly():CivilWallRuleTemplates.Clone(rule),Operations=ops};
            if(Empty)return data;
            for(int component=0;component<2;component++)
            {
                string componentId=component==0?Component0:Component1;
                var binding=new CivilWallJoinBinding{DocumentScope=native.DocumentScope,SourcePath=native.SourcePath,SourceRevision=Revision,PhaseId=request.PhaseId,GeometryViewUniqueId=view,GeometryDependencyFingerprint=new String(component==0?'a':'b',64)};
                var prepared=new RevitWallJoinPreparedComponent{ComponentId=componentId,Binding=binding,Participants=new List<CivilWallJoinParticipant>(),Evidence=new List<CivilWallJoinWallEvidence>(),Status=AllUnsupported?"unsupported":"ready",Message=AllUnsupported?"Unsupported synthetic component":"Exact set operands, synthetic UI only",Input=new CivilWallJoinInput{ContextKey=componentId,DependenciesComplete=true,AbsoluteToleranceMetres3=rule.AbsoluteToleranceMetres3,RelativeTolerance=rule.RelativeTolerance}};
                int nodeCount=Overflow||SingleWalls?1:2;
                for(int node=0;node<nodeCount;node++)
                {
                    string uid=(component==0?"A":"C")+(node==0?"":"-dep"),id=(component*10+node+1).ToString(CultureInfo.InvariantCulture);
                    bool requested=node==0;double weight=Overflow?1e308:1;int first=component*100;
                    int[] expected=Overflow?new[]{first+1}:node==0?new[]{first+1,first+2,first+3}:new[]{first+3,first+4,first+5};
                    int[] actual=Overflow?expected:node==0?expected:new[]{first+4,first+5};
                    double gross=expected.Length*weight,actualVolume=actual.Length*weight;
                    var nativeVolume=Metric(actualVolume,"m3");
                    var fact=new CivilWallFact{ElementId=id,UniqueId=uid,Name=requested?"请求墙"+uid:"范围外邻墙"+uid,Status="component-prepared",GeometryVerified=false,ParameterLength=Metric(gross,"m"),ParameterHeight=Metric(1,"m"),ParameterWidth=Metric(1,"m"),NativeVolume=nativeVolume};
                    var evidence=new CivilWallJoinWallEvidence{Fact=fact,OriginFeet=new[]{0.0,0.0,0.0},Axis=new[]{1.0,0.0,0.0},Normal=new[]{0.0,1.0,0.0},GrossBoundsFeet=new[]{0.0,3.0,0.0,1.0,0.0,1.0},ActualBoundsFeet=new[]{0.0,3.0,0.0,1.0,0.0,1.0},NormalMinFeet=0,ExpectedVolumeMetres3=gross,ActualVolumeMetres3=actualVolume};
                    prepared.Participants.Add(new CivilWallJoinParticipant{ElementUniqueId=uid,ElementId=id,Name=fact.Name,Requested=requested,GrossVolumeMetres3=gross,OpeningVolumeMetres3=0,NativeVolumeMetres3=actualVolume});prepared.Evidence.Add(evidence);
                    prepared.Input.Nodes.Add(new CivilWallJoinNode{UniqueId=uid,ElementId=id,Requested=requested,Supported=true,GrossVolumeMetres3=gross,ExpectedSolid=ops.Source(expected,weight,componentId),ActualSolid=ops.Source(actual,weight,componentId)});
                    if(requested)native.Elements.Add(new CivilElementRecord{UniqueId=uid,ElementId=id,Name=fact.Name,KindCode="wall",KindName="墙",Supported=true,Volume=nativeVolume});
                }
                if(AllUnsupported){prepared.Input.DependenciesComplete=false;foreach(var node in prepared.Input.Nodes)node.Supported=false;}
                data.Components.Add(prepared);
            }
            if(DuplicateComponent)data.Components[1].ComponentId=data.Components[0].ComponentId;
            return data;
        }
        internal void Plans(bool reverse=false)
        {
            foreach(var component in LastBundle.Components)
            {
                if(component.Status!="ready")continue;
                IEnumerable<string> ids=component.Participants.Select(wall=>wall.ElementUniqueId);if(reverse)ids=ids.Reverse();
                Window.SetPlan(component.ComponentId,CivilWallJoinPlans.Create(component.Binding,ids,"显式确认 "+component.ComponentId));
            }
        }
        internal void Ready(bool reverse=false){Window.ReadComponents();Plans(reverse);Window.Calculate();string status=Field<TextBlock>(Window,"status").Text;Check(Window.CheckFreshness(),"Fixture complete read/order/calculate commits a trusted batch: "+status);}
    }

    private static void Render(CivilWallJoinWindow window,string output,int width,int height,string name)
    {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=new FileStream(Path.Combine(output,name),FileMode.CreateNew,FileAccess.Write))encoder.Save(stream);
        Check(!window.IsVisible,"Rendering never shows a native window");foreach(string field in new[]{"components","walls","steps"})Check(Field<DataGrid>(window,field).ActualHeight>60,field+" table remains useful at "+width+"x"+height);
        foreach(string field in new[]{"readButton","calculateButton","cancelButton","csvButton","focusButton"}){var control=Field<Button>(window,field);var point=control.TranslatePoint(new Point(0,0),root);Check(control.ActualHeight>=18&&control.ActualWidth>20&&point.X>=0&&point.Y>=0&&point.X+control.ActualWidth<=width+.1&&point.Y+control.ActualHeight<=height+.1,field+" stays inside the client area");}
    }
    private static void RequireAllOutputsStale(CivilWallJoinWindow window,string reason)
    {
        Check(!window.CheckFreshness(),reason+": freshness rejects");Check(Refuses(()=>window.BuildCurrentCsv()),reason+": CSV rejects");Check(!window.PrepareFocus()&&window.SelectedFocusElements.Count==0,reason+": focus rejects and clears");Check(!Field<Button>(window,"csvButton").IsEnabled&&!Field<Button>(window,"focusButton").IsEnabled,reason+": output controls disabled");
    }
    private static void Basic(string output)
    {
        var fixture=new Fixture(output);var window=fixture.MakeWindow();Check(window.Title.Contains("崛亮视觉")&&window.Icon!=null,"Company and BV branding visible");Check(Refuses(()=>window.BuildCurrentCsv())&&!window.PrepareFocus(),"Unread window has no CSV/focus");
        window.ReadComponents();var firstBundle=fixture.LastBundle;var nativeFingerprint=CivilWallBatchIntegrity.FactsFingerprint(firstBundle.NativeSnapshot);Check(fixture.Reads==1&&Field<DataGrid>(window,"components").Items.Count==2,"Read displays both complete components");Check(window.LastBatch==null&&!window.CheckFreshness(),"Read alone does not invent a calculation");
        Check(Refuses(()=>window.Calculate())&&window.LastBatch==null,"Multiwall components require explicit complete plans");
        var incomplete=CivilWallJoinPlans.Create(firstBundle.Components[0].Binding,new[]{"A"},"漏掉依赖");Check(Refuses(()=>window.SetPlan(Component0,incomplete)),"Partial priority plan cannot omit unrequested neighbor");
        fixture.Plans();window.Calculate();string completionStatus=Field<TextBlock>(window,"status").Text;Check(window.CheckFreshness()&&window.LastBatch.Calculations.Count==2,"Two explicit plans allow complete candidate commit: "+completionStatus);
        foreach(var component in window.LastBatch.Calculations){Near(component.Result.SumResultVolumeMetres3.Value,5,"Component union is counted exactly once");Near(component.Result.Rows.Single(row=>row.Requested).ResultVolumeMetres3,3,"First-priority requested wall retains shared cell");Near(component.Result.Rows.Single(row=>!row.Requested).ResultVolumeMetres3,2,"Unrequested neighbor receives only its residual");}
        var table=Field<DataGrid>(window,"walls");Check(table.Items.Count==2,"Default rows retain outside dependency");table.SelectedIndex=1;Check(window.PrepareFocus()&&window.SelectedFocusElements.Single().UniqueId=="A-dep","Dependency row focuses its exact source UID");
        table.SelectedIndex=0;Check(window.PrepareFocus()&&window.SelectedFocusElements.Single().UniqueId=="A","Requested row focuses its exact source UID");
        var csv=window.BuildCurrentCsv();Check(csv.Contains(CivilWallJoinSession.ReportId)&&csv.Contains(CivilWallJoinCore.MethodId)&&csv.Contains(window.LastBatch.BatchId),"Production CSV retains method/report/batch identity");
        string dependencyCsv="\""+Component0+"\",\"A-dep\",\"2\",\"范围外邻墙A-dep\",\"范围外依赖\"";
        Check(csv.Contains(dependencyCsv),"Full CSV contains dependency wall quantity row");
        Field<CheckBox>(window,"dependencies").IsChecked=false;var selectedCsv=window.BuildCurrentCsv();Check(table.Items.Count==1&&!selectedCsv.Contains(dependencyCsv),"Requested-only view and CSV filter dependency rows");Near(window.LastBatch.Calculations[0].Result.SumResultVolumeMetres3.Value,5,"Filtering does not alter full-component allocation or proof");
        Field<CheckBox>(window,"dependencies").IsChecked=true;table.SelectedIndex=1;Check(Field<DataGrid>(window,"steps").Items.Count==1,"Lower-priority wall displays its residual deduction step");
        Render(window,output,1340,900,"BV-join-workbench-1340.png");Render(window,output,1080,720,"BV-join-workbench-1080.png");
        var oldBatch=window.LastBatch;fixture.Plans(true);Check(Object.ReferenceEquals(window.LastBatch,oldBatch),"Changing plans preserves completed prior batch as stale");RequireAllOutputsStale(window,"Priority change");window.Calculate();Check(window.CheckFreshness(),"Recalculation restores current batch");
        foreach(var component in window.LastBatch.Calculations){Near(component.Result.Rows.Single(row=>row.Requested).ResultVolumeMetres3,2,"Swapped explicit priority changes requested allocation");Near(component.Result.Rows.Single(row=>!row.Requested).ResultVolumeMetres3,3,"Outside neighbor explicitly promoted to priority1");Near(component.Result.SumResultVolumeMetres3.Value,5,"Swapped priority preserves union total");}
        Check(CivilWallBatchIntegrity.FactsFingerprint(firstBundle.NativeSnapshot)==nativeFingerprint,"Read/calculate/filter/CSV/focus do not mutate native source snapshot");Check(((JoinUiOps)firstBundle.Operations).Inputs.All(solid=>solid.DisposeCalls==0),"Core never disposes borrowed read-session solids");
        var callerPlan=CivilWallJoinPlans.Create(firstBundle.Components[0].Binding,new[]{"A","A-dep"},"caller plan");window.SetPlan(Component0,callerPlan);window.Calculate();callerPlan.Name="caller changed";callerPlan.Priorities[0].Rank=2;Check(window.CheckFreshness(),"SetPlan deep-clones caller data before committing");
        var storedPlan=CivilWallJoinPlans.Clone(window.LastBatch.Calculations[0].Plan);string planPath=Path.Combine(output,"workbench-import.bvwalljoins.json");CivilWallJoinPlans.Save(planPath,storedPlan);var imported=CivilWallJoinPlans.Load(planPath);window.SetPlan(Component0,imported);RequireAllOutputsStale(window,"Loaded explicit plan requires recalculation");window.Calculate();Check(window.CheckFreshness()&&CivilWallJoinPlans.Fingerprint(window.LastBatch.Calculations[0].Plan)==CivilWallJoinPlans.Fingerprint(storedPlan),"Real strict plan save/load joins the production calculation flow");
        var foreign=CivilWallJoinPlans.Clone(imported);foreign.Binding.SourceRevision++;var beforeForeign=window.LastBatch;Check(Refuses(()=>window.SetPlan(Component0,foreign))&&Object.ReferenceEquals(beforeForeign,window.LastBatch)&&window.CheckFreshness(),"Foreign revision plan is rejected before changing the completed state");
        long revision=fixture.Revision;fixture.Revision++;RequireAllOutputsStale(window,"External source revision change");fixture.Revision=revision;CalculateAt(window,"restored source revision");
        SelectChoice(window,"view","V2");RequireAllOutputsStale(window,"Geometry view change");SelectChoice(window,"view","V");CalculateAt(window,"restored geometry view");
        var previous=fixture.LastBundle;window.ReadComponents();Check(previous.IsDisposed&&previous.DisposeCalls==1,"Successful new read releases the previous native bundle exactly once");fixture.Plans();CalculateAt(window,"newly read components");Check(window.CheckFreshness(),"Newly read components can be confirmed and calculated again");
        window.Close();Check(fixture.LastBundle.IsDisposed&&((JoinUiOps)fixture.LastBundle.Operations).Inputs.All(solid=>solid.DisposeCalls==1),"Closing disposes current borrowed operands once");
    }

    private static void CalculateAt(CivilWallJoinWindow window,string stage)
    {try{window.Calculate();}catch(Exception error){throw new InvalidOperationException(stage+"; current status: "+Field<TextBlock>(window,"status").Text+"; "+error.Message);}}

    private static void MutationGuards(string output)
    {
        var fixture=new Fixture(output);var window=fixture.MakeWindow();fixture.Ready();Field<DataGrid>(window,"walls").SelectedIndex=0;
        var batch=window.LastBatch;double quantity=batch.Calculations[0].Result.Rows[0].ResultVolumeMetres3;batch.Calculations[0].Result.Rows[0].ResultVolumeMetres3+=.01;RequireAllOutputsStale(window,"Finite sealed result mutation");batch.Calculations[0].Result.Rows[0].ResultVolumeMetres3=quantity;window.Calculate();
        batch=window.LastBatch;double native=batch.Input.Components[0].Participants[0].NativeVolumeMetres3.Value;batch.Input.Components[0].Participants[0].NativeVolumeMetres3=native+.01;RequireAllOutputsStale(window,"Finite participant reference mutation");batch.Input.Components[0].Participants[0].NativeVolumeMetres3=native;window.Calculate();
        batch=window.LastBatch;double? length=batch.Input.Components[0].Evidence[0].Fact.ParameterLength.Value;batch.Input.Components[0].Evidence[0].Fact.ParameterLength.Value=length.Value+.01;RequireAllOutputsStale(window,"Finite prepared dimensional evidence mutation");batch.Input.Components[0].Evidence[0].Fact.ParameterLength.Value=length;window.Calculate();
        batch=window.LastBatch;string dependency=batch.Input.Components[0].Binding.GeometryDependencyFingerprint;batch.Input.Components[0].Binding.GeometryDependencyFingerprint=new String('f',64);RequireAllOutputsStale(window,"Dependency fingerprint mutation");batch.Input.Components[0].Binding.GeometryDependencyFingerprint=dependency;window.Calculate();
        batch=window.LastBatch;string name=batch.Calculations[0].Plan.Name;batch.Calculations[0].Plan.Name="sealed plan mutated";RequireAllOutputsStale(window,"Sealed calculation plan mutation");batch.Calculations[0].Plan.Name=name;window.Calculate();
        batch=window.LastBatch;string id=batch.BatchId;batch.BatchId=Guid.NewGuid().ToString("N");RequireAllOutputsStale(window,"Batch identity replacement");batch.BatchId=id;window.Calculate();
        var selectedRule=(CivilWallRuleTemplate)Field<ComboBox>(window,"rules").SelectedItem;string ruleName=selectedRule.Name;selectedRule.Name+=" changed";RequireAllOutputsStale(window,"Current selected rule changed without selection event");selectedRule.Name=ruleName;window.Calculate();
        var currentPlans=Field<Dictionary<string,CivilWallJoinPlan>>(window,"plans");name=currentPlans[Component0].Name;currentPlans[Component0].Name="current plan changed";RequireAllOutputsStale(window,"Current explicit plan mutated after calculation");currentPlans[Component0].Name=name;window.Calculate();
        Check(window.CheckFreshness(),"Only recalculation restores outputs after in-memory mutations");window.Close();
    }

    private static void ReadAtomicity(string output)
    {
        var fixture=new Fixture(output);var window=fixture.MakeWindow();fixture.Ready();
        foreach(string mode in new[]{"wrong-rule","wrong-view","wrong-phase","read-failure"})
        {
            var oldBatch=window.LastBatch;var oldBundle=Field<RevitWallJoinReadBundle>(window,"bundle");object oldRows=Field<DataGrid>(window,"components").ItemsSource;
            fixture.WrongRule=mode=="wrong-rule";fixture.WrongView=mode=="wrong-view";fixture.WrongPhase=mode=="wrong-phase";fixture.FailRead=mode=="read-failure";window.ReadComponents();
            Check(Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldBundle,Field<RevitWallJoinReadBundle>(window,"bundle"))&&Object.ReferenceEquals(oldRows,Field<DataGrid>(window,"components").ItemsSource),mode+" preserves the prior complete batch/bundle/rows");
            Check(!oldBundle.IsDisposed&&fixture.LastBundle.IsDisposed,mode+" releases only rejected candidate inputs");RequireAllOutputsStale(window,mode);
            fixture.WrongRule=fixture.WrongView=fixture.WrongPhase=fixture.FailRead=false;window.Calculate();Check(window.CheckFreshness(),"Existing unchanged read can be recalculated after failed reader attempt");
        }
        var batch=window.LastBatch;fixture.OnRead=delegate(CivilWallJoinWindow owner,CivilWallReadControl control,int index){if(index==1)owner.RequestCancel();};window.ReadComponents();
        Check(fixture.ReadBoundaries==1&&Object.ReferenceEquals(batch,window.LastBatch)&&fixture.LastBundle.IsDisposed,"Cancel at second component submits no partial read and releases candidate");RequireAllOutputsStale(window,"Canceled component read");fixture.OnRead=null;window.Calculate();
        fixture.OnRead=delegate(CivilWallJoinWindow owner,CivilWallReadControl control,int index){if(index==0){Check(Field<bool>(owner,"reading"),"Read flag is set at callback");Check(!Field<Button>(owner,"readButton").IsEnabled&&Field<Button>(owner,"cancelButton").IsEnabled&&!Field<ComboBox>(owner,"scope").IsEnabled,"Read disables all other inputs and enables cancellation");int count=fixture.Reads;owner.ReadComponents();Check(count==fixture.Reads,"Reentrant read does not call reader again");Check(Refuses(()=>owner.Calculate())&&Refuses(()=>owner.SetPlan("unused",null)),"Public helpers reject work during reading");}};window.ReadComponents();fixture.OnRead=null;fixture.Plans();window.Calculate();
        batch=window.LastBatch;fixture.OnRead=delegate(CivilWallJoinWindow owner,CivilWallReadControl control,int index){if(index==1)fixture.Revision++;};window.ReadComponents();Check(Object.ReferenceEquals(batch,window.LastBatch)&&fixture.LastBundle.IsDisposed,"Revision change during read refuses before swapping old batch");RequireAllOutputsStale(window,"Mid-read model revision change");fixture.OnRead=null;fixture.Ready();
        batch=window.LastBatch;fixture.OnRead=delegate(CivilWallJoinWindow owner,CivilWallReadControl control,int index){if(index==1)SelectChoice(owner,"phase","P2");};window.ReadComponents();Check(Object.ReferenceEquals(batch,window.LastBatch)&&fixture.LastBundle.IsDisposed,"Unexpected read-condition change is detected at commit");fixture.OnRead=null;SelectChoice(window,"phase","P");fixture.Ready();
        batch=window.LastBatch;fixture.OnRead=delegate(CivilWallJoinWindow owner,CivilWallReadControl control,int index){if(index==1)owner.Close();};window.ReadComponents();Check(Object.ReferenceEquals(batch,window.LastBatch)&&!Field<bool>(window,"reading")&&!Field<bool>(window,"closeWhenDone"),"Close during read cancels and unwinds before closing");fixture.OnRead=null;window.Close();
    }

    private static void CalculateAtomicity(string output)
    {
        var fixture=new Fixture(output);var window=fixture.MakeWindow();fixture.Ready();var oldBatch=window.LastBatch;object oldRows=Field<DataGrid>(window,"components").ItemsSource;var ops=(JoinUiOps)fixture.LastBundle.Operations;bool hit=false;
        ops.BeforeOperation=delegate(string component,string operation){if(component==Component1&&!hit){hit=true;window.RequestCancel();}};window.Calculate();
        Check(hit&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldRows,Field<DataGrid>(window,"components").ItemsSource),"Cancel at second calculated component submits no partial candidate or table");RequireAllOutputsStale(window,"Canceled component calculation");ops.BeforeOperation=null;window.Calculate();
        hit=false;ops.BeforeOperation=delegate(string component,string operation){if(!hit){hit=true;Check(Field<bool>(window,"calculating")&&!Field<Button>(window,"calculateButton").IsEnabled,"Calculation locks controls before geometry callback");Check(Refuses(()=>window.Calculate())&&Refuses(()=>window.SetPlan("unused",null)),"Reentrant calculate or plan edit refused");int reads=fixture.Reads;window.ReadComponents();Check(fixture.Reads==reads,"Read cannot replace operands during calculation");}};window.Calculate();Check(hit&&window.CheckFreshness(),"Guarded callback cannot corrupt completed calculation");ops.BeforeOperation=null;
        oldBatch=window.LastBatch;hit=false;ops.BeforeOperation=delegate(string component,string operation){if(!hit){hit=true;fixture.Revision++;}};window.Calculate();Check(Object.ReferenceEquals(oldBatch,window.LastBatch),"Revision changed during calculation refuses candidate atomically");RequireAllOutputsStale(window,"Mid-calculation source change");ops.BeforeOperation=null;fixture.Ready();ops=(JoinUiOps)fixture.LastBundle.Operations;
        oldBatch=window.LastBatch;ops.Fail=true;window.Calculate();Check(Object.ReferenceEquals(oldBatch,window.LastBatch),"Geometry failure leaves the previous complete batch untouched");RequireAllOutputsStale(window,"Injected native operation failure");ops.Fail=false;window.Calculate();Check(window.CheckFreshness(),"Successful recalculation recovers after geometry failure");
        window.Close();
        var overflowFixture=new Fixture(output){Overflow=true};var overflow=overflowFixture.MakeWindow();overflow.ReadComponents();var beforeRows=Field<DataGrid>(overflow,"components").ItemsSource;overflow.Calculate();
        Check(overflow.LastBatch==null&&Object.ReferenceEquals(beforeRows,Field<DataGrid>(overflow,"components").ItemsSource),"Candidate summary overflow is rejected before committing batch or tables");RequireAllOutputsStale(overflow,"Candidate total overflow");Check(Field<TextBlock>(overflow,"status").Text.Contains("溢出")||Field<TextBlock>(overflow,"status").Text.Contains("旧结果"),"Overflow gives an explicit failure status");overflow.Close();
        var unsupportedFixture=new Fixture(output){AllUnsupported=true};var unsupported=unsupportedFixture.MakeWindow();unsupported.ReadComponents();unsupported.Calculate();Check(unsupported.CheckFreshness()&&unsupported.LastBatch.Calculations.All(item=>item.Result.Status=="unsupported"),"Whole unsupported components can be reported without fabricated quantities");Check(Field<TextBlock>(unsupported,"summary").Text.Contains("—"),"All-unsupported summary is blank, not invented zero");unsupported.Close();
    }

    private static void DuplicateReadAtomicity(string output)
    {
        var fixture=new Fixture(output);var window=fixture.MakeWindow();fixture.Ready();var previous=window.LastBatch;var previousBundle=Field<RevitWallJoinReadBundle>(window,"bundle");object rows=Field<DataGrid>(window,"components").ItemsSource;fixture.DuplicateComponent=true;window.ReadComponents();
        Check(Object.ReferenceEquals(previous,window.LastBatch)&&Object.ReferenceEquals(previousBundle,Field<RevitWallJoinReadBundle>(window,"bundle"))&&Object.ReferenceEquals(rows,Field<DataGrid>(window,"components").ItemsSource),"Duplicate candidate components are rejected before replacing complete batch/bundle/rows");Check(!previousBundle.IsDisposed&&fixture.LastBundle.IsDisposed,"Invalid candidate metadata releases only candidate operands");window.Close();
    }
    private static void BoundaryFlows(string output)
    {
        var fixture=new Fixture(output){SingleWalls=true};var window=fixture.MakeWindow();window.ReadComponents();Check(Field<Dictionary<string,CivilWallJoinPlan>>(window,"plans").Count==2,"Single-wall components receive their only possible rank without inventing multiwall priority");window.Calculate();Check(window.CheckFreshness()&&window.LastBatch.Calculations.Count==2,"Independent single walls can calculate directly after reading");Near(window.LastBatch.Calculations.Sum(item=>item.Result.SumResultVolumeMetres3.Value),6,"Two single-wall component totals");window.Close();
        var emptyFixture=new Fixture(output){Empty=true};var empty=emptyFixture.MakeWindow();empty.ReadComponents();empty.Calculate();Check(empty.CheckFreshness()&&empty.LastBatch.Calculations.Count==0&&Field<DataGrid>(empty,"components").Items.Count==0,"Empty requested wall scope preserves an explicit empty complete report");Check(!empty.PrepareFocus()&&empty.BuildCurrentCsv().Contains(CivilWallJoinSession.ReportId),"Empty complete batch exports headers and cannot focus a fictitious wall");Check(Field<TextBlock>(empty,"summary").Text.Contains("—"),"Empty component totals remain blank rather than invented zero");empty.Close();
    }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output);Basic(output);ReadAtomicity(output);CalculateAtomicity(output);BoundaryFlows(output);MutationGuards(output);DuplicateReadAtomicity(output);
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks.ToString(CultureInfo.InvariantCulture)+",\"scope\":\"Production join workbench/session/core/plan with exact finite-cell reader stand-ins; offscreen WPF only, no Autodesk geometry or native desktop input\",\"uiShown\":false,\"nativeDesktopInput\":false,\"revitStarted\":false}",new UTF8Encoding(false));Console.WriteLine("PASS "+checks+" join workbench UI assertions.");
    }
    private static int Main(string[] args){Exception failure=null;var thread=new Thread(delegate(){try{Run(args[0]);}catch(Exception error){failure=error;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new InvalidOperationException("Join workbench UI fixture failed after "+checks+" assertions.",failure);return 0;}
}
