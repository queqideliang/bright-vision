// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace BrightVision.Civil
{
    // Plain SI facts. Native metrics remain read-only references to the source
    // snapshot; parameter-prism quantities are never substituted for them.
    internal sealed class CivilWallSnapshot
    {
        public CivilSnapshot NativeSnapshot { get; set; }
        public string GeometryViewUniqueId { get; set; }
        public string GeometryViewName { get; set; }
        public string GeometryPhaseId { get; set; }
        public List<CivilWallFact> Walls { get; set; }
        public CivilWallRuleTemplate RuleTemplate { get; set; }
        public CivilWallSnapshot() { Walls = new List<CivilWallFact>(); GeometryViewUniqueId = GeometryViewName = GeometryPhaseId = String.Empty; }
    }

    internal sealed class CivilWallFact
    {
        public string ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string TypeUniqueId { get; set; }
        public string TypeName { get; set; }
        public string CurveKind { get; set; }
        public string BaseElevationSource { get; set; }
        public string TopConstraintId { get; set; }
        public bool? AttachedTop { get; set; }
        public bool? AttachedBottom { get; set; }
        public List<string> JoinedElementIds { get; set; }
        public List<string> EndpointJoinedElementIds { get; set; }
        public CivilMetric ParameterLength { get; set; }
        public CivilMetric ParameterHeight { get; set; }
        public CivilMetric ParameterWidth { get; set; }
        public CivilMetric NativeLength { get; set; }
        public CivilMetric NativeArea { get; set; }
        public CivilMetric NativeVolume { get; set; }
        public double? BaseElevationMetres { get; set; }
        public double? GeometryVolumeMetres3 { get; set; }
        public bool GeometryVerified { get; set; }
        public bool InterwallOverlapCheckComplete { get; set; }
        public int OverlapCandidateCount { get; set; }
        public List<string> IntersectingWallElementIds { get; set; }
        public double? SymmetricDifferenceMetres3 { get; set; }
        public double? GeometryToleranceMetres3 { get; set; }
        public string Status { get; set; }
        public List<string> Issues { get; set; }
        public List<CivilWallInsert> Inserts { get; set; }
        public CivilWallFact()
        {
            ElementId = UniqueId = Name = TypeUniqueId = TypeName = CurveKind = BaseElevationSource = TopConstraintId = String.Empty;
            Status = "unsupported";
            JoinedElementIds = new List<string>(); EndpointJoinedElementIds = new List<string>();
            ParameterLength = MissingLength(); ParameterHeight = MissingLength(); ParameterWidth = MissingLength();
            NativeLength = MissingLength(); NativeArea = new CivilMetric { Unit = "m2" }; NativeVolume = new CivilMetric { Unit = "m3" };
            Issues = new List<string>(); Inserts = new List<CivilWallInsert>();
            IntersectingWallElementIds = new List<string>();
        }
        private static CivilMetric MissingLength() { return new CivilMetric { Unit = "m" }; }
    }

    internal sealed class CivilWallInsert
    {
        public string ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string HostUniqueId { get; set; }
        public string Kind { get; set; }
        public string CutoutMethod { get; set; }
        public string Status { get; set; }
        public string Source { get; set; }
        public double? RectX0 { get; set; }
        public double? RectX1 { get; set; }
        public double? RectZ0 { get; set; }
        public double? RectZ1 { get; set; }
        public List<string> Issues { get; set; }
        public CivilWallInsert()
        {
            ElementId = UniqueId = Name = HostUniqueId = Source = String.Empty;
            Kind = "other"; CutoutMethod = "native-opening"; Status = "unsupported"; Issues = new List<string>();
        }
    }
}
