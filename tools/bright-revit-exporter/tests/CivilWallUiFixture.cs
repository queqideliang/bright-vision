using System;
using System.Collections.Generic;
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
    private static bool Refuses(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    private static CivilMetric M(double value){return new CivilMetric {Status="available",Value=value,Unit="m",Source="native fixture"};}
    private static int Main(string[] args)
    {
        Exception failure=null;var thread=new Thread(delegate(){try{Run(args[0]);}catch(Exception error){failure=error;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new Exception("Wall UI fixture failed",failure);return 0;
    }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output);long revision=1;bool fail=false;int reads=0;
        var good=new CivilWallFact {ElementId="10",UniqueId="wall-A",Name="单墙合成算例",TypeName="200mm",Status="verified",GeometryVerified=true,InterwallOverlapCheckComplete=true,
            ParameterLength=M(10),ParameterHeight=M(3),ParameterWidth=M(0.2),GeometryVolumeMetres3=5.6,SymmetricDifferenceMetres3=0,GeometryToleranceMetres3=1e-8,
            NativeVolume=new CivilMetric {Status="available",Unit="m3",Value=5.6,Source="HOST_VOLUME_COMPUTED"},
            Inserts=new List<CivilWallInsert>{new CivilWallInsert{ElementId="20",UniqueId="opening-A",HostUniqueId="wall-A",Kind="rect-opening",Status="recognized",Source="Opening.BoundaryRect",RectX0=1,RectX1=3,RectZ0=1,RectZ1=2}}};
        var blocked=new CivilWallFact {ElementId="11",UniqueId="wall-B",Name="未知门窗切洞",Status="unsupported",Issues=new List<string>{"实际门窗 void 未确认"}};
        Func<CivilReadRequest,string,CivilWallSnapshot> reader=delegate(CivilReadRequest r,string view){reads++;if(fail)throw new InvalidDataException("fixture read failure");return new CivilWallSnapshot{NativeSnapshot=new CivilSnapshot{ModelTitle="墙体UI合成工程",DocumentScope="sample-doc",Revision=revision,ScopeLabel=r.Scope,PhaseLabel=r.PhaseId,SourcePath="H:\\fixture.rvt"},GeometryViewUniqueId=view,GeometryViewName="合成三维",GeometryPhaseId=r.PhaseId,Walls=new List<CivilWallFact>{good,blocked}};};
        var window=new CivilWallWindow(new CivilReadRequest{Scope="project",PhaseId="P",DesignOptionId="-1"},new[]{new CivilChoice{Id="P",Label="新建"}},new[]{new CivilChoice{Id="-1",Label="主模型"}},new[]{new CivilChoice{Id="V",Label="三维校验视图"}},"V",reader,delegate{return revision;});
        Check(window.Title.Contains("崛亮视觉"),"Company visible in title.");Check(Refuses(delegate{window.BuildCurrentCsv();}),"No stale/unread CSV.");
        window.RefreshCalculation();Check(reads==1&&window.LastBatch.Results.Count==2,"Production UI reads complete fact batch.");
        Check(window.LastBatch.Results[0].Status=="calculated"&&window.LastBatch.Results[1].Status=="unsupported","Successful and blocked walls remain distinct.");
        DataGrid table=Field<DataGrid>(window,"walls"),ledger=Field<DataGrid>(window,"ledger");Check(table.Items.Count==2,"Both walls visible.");
        table.SelectedIndex=0;Check(ledger.Items.Count==2,"Base and opening ledger shown.");Check(window.PrepareFocus()&&window.SelectedFocusElements[0].UniqueId=="wall-A","Wall source focus.");
        ledger.SelectedIndex=1;Check(window.PrepareFocus()&&window.SelectedFocusElements[0].UniqueId=="opening-A","Ledger focus resolves exact associated opening.");
        string csv=window.BuildCurrentCsv();Check(csv.Contains("wall-A")&&csv.Contains("opening-A")&&csv.Contains(CivilWallCalculator.RuleId),"CSV binds source and method.");
        Field<TextBox>(window,"search").Text="未知";Check(table.Items.Count==1&&window.BuildCurrentCsv().Contains("wall-B")&&!window.BuildCurrentCsv().Contains("opening-A"),"Filter applies equally to summary and ledger CSV.");
        Field<TextBox>(window,"search").Text="";Field<ComboBox>(window,"statusFilter").SelectedValue="calculated";Check(table.Items.Count==1,"Status filter selects only calculated wall.");
        table.SelectedIndex=0;ledger.SelectedIndex=1;
        FrameworkElement content=(FrameworkElement)window.Content;((Grid)content).Background=Brushes.White;content.Measure(new Size(1320,900));content.Arrange(new Rect(0,0,1320,900));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1320,900,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(output,"BV-wall-ledger.png")))encoder.Save(file);
        window.ExportCsv(Path.Combine(output,"wall-ui.csv"));Check(File.Exists(Path.Combine(output,"wall-ui.csv")),"Current CSV written through production UI writer.");
        revision++;Check(!window.CheckFreshness()&&Refuses(delegate{window.BuildCurrentCsv();}),"Model revision stales old calculations.");Check(!window.PrepareFocus(),"Stale wall cannot focus.");
        window.RefreshCalculation();Check(window.CheckFreshness(),"Refresh recovers current batch.");
        CivilWallCalculationBatch previous=window.LastBatch;fail=true;window.RefreshCalculation();Check(Object.ReferenceEquals(window.LastBatch,previous)&&!window.CheckFreshness(),"Failed read preserves prior complete result as stale.");
        fail=false;window.RefreshCalculation();int oldReads=reads;Field<ComboBox>(window,"geometryView").SelectedIndex=-1;window.RefreshCalculation();Check(reads==oldReads&&!window.CheckFreshness(),"Missing explicit geometry view refuses before API read.");
        window.Close();
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks+",\"scope\":\"Offscreen production WPF and domain; no shown window or Revit\"}");Console.WriteLine("PASS "+checks+" wall UI assertions.");
    }
}
