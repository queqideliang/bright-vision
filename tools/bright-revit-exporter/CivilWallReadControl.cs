// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;

namespace BrightVision.Civil
{
    internal sealed class CivilWallReadControl
    {
        internal Func<bool> IsCancellationRequested;
        internal Action<string, int, int> Progress;
        internal void Checkpoint(string stage, int completed, int total)
        {
            ThrowIfCancelled();
            if (Progress != null) Progress(stage, completed, total);
            ThrowIfCancelled();
        }
        internal void ThrowIfCancelled()
        { if (IsCancellationRequested != null && IsCancellationRequested()) throw new OperationCanceledException("本轮墙体计算已取消；没有提交半批结果。"); }
    }
    internal delegate CivilWallSnapshot CivilWallRuleReader(CivilReadRequest request, string geometryViewUniqueId, CivilWallRuleTemplate template, CivilWallReadControl control);
}
