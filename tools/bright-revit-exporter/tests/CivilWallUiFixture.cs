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
        internal CivilWallWindow Window()
        {
            return new CivilWallWindow(new CivilReadRequest{Scope="project",PhaseId="P",DesignOptionId="-1"},new[]{new CivilChoice{Id="P",Label="新建"},new CivilChoice{Id="P2",Label="另一阶段"}},new[]{new CivilChoice{Id="-1",Label="主模型"}},new[]{new CivilChoice{Id="V",Label="三维校验视图"}},"V",delegate(CivilReadRequest request,string view){Reads++;if(Fail)throw new InvalidDataException("fixture read failure");Native.Revision=Revision;return new CivilWallSnapshot{NativeSnapshot=Native,GeometryViewUniqueId=view,GeometryViewName="合成三维",GeometryPhaseId=request.PhaseId,Walls=Facts};},delegate{return Revision;},delegate(string message){Confirmations++;return AllowDiscard;});
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
        var fixture=new Fixture();CivilWallFact first=fixture.Facts[0],second=fixture.Facts[2];fixture.Facts.Clear();fixture.Facts.Add(first);fixture.Facts.Add(second);
        fixture.Native.Elements.RemoveAll(row=>row.UniqueId!="wall-A"&&row.UniqueId!="wall-C");
        foreach(CivilWallFact fact in fixture.Facts){fact.Inserts.Clear();fact.ParameterLength=M(1e154);fact.ParameterHeight=M(1e154);fact.ParameterWidth=M(1e-308);fact.GeometryVolumeMetres3=1;}
        var window=fixture.Window();window.RefreshCalculation();
        Check(window.LastBatch!=null,"Large finite batch must be read before view guards: "+Field<TextBlock>(window,"status").Text);
        Check(window.LastBatch.Results.All(row=>row.Status=="calculated")&&Field<bool>(window,"reportError"),"Individually finite legitimate areas can overflow a group without escaping the UI event");
        Check(!window.CheckFreshness()&&!Field<Button>(window,"csvButton").IsEnabled&&!window.PrepareFocus()&&Refuses(delegate{window.BuildCurrentCsv();}),"Overflow disables all export and focus routes");
        Field<TextBox>(window,"search").Text="单墙";
        Check(window.CheckFreshness()&&Field<DataGrid>(window,"walls").Items.Count==1&&window.BuildCurrentCsv().Contains(CivilWallReport.SchemaId)&&fixture.Reads==1,"Narrowing filter recovers finite report without geometry reread");
        var table=Field<DataGrid>(window,"walls");table.SelectedIndex=0;Check(window.PrepareFocus(),"Recovered single wall can focus");object previousRows=table.ItemsSource;double? area=window.LastBatch.Results[0].ResultAreaMetres2;
        window.LastBatch.Results[0].ResultAreaMetres2=Double.NaN;Field<TextBox>(window,"search").Text="单墙 ";
        Check(Field<bool>(window,"reportError")&&Object.ReferenceEquals(previousRows,table.ItemsSource)&&window.SelectedFocusElements.Count==0&&!Field<Button>(window,"focusButton").IsEnabled,"Corrupt row is caught before replacing complete visible tables and clears old focus");
        window.LastBatch.Results[0].ResultAreaMetres2=area;Field<TextBox>(window,"search").Text="单墙";Check(window.CheckFreshness()&&!Field<bool>(window,"reportError"),"Valid repaired facts and narrowed filter recover from a rejected row");
        Render(window,output,"BV-wall-large-finite-recovered-1080.png",1080,720);window.Close();
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
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks+",\"scope\":\"Offscreen production WPF, report v2 and project store using synthetic facts only; no shown window, file dialog, MessageBox or Revit\",\"nativeDesktopInput\":false,\"revitStarted\":false,\"uiShown\":false,\"reportSchema\":\""+CivilWallReport.SchemaId+"\",\"nativeBeforeAfterBytesEqual\":true}");Console.WriteLine("PASS "+checks+" wall UI assertions.");
    }
}
