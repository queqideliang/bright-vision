using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Export;

[assembly: AssemblyTitle("BV Revit Exporter")]
[assembly: AssemblyProduct("bright vision")]
[assembly: AssemblyCompany("崛亮视觉")]
[assembly: AssemblyCopyright("Copyright 2026 崛亮视觉 (Bright Vision)")]
[assembly: AssemblyVersion("1.11.0.0")]
[assembly: AssemblyFileVersion("1.11.0.0")]
#if BRIGHT_NET10
[assembly: TargetFramework(".NETCoreApp,Version=v10.0")]
#elif BRIGHT_NET8
[assembly: TargetFramework(".NETCoreApp,Version=v8.0")]
#elif BRIGHT_NET48
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
#else
[assembly: TargetFramework(".NETFramework,Version=v4.5")]
#endif

namespace BrightVision.Revit
{
    public sealed class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RevitCivilQuantityReader.Attach(application);
                try { application.CreateRibbonTab("BV"); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                RibbonPanel panel = application.CreateRibbonPanel("BV", "bright vision");
                PushButtonData quantities = new PushButtonData("BrightVision.CivilQuantities", "土建原始量\n工作台", Assembly.GetExecutingAssembly().Location, typeof(CivilQuantityCommand).FullName);
                quantities.ToolTip = "在 Revit 内读取墙、梁、柱、板的原生参数量，按范围和原始楼层核对、检查模型并导出 CSV。尚不包含清单或定额扣减。";
                quantities.AvailabilityClassName = typeof(ProjectAvailability).FullName;
                quantities.Image = Branding.Icon(16);
                quantities.LargeImage = Branding.Icon(32);
                panel.AddItem(quantities);
                PushButtonData joins = new PushButtonData("BrightVision.CivilWallJoins", "墙体交接\n体积核算", Assembly.GetExecutingAssembly().Location, typeof(CivilWallJoinCommand).FullName);
                joins.ToolTip = "完整读取请求墙及范围外邻墙，按明确确认的优先顺序分配重叠体积，逐条扣减并核对组件并集守恒。当前为部分普通直墙的项目约定；未支持形状不出量。";
                joins.AvailabilityClassName = typeof(ProjectAvailability).FullName; joins.Image = Branding.Icon(16); joins.LargeImage = Branding.Icon(32); panel.AddItem(joins);
                PushButtonData wall = new PushButtonData("BrightVision.CivilWallTrial", "墙体试算\n与台账", Assembly.GetExecutingAssembly().Location, typeof(CivilWallCommand).FullName);
                wall.ToolTip = "崛亮视觉 · Bright Vision：部分普通直墙的单侧轴向投影面积、体积、洞口并集台账，按计量层／施工段汇总并导出。实体校验通过才出量；不是模板、抹灰或地区清单／定额量。";
                wall.AvailabilityClassName = typeof(ProjectAvailability).FullName; wall.Image = Branding.Icon(16); wall.LargeImage = Branding.Icon(32); panel.AddItem(wall);
                PushButtonData button = new PushButtonData("BrightVision.Export", "本地导出\n模型 ZIP", Assembly.GetExecutingAssembly().Location, typeof(ExportCommand).FullName);
                button.ToolTip = "选择三维视图、保存位置和导出内容，生成独立本地模型 ZIP；按 Esc 取消导出。";
                button.AvailabilityClassName = typeof(ProjectAvailability).FullName;
                button.Image = Branding.Icon(16);
                button.LargeImage = Branding.Icon(32);
                panel.AddItem(button);
                PushButtonData updates = new PushButtonData("BrightVision.CheckUpdates", "检查\n更新", Assembly.GetExecutingAssembly().Location, typeof(PluginUpdateCommand).FullName);
                updates.ToolTip = "检查崛亮视觉官方插件版本，下载并校验更新。安装时需先保存工程并退出 Revit；不上传模型数据。"; updates.AvailabilityClassName = typeof(PluginSupportAvailability).FullName; updates.Image = Branding.Icon(16); panel.AddItem(updates);
                PushButtonData help = new PushButtonData("BrightVision.Help", "功能介绍\n使用指南", Assembly.GetExecutingAssembly().Location, typeof(PluginHelpCommand).FullName);
                help.ToolTip = "离线查看各功能用途、操作顺序、结果口径与入门步骤。"; help.AvailabilityClassName = typeof(PluginSupportAvailability).FullName; help.Image = Branding.Icon(16); panel.AddItem(help);
                PushButtonData about = new PushButtonData("BrightVision.About", "关于\n崛亮视觉", Assembly.GetExecutingAssembly().Location, typeof(AboutCommand).FullName);
                about.ToolTip = "崛亮视觉 · Bright Vision 社区版；版权与 Apache-2.0 开源许可。"; about.Image = Branding.Icon(16); panel.AddItem(about);
                return Result.Succeeded;
            }
            catch (Exception error)
            {
                RevitCivilQuantityReader.Detach(application);
                TaskDialog.Show("BV · bright vision", "无法创建插件入口：\n" + error.Message); return Result.Failed;
            }
        }
        public Result OnShutdown(UIControlledApplication application) { RevitCivilQuantityReader.Detach(application); return Result.Succeeded; }
    }

    // Revit rejects an empty same-name user manifest. An installer-owned mask
    // can register this inert application to supersede an old machine manifest
    // without loading its exporter or creating another ribbon entry.
    public sealed class LegacyMaskApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application) { return Result.Succeeded; }
        public Result OnShutdown(UIControlledApplication application) { return Result.Succeeded; }
    }

    public sealed class ProjectAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication application, CategorySet categories)
        {
            if (application.ActiveUIDocument == null) return false;
            return !application.ActiveUIDocument.Document.IsFamilyDocument;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            TaskDialog.Show("崛亮视觉 · Bright Vision", "BV 1.11.0 社区版开发预览\nCopyright 2026 崛亮视觉 (Bright Vision)\n\n自有社区源码采用 Apache License 2.0；保留版权、NOTICE 及第三方许可。品牌不代表分支版本获得官方背书。\n\n包含模型导出、原生量核对、工程归属设置和部分普通墙的面积／体积试算、分组与台账。功能介绍和检查更新可直接从 BV 页签打开；尚不是完整地区清单／定额算量产品。\n\n已安装许可文件：" + Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument ui = commandData.Application.ActiveUIDocument;
            Document document = ui == null ? null : ui.Document;
            if (document == null || !document.IsValidObject || document.IsFamilyDocument)
            { TaskDialog.Show("BV · bright vision", "请打开一个工程模型，再执行导出。"); return Result.Cancelled; }
            string output = null;
            ExportContext context = null;
            try
            {
                ExportOptions options = ExportOptionsDialog.Show(ui);
                if (options == null) return Result.Cancelled;
                output = options.OutputPath;
                View3D view = SelectedViewResolver.Resolve(document, options.View);
                options.View = view;
                Stopwatch elapsed = Stopwatch.StartNew();
                using (PackageWriter package = new PackageWriter(output))
                {
                    context = new ExportContext(document, view, package, options);
                    using (CustomExporter exporter = new CustomExporter(document, context))
                    {
#if !REVIT_2015
                        exporter.IncludeGeometricObjects = false;
#endif
                        exporter.ShouldStopOnError = true;
#if REVIT_2020_OR_GREATER
                        exporter.Export((View)view);
#else
                        exporter.Export(view);
#endif
                    }
                    if (context.Cancelled) throw new OperationCanceledException();
                    context.WriteAttachments();
                    if (context.IsCanceled()) throw new OperationCanceledException();
                    SourceModelSize sourceSize = ReadSourceSize(document);
                    bool modified = document.IsModified;
                    Dictionary<string, object> manifest = context.Manifest();
                    manifest["exportStatistics"] = ExportStatistics.SourceManifest(sourceSize, modified);
                    package.Complete(manifest);
                    long? zipBytes = ExportStatistics.ReadFileSize(output);
                    elapsed.Stop();
                    TaskDialog.Show("BV · bright vision", "模型 ZIP 已保存：\n" + output + "\n构件记录：" + package.ComponentCount + "，几何：" + package.GeometryCount
                        + "，三角面（含放置）：" + package.TriangleCount + "\n" + context.ContentSummary()
                        + "\n总耗时：" + elapsed.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒"
                        + "。\n\n" + ExportStatistics.Summary(sourceSize, modified, zipBytes, package.ModelJsonBytes, package.AssetBytes)
                        + (package.Warnings.Count > 0 ? "\n导出提示：" + String.Join("；", package.Warnings.ToArray()) : ""));
                }
                return Result.Succeeded;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException || (context != null && context.Cancelled)) return Result.Cancelled;
                message = error.Message;
                TaskDialog.Show("BV · bright vision", "导出未完成，未替换原有模型包。\n" + error.Message);
                return Result.Failed;
            }
        }

        private static SourceModelSize ReadSourceSize(Document document)
        {
            try
            {
                // The 2015 base API metadata lacks this property despite later
                // XML docs labelling it "since 2015". Probe it when available;
                // URI/non-filesystem PathName values are also rejected below.
                PropertyInfo cloud = typeof(Document).GetProperty("IsModelInCloud", BindingFlags.Public | BindingFlags.Instance);
                bool isCloud = cloud != null && (bool)cloud.GetValue(document, null);
                return ExportStatistics.ReadSavedRvt(document.PathName, isCloud);
            }
            catch { return new SourceModelSize("unavailable", null); }
        }

    }

    internal sealed class ExportContext : IExportContext
    {
        private const double Metres = 0.3048;
        private readonly Document root;
        private readonly View3D view;
        private readonly PackageWriter writer;
        private readonly ExportOptions options;
        private readonly ExportAttachments attachments;
        private Document document;
        private Transform transform = Transform.Identity;
        // A Level belongs to its document, independent of family geometry.
        private Transform documentTransform = Transform.Identity;
        private string linkScope = "root";
        private readonly Stack<Transform> transforms = new Stack<Transform>();
        private readonly Stack<LinkFrame> links = new Stack<LinkFrame>();
        private readonly Stack<ElementFrame> elements = new Stack<ElementFrame>();
        private readonly Dictionary<string, string> propertyCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> instanceSnapshots = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Queue<KeyValuePair<string, int>> snapshotOrder = new Queue<KeyValuePair<string, int>>();
        private int snapshotCharacters;
        private long snapshotReads;
        private readonly Dictionary<string, TypeRecord> typeCache = new Dictionary<string, TypeRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, Level> levelCache = new Dictionary<string, Level>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<Document, string> documentScopes = new Dictionary<Document, string>();
        private string material = "default";
        private bool completeLinks = true;
        private bool rpcSkipped;
        internal bool Cancelled { get; private set; }
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        internal ExportContext(Document document, View3D view, PackageWriter writer, ExportOptions options)
        {
            root = this.document = document; this.view = view; this.writer = writer; this.options = options;
            attachments = new ExportAttachments(writer, options, IsCanceled);
            attachments.CaptureDocument(document, DocumentScope(document), linkScope, documentTransform);
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)))
                if (((RevitLinkInstance)element).GetLinkDocument() == null) completeLinks = false;
            if (!completeLinks) writer.Warnings.Add("存在未加载的链接模型，未包含其几何");
        }

        internal Dictionary<string, object> Manifest()
        {
            return PackageWriter.Fields("schema", "Bright.Vision.Model/1", "product", "bright vision", "publisher", "崛亮视觉", "exporterVersion", "1.11.0",
                "revitVersion", root.Application.VersionNumber, "revitBuild", root.Application.VersionBuild,
                "modelName", options.ModelName, "sourceDocument", root.Title, "documentScope", DocumentScope(root), "viewUniqueId", view.UniqueId,
                "viewName", view.Name, "scope", "visible-selected-3d-view", "linkedModels", completeLinks,
                "rpcGeometry", !rpcSkipped, "matrixLayout", "column-major", "coordinates", "Revit internal axes; metres; node local-anchor transforms",
                "exportMethod", "CustomExporter/IExportContext", "independentExporter", true,
                "exportOptions", attachments.OptionManifest(), "exportContent", attachments.CountManifest(),
                "quantitySnapshot", PackageWriter.Fields("schemaVersion", 1, "source", "revit-parameters", "scope", "exported-components", "units", "SI"),
                "instanceParameterReads", snapshotReads, "cachedInstanceSnapshots", instanceSnapshots.Count,
                "snapshotCacheEstimatedCharacters", snapshotCharacters);
        }

        internal void WriteAttachments() { attachments.Write(root); }
        internal string ContentSummary() { return attachments.Summary(); }

        public bool Start() { return true; }
        public void Finish() { }
        public bool IsCanceled() { Cancelled |= (GetAsyncKeyState(0x1b) & 0x8000) != 0; return Cancelled; }
        public RenderNodeAction OnViewBegin(ViewNode node) { return RenderNodeAction.Proceed; }
        public void OnViewEnd(ElementId id) { }
        public RenderNodeAction OnFaceBegin(FaceNode node) { return RenderNodeAction.Proceed; }
        public void OnFaceEnd(FaceNode node) { }
        public void OnLight(LightNode node) { }
