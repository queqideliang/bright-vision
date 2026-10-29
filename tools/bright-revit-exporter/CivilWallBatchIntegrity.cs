// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace BrightVision.Civil
{
    // A calculation is committed once. Reporting never blesses mutable results
    // again: it checks their original facts, rule snapshot and ledger each time.
    internal static class CivilWallBatchIntegrity
    {
        private sealed class SealState
        {
            internal string Id, Rule, Input, Results;
        }
        private static readonly ConditionalWeakTable<CivilWallCalculationBatch, SealState> Seals = new ConditionalWeakTable<CivilWallCalculationBatch, SealState>();
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new ConcurrentDictionary<Type, PropertyInfo[]>();
        internal static void Seal(CivilWallCalculationBatch batch)
        {
            if (batch == null || batch.Input == null || batch.Results == null) throw new InvalidDataException("缺少完整计算批次。");
            SealState existing;
            if (!String.IsNullOrEmpty(batch.BatchId) || Seals.TryGetValue(batch, out existing)) throw new InvalidDataException("计算批次已经封存，不能重新封存修改后的数据。");
            CivilWallRuleTemplate rule = CivilWallRuleTemplates.Clone(batch.Input.RuleTemplate ?? CivilWallRuleTemplates.Default());
            foreach (CivilWallResult row in batch.Results) ValidateResult(row);
            batch.RuleTemplate = rule; batch.RuleFingerprint = CivilWallRuleTemplates.Fingerprint(rule);
            batch.InputFingerprint = Digest(batch.Input); batch.ResultFingerprint = Digest(batch.Results);
            batch.BatchId = Guid.NewGuid().ToString("N");
            Seals.Add(batch, new SealState { Id = batch.BatchId, Rule = batch.RuleFingerprint, Input = batch.InputFingerprint, Results = batch.ResultFingerprint });
        }

        internal static void Validate(CivilWallCalculationBatch batch)
        {
            if (batch == null || batch.Input == null || batch.Results == null || String.IsNullOrEmpty(batch.BatchId) || batch.RuleTemplate == null)
                throw new InvalidDataException("计算批次未完整封存，请重新读取计算。");
            SealState original;
            if (!Seals.TryGetValue(batch, out original) || batch.BatchId != original.Id || batch.RuleFingerprint != original.Rule ||
                batch.InputFingerprint != original.Input || batch.ResultFingerprint != original.Results)
                throw new InvalidDataException("计算批次身份或封存指纹已变化，请重新读取计算。");
            if (CivilWallRuleTemplates.Fingerprint(batch.RuleTemplate) != batch.RuleFingerprint ||
                Digest(batch.Input) != batch.InputFingerprint || Digest(batch.Results) != batch.ResultFingerprint)
                throw new InvalidDataException("计算事实、规则或结果在封存后发生变化，请重新读取计算；旧结果不可导出或定位。");
            foreach (CivilWallResult row in batch.Results) ValidateResult(row);
        }

        internal static void ValidateResult(CivilWallResult row)
        {
            if (row == null || row.Fact == null || row.Ledger == null) throw new InvalidDataException("墙体结果或台账缺失。");
            if (row.Status != "calculated")
            {
                if ((row.Status != "unsupported" && row.Status != "failed") || row.Ledger.Count != 0 || row.BasisAreaMetres2.HasValue ||
                    row.ResultAreaMetres2.HasValue || row.BasisVolumeMetres3.HasValue || row.OpeningUnionAreaMetres2.HasValue ||
                    row.DeductionVolumeMetres3.HasValue || row.ResultVolumeMetres3.HasValue)
                    throw new InvalidDataException("未通过项不能保留试算量或台账。");
                return;
            }
            double area = Quantity(row.BasisAreaMetres2), volume = Quantity(row.BasisVolumeMetres3), opening = Quantity(row.OpeningUnionAreaMetres2);
            double remainingArea = Quantity(row.ResultAreaMetres2), deduction = Quantity(row.DeductionVolumeMetres3), remaining = Quantity(row.ResultVolumeMetres3);
            if (row.Ledger.Count == 0) throw new InvalidDataException("通过项缺少基准台账。");
            Near(area - opening, remainingArea, area); Near(volume - deduction, remaining, volume);
            double areaSum = 0, volumeSum = 0;
            for (int index = 0; index < row.Ledger.Count; index++)
            {
                CivilWallLedgerEntry entry = row.Ledger[index];
                if (entry == null || !Finite(entry.DeltaAreaMetres2) || !Finite(entry.DeltaVolumeMetres3) || !Finite(entry.AreaMetres2) || entry.AreaMetres2 < 0 ||
                    String.IsNullOrWhiteSpace(entry.ElementId) || String.IsNullOrWhiteSpace(entry.UniqueId) ||
                    String.IsNullOrWhiteSpace(entry.Formula) || String.IsNullOrWhiteSpace(entry.AreaFormula) || String.IsNullOrWhiteSpace(entry.Source))
                    throw new InvalidDataException("台账缺少有限变化值、关联身份、公式或来源。");
                Near(Math.Abs(entry.DeltaAreaMetres2), entry.AreaMetres2, area);
                if (index == 0)
                {
                    if (entry.Kind != "basis" || entry.ElementId != row.Fact.ElementId || entry.UniqueId != row.Fact.UniqueId)
                        throw new InvalidDataException("基准台账没有绑定当前墙体。");
                    Near(entry.DeltaAreaMetres2, area, area); Near(entry.DeltaVolumeMetres3, volume, volume);
                }
                else if (entry.Kind != "deduction" || entry.DeltaAreaMetres2 > 0 || entry.DeltaVolumeMetres3 > 0)
                    throw new InvalidDataException("扣减台账符号或步骤类型无效。");
                areaSum += entry.DeltaAreaMetres2; volumeSum += entry.DeltaVolumeMetres3;
                if (!Finite(areaSum) || !Finite(volumeSum)) throw new InvalidDataException("台账累计溢出。");
            }
            Near(areaSum, remainingArea, area); Near(volumeSum, remaining, volume);
        }

        private static double Quantity(double? value)
        { if (!value.HasValue || !Finite(value.Value) || value.Value < 0) throw new InvalidDataException("计算量值缺失、非有限或为负。"); return value.Value; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static void Near(double actual, double expected, double scale)
        {
            if (!Finite(actual) || !Finite(expected) || Math.Abs(actual - expected) > Math.Max(1e-10, Math.Abs(scale) * 1e-10))
                throw new InvalidDataException("基准、扣减、结果或带符号台账不守恒，未生成可用报表。");
        }

        internal static string SnapshotFingerprint(CivilSnapshot snapshot) { return Digest(snapshot); }
        internal static string FactsFingerprint(object facts) { return Digest(facts); }
        private static string Digest(object value)
        {
            using (SHA256 hash = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var text = new StreamWriter(stream, new UTF8Encoding(false), 65536))
            {
                Write(text, value, 0); text.Flush(); stream.FlushFinalBlock();
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        // Length-prefixed, type-tagged, fixed-property-order representation.
        // Reflection is restricted to our plain fact DTOs; it never invokes a
        // user expression, deserialized type or application method.
        private static void Write(TextWriter text, object value, int depth)
        {
            if (depth > 20) throw new InvalidDataException("计算事实嵌套超出限制。");
            if (value == null) { text.Write("N;"); return; }
            Type type = value.GetType();
            if (value is string) { string item = (string)value; text.Write("S"); text.Write(item.Length.ToString(CultureInfo.InvariantCulture)); text.Write(':'); text.Write(item); text.Write(';'); return; }
            if (value is double) { text.Write("D"); text.Write(((double)value).ToString("R", CultureInfo.InvariantCulture)); text.Write(';'); return; }
            if (value is bool) { text.Write((bool)value ? "B1;" : "B0;"); return; }
            if (value is int || value is long) { text.Write(type.Name); text.Write(':'); text.Write(Convert.ToString(value, CultureInfo.InvariantCulture)); text.Write(';'); return; }
            IEnumerable list = value as IEnumerable;
            if (list != null) { text.Write("["); foreach (object item in list) Write(text, item, depth + 1); text.Write("]"); return; }
            if (type.Assembly != typeof(CivilWallSnapshot).Assembly || type.Namespace != "BrightVision.Civil")
                throw new InvalidDataException("未知的计算事实类型，未执行其属性。");
            text.Write('{'); text.Write(type.Name); text.Write(';');
            foreach (PropertyInfo property in Properties.GetOrAdd(type, key => key.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray()))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) throw new InvalidDataException("不支持的计算事实属性。");
                text.Write(property.Name); text.Write(':'); Write(text, property.GetValue(value, null), depth + 1);
            }
            text.Write('}');
        }
    }
}
