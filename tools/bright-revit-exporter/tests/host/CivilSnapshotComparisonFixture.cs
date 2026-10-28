// Copyright 2026 崛亮视觉 (Bright Vision).
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace BrightVision.CivilWallHostAcceptance
{
    internal static class CivilSnapshotComparisonFixture
    {
        // Representative serialized native DTO: a 10m wall with one 2m2 opening,
        // native area 28m2 and native volume 5.6m3. No production/API types are loaded.
        private const string OneOpeningSnapshotJson = @"{
            ""Elements"":[{
                ""ElementId"":""1001"",""UniqueId"":""wall-one-opening"",""Name"":""One opening wall"",
                ""Category"":""Walls"",""KindCode"":""wall"",""KindName"":""Wall"",""Family"":""Basic Wall"",
                ""TypeName"":""200mm concrete"",""LevelId"":""2001"",""LevelName"":""Level 1"",
                ""RawLevelId"":""2001"",""RawLevelName"":""Level 1"",""LevelUniqueId"":""level-1"",""LevelSource"":""base-level"",
                ""QuantityLevelId"":""2001"",""QuantityLevelName"":""Level 1"",""SegmentId"":""segment-a"",""SegmentName"":""Segment A"",
                ""PhaseStatus"":""existing"",""DesignOptionName"":""Main Model"",""ExclusionReason"":"""",""Supported"":true,
                ""Issues"":[],""ProjectIssues"":[],
                ""Length"":{""Status"":""available"",""Value"":10.0,""Unit"":""m"",""Source"":""CURVE_ELEM_LENGTH""},
                ""Area"":{""Status"":""available"",""Value"":28.0,""Unit"":""m2"",""Source"":""HOST_AREA_COMPUTED""},
                ""Volume"":{""Status"":""available"",""Value"":5.6,""Unit"":""m3"",""Source"":""HOST_VOLUME_COMPUTED""}
            }],
            ""Exclusions"":[],""Notices"":[""Native instance quantities"",""One opening fixture""],""ProjectNotices"":[],
            ""ModelTitle"":""One opening fixture"",""DocumentScope"":""document-a"",""SourcePath"":""H:\\fixtures\\one-opening.rvt"",
            ""Levels"":[{""Id"":""2001"",""UniqueId"":""level-1"",""Name"":""Level 1"",""ElevationMetres"":0.0},
                          {""Id"":""2002"",""UniqueId"":""level-2"",""Name"":""Level 2"",""ElevationMetres"":3.0}],
            ""ProjectId"":""project-a"",""ProjectName"":""Wall comparison fixture"",""ProjectRevision"":3,
            ""CapturedAtUtc"":""2026-10-28T00:00:00Z"",""ScopeLabel"":""Active view"",""PhaseLabel"":""Existing"",""DesignOptionLabel"":""Main Model"",
            ""CandidateCount"":1,""LinkInstanceCount"":0,""MissingLinkCount"":0,""Revision"":17
        }";

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly List<Dictionary<string, object>> Cases = new List<Dictionary<string, object>>();
        private static int assertions;
        private static int equalities;
        private static int differences;

        internal static string Run()
        {
            assertions = equalities = differences = 0;
            Cases.Clear();
            Dictionary<string, object> baseline = Read();
            Check(Convert.ToDecimal(Metric(baseline, "Volume")["Value"], CultureInfo.InvariantCulture) == 5.6m,
                "Fixture native volume must remain 5.6m3.");
            Check(((object[])baseline["Elements"]).Length == 1 && (string)Wall(baseline)["KindCode"] == "wall",
                "Fixture must contain exactly one native wall record.");
            Equal("identical-native-json", OneOpeningSnapshotJson, OneOpeningSnapshotJson);

            string rootReordered = Serializer.Serialize(ReverseProperties(baseline, false));
            Check(rootReordered != Serializer.Serialize(baseline), "Top-level key permutation did not change JSON text.");
            Equal("top-level-object-order", OneOpeningSnapshotJson, rootReordered);
            string allReordered = Serializer.Serialize(ReverseProperties(baseline, true));
            Check(allReordered != rootReordered, "Nested key permutation did not change nested JSON text.");
            Equal("all-nested-object-orders", OneOpeningSnapshotJson, allReordered);
            Equal("json-whitespace", "{\"Value\":1}", " { \"Value\" : 1 } ");
            Equal("empty-objects", "{}", "{}");
            Equal("empty-arrays", "[]", "[]");
            Equal("null-equality", "null", "null");

            Mutated("add-root-property", "$.Additional", delegate(Dictionary<string, object> value) { value["Additional"] = true; });
            Mutated("delete-root-property", "$.CandidateCount", delegate(Dictionary<string, object> value) { value.Remove("CandidateCount"); });
            Mutated("add-metric-property", "$.Elements[0].Volume.Additional", delegate(Dictionary<string, object> value) { Metric(value, "Volume")["Additional"] = 1; });
            Mutated("delete-metric-property", "$.Elements[0].Volume.Source", delegate(Dictionary<string, object> value) { Metric(value, "Volume").Remove("Source"); });
            Mutated("property-name-case", "$.Elements[0].Volume.Value", delegate(Dictionary<string, object> value)
            {
                Dictionary<string, object> metric = Metric(value, "Volume");
                metric["value"] = metric["Value"];
                metric.Remove("Value");
            });
            Mutated("notice-array-order", "$.Notices[0]", delegate(Dictionary<string, object> value) { Swap((object[])value["Notices"]); });
            Mutated("level-array-order", "$.Levels[0].ElevationMetres", delegate(Dictionary<string, object> value) { Swap((object[])value["Levels"]); });
            Mutated("element-array-addition", "$.Elements[1]", delegate(Dictionary<string, object> value)
            {
                object wall = ((object[])value["Elements"])[0];
                value["Elements"] = new object[] { wall, wall };
            });
            Mutated("element-array-removal", "$.Elements[0]", delegate(Dictionary<string, object> value) { value["Elements"] = new object[0]; });
            Mutated("notice-array-removal", "$.Notices[1]", delegate(Dictionary<string, object> value)
            { value["Notices"] = new object[] { ((object[])value["Notices"])[0] }; });
            Mutated("array-element-type", "$.Elements[0]", delegate(Dictionary<string, object> value) { value["Elements"] = new object[] { "wall-one-opening" }; });
            Mutated("array-container-type", "$.Notices", delegate(Dictionary<string, object> value) { value["Notices"] = "Native instance quantities"; });
            Mutated("volume-number-change", "$.Elements[0].Volume.Value", delegate(Dictionary<string, object> value) { Metric(value, "Volume")["Value"] = 5.7m; });
            Mutated("volume-number-small-change", "$.Elements[0].Volume.Value", delegate(Dictionary<string, object> value) { Metric(value, "Volume")["Value"] = 5.600000000000000000000000001m; });
            Mutated("revision-number-type", "$.Revision", delegate(Dictionary<string, object> value) { value["Revision"] = 17L; });
            Mutated("volume-number-type", "$.Elements[0].Volume.Value", delegate(Dictionary<string, object> value)
            {
                Dictionary<string, object> metric = Metric(value, "Volume");
                metric["Value"] = metric["Value"].GetType() == typeof(decimal) ? (object)5.6d : (object)5.6m;
            });
            Mutated("scalar-string", "$.ModelTitle", delegate(Dictionary<string, object> value) { value["ModelTitle"] = "Changed title"; });
            Mutated("scalar-bool", "$.Elements[0].Supported", delegate(Dictionary<string, object> value) { Wall(value)["Supported"] = false; });
            Mutated("scalar-null", "$.Elements[0].Volume.Value", delegate(Dictionary<string, object> value) { Metric(value, "Volume")["Value"] = null; });
            Mutated("scalar-string-type", "$.Elements[0].Supported", delegate(Dictionary<string, object> value) { Wall(value)["Supported"] = "true"; });

            Difference("json-number-type", "{\"Value\":1}", "{\"Value\":1.0}", "$.Value");
            Difference("root-null-to-value", "null", "0", "$");
            Difference("root-value-to-null", "false", "null", "$");
            Difference("root-type", "{}", "[]", "$");
            Difference("root-string-case", "\"Wall\"", "\"wall\"", "$");
            Difference("root-bool", "true", "false", "$");
            Difference("nested-null-to-value", "{\"Value\":null}", "{\"Value\":0}", "$.Value");
            Difference("array-order", "[1,2]", "[2,1]", "$[0]");
            Difference("array-extra-null", "[]", "[null]", "$[0]");
            Difference("array-earlier-value-before-length", "[1]", "[2,3]", "$[0]");
            Difference("ordinal-property-order", "{\"z\":0,\"a\":0,\"A\":0}", "{\"a\":1,\"z\":1,\"A\":1}", "$.A");
            Difference("ordinal-key-union", "{\"z\":0,\"a\":0}", "{\"a\":1,\"Z\":1}", "$.Z");
            Difference("depth-first-before-later-key-removal", "{\"Z\":1,\"A\":{\"Value\":1}}", "{\"A\":{\"Value\":2}}", "$.A.Value");
            Difference("special-key-path", "{\"a.b\":{\"quote\\\"slash\\\\\\n\":1}}", "{\"a.b\":{\"quote\\\"slash\\\\\\n\":2}}", "$[\"a.b\"][\"quote\\\"slash\\\\\\n\"]");
            Difference("empty-key-path", "{\"\":1}", "{\"\":2}", "$[\"\"]");
            Difference("numeric-key-path", "{\"1field\":1}", "{\"1field\":2}", "$[\"1field\"]");
            DifferenceObjects("int-versus-long", 1, 1L, "$");
            DifferenceObjects("decimal-versus-double", 5.6m, 5.6d, "$");

            CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                Difference("ordinal-independent-of-culture", "{\"i\":0,\"I\":0}", "{\"i\":1,\"I\":1}", "$.I");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }

            return Serializer.Serialize(new Dictionary<string, object>
            {
                { "Result", "PASS" }, { "Assertions", assertions }, { "EqualityCases", equalities },
                { "DifferenceCases", differences }, { "Fixture", "one-opening-native-dto" }, { "NativeVolume", 5.6m }, { "Cases", Cases }
            });
        }

        private static Dictionary<string, object> Read()
        { return (Dictionary<string, object>)Serializer.DeserializeObject(OneOpeningSnapshotJson); }

        private static Dictionary<string, object> Wall(Dictionary<string, object> value)
        { return (Dictionary<string, object>)((object[])value["Elements"])[0]; }

        private static Dictionary<string, object> Metric(Dictionary<string, object> value, string metric)
        { return (Dictionary<string, object>)Wall(value)[metric]; }

        private static void Swap(object[] values)
        { object first = values[0]; values[0] = values[1]; values[1] = first; }

        private static object ReverseProperties(object value, bool recursive)
        {
            Dictionary<string, object> source = value as Dictionary<string, object>;
            if (source != null)
            {
                Dictionary<string, object> reordered = new Dictionary<string, object>();
                List<string> keys = new List<string>(source.Keys);
                keys.Reverse();
                foreach (string key in keys) reordered.Add(key, recursive ? ReverseProperties(source[key], true) : source[key]);
                return reordered;
            }
            object[] array = value as object[];
            if (array == null || !recursive) return value;
            object[] reorderedArray = new object[array.Length];
            for (int index = 0; index < array.Length; index++) reorderedArray[index] = ReverseProperties(array[index], true);
            return reorderedArray;
        }

        private static void Mutated(string name, string expectedPath, Action<Dictionary<string, object>> mutate)
        {
            Dictionary<string, object> before = Read();
            Dictionary<string, object> after = Read();
            mutate(after);
            DifferenceObjects(name, before, after, expectedPath);
        }

        private static void Equal(string name, string before, string after)
        {
            string actual = CivilSnapshotComparison.CompareJson(Serializer, before, after);
            Check(actual == null, name + " should compare equal; got " + actual);
            equalities++;
            Cases.Add(new Dictionary<string, object> { { "Name", name }, { "Difference", null } });
        }

        private static void Difference(string name, string before, string after, string expectedPath)
        { DifferenceObjects(name, Serializer.DeserializeObject(before), Serializer.DeserializeObject(after), expectedPath); }

        private static void DifferenceObjects(string name, object before, object after, string expectedPath)
        {
            string actual = CivilSnapshotComparison.FirstDifference(before, after);
            Check(String.Equals(actual, expectedPath, StringComparison.Ordinal), name + " expected " + expectedPath + "; got " + (actual ?? "<equal>"));
            differences++;
            Cases.Add(new Dictionary<string, object> { { "Name", name }, { "Difference", actual } });
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); assertions++; }
    }
}
