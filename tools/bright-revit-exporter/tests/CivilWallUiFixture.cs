using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrightVision.Civil;
using BrightVision.Revit;

internal static class CivilWallUiFixture
{
    private static int checks;
    private static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    private static void Near(double? actual,double expected,string message){Check(actual.HasValue&&Math.Abs(actual.Value-expected)<1e-9,message);}
    private static bool Refuses(Action action){try{action();return false;}catch(InvalidOperationException){return true;}catch(InvalidDataException){return true;}catch(IOException){return true;}}
    private static CivilMetric M(double value){return new CivilMetric{Status="available",Value=value,Unit="m",Source="native fixture"};}
    private sealed class Fixture
    {
        internal long Revision=1;internal bool Fail,AllowDiscard;internal int Reads,Confirmations;
        internal readonly CivilSnapshot Native;
        internal readonly List<CivilWallFact> Facts;
        internal Fixture()
        {
            Native=new CivilSnapshot{ModelTitle="墙体UI合成工程",DocumentScope="sample-doc",Revision=1,SourcePath="H:\\fixture.rvt",ScopeLabel="project",PhaseLabel="P",DesignOptionLabel="-1"};
            Native.Levels.Add(new CivilLevelInfo{Id="L1-id",UniqueId="L1",Name="一层",ElevationMetres=0});Native.Levels.Add(new CivilLevelInfo{Id="L2-id",UniqueId="L2",Name="二层",ElevationMetres=3});
            Native.Elements.Add(Source("10","wall-A","单墙合成算例","L1","一层",5.6));Native.Elements.Add(Source("11","wall-B","未知门窗切洞","L1","一层",777));
            Native.Elements.Add(Source("12","wall-C","第二层无孔墙","L2","二层",6));Native.Elements.Add(Source("13","wall-D","读取失败墙","","",888));
            var good=Good("10","wall-A","单墙合成算例",5.6);good.NativeVolume=Native.Elements[0].Volume;
            good.Inserts.Add(new CivilWallInsert{ElementId="20",UniqueId="opening-A",HostUniqueId="wall-A",Kind="rect-opening",Status="recognized",Source="Opening.BoundaryRect",RectX0=1,RectX1=3,RectZ0=1,RectZ1=2});
            Facts=new List<CivilWallFact>{good,new CivilWallFact{ElementId="11",UniqueId="wall-B",Name="未知门窗切洞",TypeUniqueId="type-1",TypeName="200mm",Status="unsupported",Issues=new List<string>{"实际门窗 void 未确认"}},Good("12","wall-C","第二层无孔墙",6),new CivilWallFact{ElementId="13",UniqueId="wall-D",Name="读取失败墙",TypeUniqueId="type-2",TypeName="其他类型",Status="failed",Issues=new List<string>{"读取失败"}}};
        }
        private static CivilElementRecord Source(string id,string uid,string name,string level,string levelName,double volume){return new CivilElementRecord{ElementId=id,UniqueId=uid,Name=name,KindCode="wall",KindName="墙",Category="墙",TypeName="200mm",LevelUniqueId=level,LevelId=level.Length==0?"":level+"-id",LevelName=levelName,RawLevelId=level,RawLevelName=levelName,LevelSource="fixture explicit native level",Supported=true,Length=M(10),Area=new CivilMetric{Status="available",Unit="m2",Value=999,Source="HOST_AREA_COMPUTED"},Volume=new CivilMetric{Status="available",Unit="m3",Value=volume,Source="HOST_VOLUME_COMPUTED"}};}
        private static CivilWallFact Good(string id,string uid,string name,double volume){return new CivilWallFact{ElementId=id,UniqueId=uid,Name=name,TypeUniqueId="type-1",TypeName="200mm",Status="verified",GeometryVerified=true,InterwallOverlapCheckComplete=true,ParameterLength=M(10),ParameterHeight=M(3),ParameterWidth=M(0.2),GeometryVolumeMetres3=volume,SymmetricDifferenceMetres3=0,GeometryToleranceMetres3=1e-8,NativeVolume=new CivilMetric{Status="available",Unit="m3",Value=volume,Source="HOST_VOLUME_COMPUTED"}};}
        internal CivilWallWindow Window(CivilWallRuleReader controlled = null)
        {
            return new CivilWallWindow(new CivilReadRequest{Scope="project",PhaseId="P",DesignOptionId="-1"},new[]{new CivilChoice{Id="P",Label="新建"},new CivilChoice{Id="P2",Label="另一阶段"}},new[]{new CivilChoice{Id="-1",Label="主模型"}},new[]{new CivilChoice{Id="V",Label="三维校验视图"}},"V",delegate(CivilReadRequest request,string view){Reads++;if(Fail)throw new InvalidDataException("fixture read failure");Native.Revision=Revision;return new CivilWallSnapshot{NativeSnapshot=Native,GeometryViewUniqueId=view,GeometryViewName="合成三维",GeometryPhaseId=request.PhaseId,Walls=Facts};},delegate{return Revision;},delegate(string message){Confirmations++;return AllowDiscard;},controlled);
        }
        internal CivilProjectSettings Profile()
        {
            var profile=CivilProject.Create(Native,"合成计量工程");profile.QuantityLevels.Add(new CivilQuantityLevel{Id="Q1",Name="首层计量"});profile.QuantityLevels.Add(new CivilQuantityLevel{Id="Q2",Name="二层计量"});
            profile.LevelMappings.Add(new CivilLevelMapping{SourceLevelUniqueId="L1",QuantityLevelId="Q1"});profile.LevelMappings.Add(new CivilLevelMapping{SourceLevelUniqueId="L2",QuantityLevelId="Q2"});
            profile.Segments.Add(new CivilSegment{Id="S1",Name="A施工段"});profile.Segments.Add(new CivilSegment{Id="S2",Name="B施工段"});CivilProject.AssignSegment(profile,Native.Elements.Take(2),"S1");CivilProject.AssignSegment(profile,Native.Elements.Skip(2).Take(1),"S2");return profile;
        }
    }
    private static string NativeEvidence(Fixture fixture){return CivilSchedule.ToCsv(fixture.Native,fixture.Native.Elements,"element")+"\n"+String.Join("|",fixture.Native.Elements.Select(row=>row.UniqueId+":"+row.QuantityLevelId+":"+row.SegmentId+":"+String.Join(";",row.ProjectIssues)))+"\n"+fixture.Native.ProjectId+"|"+String.Join(";",fixture.Native.ProjectNotices);}
    private static int Main(string[] args)
    {
        Exception failure=null;var thread=new Thread(delegate(){try{Run(args[0]);}catch(Exception error){failure=error;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new Exception("Wall UI fixture failed",failure);return 0;
    }
    private static void Render(CivilWallWindow window,string output,string name,int width,int height)
    {
        FrameworkElement content=(FrameworkElement)window.Content;((Grid)content).Background=Brushes.White;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,name)))encoder.Save(file);
        Check(Field<Button>(window,"csvButton").ActualHeight>20&&Field<Button>(window,"focusButton").ActualWidth>100,"Footer actions remain laid out at "+width+"x"+height);
    }
    private static void CheckOverflowRecovery(string output)
    {
        var fixture=new Fixture();bool large=false;int reads=0;
        var window=fixture.Window(delegate(CivilReadRequest request,string view,CivilWallRuleTemplate rule,CivilWallReadControl control){
            reads++;var input=new Fixture();input.Native.Revision=fixture.Revision;
            input.Native.Elements.RemoveAll(row=>row.UniqueId!="wall-A"&&row.UniqueId!="wall-C");input.Facts.RemoveAll(fact=>fact.UniqueId!="wall-A"&&fact.UniqueId!="wall-C");
            foreach(CivilWallFact fact in input.Facts){fact.Inserts.Clear();fact.GeometryVolumeMetres3=6;if(large){fact.ParameterLength=M(1e154);fact.ParameterHeight=M(1e154);fact.ParameterWidth=M(1e-308);fact.GeometryVolumeMetres3=fact.ParameterLength.Value.Value*fact.ParameterHeight.Value.Value*fact.ParameterWidth.Value.Value;}}
            return new CivilWallSnapshot{NativeSnapshot=input.Native,Walls=input.Facts,GeometryViewUniqueId=view,GeometryPhaseId=request.PhaseId,RuleTemplate=CivilWallRuleTemplates.Clone(rule)};
        });
        window.RefreshCalculation();Check(window.CheckFreshness(),"Finite baseline commits before overflow attempt");var originalBatch=window.LastBatch;var originalReport=window.LastReport;var table=Field<DataGrid>(window,"walls");object originalRows=table.ItemsSource;
        large=true;window.RefreshCalculation();
        Check(Object.ReferenceEquals(originalBatch,window.LastBatch)&&Object.ReferenceEquals(originalReport,window.LastReport)&&Object.ReferenceEquals(originalRows,table.ItemsSource),"Candidate filtered/group total overflow refuses before replacing the complete batch, report or visible rows");
        Check(!window.CheckFreshness()&&!Field<Button>(window,"csvButton").IsEnabled&&!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();}),"Failed candidate leaves old complete objects stale and all output routes disabled");
        Field<TextBox>(window,"search").Text="单墙";window.RefreshCalculation();
        Check(window.CheckFreshness()&&table.Items.Count==1&&window.LastBatch.Results.All(row=>row.Status=="calculated")&&reads==3,"Rereading legitimate large finite facts with a narrow valid view commits a new complete sealed batch");
        table.SelectedIndex=0;Check(window.PrepareFocus(),"Committed narrow view can focus");object previousRows=table.ItemsSource;
        Field<TextBox>(window,"search").Text="";
        Check(Field<bool>(window,"reportError")&&Object.ReferenceEquals(previousRows,table.ItemsSource)&&window.SelectedFocusElements.Count==0&&!window.PrepareFocus(),"Broadening a complete valid batch to an overflowing view preserves the last complete visible rows");
        Field<TextBox>(window,"search").Text="单墙";Check(window.CheckFreshness()&&!Field<bool>(window,"reportError"),"Narrowing only the report view recovers without rereading facts");previousRows=table.ItemsSource;
        double? area=window.LastBatch.Results[0].ResultAreaMetres2;window.LastBatch.Results[0].ResultAreaMetres2=Double.NaN;Field<TextBox>(window,"search").Text="单墙 ";
        Check(Field<bool>(window,"reportError")&&Object.ReferenceEquals(previousRows,table.ItemsSource)&&!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();}),"Post-seal result tampering is rejected before report replacement, focus or CSV");
        window.LastBatch.Results[0].ResultAreaMetres2=area;window.RefreshCalculation();Check(window.CheckFreshness(),"A fresh sealed read recovers after a rejected mutation");
        Render(window,output,"BV-wall-large-finite-recovered-1080.png",1080,720);window.Close();
    }
    private static void CheckRulesAndProjects(string output)
    {
        var fixture=new Fixture();var window=fixture.Window();window.RefreshCalculation();
        Check(window.CheckFreshness()&&Field<ComboBox>(window,"rulePicker").Items.Count==2,"Workbench starts with the two independent BV built-in project templates");
        string originalFingerprint=window.LastBatch.RuleFingerprint;var detached=window.SelectedRuleTemplate;detached.Name="只改返回副本";
        Check(CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate)==originalFingerprint,"Selected rule getter returns a detached clone");
        var project=fixture.Profile();window.SetProject(project);string defaultProject=Path.Combine(output,"rule-bound-default.bvproject.json");window.SaveProject(defaultProject);
        var saved=CivilProjectStore.Load(defaultProject);
        Check(saved.SchemaVersion==2&&saved.WallRuleTemplate!=null&&CivilWallRuleTemplates.Fingerprint(saved.WallRuleTemplate)==originalFingerprint,"Saving migrates attribution to schema 2 with the exact active rule fingerprint");
        var oldBatch=window.LastBatch;var oldReport=window.LastReport;int reads=fixture.Reads;
        var picker=Field<ComboBox>(window,"rulePicker");picker.SelectedItem=picker.Items.Cast<CivilWallRuleTemplate>().Single(rule=>rule.TemplateId=="BV.Wall.LegacyExplicitOpeningOnly");
        Check(!window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport)&&fixture.Reads==reads,"Picking another rule immediately expires the prior batch without relabeling or rereading it");
        Check(Refuses(delegate{window.BuildCurrentCsv();})&&!window.PrepareFocus()&&Refuses(delegate{window.SaveProject(defaultProject);}),"Changed rule disables CSV, focus and project save until a new complete read");
        Check(oldBatch.RuleFingerprint==originalFingerprint&&Field<TextBlock>(window,"ruleStatus").Text.Contains("仅原生矩形"),"Rule status describes the selected pending policy while completed batch retains its original fingerprint");
        string standalone=Path.Combine(output,"legacy-standalone.bvwallrule.json");window.SaveRuleTemplate(standalone);
        Check(CivilWallRuleTemplates.Load(standalone).OpeningPolicy==CivilWallRuleTemplates.NativeOpeningOnly,"Pending active template can be exported independently of model calculation");
        window.RefreshCalculation();Check(window.CheckFreshness()&&window.LastBatch.RuleTemplate.OpeningPolicy==CivilWallRuleTemplates.NativeOpeningOnly&&window.BuildCurrentCsv().Contains("native-opening-only"),"Fresh CSV is labeled only with the exact rule sealed in its completed batch");
        oldBatch=window.LastBatch;oldReport=window.LastReport;reads=fixture.Reads;window.LoadProject(defaultProject);
        Check(fixture.Reads==reads&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport)&&!window.CheckFreshness(),"Loading schema 2 with different rules preserves the complete old pair and requires recalculation");
        Check(window.SelectedRuleTemplate.TemplateId=="BV.Wall.ProjectGeometry"&&oldBatch.RuleTemplate.TemplateId=="BV.Wall.LegacyExplicitOpeningOnly","Loaded project rule and old batch rule remain separate until reread");
        window.RefreshCalculation();Check(window.CheckFreshness()&&window.LastBatch.RuleFingerprint==originalFingerprint,"Reread uses the loaded project rule");
        oldBatch=window.LastBatch;reads=fixture.Reads;window.LoadProject(defaultProject);
        Check(window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&fixture.Reads==reads,"Loading a project with matching rule changes attribution without geometry reread");
        var currentProject=Field<CivilProjectSettings>(window,"currentProject");var currentReport=window.LastReport;string currentRule=CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate);
        var foreign=fixture.Profile();foreign.SourceDocumentScope="other-source";foreign.SchemaVersion=2;foreign.WallRuleTemplate=CivilWallRuleTemplates.LegacyExplicitOpeningOnly();
        Check(Refuses(delegate{window.SetProject(foreign);})&&Object.ReferenceEquals(currentProject,Field<CivilProjectSettings>(window,"currentProject"))&&Object.ReferenceEquals(currentReport,window.LastReport)&&CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate)==currentRule,"Foreign-source project cannot partially replace profile, rule or report");
        var different=fixture.Profile();different.SchemaVersion=2;different.WallRuleTemplate=CivilWallRuleTemplates.LegacyExplicitOpeningOnly();oldBatch=window.LastBatch;oldReport=window.LastReport;window.SetProject(different);
        Check(!window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport),"SetProject follows the same stale-without-relabeling rule as file loading");
        window.RefreshCalculation();
        var legacyProfile=fixture.Profile();string legacyPath=Path.Combine(output,"schema1-no-wall-rule.bvproject.json");CivilProjectStore.Save(legacyPath,legacyProfile);string beforeLegacy=CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate);window.LoadProject(legacyPath);
        Check(window.CheckFreshness()&&CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate)==beforeLegacy&&Field<TextBlock>(window,"status").Text.Contains("格式 1")&&Field<TextBlock>(window,"status").Text.Contains("沿用"),"Schema 1 loads with an explicit current-rule notice and never silently switches algorithms");
        string migratedPath=Path.Combine(output,"schema1-migrated-with-wall-rule.bvproject.json");window.SaveProject(migratedPath);Check(CivilProjectStore.Load(migratedPath).SchemaVersion==2,"Explicit save-as binds a formerly schema 1 project to the current rule while preserving its source file");
        string damaged=Path.Combine(output,"damaged-wall-rule.json");File.WriteAllText(damaged,"{invalid");currentRule=CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate);oldBatch=window.LastBatch;
        Check(Refuses(delegate{window.LoadRuleTemplate(damaged);})&&CivilWallRuleTemplates.Fingerprint(window.SelectedRuleTemplate)==currentRule&&Object.ReferenceEquals(oldBatch,window.LastBatch),"Invalid imported rule leaves selected rule and completed data untouched");
        window.LoadRuleTemplate(standalone);Check(window.CheckFreshness(),"Reimporting the exact active fingerprint does not expire a valid batch");
        Render(window,output,"BV-wall-rule-bound-workbench-1080.png",1080,720);window.Close();
    }
    private static void CheckCancellationAndAtomicRead(string output)
    {
        var fixture=new Fixture();CivilWallWindow window=null;int reads=0,processed=0;bool cancelSecond=false,changeRevision=false,changeCondition=false,wrongRule=false,closeSecond=false,inspectLocks=true;
        window=fixture.Window(delegate(CivilReadRequest request,string view,CivilWallRuleTemplate rule,CivilWallReadControl control){
            reads++;processed=0;var input=new Fixture();input.Native.Revision=fixture.Revision;
            for(int index=0;index<input.Facts.Count;index++)
            {
                control.Checkpoint("墙体几何边界",index,input.Facts.Count);
                if(inspectLocks&&index==0)
                {
                    Check(Field<Button>(window,"cancelReadButton").IsEnabled&&!Field<Button>(window,"refreshButton").IsEnabled&&!Field<Button>(window,"csvButton").IsEnabled&&!Field<Button>(window,"focusButton").IsEnabled,"Only cancellation remains active while a read is in flight");
                    Check(!Field<ComboBox>(window,"scope").IsEnabled&&!Field<ComboBox>(window,"rulePicker").IsEnabled&&!Field<Button>(window,"projectButton").IsEnabled&&!Field<Button>(window,"loadRuleButton").IsEnabled&&!Field<TextBox>(window,"search").IsEnabled,"Read conditions, rules, project actions and filters are locked during API work");
                    int before=reads;window.RefreshCalculation();Check(reads==before,"A reentrant refresh request cannot start a second read");
                    Check(Refuses(delegate{window.SetRuleTemplate(CivilWallRuleTemplates.LegacyExplicitOpeningOnly());})&&Refuses(delegate{window.LoadProject("unused.bvproject.json");}),"Public helpers also reject rule/project mutation while reading");
                    inspectLocks=false;
                }
                if(index==1)
                {
                    if(cancelSecond)Field<Button>(window,"cancelReadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(closeSecond)window.Close();
                    if(changeRevision)fixture.Revision++;
                    if(changeCondition)Field<ComboBox>(window,"phase").SelectedValue="P2";
                }
                control.ThrowIfCancelled();processed++;
            }
            return new CivilWallSnapshot{NativeSnapshot=input.Native,Walls=input.Facts,GeometryViewUniqueId=view,GeometryPhaseId=request.PhaseId,RuleTemplate=wrongRule?CivilWallRuleTemplates.LegacyExplicitOpeningOnly():CivilWallRuleTemplates.Clone(rule)};
        });
        window.RefreshCalculation();Check(window.CheckFreshness()&&processed==4,"Controlled reader commits a complete initial batch");var oldBatch=window.LastBatch;var oldReport=window.LastReport;object oldRows=Field<DataGrid>(window,"walls").ItemsSource;
        cancelSecond=true;window.RefreshCalculation();
        Check(processed==1&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport)&&Object.ReferenceEquals(oldRows,Field<DataGrid>(window,"walls").ItemsSource),"Cancel at the second wall boundary commits no partial batch, report or rows");
        Check(!window.CheckFreshness()&&window.SelectedFocusElements.Count==0&&!Field<Button>(window,"cancelReadButton").IsEnabled&&Field<Button>(window,"refreshButton").IsEnabled&&Field<TextBlock>(window,"status").Text.Contains("取消"),"Cancellation restores controls, clears focus, retains stale results and gives an explicit status");
        Check(Refuses(delegate{window.BuildCurrentCsv();})&&!window.PrepareFocus(),"Canceled old results cannot be exported or located");
        cancelSecond=false;window.RefreshCalculation();Check(window.CheckFreshness()&&!Object.ReferenceEquals(oldBatch,window.LastBatch),"A fresh read resets cancellation and recovers with a new complete batch");
        oldBatch=window.LastBatch;oldReport=window.LastReport;changeRevision=true;window.RefreshCalculation();
        Check(!window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport),"Source revision changed during reading refuses before replacing the complete pair");
        changeRevision=false;window.RefreshCalculation();Check(window.CheckFreshness(),"Reread after revision change commits against the current source");
        oldBatch=window.LastBatch;changeCondition=true;window.RefreshCalculation();
        Check(!window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch),"Unexpected programmatic condition changes during a read are caught by the final request key guard");
        changeCondition=false;Field<ComboBox>(window,"phase").SelectedValue="P";window.RefreshCalculation();
        oldBatch=window.LastBatch;oldReport=window.LastReport;wrongRule=true;window.RefreshCalculation();
        Check(!window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport),"A reader returning a different rule snapshot is refused before relabeling any quantity");
        wrongRule=false;window.RefreshCalculation();
        var group=Field<ComboBox>(window,"groupPicker");object previousChoices=group.ItemsSource;group.ItemsSource=new[]{new CivilChoice{Id="unsupported-group",Label="未实现分组"}};group.SelectedIndex=0;oldBatch=window.LastBatch;oldReport=window.LastReport;window.RefreshCalculation();
        Check(!window.CheckFreshness()&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&Object.ReferenceEquals(oldReport,window.LastReport),"Candidate current grouping must validate before the completed pair is replaced");
        group.ItemsSource=(System.Collections.IEnumerable)previousChoices;group.SelectedValue="quantity-level";window.RefreshCalculation();
        oldBatch=window.LastBatch;closeSecond=true;window.RefreshCalculation();
        Check(processed==1&&Object.ReferenceEquals(oldBatch,window.LastBatch)&&!Field<bool>(window,"reading")&&!Field<bool>(window,"closeAfterRead"),"Closing during a read requests cancellation, defers close, and unwinds the in-flight operation");
        closeSecond=false;window.RefreshCalculation();Check(window.CheckFreshness(),"A canceled offscreen close leaves no lingering read flag");
        var profile=fixture.Profile();window.SetProject(profile);var current=window.LastBatch;double? quantity=current.Results[0].ResultVolumeMetres3;current.Results[0].ResultVolumeMetres3=quantity.Value+0.01;
        Check(!window.CheckFreshness()&&!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();})&&Refuses(delegate{window.SaveProject(Path.Combine(output,"tampered-batch.bvproject.json"));}),"Finite post-seal result tampering expires every output and project-save path");
        current.Results[0].ResultVolumeMetres3=quantity;window.RefreshCalculation();Check(window.CheckFreshness(),"Recalculation restores a trusted complete batch after finite tampering");
        current=window.LastBatch;double? height=current.Results[0].Fact.ParameterHeight.Value;current.Results[0].Fact.ParameterHeight.Value=height.Value+0.01;
        Check(!window.CheckFreshness()&&!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();}),"Finite post-seal fact changes also expire every read-derived output");current.Results[0].Fact.ParameterHeight.Value=height;window.RefreshCalculation();
        current=window.LastBatch;double tolerance=current.RuleTemplate.RelativeTolerance;current.RuleTemplate.RelativeTolerance=1e-9;
        Check(!window.CheckFreshness()&&!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();}),"Post-seal rule changes cannot relabel existing quantities");current.RuleTemplate.RelativeTolerance=tolerance;window.RefreshCalculation();
        Check(window.CheckFreshness(),"Only a new complete read restores a batch after fact or rule mutation");
        Render(window,output,"BV-wall-controlled-read-recovered-1080.png",1080,720);fixture.AllowDiscard=true;window.Close();
    }
    private static void CheckAppliedReportSnapshotGuards(string output)
    {
        foreach(string mutation in new[]{"native-metric","attribution"})
        foreach(string firstAction in new[]{"freshness","focus","project-save","csv"})
        {
            var fixture=new Fixture();var window=fixture.Window();window.RefreshCalculation();window.SetProject(fixture.Profile());
            var table=Field<DataGrid>(window,"walls");table.SelectedIndex=0;
            Check(window.CheckFreshness()&&window.PrepareFocus(),"Applied report starts with a verified focused source before "+mutation+" / "+firstAction);
            CivilWallCalculationBatch batch=window.LastBatch;CivilWallReportData report=window.LastReport;string nativeBefore=NativeEvidence(fixture);
            Check(!Object.ReferenceEquals(report.Snapshot,batch.Input.NativeSnapshot)&&report.Rows[0].QuantityLevelId=="Q1","Project application uses a separate attributed snapshot");
            CivilElementRecord row=report.Snapshot.Elements[0];
            if(mutation=="native-metric")
            {
                // Replace the cloned row's metric object instead of mutating
                // the deliberately borrowed, read-only source metric itself.
                // This isolates report-snapshot tampering from batch tampering.
                CivilMetric metric=row.Volume;row.Volume=new CivilMetric{Status=metric.Status,Unit=metric.Unit,Source=metric.Source,Value=metric.Value.Value+.01};
            }
            else{row.QuantityLevelId="tampered-quantity-level";row.QuantityLevelName="被篡改归属";row.SegmentName="被篡改施工段";}
            CivilWallBatchIntegrity.Validate(batch);
            Check(NativeEvidence(fixture)==nativeBefore,"Applied-snapshot mutation leaves native facts, metrics and source annotations unchanged");
            string destination=Path.Combine(output,"refused-applied-"+mutation+"-"+firstAction+".bvproject.json");
            if(firstAction=="freshness")Check(!window.CheckFreshness(),"CheckFreshness independently refuses altered applied "+mutation);
            else if(firstAction=="focus")Check(!window.PrepareFocus(),"PrepareFocus independently deep-validates altered applied "+mutation);
            else if(firstAction=="project-save")Check(Refuses(delegate{window.SaveProject(destination);}),"SaveProject independently deep-validates altered applied "+mutation);
            else Check(Refuses(delegate{window.BuildCurrentCsv();}),"CSV independently deep-validates altered applied "+mutation);
            Check(!window.CheckFreshness()&&!window.PrepareFocus()&&Refuses(delegate{window.SaveProject(destination);})&&Refuses(delegate{window.BuildCurrentCsv();}),"Every output refuses the applied-snapshot mutation regardless of which action discovered it first");
            Check(window.SelectedFocusElements.Count==0&&Field<bool>(window,"stale")&&!Field<Button>(window,"csvButton").IsEnabled&&!Field<Button>(window,"focusButton").IsEnabled&&!Field<Button>(window,"saveProjectButton").IsEnabled,"Applied snapshot rejection clears prior focus and disables all output buttons");
            Check(!File.Exists(destination)&&Object.ReferenceEquals(batch,window.LastBatch)&&Object.ReferenceEquals(report,window.LastReport),"Rejected applied snapshot does not save a file or replace the retained batch/report objects");
            Check(NativeEvidence(fixture)==nativeBefore,"Guard checks never repair by modifying native facts");
            fixture.AllowDiscard=true;window.Close();
        }
        var pollFixture=new Fixture();var polled=pollFixture.Window();polled.RefreshCalculation();Field<DataGrid>(polled,"walls").SelectedIndex=0;Check(polled.PrepareFocus()&&polled.PollSourceFreshness(),"Fast timer poll accepts a matching source revision and key");
        var prior=polled.LastBatch;pollFixture.Revision++;
        Check(!polled.PollSourceFreshness()&&polled.SelectedFocusElements.Count==0&&Object.ReferenceEquals(prior,polled.LastBatch),"Fast timer poll immediately expires a changed source revision without replacing the batch");
        Check(!Field<Button>(polled,"csvButton").IsEnabled&&!Field<Button>(polled,"focusButton").IsEnabled&&Refuses(delegate{polled.BuildCurrentCsv();}),"Timer-detected source changes disable output before a user action");
        polled.RefreshCalculation();Check(polled.PollSourceFreshness()&&polled.CheckFreshness(),"Fast poll and deep action validation recover after a new complete read");polled.Close();
    }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output);var fixture=new Fixture();var window=fixture.Window();
        Check(window.Title.Contains("崛亮视觉"),"Company visible in title");Check(Refuses(delegate{window.BuildCurrentCsv();}),"Unread batch cannot export");Check(!window.PrepareFocus(),"Unread batch cannot focus");
        window.RefreshCalculation();Check(fixture.Reads==1&&window.LastBatch.Results.Count==4&&window.LastReport.Rows.Count==4,"Complete domain batch associated with four native walls");
        Check(window.LastBatch.Results.Count(row=>row.Status=="calculated")==2&&window.LastBatch.Results.Count(row=>row.Status=="unsupported")==1&&window.LastBatch.Results.Count(row=>row.Status=="failed")==1,"Success unsupported and failed states remain distinct");
        var table=Field<DataGrid>(window,"walls");var ledger=Field<DataGrid>(window,"ledger");var groupTable=Field<DataGrid>(window,"groups");var tabs=Field<TabControl>(window,"tabs");
        Check(table.Items.Count==4,"Four wall rows visible");table.SelectedIndex=0;Check(ledger.Items.Count==2,"Basis and opening ledger displayed");
        Near(window.LastBatch.Results[0].BasisAreaMetres2,30,"Area baseline 30");Near(window.LastBatch.Results[0].OpeningUnionAreaMetres2,2,"Opening area 2");Near(window.LastBatch.Results[0].ResultAreaMetres2,28,"Net projected area 28");
        Check(ledger.Columns.Any(column=>Convert.ToString(column.Header).Contains("面积变化"))&&table.Columns.Any(column=>Convert.ToString(column.Header).Contains("净投影")),"Area evidence appears in actual grids");
        Check(window.PrepareFocus()&&window.SelectedFocusElements.Single().UniqueId=="wall-A","Wall source focus");ledger.SelectedIndex=1;Check(window.PrepareFocus()&&window.SelectedFocusElements.Single().UniqueId=="opening-A","Ledger focus uses exact associated opening");
        Check(window.BuildCurrentCsv().Contains(CivilWallReport.SchemaId)&&window.BuildCurrentCsv().Contains(CivilWallCalculator.AreaRuleId),"UI exports v2 report with area rule");
        string nativeBefore=NativeEvidence(fixture);File.WriteAllText(Path.Combine(output,"native-before.csv.txt"),nativeBefore,new System.Text.UTF8Encoding(false));
        CivilWallCalculationBatch originalBatch=window.LastBatch;var originalFact=window.LastBatch.Results[0].Fact;int readsBeforeProject=fixture.Reads;var profile=fixture.Profile();int profileRevision=profile.Revision;
        window.SetProject(profile);Check(fixture.Reads==readsBeforeProject&&Object.ReferenceEquals(originalBatch,window.LastBatch)&&Object.ReferenceEquals(originalFact,window.LastBatch.Results[0].Fact),"Attribution does not reread geometry or replace facts");
        Check(!Object.ReferenceEquals(fixture.Native,window.LastReport.Snapshot)&&profile.Revision==profileRevision,"Report applies a cloned project to a separate annotation snapshot");
        Check(window.LastReport.Rows[0].QuantityLevelId=="Q1"&&window.LastReport.Rows[0].SegmentId=="S1"&&fixture.Native.Elements[0].QuantityLevelId=="","Attribution is visible only on report rows");
        Check(!window.CanDiscardProjectChanges("fixture unsaved confirmation")&&fixture.Confirmations==1,"Unsaved confirmation callback can refuse without a dialog");
        fixture.AllowDiscard=true;Check(window.CanDiscardProjectChanges("fixture confirmed discard")&&fixture.Confirmations==2,"Discard confirmation can explicitly permit continuing");fixture.AllowDiscard=false;
        string path=Path.Combine(output,"wall-ui.bvproject.json");window.SaveProject(path);Check(File.Exists(path)&&!Field<bool>(window,"projectDirty")&&window.CanDiscardProjectChanges("no prompt")&&fixture.Confirmations==2,"Atomic save resets dirty without invoking discard confirmation");
        CivilProjectSettings saved=CivilProjectStore.Load(path);Check(saved.PreferredScope=="project"&&saved.PhaseId=="P"&&saved.DesignOptionId=="-1","Saved metadata records current read conditions");
        var quantity=Field<ComboBox>(window,"quantityLevelPicker");var segment=Field<ComboBox>(window,"segmentPicker");var group=Field<ComboBox>(window,"groupPicker");
        quantity.SelectedValue="Q1";Check(table.Items.Count==2&&window.BuildCurrentCsv().Contains("wall-A")&&window.BuildCurrentCsv().Contains("wall-B")&&!window.BuildCurrentCsv().Contains("wall-C"),"Quantity level filter applies to walls and CSV");
        segment.SelectedValue="S2";Check(table.Items.Count==0,"Level and segment filters intersect");segment.SelectedValue="S1";Check(table.Items.Count==2,"Segment filter retains the exact matching two walls");
        quantity.SelectedValue="";segment.SelectedValue="__unassigned__";Check(table.Items.Count==1&&window.BuildCurrentCsv().Contains("wall-D"),"Unassigned segment remains an explicit filter");
        Check(Field<TextBlock>(window,"summary").Text.Contains("—"),"Only-failed totals are not displayed as fabricated zero quantities");
        segment.SelectedValue="";quantity.SelectedValue="__unassigned__";Check(table.Items.Count==1,"Unmapped level remains explicit");quantity.SelectedValue="";
        Field<TextBox>(window,"search").Text="未知";Check(table.Items.Count==1&&!window.BuildCurrentCsv().Contains("opening-A"),"Search filters both summary and detailed ledger CSV");Field<TextBox>(window,"search").Text="";
        Field<ComboBox>(window,"statusFilter").SelectedValue="calculated";Check(table.Items.Count==2,"Calculated state filter");Field<ComboBox>(window,"statusFilter").SelectedValue="failed";Check(table.Items.Count==1,"Failed state filter");Field<ComboBox>(window,"statusFilter").SelectedValue="";
        tabs.SelectedIndex=1;group.SelectedValue="quantity-level";var firstGroup=groupTable.Items.Cast<CivilWallReportGroup>().Single(row=>row.Rows.Any(item=>item.Source.UniqueId=="wall-A"));groupTable.SelectedItem=firstGroup;
        Check(firstGroup.WallCount==2&&firstGroup.CalculatedCount==1&&firstGroup.UnsupportedCount==1&&firstGroup.FailedCount==0,"Group covers unsupported members separately");Near(firstGroup.ResultAreaMetres2,28,"Group area includes calculated rows only");
        Check(window.PrepareFocus()&&window.SelectedFocusElements.Select(row=>row.UniqueId).OrderBy(uid=>uid).SequenceEqual(new[]{"wall-A","wall-B"}),"Selected group focuses its complete source walls including unsupported members");
        foreach(string dimension in new[]{"segment","level","type"}){group.SelectedValue=dimension;Check(groupTable.Items.Count>=2,"Distinct "+dimension+" groups visible");var selected=groupTable.Items.Cast<CivilWallReportGroup>().First(row=>row.Rows.Count>1);groupTable.SelectedItem=selected;Check(window.PrepareFocus()&&window.SelectedFocusElements.Count==selected.WallCount,"Full-source focus for "+dimension+" grouping");}
        group.SelectedValue="quantity-level";groupTable.SelectedIndex=0;Render(window,output,"BV-wall-groups-1320.png",1320,900);Render(window,output,"BV-wall-groups-1080.png",1080,720);Check(groupTable.ActualHeight>100,"Group table remains usable at minimum layout");
        tabs.SelectedIndex=0;table.SelectedIndex=0;ledger.SelectedIndex=1;Render(window,output,"BV-wall-ledger-1080.png",1080,720);Check(table.ActualHeight>60&&ledger.ActualHeight>50,"Both detail tables retain usable heights at minimum layout");
        window.ExportCsv(Path.Combine(output,"wall-ui.csv"));Check(File.Exists(Path.Combine(output,"wall-ui.csv")),"Current v2 report written with production atomic CSV writer");
        CivilProjectSettings currentProfile=Field<CivilProjectSettings>(window,"currentProject");CivilWallReportData currentReport=window.LastReport;string currentPath=Field<string>(window,"projectPath");bool currentDirty=Field<bool>(window,"projectDirty");
        string queryBefore=Field<TextBox>(window,"search").Text,scopeBefore=Convert.ToString(Field<ComboBox>(window,"scope").SelectedValue),viewBefore=Convert.ToString(Field<ComboBox>(window,"geometryView").SelectedValue);
        var foreign=fixture.Profile();foreign.SourceDocumentScope="foreign-doc";string foreignPath=Path.Combine(output,"foreign.bvproject.json");CivilProjectStore.Save(foreignPath,foreign);
        Check(Refuses(delegate{window.LoadProject(foreignPath);}),"Foreign project rejected");
        Check(Object.ReferenceEquals(currentProfile,Field<CivilProjectSettings>(window,"currentProject"))&&Object.ReferenceEquals(currentReport,window.LastReport)&&currentPath==Field<string>(window,"projectPath")&&currentDirty==Field<bool>(window,"projectDirty"),"Failed foreign load preserves prior profile report path and dirty state");
        Check(queryBefore==Field<TextBox>(window,"search").Text&&scopeBefore==Convert.ToString(Field<ComboBox>(window,"scope").SelectedValue)&&viewBefore==Convert.ToString(Field<ComboBox>(window,"geometryView").SelectedValue),"Failed load never changes filters or read conditions");
        Check(Refuses(delegate{window.SaveProject(foreignPath);})&&Object.ReferenceEquals(currentProfile,Field<CivilProjectSettings>(window,"currentProject"))&&Object.ReferenceEquals(currentReport,window.LastReport),"Atomic save refuses another project's file and preserves UI state");
        string badPath=Path.Combine(output,"damaged.bvproject.json");File.WriteAllText(badPath,"{broken");Check(Refuses(delegate{window.LoadProject(badPath);})&&Object.ReferenceEquals(currentReport,window.LastReport),"Damaged JSON never replaces valid report");
        var change=fixture.Profile();change.Name="新草稿";window.SetProject(change);Check(Field<bool>(window,"projectDirty"),"Changed attribution marked unsaved");int loadReads=fixture.Reads;window.LoadProject(path);
        Check(fixture.Reads==loadReads&&!Field<bool>(window,"projectDirty")&&Field<CivilProjectSettings>(window,"currentProject").Name==saved.Name,"Reload restores saved attribution without a geometry reread");
        Check(scopeBefore==Convert.ToString(Field<ComboBox>(window,"scope").SelectedValue)&&viewBefore==Convert.ToString(Field<ComboBox>(window,"geometryView").SelectedValue),"Project open does not switch scope or geometry view");
        string nativeAfter=NativeEvidence(fixture);File.WriteAllText(Path.Combine(output,"native-after.csv.txt"),nativeAfter,new System.Text.UTF8Encoding(false));Check(nativeBefore==nativeAfter&&Object.ReferenceEquals(fixture.Facts[0].NativeVolume,fixture.Native.Elements[0].Volume),"Native snapshot CSV identities metrics annotations and metric reference remain unchanged");
        Check(fixture.Native.Elements.All(row=>row.QuantityLevelId.Length==0&&row.SegmentId.Length==0&&row.ProjectIssues.Count==0),"No reporting annotations leaked into native source records");
        fixture.Revision++;Check(!window.CheckFreshness()&&Refuses(delegate{window.BuildCurrentCsv();})&&!window.PrepareFocus(),"Model revision invalidates all export and focus routes");window.RefreshCalculation();Check(window.CheckFreshness(),"Fresh read restores current batch");
        var priorBatch=window.LastBatch;var priorReport=window.LastReport;fixture.Fail=true;window.RefreshCalculation();Check(Object.ReferenceEquals(priorBatch,window.LastBatch)&&Object.ReferenceEquals(priorReport,window.LastReport)&&!window.CheckFreshness(),"Failed read preserves prior complete batch and report as stale");Check(!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();}),"Failed refresh cannot export or focus the old result");
        fixture.Fail=false;window.RefreshCalculation();int oldReads=fixture.Reads;Field<ComboBox>(window,"geometryView").SelectedIndex=-1;window.RefreshCalculation();Check(fixture.Reads==oldReads&&!window.CheckFreshness(),"Missing geometry view refuses before API read");Field<ComboBox>(window,"geometryView").SelectedValue="V";window.RefreshCalculation();
        Field<ComboBox>(window,"phase").SelectedValue="P2";Check(!window.CheckFreshness()&&Refuses(delegate{window.SaveProject(path);}),"Read condition changes stale the result and prevent saving obsolete attribution");Field<ComboBox>(window,"phase").SelectedValue="P";window.RefreshCalculation();
        var reopenFixture=new Fixture();var reopened=reopenFixture.Window();reopened.RefreshCalculation();reopened.LoadProject(path);Check(reopened.LastReport.Rows[0].QuantityLevelId=="Q1"&&reopened.LastReport.Rows[0].SegmentId=="S1"&&reopenFixture.Reads==1,"Saved project reopens in a new workbench instance with exact source binding");reopened.Close();window.Close();
        CheckOverflowRecovery(output);
        CheckRulesAndProjects(output);
        CheckCancellationAndAtomicRead(output);
        CheckAppliedReportSnapshotGuards(output);
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks+",\"scope\":\"Offscreen production WPF, report v2 and project store using synthetic facts only; no shown window, file dialog, MessageBox or Revit\",\"nativeDesktopInput\":false,\"revitStarted\":false,\"uiShown\":false,\"reportSchema\":\""+CivilWallReport.SchemaId+"\",\"nativeBeforeAfterBytesEqual\":true}");Console.WriteLine("PASS "+checks+" wall UI assertions.");
    }
}
