// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
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

internal static class CivilWallJoinPlanUiFixture
{
    private static int checks;
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    private static void Reject(Action action,string reason){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}catch(ArgumentException){rejected=true;}Check(rejected,reason);}
    private static T Field<T>(object owner,string name){return(T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
    private static object Property(object owner,string name){return owner.GetType().GetProperty(name).GetValue(owner,null);}
    private static void Click(Button value){value.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,value));}
    private static CivilWallJoinBinding Binding(string output){return new CivilWallJoinBinding{DocumentScope="doc-scope",SourcePath=Path.Combine(output,"fixture.rvt"),PhaseId="P",GeometryViewUniqueId="V",SourceRevision=123,GeometryDependencyFingerprint=new String('c',64)};}
    private static List<CivilWallJoinParticipant> Participants(){return new List<CivilWallJoinParticipant>{
        new CivilWallJoinParticipant{ElementUniqueId="Z",ElementId="20",Name="范围内主墙",Requested=true,GrossVolumeMetres3=6,OpeningVolumeMetres3=.4,NativeVolumeMetres3=5.4},
        new CivilWallJoinParticipant{ElementUniqueId="A",ElementId="10",Name="范围外依赖墙",Requested=false,GrossVolumeMetres3=4,OpeningVolumeMetres3=0,NativeVolumeMetres3=3.8},
        new CivilWallJoinParticipant{ElementUniqueId="M",ElementId="30",Name="第三参与墙",Requested=true,GrossVolumeMetres3=null,OpeningVolumeMetres3=null,NativeVolumeMetres3=null}};}
    private static string[] Order(CivilWallJoinPlanWindow window){return Field<DataGrid>(window,"participants").Items.Cast<object>().Select(item=>(string)Property(item,"UniqueId")).ToArray();}
    private static void Confirm(CivilWallJoinPlanWindow window){Field<CheckBox>(window,"confirmOrder").IsChecked=true;}
    private static void Render(CivilWallJoinPlanWindow window,string output,int width,int height,string file)
    {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(DrawingContext drawing=background.RenderOpen())drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));image.Render(background);image.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=new FileStream(Path.Combine(output,file),FileMode.CreateNew,FileAccess.Write))encoder.Save(stream);
        Check(!window.IsVisible,"Offscreen rendering does not display a window");Check(Field<DataGrid>(window,"participants").ActualHeight>130,"Participant table retains useful height at "+width+"x"+height);
        foreach(string name in new[]{"upButton","downButton","applyButton","cancelButton","confirmOrder"}){FrameworkElement control=Field<FrameworkElement>(window,name);Point origin=control.TranslatePoint(new Point(0,0),root);Check(control.ActualWidth>20&&control.ActualHeight>=18&&origin.X>=0&&origin.Y>=0&&origin.X+control.ActualWidth<=width+.1&&origin.Y+control.ActualHeight<=height+.1,name+" stays within client area");}
    }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output);var context=Binding(output);var sources=Participants();var contextBefore=CivilWallJoinPlans.CloneBinding(context);
        var window=new CivilWallJoinPlanWindow(context,sources);var table=Field<DataGrid>(window,"participants");var confirmation=Field<CheckBox>(window,"confirmOrder");var apply=Field<Button>(window,"applyButton");
        Check(window.Result==null&&!window.IsVisible&&window.Icon!=null,"Construction neither shows the window nor yields a confirmed plan");
        Check(window.Title.Contains("崛亮视觉")&&window.Title.Contains("BV"),"Brand is visible in title");Check(confirmation.IsChecked!=true&&!apply.IsEnabled,"Unconfirmed initial order cannot be applied");
        Check(Order(window).SequenceEqual(new[]{"Z","A","M"}),"Display order is the supplied order, not UID or ElementId business sorting");
        Check(!table.CanUserSortColumns&&!table.CanUserReorderColumns&&table.IsReadOnly,"No header sorting or row editing silently changes priority");
        Check(table.Items.Count==3&&((string)Property(table.Items[1],"Scope")).Contains("范围外"),"Unrequested dependency is shown as a component participant");
        Check((string)Property(table.Items[2],"Gross")=="—"&&(string)Property(table.Items[2],"Native")=="—","Missing reference metrics display explicit blanks rather than invented zero");
        Reject(delegate{window.BuildPlan();},"BuildPlan requires explicit confirmation");Check(window.Result==null,"Failed unconfirmed build does not create Result");
        Confirm(window);Check(apply.IsEnabled,"Explicit confirmation enables Apply");var first=window.BuildPlan();
        Check(first.Priorities.OrderBy(p=>p.Rank).Select(p=>p.ElementUniqueId).SequenceEqual(new[]{"Z","A","M"}),"Explicitly accepted order includes all dependencies");
        Check(window.Result==null,"Offscreen BuildPlan validates without committing a window result");Check(CivilWallJoinPlans.Fingerprint(first)==CivilWallJoinPlans.Fingerprint(window.BuildPlan()),"Repeated BuildPlan keeps draft plan identity stable");
        context.PhaseId="caller-changed";sources[0].ElementUniqueId="caller-changed";sources[1].NativeVolumeMetres3=99;sources[1].Requested=true;
        var isolated=window.BuildPlan();Check(isolated.Binding.PhaseId=="P"&&isolated.Priorities[0].ElementUniqueId=="Z","Window detaches binding and participant identities");
        Check((string)Property(table.Items[1],"Native")!="99"&&((string)Property(table.Items[1],"Scope")).Contains("范围外"),"Window detaches reference metrics and requested flags");
        table.SelectedIndex=1;Click(Field<Button>(window,"upButton"));Check(Order(window).SequenceEqual(new[]{"A","Z","M"})&&table.SelectedIndex==0,"Up moves the selected wall ahead exactly once");
        Check(confirmation.IsChecked!=true&&!apply.IsEnabled,"Moving a row invalidates previous confirmation");Reject(delegate{window.BuildPlan();},"Changed order must be reconfirmed");
        Confirm(window);var moved=window.BuildPlan();Check(moved.Priorities[0].ElementUniqueId=="A"&&moved.Priorities[0].Rank==1,"Confirmed outside dependency can explicitly receive first priority");
        CivilWallJoinPlans.ValidateFor(moved,contextBefore,new[]{"Z","A","M"});Check(true,"Moved result remains bound to complete original component");
        Click(Field<Button>(window,"downButton"));Check(Order(window).SequenceEqual(new[]{"Z","A","M"})&&table.SelectedIndex==1,"Down moves the selected wall back exactly once");
        table.SelectedIndex=0;Confirm(window);Click(Field<Button>(window,"upButton"));Check(confirmation.IsChecked==true&&Order(window)[0]=="Z","Boundary move is a no-op and does not invent an order change");
        Reject(delegate{window.MoveSelected(2);},"Unknown movement direction is refused");
        Field<TextBox>(window,"planName").Text="新确认方案";Check(confirmation.IsChecked!=true&&!apply.IsEnabled,"Editing the plan name also resets explicit confirmation");Confirm(window);Click(apply);
        Check(window.Result!=null&&window.Result.Name=="新确认方案"&&window.Result.Revision==1,"Apply commits a detached new confirmed plan");var committed=window.Result;committed.Binding.SourceRevision=0;
        Check(window.BuildPlan().Binding.SourceRevision==123,"Committed result is detached from the window draft");
        Click(Field<Button>(window,"cancelButton"));Check(window.Result==null,"Cancel clears a previously test-committed result");
        Field<TextBox>(window,"planName").Text=" ";Confirm(window);Click(apply);Check(window.Result==null&&Field<TextBlock>(window,"message").Text.Length>0,"Invalid plan name produces an inline error and no committed result");
        Field<TextBox>(window,"planName").Text="可用顺序";Confirm(window);Render(window,output,1080,650,"BV-wall-join-priority-1080.png");Render(window,output,920,580,"BV-wall-join-priority-min.png");window.Close();

        var participants=Participants();var original=CivilWallJoinPlans.Create(contextBefore,new[]{"M","Z","A"},"保存过的方案");original.Revision=7;string originalHash=CivilWallJoinPlans.Fingerprint(original);
        var loadedWindow=new CivilWallJoinPlanWindow(contextBefore,participants,original);Check(Order(loadedWindow).SequenceEqual(new[]{"M","Z","A"}),"Saved explicit rank controls display order independently of source list order");
        Check(loadedWindow.Result==null&&!Field<Button>(loadedWindow,"applyButton").IsEnabled,"Loading a valid plan does not itself apply anything");
        Reject(delegate{loadedWindow.BuildPlan();},"Loaded plan still needs this-time confirmation");Confirm(loadedWindow);var unchanged=loadedWindow.BuildPlan();
        Check(unchanged.Revision==7&&CivilWallJoinPlans.Fingerprint(unchanged)==originalHash,"Unchanged reconfirmed plan keeps revision and fingerprint");
        var loadedGrid=Field<DataGrid>(loadedWindow,"participants");loadedGrid.SelectedIndex=2;loadedWindow.MoveSelected(-1);Confirm(loadedWindow);var updated=loadedWindow.BuildPlan();
        Check(updated.Revision==8&&updated.PlanId==original.PlanId,"Changing saved order increases revision exactly once");Check(loadedWindow.BuildPlan().Revision==8,"Preview or repeated BuildPlan does not increment the revision repeatedly");
        Check(CivilWallJoinPlans.Fingerprint(original)==originalHash,"Editing never mutates the caller's saved plan");
        loadedGrid.SelectedIndex=1;loadedWindow.MoveSelected(1);Confirm(loadedWindow);Check(CivilWallJoinPlans.Fingerprint(loadedWindow.BuildPlan())==originalHash,"Reverting all edits restores the original revision");
        Field<TextBox>(loadedWindow,"planName").Text="另一个名称";Confirm(loadedWindow);Check(loadedWindow.BuildPlan().Revision==8,"Name change also increments saved revision");
        Click(Field<Button>(loadedWindow,"cancelButton"));Check(loadedWindow.Result==null&&CivilWallJoinPlans.Fingerprint(original)==originalHash,"Cancel leaves the saved source plan unchanged");loadedWindow.Close();
        var ceiling=CivilWallJoinPlans.Clone(original);ceiling.Revision=Int32.MaxValue;var ceilingWindow=new CivilWallJoinPlanWindow(contextBefore,participants,ceiling);Confirm(ceilingWindow);Check(ceilingWindow.BuildPlan().Revision==Int32.MaxValue,"Unchanged maximum revision can be reconfirmed");
        Field<TextBox>(ceilingWindow,"planName").Text="必须另存";Confirm(ceilingWindow);Reject(delegate{ceilingWindow.BuildPlan();},"Revision overflow is refused");Click(Field<Button>(ceilingWindow,"applyButton"));Check(ceilingWindow.Result==null&&Field<TextBlock>(ceilingWindow,"message").Text.Contains("修订"),"Revision overflow remains an inline refusal");ceilingWindow.Close();
        var changedBinding=CivilWallJoinPlans.CloneBinding(contextBefore);changedBinding.SourceRevision++;Reject(delegate{new CivilWallJoinPlanWindow(changedBinding,participants,original);},"Old source revision cannot be loaded as a current priority plan");
        changedBinding=CivilWallJoinPlans.CloneBinding(contextBefore);changedBinding.GeometryDependencyFingerprint=new String('d',64);Reject(delegate{new CivilWallJoinPlanWindow(changedBinding,participants,original);},"Changed geometry dependencies cannot reuse old plan");
        Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,participants.Take(2).ToList(),original);},"Missing outside dependency refuses saved plan before showing a window");
        Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,null);},"Null participant component refused");Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,new List<CivilWallJoinParticipant>());},"Empty participant component refused");
        var duplicate=Participants();duplicate[1].ElementUniqueId=duplicate[0].ElementUniqueId;Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,duplicate);},"Duplicate participant UID refused");
        duplicate=Participants();duplicate[1].ElementId=duplicate[0].ElementId;Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,duplicate);},"Duplicate native ElementId refused within root document");
        foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,-1.0}){var bad=Participants();bad[0].GrossVolumeMetres3=invalid;Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,bad);},"Invalid display quantity refuses the component");}
        var tooMany=Enumerable.Range(0,65).Select(i=>new CivilWallJoinParticipant{ElementUniqueId="U"+i,ElementId=i.ToString(),Name="Wall"}).ToList();Reject(delegate{new CivilWallJoinPlanWindow(contextBefore,tooMany);},"65th participant is not silently truncated");
        var oldCulture=Thread.CurrentThread.CurrentCulture;try{Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");var localized=new CivilWallJoinPlanWindow(contextBefore,Participants());Confirm(localized);Check(localized.BuildPlan().Binding.SourceRevision==123,"Localization cannot change source identity or order");localized.Close();}finally{Thread.CurrentThread.CurrentCulture=oldCulture;}
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks.ToString(CultureInfo.InvariantCulture)+",\"scope\":\"Production priority editor with synthetic source binding and participants, offscreen WPF only\",\"uiShown\":false,\"nativeDesktopInput\":false,\"revitStarted\":false}");Console.WriteLine("PASS "+checks+" wall join plan UI assertions.");
    }
    private static int Main(string[] args){Exception failure=null;var thread=new Thread(delegate(){try{Run(args[0]);}catch(Exception error){failure=error;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new InvalidOperationException("Join plan editor fixture failed.",failure);return 0;}
}
