// Copyright 2026 崛亮视觉 (Bright Vision).
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace BrightVision.CivilWallHostAcceptance
{
    // Test-only comparison of JavaScriptSerializer.DeserializeObject trees.
    // Only object property order is ignored; array order and CLR scalar types matter.
    internal static class CivilSnapshotComparison
    {
        internal static string CompareJson(JavaScriptSerializer serializer, string before, string after)
        {
            if (serializer == null) throw new ArgumentNullException("serializer");
            return FirstDifference(serializer.DeserializeObject(before), serializer.DeserializeObject(after));
        }

        internal static string FirstDifference(object before, object after)
        {
            return FirstDifference(before, after, "$");
        }

        private static string FirstDifference(object before, object after, string path)
        {
            if (before == null || after == null) return before == after ? null : path;
            if (before.GetType() != after.GetType()) return path;

            Dictionary<string, object> beforeObject = before as Dictionary<string, object>;
            if (beforeObject != null)
            {
                Dictionary<string, object> afterObject = (Dictionary<string, object>)after;
                List<string> beforeKeys = new List<string>(beforeObject.Keys);
                List<string> afterKeys = new List<string>(afterObject.Keys);
                beforeKeys.Sort(StringComparer.Ordinal);
                afterKeys.Sort(StringComparer.Ordinal);
                int beforeIndex = 0;
                int afterIndex = 0;
                while (beforeIndex < beforeKeys.Count || afterIndex < afterKeys.Count)
                {
                    if (beforeIndex == beforeKeys.Count) return PropertyPath(path, afterKeys[afterIndex]);
                    if (afterIndex == afterKeys.Count) return PropertyPath(path, beforeKeys[beforeIndex]);
                    string beforeKey = beforeKeys[beforeIndex];
                    string afterKey = afterKeys[afterIndex];
                    int order = StringComparer.Ordinal.Compare(beforeKey, afterKey);
                    if (order < 0) return PropertyPath(path, beforeKey);
                    if (order > 0) return PropertyPath(path, afterKey);
                    string difference = FirstDifference(beforeObject[beforeKey], afterObject[afterKey], PropertyPath(path, beforeKey));
                    if (difference != null) return difference;
                    beforeIndex++;
                    afterIndex++;
                }
                return null;
            }

            object[] beforeArray = before as object[];
            if (beforeArray != null)
            {
                object[] afterArray = (object[])after;
                int commonLength = Math.Min(beforeArray.Length, afterArray.Length);
                for (int index = 0; index < commonLength; index++)
                {
                    string difference = FirstDifference(beforeArray[index], afterArray[index], IndexPath(path, index));
                    if (difference != null) return difference;
                }
                // A missing or added array item is identified by its first absent index.
                return beforeArray.Length == afterArray.Length ? null : IndexPath(path, commonLength);
            }

            return before.Equals(after) ? null : path;
        }

        private static string IndexPath(string path, int index)
        {
            return path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        private static string PropertyPath(string path, string key)
        {
            if (IsIdentifier(key)) return path + "." + key;
            StringBuilder escaped = new StringBuilder(path).Append("[\"");
            foreach (char value in key)
            {
                switch (value)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (value < 32) escaped.Append("\\u").Append(((int)value).ToString("x4", CultureInfo.InvariantCulture));
                        else escaped.Append(value);
                        break;
                }
            }
            return escaped.Append("\"]").ToString();
        }

        private static bool IsIdentifier(string key)
        {
            if (key.Length == 0 || !IsIdentifierStart(key[0])) return false;
            for (int index = 1; index < key.Length; index++)
                if (!IsIdentifierStart(key[index]) && (key[index] < '0' || key[index] > '9')) return false;
            return true;
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || value == '$' || (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }
    }
}
