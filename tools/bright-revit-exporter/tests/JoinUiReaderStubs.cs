// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
// Project-owned UI contracts and exact finite-set operands. No Autodesk type,
// assembly, native shape, document or desktop surface is used by this fixture.
using System;
using System.Collections.Generic;
using System.Linq;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    internal sealed class RevitWallJoinReadBundle : IDisposable
    {
        public CivilSnapshot NativeSnapshot { get; internal set; }
        public string GeometryViewUniqueId { get; internal set; }
        public string GeometryViewName { get; internal set; }
        public string GeometryPhaseId { get; internal set; }
        public CivilWallRuleTemplate RuleTemplate { get; internal set; }
        public List<RevitWallJoinPreparedComponent> Components { get; internal set; }
        public List<string> Notices { get; internal set; }
        public ICivilWallJoinSolidOps Operations { get; internal set; }
        public bool IsDisposed { get; private set; }
        internal int DisposeCalls;
        internal RevitWallJoinReadBundle() { Components = new List<RevitWallJoinPreparedComponent>(); Notices = new List<string>(); }
        public void Dispose()
        {
            if(IsDisposed)return;IsDisposed=true;DisposeCalls++;
            JoinUiOps ops=Operations as JoinUiOps;if(ops!=null)ops.ReleaseInputs();
        }
    }
    internal sealed class RevitWallJoinPreparedComponent
    {
        public string ComponentId { get; internal set; }
        public CivilWallJoinBinding Binding { get; internal set; }
        public List<CivilWallJoinParticipant> Participants { get; internal set; }
        public List<CivilWallJoinWallEvidence> Evidence { get; internal set; }
        public CivilWallJoinInput Input { get; internal set; }
        public string Status { get; internal set; }
        public string Message { get; internal set; }
    }
    internal sealed class JoinUiSolid
    {
        internal readonly HashSet<int> Cells;
        internal readonly double Weight;
        internal readonly string Component;
        internal int DisposeCalls;
        internal JoinUiSolid(IEnumerable<int> cells,double weight,string component){Cells=new HashSet<int>(cells);Weight=weight;Component=component;}
    }
    internal sealed class JoinUiOps : ICivilWallJoinSolidOps
    {
        internal readonly List<JoinUiSolid> Inputs=new List<JoinUiSolid>(),Created=new List<JoinUiSolid>();
        internal Action<string,string> BeforeOperation;
        internal int Calls;
        internal bool Fail;
        internal JoinUiSolid Source(IEnumerable<int> cells,double weight,string component){var solid=new JoinUiSolid(cells,weight,component);Inputs.Add(solid);return solid;}
        private JoinUiSolid Read(object value){var solid=value as JoinUiSolid;if(solid==null||solid.DisposeCalls!=0)throw new InvalidOperationException("Invalid or disposed UI fixture operand");return solid;}
        private void Before(string operation,JoinUiSolid solid){Calls++;if(BeforeOperation!=null)BeforeOperation(solid.Component,operation);if(Fail)throw new InvalidOperationException("Injected UI-only geometry failure");}
        private object New(IEnumerable<int> cells,JoinUiSolid source){var solid=new JoinUiSolid(cells,source.Weight,source.Component);Created.Add(solid);return solid;}
        private void Pair(JoinUiSolid left,JoinUiSolid right){if(left.Weight!=right.Weight||left.Component!=right.Component)throw new InvalidOperationException("Fixture operands cross components");}
        public object Clone(object value){var solid=Read(value);Before("clone",solid);return New(solid.Cells,solid);}
        public object Union(object left,object right){var a=Read(left);var b=Read(right);Pair(a,b);Before("union",a);return New(a.Cells.Union(b.Cells),a);}
        public object Difference(object left,object right){var a=Read(left);var b=Read(right);Pair(a,b);Before("difference",a);return New(a.Cells.Except(b.Cells),a);}
        public object Intersect(object left,object right){var a=Read(left);var b=Read(right);Pair(a,b);Before("intersection",a);return New(a.Cells.Intersect(b.Cells),a);}
        public double VolumeMetres3(object value){var solid=Read(value);return solid.Cells.Count*solid.Weight;}
        public void Dispose(object value){var solid=(JoinUiSolid)value;solid.DisposeCalls++;if(solid.DisposeCalls!=1)throw new InvalidOperationException("Fixture operand disposed twice");}
        internal void ReleaseInputs(){foreach(var solid in Inputs)Dispose(solid);}
    }
}