#if REVIT_2015 || REVIT_2016
        public void OnDaylightPortal(DaylightPortalNode node) { }
#endif
        public void OnRPC(RPCNode node)
        {
            if (elements.Count > 0 && elements.Peek().Skipped) return;
            if (!rpcSkipped) writer.Warnings.Add("RPC 内容未转为三角网格");
            rpcSkipped = true;
        }

        public RenderNodeAction OnElementBegin(ElementId id)
        {
            Element element = document == null ? null : document.GetElement(id);
            bool parentSkipped = elements.Count > 0 && elements.Peek().Skipped;
            ElementFrame frame = new ElementFrame { Skipped = parentSkipped }; elements.Push(frame);
            if (element == null) return RenderNodeAction.Proceed;
            // Keep begin/end pairing even on hosts whose Skip handling differs.
            // Omit all records and nested geometry in an unchecked CAD branch.
            if (parentSkipped || (element is ImportInstance && !options.IncludeCadLinks)) { frame.Skipped = true; return RenderNodeAction.Proceed; }
            frame.Element = element;
            string scope = DocumentScope(document);
            string seed = "e-" + PackageWriter.Hash(scope + "|" + linkScope + "|" + element.UniqueId).Substring(0, 32);
            int count; occurrences.TryGetValue(seed, out count); occurrences[seed] = count + 1;
            frame.Id = count == 0 ? seed : seed + "-" + count;
            frame.Scope = scope;
            frame.LinkScope = linkScope;
            frame.Category = element.Category == null ? "未分类" : element.Category.Name;
            frame.Nodes = new List<object>();
            string typeKey = scope + "|" + IdString(element.GetTypeId());
            TypeRecord typeRecord;
            if (!typeCache.TryGetValue(typeKey, out typeRecord))
            {
                Element type = document.GetElement(element.GetTypeId());
                FamilySymbol symbol = type as FamilySymbol;
                typeRecord = new TypeRecord { Element = type, Family = symbol == null ? null : symbol.Family,
                    Name = type == null ? "" : type.Name, FamilyName = symbol != null && symbol.Family != null ? symbol.Family.Name : StringProperty(type, "FamilyName") };
                typeCache.Add(typeKey, typeRecord);
            }
            frame.TypeName = typeRecord.Name;
            frame.FamilyName = typeRecord.FamilyName;
            frame.InstanceProperties = "p-" + frame.Id;
            string snapshotKey = scope + "|" + element.UniqueId;
            Dictionary<string, object> snapshot;
            if (!instanceSnapshots.TryGetValue(snapshotKey, out snapshot))
            {
                snapshot = Parameters(element); snapshotReads++;
                int cost = EstimateCharacters(snapshot);
                // Bound retained parameter data. Large models must not keep a
                // full duplicate of every exported property's object graph.
                if (cost <= 65536)
                {
                    while (snapshotOrder.Count > 0 && (instanceSnapshots.Count >= 256 || snapshotCharacters + cost > 4 * 1024 * 1024))
                    {
                        KeyValuePair<string, int> old = snapshotOrder.Dequeue();
                        instanceSnapshots.Remove(old.Key); snapshotCharacters -= old.Value;
                    }
                    instanceSnapshots.Add(snapshotKey, snapshot); snapshotOrder.Enqueue(new KeyValuePair<string, int>(snapshotKey, cost)); snapshotCharacters += cost;
                }
            }
            Dictionary<string, object> props = new Dictionary<string, object>(snapshot, StringComparer.Ordinal);
            props["来源标识"] = PackageWriter.Fields("UniqueId", element.UniqueId, "ElementId", IdString(element.Id), "DocumentScope", scope, "LinkInstanceScope", linkScope);
            writer.AddProperties(frame.InstanceProperties, props);
            frame.TypeProperties = SharedProperties(typeRecord.Element, scope, "type");
            frame.FamilyProperties = SharedProperties(typeRecord.Family, scope, "family");
            string levelKey = scope + "|" + IdString(element.LevelId);
            Level level;
            if (!levelCache.TryGetValue(levelKey, out level)) { level = document.GetElement(element.LevelId) as Level; levelCache.Add(levelKey, level); }
            frame.Level = level == null ? "未指定楼层" : level.Name;
            if (!document.Equals(root)) frame.Level = document.Title + " · " + frame.Level;
            frame.Elevation = level == null ? (double?)null : PackageWriter.HorizontalLevelElevation(Matrix(documentTransform), level.Elevation * Metres);
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId id)
        {
            if (elements.Count == 0) throw new InvalidDataException("Unbalanced element callbacks.");
            ElementFrame frame = elements.Pop();
            if (frame.Element == null || frame.Skipped) return;
            writer.AddComponent(PackageWriter.Fields("Id", frame.Id, "name", frame.Element.Name, "type", frame.Category,
                "BuildInCategory", frame.Element.Category == null ? "" : IdString(frame.Element.Category.Id),
                "familyName", frame.FamilyName, "familySymbol", frame.TypeName, "matrix", PackageWriter.Identity(), "Nodes", frame.Nodes,
                "Property", frame.InstanceProperties, "SymbolProperty", frame.TypeProperties, "FamilyProperty", frame.FamilyProperties,
                "QuantitySnapshot", RevitQuantityReader.Capture(frame.Element, IdString(frame.Element.Id), frame.Scope, frame.LinkScope)), frame.Level, frame.Elevation, frame.Category);
            attachments.RecordElement(frame.Element, frame.Id, frame.Scope, frame.LinkScope);
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        { transforms.Push(transform); transform = transform.Multiply(node.GetTransform()); return RenderNodeAction.Proceed; }
        public void OnInstanceEnd(InstanceNode node)
        { if (transforms.Count == 0) throw new InvalidDataException("Unbalanced instance callbacks."); transform = transforms.Pop(); }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            Document linked = node.GetDocument();
            links.Push(new LinkFrame { Document = document, Transform = transform, DocumentTransform = documentTransform, Scope = linkScope, Material = material });
            string instance = elements.Count > 0 && elements.Peek().Element != null ? elements.Peek().Element.UniqueId : node.NodeName + "|" + Json.Encode(Matrix(node.GetTransform()));
            linkScope = PackageWriter.Hash(linkScope + "|" + instance).Substring(0, 32);
            // Always Proceed so every pushed frame has a matching End callback.
            // A missing document creates an empty frame; geometry cannot bind
            // accidentally to elements in the parent document.
            if (linked == null) completeLinks = false;
            document = linked;
            documentTransform = documentTransform.Multiply(node.GetTransform());
            transform = transform.Multiply(node.GetTransform()); material = "default";
            if (linked != null) attachments.CaptureDocument(linked, DocumentScope(linked), linkScope, documentTransform);
            return RenderNodeAction.Proceed;
        }
        public void OnLinkEnd(LinkNode node)
        {
            if (links.Count == 0) throw new InvalidDataException("Unbalanced link callbacks.");
            LinkFrame old = links.Pop(); document = old.Document; transform = old.Transform; documentTransform = old.DocumentTransform; linkScope = old.Scope; material = old.Material;
        }

        public void OnMaterial(MaterialNode node)
        {
            if (elements.Count > 0 && elements.Peek().Skipped) return;
            Color color = node.Color;
            string rgb = String.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", color.Red, color.Green, color.Blue);
            double opacity = Math.Max(0, Math.Min(1, 1 - node.Transparency));
            Dictionary<string, object> data = PackageWriter.Fields("color", rgb, "opacity", opacity);
            if (document != null && options.IncludeTextures) attachments.ApplyTexture(document, node, data);
            material = "m-" + PackageWriter.Hash(Json.Encode(data)).Substring(0, 32);
            writer.AddMaterial(material, data);
        }

        public void OnPolymesh(PolymeshTopology mesh)
        {
            if (elements.Count == 0 || elements.Peek().Element == null || elements.Peek().Skipped) return;
            IList<XYZ> points = mesh.GetPoints(); IList<XYZ> normals = mesh.GetNormals(); IList<UV> uv = mesh.GetUVs();
            IList<PolymeshFacet> facets = mesh.GetFacets();
            DistributionOfNormals distribution = mesh.DistributionOfNormals;
            // Revit supplies one normal per point/facet/face. Normalize each
            // supplied value once, and only calculate a triangle normal when a
            // missing or zero normal actually needs a fallback.
            double[] normalized = new double[normals.Count * 3];
            bool[] validNormal = new bool[normals.Count];
            for (int i = 0; i < normals.Count; i++)
            {
                XYZ normal = normals[i];
                double length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
                if (length <= 1e-12) continue;
                validNormal[i] = true;
                normalized[i * 3] = normal.X / length; normalized[i * 3 + 1] = normal.Y / length; normalized[i * 3 + 2] = normal.Z / length;
            }
            for (int start = 0; start < facets.Count; start += 20000)
            {
                if (IsCanceled()) throw new OperationCanceledException();
                int count = Math.Min(20000, facets.Count - start);
                XYZ anchor = points[facets[start].V1];
                double[] positions = new double[count * 9], outputNormals = new double[count * 9], outputUv = new double[count * 6];
                int[] indices = new int[count * 3];
                for (int at = 0; at < count; at++)
                {
                    PolymeshFacet facet = facets[start + at];
                    double fallbackX = 0, fallbackY = 0, fallbackZ = 1;
                    bool fallbackReady = false;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vertex = corner == 0 ? facet.V1 : corner == 1 ? facet.V2 : facet.V3;
                        int target = at * 3 + corner; XYZ point = points[vertex];
                        positions[target * 3] = (point.X - anchor.X) * Metres; positions[target * 3 + 1] = (point.Y - anchor.Y) * Metres; positions[target * 3 + 2] = (point.Z - anchor.Z) * Metres;
                        int normalIndex = distribution == DistributionOfNormals.AtEachPoint ? vertex : distribution == DistributionOfNormals.OnEachFacet ? start + at : 0;
                        if (normalIndex >= 0 && normalIndex < normals.Count && validNormal[normalIndex])
                        {
                            outputNormals[target * 3] = normalized[normalIndex * 3]; outputNormals[target * 3 + 1] = normalized[normalIndex * 3 + 1]; outputNormals[target * 3 + 2] = normalized[normalIndex * 3 + 2];
                        }
                        else
                        {
                            if (!fallbackReady)
                            {
                                XYZ a = points[facet.V1], b = points[facet.V2], c = points[facet.V3];
                                double ax = b.X - a.X, ay = b.Y - a.Y, az = b.Z - a.Z, bx = c.X - a.X, by = c.Y - a.Y, bz = c.Z - a.Z;
                                double x = ay * bz - az * by, y = az * bx - ax * bz, z = ax * by - ay * bx;
                                double length = Math.Sqrt(x * x + y * y + z * z);
                                if (length > 1e-12) { fallbackX = x / length; fallbackY = y / length; fallbackZ = z / length; }
                                fallbackReady = true;
                            }
                            outputNormals[target * 3] = fallbackX; outputNormals[target * 3 + 1] = fallbackY; outputNormals[target * 3 + 2] = fallbackZ;
                        }
                        if (uv.Count == points.Count) { outputUv[target * 2] = uv[vertex].U; outputUv[target * 2 + 1] = uv[vertex].V; }
                        indices[target] = target;
                    }
                }
                string geometry = writer.AddGeometry(positions, indices, outputNormals, outputUv);
                double[] matrix = Matrix(transform.Multiply(Transform.CreateTranslation(anchor)));
                elements.Peek().Nodes.Add(writer.AddNode(geometry, material, matrix, count));
            }
        }

        private static double[] Matrix(Transform value)
        {
            return new double[] { value.BasisX.X, value.BasisX.Y, value.BasisX.Z, 0, value.BasisY.X, value.BasisY.Y, value.BasisY.Z, 0,
                value.BasisZ.X, value.BasisZ.Y, value.BasisZ.Z, 0, value.Origin.X * Metres, value.Origin.Y * Metres, value.Origin.Z * Metres, 1 };
        }
        private static string IdString(ElementId id)
        {
#if REVIT_2024_OR_GREATER
            return id.Value.ToString(CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }
        private string DocumentScope(Document doc)
        {
            string existing;
            if (documentScopes.TryGetValue(doc, out existing)) return existing;
            string unique = doc.ProjectInformation == null ? doc.Title : doc.ProjectInformation.UniqueId;
            existing = PackageWriter.Hash(unique).Substring(0, 24);
            documentScopes.Add(doc, existing);
            return existing;
        }
        private static string StringProperty(object value, string name)
        {
            if (value == null) return "";
            PropertyInfo property = value.GetType().GetProperty(name);
            return property == null ? "" : Convert.ToString(property.GetValue(value, null), CultureInfo.InvariantCulture);
        }
        private string SharedProperties(Element element, string scope, string kind)
        {
            if (element == null) return "";
            string key = scope + "|" + kind + "|" + element.UniqueId;
            string id;
            if (!propertyCache.TryGetValue(key, out id))
            { id = "p-" + PackageWriter.Hash(key); propertyCache.Add(key, id); writer.AddProperties(id, Parameters(element)); }
            return id;
        }
        private static Dictionary<string, object> Parameters(Element element)
        {
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> raw = new Dictionary<string, object>(StringComparer.Ordinal);
            int count = 0;
            foreach (Parameter parameter in element.Parameters)
            {
                if (++count > 2000) throw new InvalidDataException("Element parameter limit exceeded.");
                if (parameter.Definition == null) continue;
                string name = parameter.Definition.Name;
                if (values.ContainsKey(name)) name += " [" + IdString(parameter.Id) + "]";
                object value = null;
                switch (parameter.StorageType)
                {
                    case StorageType.String: value = parameter.AsString(); break;
                    case StorageType.Integer: value = parameter.AsInteger(); break;
                    case StorageType.Double: raw[name] = parameter.AsDouble(); value = parameter.AsValueString() ?? parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture); break;
                    case StorageType.ElementId: value = parameter.AsValueString() ?? IdString(parameter.AsElementId()); break;
                }
                string text = value as string;
                if (text != null && text.Length > 4096) value = text.Substring(0, 4096);
                values[name] = value;
            }
            return PackageWriter.Fields("参数", values, "数值原始值（Revit 内部单位）", raw);
        }
        private static int EstimateCharacters(IDictionary<string, object> values)
        {
            int cost = 0;
            foreach (KeyValuePair<string, object> pair in values)
            {
                cost += pair.Key.Length + 16;
                string text = pair.Value as string;
                IDictionary<string, object> child = pair.Value as IDictionary<string, object>;
                if (text != null) cost += text.Length;
                else if (child != null) cost += EstimateCharacters(child);
                if (cost > 65536) return cost;
            }
            return cost;
        }
        private sealed class LinkFrame { internal Document Document; internal Transform Transform, DocumentTransform; internal string Scope; internal string Material; }
        private sealed class TypeRecord { internal Element Element, Family; internal string Name, FamilyName; }
        private sealed class ElementFrame
        {
            internal bool Skipped;
            internal Element Element; internal string Id, Scope, LinkScope, Category, FamilyName, TypeName, InstanceProperties, TypeProperties, FamilyProperties, Level;
            internal List<object> Nodes; internal double? Elevation;
        }
    }
}
