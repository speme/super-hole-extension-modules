using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CustomTaskpane
{
    /// <summary>
    /// Stores the user's reference axes and target face for the assembly top-down hole workflow.
    /// Geometry is captured from the active document, then mapped through component transforms.
    /// </summary>
    internal static class AssemblyHoleAlignment
    {
        private sealed class AxisReference
        {
            internal Component2 Component;
            internal double[] Origin;
            internal double[] Direction;
            internal string Label;
        }

        private sealed class TargetPlane
        {
            internal Face2 Face;
            internal Component2 Component;
            internal double[] Origin;
            internal double[] Normal;
        }

        private sealed class SlotReference
        {
            internal Component2 Component;
            internal double[] End1;
            internal double[] End2;
            internal double[] Normal;
            internal double Width;
        }

        internal sealed class ThreadSpec
        {
            internal string Size;
            internal double NominalMm;
            internal double PitchMm;
            internal double TapDrillMm;
            internal int CosmeticStandard = (int)swCosmeticStandardType_e.swStandardType_StandardISO;
            internal string CosmeticStandardType = "Tapped Hole";
            internal string CosmeticSize;
            internal string CosmeticNote;
        }

        // No Toolbox drill data is redistributed by this repository. Integrators
        // may supply their own metric rows from a separately licensed source.
        private static readonly string[] MetricTapDrillRows = new string[0];
        private static readonly Dictionary<string, ThreadSpec> MetricThreadSpecs = BuildMetricThreadSpecs();

        private sealed class CountersinkSpec
        {
            internal string Size;
            internal double ClearanceHoleMm;
            internal double HeadDiameterMm;
            internal double AngleDeg;
        }

        internal sealed class FastenerRecessSpec
        {
            internal string Kind;
            internal string StandardKey;
            internal string StandardLabel;
            internal string FamilyKey;
            internal string FamilyLabel;
            internal string ScrewSize;
            internal int HoleType;
            internal int Standard;
            internal int FastenerType;
        }

        private sealed class FastenerRecessDimensions
        {
            internal double HoleDiameter;
            internal double RecessDiameter;
            internal double RecessDepth;
            internal double RecessAngle;
        }

        private static readonly List<AxisReference> References = new List<AxisReference>();
        private static readonly List<SlotReference> SlotReferences = new List<SlotReference>();
        private static readonly Dictionary<string, string[]> RecessSizeCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private static TargetPlane Target;
        private static ModelDoc2 CapturedModel;
        private const double GeomTol = 0.00002;

        internal static int ReferenceCount { get { return References.Count + SlotReferences.Count; } }
        internal static int SlotReferenceCount { get { return SlotReferences.Count; } }
        internal static bool HasTarget { get { return Target != null; } }

        internal static int CaptureReferences(ModelDoc2 model, string mode)
        {
            if (model == null) throw new InvalidOperationException("没有打开的模型。");
            if (CapturedModel != null && !SameModel(CapturedModel, model)) Clear();
            if (CapturedModel == null) CapturedModel = model;
            SelectionMgr selection = model.SelectionManager;
            int count = selection.GetSelectedObjectCount2(-1);
            if (count < 1) throw new InvalidOperationException("请先选择参照孔的圆柱面或圆边。");
            if (References.Count + count > 100) throw new InvalidOperationException("一批最多收集 100 个参照对象。");
            var pending = new List<AxisReference>();
            for (int i = 1; i <= count; i++)
            {
                object entity = selection.GetSelectedObject6(i, -1);
                Component2 component = selection.GetSelectedObjectsComponent3(i, -1) as Component2;
                AxisReference axis = AxisFromEntity(entity, component, mode);
                if (axis == null) throw new InvalidOperationException("第 " + i.ToString(CultureInfo.InvariantCulture) + " 个选择与“" + mode + "”参照方式不匹配。");
                pending.Add(axis);
            }
            MathUtility math = AddinRuntime.App.GetMathUtility() as MathUtility;
            int added = 0;
            foreach (AxisReference axis in pending)
            {
                if (References.Any(existing => SameAxis(math, existing, axis))) continue;
                References.Add(axis);
                added++;
            }
            if (added == 0) throw new InvalidOperationException("这些孔轴线已在参照列表中。");
            return added;
        }

        internal static void CaptureTarget(ModelDoc2 model)
        {
            if (model == null) throw new InvalidOperationException("没有打开的模型。");
            if (CapturedModel != null && !SameModel(CapturedModel, model)) throw new InvalidOperationException("参照孔来自另一个文档。请先清空参照，再重新选择。");
            if (CapturedModel == null) CapturedModel = model;
            SelectionMgr selection = model.SelectionManager;
            if (selection.GetSelectedObjectCount2(-1) != 1 || selection.GetSelectedObjectType3(1, -1) != (int)swSelectType_e.swSelFACES)
                throw new InvalidOperationException("请在装配体中只选择一个目标零件的平面。");
            Face2 face = selection.GetSelectedObject6(1, -1) as Face2;
            Surface surface = face == null ? null : face.GetSurface() as Surface;
            if (surface == null || !surface.IsPlane()) throw new InvalidOperationException("目标面必须是平面。");
            double[] plane = surface.PlaneParams as double[];
            if (plane == null || plane.Length < 6) throw new InvalidOperationException("无法读取目标平面参数。");
            double faceSense = face.FaceInSurfaceSense() ? -1.0 : 1.0;
            Target = new TargetPlane
            {
                Face = face,
                Component = selection.GetSelectedObjectsComponent3(1, -1) as Component2,
                Normal = Normalize(new[] { plane[0] * faceSense, plane[1] * faceSense, plane[2] * faceSense }),
                Origin = new[] { plane[3], plane[4], plane[5] }
            };
        }

        internal static int CaptureSlotReferences(ModelDoc2 model)
        {
            if (model == null) throw new InvalidOperationException("没有打开的模型。");
            if (CapturedModel != null && !SameModel(CapturedModel, model)) Clear();
            if (CapturedModel == null) CapturedModel = model;
            SelectionMgr selection = model.SelectionManager;
            int count = selection.GetSelectedObjectCount2(-1);
            if (count < 1) throw new InvalidOperationException("请选中腰型孔的平面环形面。");
            var pending = new List<SlotReference>();
            for (int i = 1; i <= count; i++)
            {
                if (selection.GetSelectedObjectType3(i, -1) != (int)swSelectType_e.swSelFACES)
                    throw new InvalidOperationException("“选腰孔”需要选择腰型孔的平面环形面。");
                Face2 face = selection.GetSelectedObject6(i, -1) as Face2;
                Surface surface = face == null ? null : face.GetSurface() as Surface;
                if (surface == null || !surface.IsPlane()) throw new InvalidOperationException("所选腰型孔参照不是平面环形面。");
                object rawEdges = face.GetEdges();
                object[] edges = rawEdges as object[];
                if (edges == null) throw new InvalidOperationException("无法读取腰型孔边界。");
                var circles = new List<(double[] Center, double Radius)>();
                int straightEdges = 0;
                foreach (object edgeObj in edges)
                {
                    Edge edge = edgeObj as Edge;
                    Curve curve = edge == null ? null : edge.GetCurve() as Curve;
                    if (curve != null && curve.IsLine()) straightEdges++;
                    if (curve == null || !curve.IsCircle()) continue;
                    double[] cp = curve.CircleParams as double[];
                    if (cp == null || cp.Length < 7) continue;
                    double[] center = cp.Take(3).ToArray();
                    if (circles.Any(c => Distance(c.Center, center) < GeomTol)) continue;
                    circles.Add((center, cp[6]));
                }
                if (circles.Count != 2 || straightEdges != 2 || edges.Length != 4 || Math.Abs(circles[0].Radius - circles[1].Radius) > GeomTol)
                    throw new InvalidOperationException("选中的平面面没有恰好两个同半径圆弧端部，无法识别为直腰型孔。");
                double[] plane = surface.PlaneParams as double[];
                if (plane == null || plane.Length < 3) throw new InvalidOperationException("无法读取腰型孔平面的法向。");
                double faceSense = face.FaceInSurfaceSense() ? -1.0 : 1.0;
                pending.Add(new SlotReference
                {
                    Component = selection.GetSelectedObjectsComponent3(i, -1) as Component2,
                    End1 = circles[0].Center,
                    End2 = circles[1].Center,
                    Normal = Normalize(plane.Take(3).Select(v => v * faceSense).ToArray()),
                    Width = 2 * circles[0].Radius
                });
            }
            if (SlotReferences.Count + pending.Count > 100) throw new InvalidOperationException("一批最多收集 100 个腰型孔参照。");
            int added = 0;
            foreach (SlotReference slot in pending)
            {
                bool duplicate = SlotReferences.Any(existing => SameSlot(existing, slot));
                if (!duplicate) { SlotReferences.Add(slot); added++; }
            }
            if (added == 0) throw new InvalidOperationException("这些腰型孔参照已在列表中。");
            return added;
        }

        internal static void Clear()
        {
            References.Clear();
            SlotReferences.Clear();
            Target = null;
            CapturedModel = null;
        }

        internal static object CreateThreadedHolesByPoints(ModelDoc2 model, string threadSize, IList<double[]> centersMm,
            double[] outwardNormal, string endCondition, double depthMm, double threadDepthMm,
            string cosmeticStandard = null, string cosmeticThreadType = null, string cosmeticSize = null,
            double tapDrillDiameterMm = 0, double majorDiameterMm = 0, string cosmeticNote = null)
        {
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new InvalidOperationException("坐标式螺纹孔要求活动文档为零件；不会跨装配体组件计算深度。");
            if (centersMm == null || centersMm.Count == 0 || centersMm.Count > 100)
                throw new ArgumentException("centers_mm 必须包含 1 到 100 个孔心。");
            if (outwardNormal == null || outwardNormal.Length != 3)
                throw new ArgumentException("face_normal 必须是三维向量。");
            string end = string.IsNullOrWhiteSpace(endCondition) ? "auto" : endCondition;
            if (end != "auto" && end != "blind" && end != "through_all")
                throw new ArgumentException("end_condition 必须为 auto、blind 或 through_all。");

            ThreadSpec spec = string.IsNullOrWhiteSpace(threadSize)
                ? ResolveCosmeticThread(cosmeticStandard, cosmeticThreadType, cosmeticSize, tapDrillDiameterMm, majorDiameterMm, cosmeticNote)
                : ResolveThread(threadSize);
            double[] normal = Normalize(outwardNormal);
            if (end == "blind" && !FinitePositive(depthMm)) throw new ArgumentOutOfRangeException("depthMm", "盲孔必须提供正数 depth_mm。");
            if (end != "auto" && !FinitePositive(threadDepthMm)) throw new ArgumentOutOfRangeException("threadDepthMm", "显式盲孔或贯穿孔必须提供正数 thread_depth_mm。");
            if (threadDepthMm < 0 || double.IsNaN(threadDepthMm) || double.IsInfinity(threadDepthMm))
                throw new ArgumentOutOfRangeException("threadDepthMm", "thread_depth_mm 必须为非负有限数值。");

            MathUtility math = AddinRuntime.App == null ? null : AddinRuntime.App.GetMathUtility() as MathUtility;
            if (math == null) throw new InvalidOperationException("SolidWorks 数学工具不可用。");
            double[][] centers = centersMm.Select(p =>
            {
                if (p == null || p.Length != 3 || p.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    throw new ArgumentException("centers_mm 中的每个孔心都必须是有限三维坐标。");
                return p.Select(v => v / 1000.0).ToArray();
            }).ToArray();

            ModelDoc2 oldModel = CapturedModel;
            TargetPlane oldTarget = Target;
            AxisReference[] oldReferences = References.ToArray();
            try
            {
                TargetPlane target = null;
                foreach (double[] center in centers)
                {
                    model.ClearSelection2(true);
                    double lift = 0.005;
                    bool selected = model.Extension.SelectByRay(
                        center[0] + normal[0] * lift, center[1] + normal[1] * lift, center[2] + normal[2] * lift,
                        -normal[0], -normal[1], -normal[2], 0.00002,
                        (int)swSelectType_e.swSelFACES, false, 0, 0);
                    if (!selected) throw new InvalidOperationException("未能沿 face_normal 在孔心处选中入口面。");
                    Face2 face = model.SelectionManager.GetSelectedObject6(1, -1) as Face2;
                    Surface surface = face == null ? null : face.GetSurface() as Surface;
                    double[] plane = surface == null ? null : surface.PlaneParams as double[];
                    if (surface == null || !surface.IsPlane() || plane == null || plane.Length < 6)
                        throw new InvalidOperationException("每个孔心都必须落在同一平面入口面上。");
                    double faceSense = face.FaceInSurfaceSense() ? -1.0 : 1.0;
                    double[] faceNormal = Normalize(plane.Take(3).Select(v => v * faceSense).ToArray());
                    double[] origin = plane.Skip(3).Take(3).ToArray();
                    if (Dot(faceNormal, normal) < 0.9999 || Math.Abs(Dot(Sub(center, origin), normal)) > GeomTol)
                        throw new InvalidOperationException("孔心不在与 face_normal 同向的同一入口平面上；已停止创建。");
                    if (target == null)
                    {
                        target = new TargetPlane { Face = face, Component = null, Origin = origin, Normal = normal };
                    }
                    else if (Math.Abs(Dot(Normalize(plane.Take(3).ToArray()), Normalize(((double[])target.Face.GetSurface().PlaneParams).Take(3).ToArray()))) < 0.9999
                        || Math.Abs(Dot(Sub(origin, target.Origin), normal)) > GeomTol)
                    {
                        throw new InvalidOperationException("一批坐标式螺纹孔必须位于同一平面入口面。");
                    }
                }

                CapturedModel = model;
                Target = target;
                References.Clear();
                foreach (double[] center in centers)
                    References.Add(new AxisReference { Component = null, Origin = center, Direction = normal, Label = "DWG 坐标孔轴" });
                return CreateRoundHoles(spec.TapDrillMm, end == "through_all", depthMm,
                    threadSize: string.IsNullOrWhiteSpace(threadSize) ? null : threadSize,
                    threadDepthMm: threadDepthMm, realThread: false, automaticDepth: end == "auto", cosmeticThread: spec);
            }
            finally
            {
                model.ClearSelection2(true);
                References.Clear();
                References.AddRange(oldReferences);
                Target = oldTarget;
                CapturedModel = oldModel;
            }
        }

        internal static object CreateRoundHoles(double diameterMm, bool throughAll, double depthMm,
            double? countersinkDiameterMm = null, double countersinkAngleDeg = 90.0, string threadSize = null, double threadDepthMm = 0,
            string countersinkStandard = null, FastenerRecessSpec fastenerRecess = null, bool realThread = false, bool automaticDepth = false,
            ThreadSpec cosmeticThread = null)
        {
            SldWorks app = AddinRuntime.App;
            ModelDoc2 model = app == null ? null : app.ActiveDoc as ModelDoc2;
            if (model == null) throw new InvalidOperationException("没有打开的模型。");
            if (CapturedModel == null || !SameModel(CapturedModel, model)) throw new InvalidOperationException("当前活动文档与捕获参照时不同。请清空并重新捕获参照和目标面。");
            if (References.Count == 0) throw new InvalidOperationException("尚未收集参照孔轴线。");
            if (Target == null) throw new InvalidOperationException("尚未设置目标平面。");
            if (double.IsNaN(diameterMm) || double.IsInfinity(diameterMm) || diameterMm <= 0 || diameterMm > 500)
                throw new ArgumentOutOfRangeException("diameterMm", "直径范围为 (0, 500] mm。");
            if (throughAll && automaticDepth) throw new ArgumentException("自动深度不能同时指定完全贯穿。", "automaticDepth");
            if (!throughAll && !automaticDepth && (double.IsNaN(depthMm) || double.IsInfinity(depthMm) || depthMm <= 0))
                throw new ArgumentOutOfRangeException("depthMm", "盲孔深度必须大于零。");
            if (countersinkDiameterMm.HasValue && (!FinitePositive(countersinkDiameterMm.Value) || countersinkDiameterMm.Value <= diameterMm || countersinkDiameterMm.Value > 1000))
                throw new ArgumentOutOfRangeException("countersinkDiameterMm", "沉头直径必须大于孔径且不超过 1000 mm。");
            if (countersinkDiameterMm.HasValue && (!FinitePositive(countersinkAngleDeg) || countersinkAngleDeg < 30 || countersinkAngleDeg > 150))
                throw new ArgumentOutOfRangeException("countersinkAngleDeg", "沉头包含角应在 30° 到 150° 之间。");
            ThreadSpec thread = cosmeticThread ?? (string.IsNullOrWhiteSpace(threadSize) ? null : ResolveThread(threadSize));
            if (realThread && thread == null) throw new ArgumentException("真实螺纹选项只能用于螺纹孔操作。");
            if (realThread && thread != null && !IsValidatedRealThread(thread))
                throw new ArgumentException("真实实体螺纹仍只允许已验证的 M3、M4、M5、M6、M8、M10、M12 公制粗牙规格；其他规格请使用装饰螺纹。 ");
            if (thread != null)
            {
                if (countersinkDiameterMm.HasValue) throw new ArgumentException("当前操作不能同时创建螺纹孔和沉头孔。");
                if (Math.Abs(diameterMm - thread.TapDrillMm) > 1e-6) throw new ArgumentException("螺纹底孔直径应使用规格表的攻丝底孔值。");
                if (automaticDepth)
                {
                    if (threadDepthMm != 0 && (!FinitePositive(threadDepthMm) || threadDepthMm > 500))
                        throw new ArgumentOutOfRangeException("threadDepthMm", "自动深度下，螺纹深度应留空或为 0 到 500 mm 之间的正数。");
                }
                else
                {
                    if (!FinitePositive(threadDepthMm) || threadDepthMm > 500) throw new ArgumentOutOfRangeException("threadDepthMm", "螺纹深度必须为 0 到 500 mm 之间的正数。");
                    if (!throughAll && threadDepthMm > depthMm) throw new ArgumentException("盲孔的螺纹深度不能超过底孔深度。");
                }
            }

            FastenerRecessDimensions recessDimensions = fastenerRecess == null ? null : ReadFastenerRecessDimensions(model, fastenerRecess, throughAll && !automaticDepth);
            if (recessDimensions != null) diameterMm = recessDimensions.HoleDiameter * 1000.0;

            MathUtility math = app.GetMathUtility() as MathUtility;
            if (math == null) throw new InvalidOperationException("SolidWorks 数学工具不可用。");
            bool assembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            if (model.GetType() != (int)swDocumentTypes_e.swDocPART && !assembly)
                throw new InvalidOperationException("此操作只支持零件或装配体文档。");
            if (assembly && Target.Component == null) throw new InvalidOperationException("目标平面不属于装配体零件组件。");

            double[] targetOrigin = MapPoint(math, Target.Component, Target.Origin);
            double[] targetNormal = Normalize(MapVector(math, Target.Component, Target.Normal));
            var centers = new List<double[]>();
            foreach (AxisReference reference in References)
            {
                double[] origin = MapPoint(math, reference.Component, reference.Origin);
                double[] direction = Normalize(MapVector(math, reference.Component, reference.Direction));
                double denom = Dot(direction, targetNormal);
                if (Math.Abs(denom) < 1e-9)
                    throw new InvalidOperationException("参照孔轴线“" + reference.Label + "”与目标平面平行，无法求交点。");
                double t = Dot(Sub(targetOrigin, origin), targetNormal) / denom;
                double[] point = Add(origin, Scale(direction, t));
                if (centers.Any(p => Distance(p, point) < diameterMm / 1000.0))
                    throw new InvalidOperationException("参照孔轴线映射到目标面后出现重叠孔，请检查参照选择或孔径。");
                centers.Add(point);
            }

            AssemblyDoc asm = assembly ? model as AssemblyDoc : null;
            Component2 priorEditTarget = asm == null ? null : asm.GetEditTargetComponent() as Component2;
            if (assembly && priorEditTarget != null && !SameComponent(priorEditTarget, Target.Component))
                throw new InvalidOperationException("当前正在编辑另一个零件。请退出零件编辑后重试。");
            bool startedEdit = false;
            if (assembly && priorEditTarget == null)
            {
                model.ClearSelection2(true);
                if (!Target.Component.Select4(false, null, false)) throw new InvalidOperationException("无法选择目标零件组件。");
                int editErrors = 0;
                int result = asm.EditPart2(false, false, ref editErrors);
                if (result == 0) throw new InvalidOperationException("无法进入目标组件编辑状态（SolidWorks 错误 " + editErrors.ToString(CultureInfo.InvariantCulture) + "）。");
                startedEdit = true;
            }

            var created = new List<Feature>();
            var reports = new List<object>();
            try
            {
                foreach (double[] point in centers)
                {
                    model.ClearSelection2(true);
                    double lift = 0.005;
                    bool selected = model.Extension.SelectByRay(
                        point[0] + targetNormal[0] * lift, point[1] + targetNormal[1] * lift, point[2] + targetNormal[2] * lift,
                        -targetNormal[0], -targetNormal[1], -targetNormal[2], 0.00002,
                        (int)swSelectType_e.swSelFACES, false, 0, 0);
                    if (!selected) throw new InvalidOperationException("SolidWorks 未能在映射孔中心处选中目标平面。");
                    if (assembly)
                    {
                        Component2 picked = model.SelectionManager.GetSelectedObjectsComponent3(1, -1) as Component2;
                        if (!SameComponent(Target.Component, picked)) throw new InvalidOperationException("映射孔中心射线命中了其他组件；未继续打孔。");
                    }
                    Face2 pickedFace = model.SelectionManager.GetSelectedObject6(1, -1) as Face2;
                    if (!MatchesTargetPlane(math, pickedFace, Target.Component, targetOrigin, targetNormal))
                        throw new InvalidOperationException("映射点落在目标面的范围之外，射线命中了目标零件的其他面。");

                    double[] localPoint = MapPoint(math, Inverse(Target.Component), point);
                    double featureDepthMm = depthMm;
                    double featureThreadDepthMm = threadDepthMm;
                    bool featureThroughAll = throughAll;
                    MaterialDepthResult resolvedDepth = null;
                    if (automaticDepth)
                    {
                        resolvedDepth = MaterialDepthResolver.Resolve(model, Target.Component, localPoint, Target.Normal);
                        featureDepthMm = resolvedDepth.DepthMm;
                        featureThroughAll = false;
                        if (thread != null)
                        {
                            if (featureThreadDepthMm <= 0) featureThreadDepthMm = featureDepthMm;
                            if (featureThreadDepthMm > featureDepthMm + 1e-6)
                                throw new ArgumentException("螺纹深度不能超过自动解析出的底孔深度 " + featureDepthMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm。");
                        }
                    }
                    double diameter = fastenerRecess == null ? diameterMm / 1000.0 : recessDimensions.HoleDiameter;
                    double depth = featureThroughAll ? 0.01 : featureDepthMm / 1000.0;
                    string size = "Ø" + diameterMm.ToString("0.0##", CultureInfo.InvariantCulture);
                    Feature feature = model.FeatureManager.HoleWizard5(
                        fastenerRecess == null ? (int)swWzdGeneralHoleTypes_e.swWzdHole : fastenerRecess.HoleType,
                        fastenerRecess == null ? (int)swWzdHoleStandards_e.swStandardAnsiMetric : fastenerRecess.Standard,
                        fastenerRecess == null ? (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricDrillSizes : fastenerRecess.FastenerType,
                        fastenerRecess == null ? size : fastenerRecess.ScrewSize,
                        (short)(featureThroughAll ? swEndConditions_e.swEndCondThroughAllBoth : swEndConditions_e.swEndCondBlind),
                        diameter, depth, -1,
                        -1,
                        -1, -1, fastenerRecess == null ? -1 : (double)swWzdHoleScrewClearanceTypes_e.swScrewClearanceNormal,
                        -1, -1, -1, -1, -1, -1, -1, -1,
                        "", false, true, true, true, true, false);
                    model.ClearSelection2(true);
                    if (feature == null) throw new InvalidOperationException("孔向导没有返回特征对象。");
                    created.Add(feature);
                    if (!model.EditRebuild3()) throw new InvalidOperationException("特征“" + feature.Name + "”重建失败。");

                    double expectedHoleDiameter = recessDimensions == null ? diameter : recessDimensions.HoleDiameter;
                    bool verified = VerifyHoleAxis(feature, localPoint, expectedHoleDiameter / 2.0, Target.Normal);
                    if (!verified) throw new InvalidOperationException("特征“" + feature.Name + "”未通过孔轴线、板面法向和半径回读核验；本批已回退。");
                    if (fastenerRecess != null)
                    {
                        FastenerRecessDimensions actual = ReadFeatureRecessDimensions(feature, fastenerRecess.Kind);
                        if (!DimensionsMatch(actual, recessDimensions)) throw new InvalidOperationException("孔向导实际尺寸与所选标准库尺寸不一致；本批将回退。");
                        if (!VerifyFastenerRecessBrep(feature, localPoint, Target.Normal, fastenerRecess.Kind, recessDimensions))
                            throw new InvalidOperationException("沉孔的实际 B-rep 尺寸未通过独立核验；本批将回退。");
                    }
                    Feature countersink = null;
                    Feature threadFeature = null;
                    string threadMethod = null;
                    if (thread != null)
                    {
                        if (realThread)
                        {
                            threadFeature = CreateRealThread(model, feature, localPoint, Target.Normal, thread, featureThreadDepthMm, featureThroughAll);
                            threadMethod = "real_thread_feature";
                        }
                        else
                        {
                            threadFeature = CreateCosmeticThread(model, feature, localPoint, Target.Normal, thread, featureThreadDepthMm);
                            threadMethod = "cosmetic_thread";
                        }
                        created.Add(threadFeature);
                        if (!model.EditRebuild3() || !FeatureTreeContains(model, threadFeature.Name))
                            throw new InvalidOperationException((realThread ? "真实螺纹" : "装饰螺纹") + "重建或特征树回读失败。");
                    }
                    if (countersinkDiameterMm.HasValue)
                    {
                        countersink = AddCountersinkChamfer(model, feature, localPoint, Target.Normal, diameterMm, countersinkDiameterMm.Value, countersinkAngleDeg);
                        created.Add(countersink);
                        if (!model.EditRebuild3()) throw new InvalidOperationException("沉头倒角“" + countersink.Name + "”重建失败。");
                    }
                    reports.Add(new Dictionary<string, object>
                    {
                        { "feature", feature.Name }, { "thread_feature", threadFeature == null ? null : threadFeature.Name }, { "thread_method", threadMethod },
                        { "thread_feature_tree_verified", threadFeature != null },
                        { "countersink_feature", countersink == null ? null : countersink.Name },
                        { "center_m", point }, { "diameter_mm", expectedHoleDiameter * 1000.0 }, { "axis_radius_verified", true },
                        { "automatic_depth", automaticDepth }, { "resolved_depth_mm", automaticDepth ? (object)featureDepthMm : null },
                        { "depth_stop_reason", resolvedDepth == null ? null : resolvedDepth.StopReason },
                        { "intersected_body_count", resolvedDepth == null ? (object)null : resolvedDepth.IntersectedBodies },
                        { "thread_depth_mm", thread == null ? (object)null : featureThreadDepthMm },
                        { "recess_type", fastenerRecess == null ? null : fastenerRecess.Kind },
                        { "standard", fastenerRecess == null ? null : fastenerRecess.StandardLabel },
                        { "fastener_family", fastenerRecess == null ? null : fastenerRecess.FamilyLabel },
                        { "screw_size", fastenerRecess == null ? null : fastenerRecess.ScrewSize },
                        { "recess_brep_verified", fastenerRecess != null },
                        { "recess_diameter_mm", recessDimensions == null ? (object)null : recessDimensions.RecessDiameter * 1000.0 },
                        { "recess_depth_mm", recessDimensions == null ? (object)null : recessDimensions.RecessDepth * 1000.0 },
                        { "recess_angle_deg", recessDimensions == null ? (object)null : recessDimensions.RecessAngle * 180.0 / Math.PI },
                        { "countersink_diameter_mm", countersinkDiameterMm }, { "countersink_angle_deg", countersinkAngleDeg }
                    });
                }
            }
            catch (Exception ex)
            {
                RollBack(model, created);
                throw new InvalidOperationException(ex.Message + "；本批已创建特征已整体回退。", ex);
            }
            finally
            {
                model.ClearSelection2(true);
                if (startedEdit) { try { asm.EditAssembly(); } catch { } }
            }

            return new Dictionary<string, object>
            {
                { "count", reports.Count }, { "features", reports }, { "diameter_mm", diameterMm },
                { "countersink_diameter_mm", countersinkDiameterMm }, { "countersink_angle_deg", countersinkDiameterMm.HasValue ? (object)countersinkAngleDeg : null },
                { "end_condition", automaticDepth ? "auto" : throughAll ? "through_all" : "blind" },
                { "depth_mm", throughAll || automaticDepth ? (object)null : depthMm },
                { "coordinate_space", assembly ? "assembly" : "part" }, { "geometry_verified", true },
                { "countersink_geometry_verified", false }, { "countersink_method", countersinkDiameterMm.HasValue ? "drill_plus_chamfer" : null },
                { "countersink_standard", countersinkDiameterMm.HasValue ? countersinkStandard : null },
                { "recess_type", fastenerRecess == null ? null : fastenerRecess.Kind },
                { "standard", fastenerRecess == null ? null : fastenerRecess.StandardLabel },
                { "fastener_family", fastenerRecess == null ? null : fastenerRecess.FamilyLabel },
                { "screw_size", fastenerRecess == null ? null : fastenerRecess.ScrewSize },
                { "recess_parameter_readback_verified", fastenerRecess != null },
                { "recess_brep_verified", fastenerRecess != null },
                { "thread_size", thread == null ? null : thread.Size }, { "thread_tap_drill_mm", thread == null ? (object)null : thread.TapDrillMm },
                { "thread_depth_mm", thread == null ? (object)null : threadDepthMm },
                { "thread_representation", thread == null ? null : (realThread ? "real_thread" : "cosmetic_thread") },
                { "thread_method", thread == null ? null : (realThread ? "ThreadFeatureData" : "InsertCosmeticThread3") },
                { "thread_feature_tree_verified", thread != null },
                { "position_associative", false }
            };
        }

        internal static object CreateCountersunkHoles(string screwSize, bool throughAll, double depthMm, bool automaticDepth = false)
        {
            CountersinkSpec spec = ResolveCountersink(screwSize);
            return CreateRoundHoles(spec.ClearanceHoleMm, throughAll, depthMm, spec.HeadDiameterMm, spec.AngleDeg,
                countersinkStandard: "ISO 10642/DIN 7991 + ISO 273 normal fit", automaticDepth: automaticDepth);
        }

        internal static object GetCountersinkSummary(string screwSize)
        {
            CountersinkSpec spec = ResolveCountersink(screwSize);
            return new Dictionary<string, object>
            {
                { "size", spec.Size }, { "clearance_hole_mm", spec.ClearanceHoleMm },
                { "head_diameter_mm", spec.HeadDiameterMm }, { "angle_deg", spec.AngleDeg },
                { "fit", "ISO 273 normal (H13)" }, { "head_standard", "ISO 10642 / DIN 7991" }
            };
        }

        internal static object CreateFastenerRecesses(string recessKind, string standardKey, string familyKey, string screwSize, bool throughAll, double depthMm, bool automaticDepth = false)
        {
            FastenerRecessSpec spec = ResolveFastenerRecess(recessKind, standardKey, familyKey, screwSize);
            return CreateRoundHoles(1, throughAll, depthMm, fastenerRecess: spec, automaticDepth: automaticDepth);
        }

        internal static object GetFastenerRecessSummary(string recessKind, string standardKey, string familyKey, string screwSize)
        {
            ModelDoc2 model = AddinRuntime.App == null ? null : AddinRuntime.App.ActiveDoc as ModelDoc2;
            if (model == null) return new Dictionary<string, object> { { "standard", "" }, { "note", "打开零件后读取 SolidWorks Hole Wizard 标准库尺寸。" } };
            FastenerRecessSpec spec = ResolveFastenerRecess(recessKind, standardKey, familyKey, screwSize);
            FastenerRecessDimensions d = ReadFastenerRecessDimensions(model, spec, true);
            return new Dictionary<string, object>
            {
                { "recess_type", spec.Kind }, { "standard", spec.StandardLabel }, { "fastener_family", spec.FamilyLabel }, { "screw_size", spec.ScrewSize },
                { "normal_clearance_hole_mm", d.HoleDiameter * 1000.0 },
                { "recess_diameter_mm", d.RecessDiameter * 1000.0 },
                { "recess_depth_mm", d.RecessDepth * 1000.0 },
                { "recess_angle_deg", d.RecessAngle * 180.0 / Math.PI },
                { "dimension_source", "SolidWorks Hole Wizard standard database" }
            };
        }

        internal static string[] GetSupportedFastenerRecessSizes(string recessKind, string standardKey, string familyKey, string[] candidates)
        {
            if (candidates == null || candidates.Length == 0) return new string[0];
            ModelDoc2 model = AddinRuntime.App == null ? null : AddinRuntime.App.ActiveDoc as ModelDoc2;
            if (model == null) return (string[])candidates.Clone();
            string cacheKey = (recessKind ?? "") + "|" + (standardKey ?? "") + "|" + (familyKey ?? "") + "|" + string.Join(",", candidates);
            string[] cached;
            if (RecessSizeCache.TryGetValue(cacheKey, out cached)) return (string[])cached.Clone();
            var supported = new List<string>();
            foreach (string candidate in candidates)
            {
                try
                {
                    FastenerRecessSpec spec = ResolveFastenerRecess(recessKind, standardKey, familyKey, candidate);
                    ReadFastenerRecessDimensions(model, spec, true);
                    supported.Add(candidate);
                }
                catch { }
            }
            string[] result = supported.ToArray();
            if (result.Length > 0) RecessSizeCache[cacheKey] = result;
            return (string[])result.Clone();
        }

        private static FastenerRecessSpec ResolveFastenerRecess(string recessKind, string standardKey, string familyKey, string screwSize)
        {
            string kind = (recessKind ?? "").Trim().ToLowerInvariant();
            string key = (standardKey ?? "").Trim().ToLowerInvariant();
            string family = (familyKey ?? "").Trim().ToLowerInvariant();
            string size = (screwSize ?? "").Trim().ToUpperInvariant();
            if (kind != "counterbore" && kind != "countersink") throw new ArgumentException("recess_type 必须为 counterbore 或 countersink。");
            if (string.IsNullOrWhiteSpace(size)) throw new ArgumentException("必须指定螺钉规格。");
            bool metric = key == "iso_metric";
            bool bsi = key == "bsi_bsw" || key == "bsi_bsf";
            bool ansi = key == "ansi_unc_82" || key == "ansi_unf_82" || key == "ansi_unc_100" || key == "ansi_unf_100";
            if (!metric && !bsi && !ansi)
                throw new ArgumentException("标准必须选择 ISO 公制、ANSI UNC/UNF 或 BSI BSW/BSF。");
            if (kind == "counterbore" && key.EndsWith("100"))
                throw new ArgumentException("100° 是锥形沉头规格，只适用于 countersink。");
            int standard = metric ? (int)swWzdHoleStandards_e.swStandardISO : bsi ? (int)swWzdHoleStandards_e.swStandardBSI : (int)swWzdHoleStandards_e.swStandardAnsiInch;
            int fastenerType;
            int holeType;
            string label;
            string familyLabel;
            if (kind == "counterbore")
            {
                if (family != "socket_cap" && family != "hex_head") throw new ArgumentException("平底沉孔的 fastener_family 必须为 socket_cap 或 hex_head。");
                fastenerType = family == "socket_cap" ? (metric ? (int)swWzdHoleStandardFastenerTypes_e.swStandardISOSocketHeadCap : bsi ?
                    (int)swWzdHoleStandardFastenerTypes_e.swStandardBSISocketCapScrew : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchSocketCapScrew) :
                    (metric ? (int)swWzdHoleStandardFastenerTypes_e.swStandardISOHexCapScrew : bsi ?
                    (int)swWzdHoleStandardFastenerTypes_e.swStandardBSIHexCapScrew : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchHexScrew);
                holeType = (int)swWzdGeneralHoleTypes_e.swWzdCounterBore;
                familyLabel = family == "socket_cap" ? "内六角圆柱头" : "六角头";
                label = metric ? "ISO 公制" : bsi ? "英国 BSI（" + (key == "bsi_bsf" ? "BSF" : "BSW") + "）" : "ANSI 英制（" + (key.Contains("unf") ? "UNF" : "UNC") + " 螺纹系列）";
            }
            else if (metric || bsi)
            {
                if (family != "socket_flat" && family != "machine_flat") throw new ArgumentException("锥形沉头的 fastener_family 必须为 socket_flat 或 machine_flat。");
                fastenerType = family == "socket_flat" ? (bsi ? (int)swWzdHoleStandardFastenerTypes_e.swStandardBSIFlatSocketCap :
                    (int)swWzdHoleStandardFastenerTypes_e.swStandardISOSocketCTSKFlatHead) : (bsi ?
                    (int)swWzdHoleStandardFastenerTypes_e.swStandardBSIFlatHead : (int)swWzdHoleStandardFastenerTypes_e.swStandardISOCTSKFlatHead);
                holeType = (int)swWzdGeneralHoleTypes_e.swWzdCounterSink;
                familyLabel = family == "socket_flat" ? "内六角沉头" : "普通平头";
                label = metric ? "ISO 公制" : "英国 BSI（" + (key == "bsi_bsf" ? "BSF" : "BSW") + "）";
            }
            else
            {
                if (family != "socket_flat" && family != "machine_flat") throw new ArgumentException("锥形沉头的 fastener_family 必须为 socket_flat 或 machine_flat。");
                bool hundred = key.EndsWith("100");
                if (hundred && family == "socket_flat") throw new ArgumentException("ANSI 100° 规格不提供内六角沉头系列，请选择普通平头或 82° 标准。");
                fastenerType = family == "socket_flat" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchFlatSocket82 :
                    (hundred ? (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchFlatHead100 : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchFlatHead82);
                holeType = (int)swWzdGeneralHoleTypes_e.swWzdCounterSink;
                familyLabel = family == "socket_flat" ? "内六角沉头" : "普通平头";
                label = "ANSI 英制（" + (hundred ? "100°" : "82°") + "，" + (key.Contains("unf") ? "UNF" : "UNC") + " 系列）";
            }
            return new FastenerRecessSpec { Kind = kind, StandardKey = key, StandardLabel = label, FamilyKey = family, FamilyLabel = familyLabel, ScrewSize = size,
                HoleType = holeType, Standard = standard, FastenerType = fastenerType };
        }

        private static FastenerRecessDimensions ReadFastenerRecessDimensions(ModelDoc2 model, FastenerRecessSpec spec, bool throughAll)
        {
            IWizardHoleFeatureData2 data = model.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmHoleWzd) as IWizardHoleFeatureData2;
            if (data == null) throw new InvalidOperationException("SolidWorks 无法初始化 Hole Wizard 标准数据。");
            try
            {
                data.InitializeHole(spec.HoleType, spec.Standard, spec.FastenerType, spec.ScrewSize,
                    (int)(throughAll ? swEndConditions_e.swEndCondThroughAllBoth : swEndConditions_e.swEndCondBlind));
                data.HoleFit = (int)swWzdHoleScrewClearanceTypes_e.swScrewClearanceNormal;
                FastenerRecessDimensions d = new FastenerRecessDimensions { HoleDiameter = data.Diameter };
                if (spec.Kind == "counterbore")
                {
                    d.RecessDiameter = data.CounterBoreDiameter;
                    d.RecessDepth = data.CounterBoreDepth;
                }
                else
                {
                    d.RecessDiameter = data.CounterSinkDiameter;
                    d.RecessAngle = data.CounterSinkAngle;
                }
                if (!FinitePositive(d.HoleDiameter) || !FinitePositive(d.RecessDiameter) ||
                    (spec.Kind == "counterbore" && !FinitePositive(d.RecessDepth)) ||
                    (spec.Kind == "countersink" && !FinitePositive(d.RecessAngle)))
                    throw new InvalidOperationException("SolidWorks Hole Wizard 标准库没有返回此规格的有效尺寸：" + spec.ScrewSize + "。请检查安装的标准数据库和规格名称。");
                return d;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法从 SolidWorks Hole Wizard 标准库读取 " + spec.StandardLabel + " / " + spec.ScrewSize + "：" + ex.Message, ex);
            }
            finally { try { data.ReleaseSelectionAccess(); } catch { } }
        }

        private static FastenerRecessDimensions ReadFeatureRecessDimensions(Feature feature, string kind)
        {
            IWizardHoleFeatureData2 data = feature.GetDefinition() as IWizardHoleFeatureData2;
            if (data == null) throw new InvalidOperationException("孔向导特征没有返回可读的规格数据。");
            return new FastenerRecessDimensions
            {
                HoleDiameter = data.Diameter,
                RecessDiameter = kind == "counterbore" ? data.CounterBoreDiameter : data.CounterSinkDiameter,
                RecessDepth = kind == "counterbore" ? data.CounterBoreDepth : 0,
                RecessAngle = kind == "countersink" ? data.CounterSinkAngle : 0
            };
        }

        private static bool DimensionsMatch(FastenerRecessDimensions a, FastenerRecessDimensions b)
        {
            double linearTolerance = Math.Max(GeomTol / 10.0, 1e-7);
            return Math.Abs(a.HoleDiameter - b.HoleDiameter) <= linearTolerance &&
                Math.Abs(a.RecessDiameter - b.RecessDiameter) <= linearTolerance &&
                Math.Abs(a.RecessDepth - b.RecessDepth) <= linearTolerance &&
                Math.Abs(a.RecessAngle - b.RecessAngle) <= linearTolerance;
        }

        private static bool VerifyFastenerRecessBrep(Feature feature, double[] center, double[] normal, string kind,
            FastenerRecessDimensions expected)
        {
            object[] faces = feature.GetFaces() as object[];
            if (faces == null || faces.Length == 0) return false;
            double boreRadius = expected.HoleDiameter / 2.0;
            double recessRadius = expected.RecessDiameter / 2.0;
            if (kind == "counterbore")
            {
                double lengthTolerance = Math.Max(GeomTol * 2, expected.RecessDepth * 1e-5);
                foreach (object faceObj in faces)
                {
                    Face2 face = faceObj as Face2;
                    Surface surface = face == null ? null : face.GetSurface() as Surface;
                    if (surface == null || !surface.IsCylinder()) continue;
                    double[] cylinder = surface.CylinderParams as double[];
                    if (cylinder == null || cylinder.Length < 7 || Math.Abs(cylinder[6] - recessRadius) > GeomTol ||
                        !AxisPassesThrough(cylinder.Take(3).ToArray(), cylinder.Skip(3).Take(3).ToArray(), center, normal)) continue;
                    List<double> stations = CircleStations(face, center, normal, recessRadius);
                    if (HasSpan(stations, expected.RecessDepth, lengthTolerance)) return true;
                }
                return false;
            }

            double angleTolerance = 0.005;
            double depthExpected = (recessRadius - boreRadius) / Math.Tan(expected.RecessAngle / 2.0);
            double depthTolerance = Math.Max(GeomTol * 2, depthExpected * 1e-5);
            foreach (object faceObj in faces)
            {
                Face2 face = faceObj as Face2;
                Surface surface = face == null ? null : face.GetSurface() as Surface;
                if (surface == null || !surface.IsCone()) continue;
                double[] cone = surface.ConeParams2 as double[];
                if (cone == null || cone.Length < 8 || Math.Abs(2.0 * cone[7] - expected.RecessAngle) > angleTolerance ||
                    !AxisPassesThrough(cone.Take(3).ToArray(), cone.Skip(3).Take(3).ToArray(), center, normal)) continue;
                List<double> topStations = CircleStations(face, center, normal, recessRadius);
                List<double> boreStations = CircleStations(face, center, normal, boreRadius);
                if (topStations.Count == 0 || boreStations.Count == 0) continue;
                foreach (double top in topStations)
                    foreach (double bore in boreStations)
                        if (Math.Abs(Math.Abs(top - bore) - depthExpected) <= depthTolerance) return true;
            }
            return false;
        }

        private static bool AxisPassesThrough(double[] axisOrigin, double[] axisDirection, double[] center, double[] normal)
        {
            double[] axis = Normalize(axisDirection);
            if (Math.Abs(Dot(axis, Normalize(normal))) < 1.0 - 1e-5) return false;
            double[] delta = Sub(center, axisOrigin);
            return Distance(delta, Scale(axis, Dot(delta, axis))) <= GeomTol * 2;
        }

        private static List<double> CircleStations(Face2 face, double[] center, double[] normal, double expectedRadius)
        {
            var stations = new List<double>();
            object[] edges = face.GetEdges() as object[];
            if (edges == null) return stations;
            double[] axis = Normalize(normal);
            foreach (object edgeObj in edges)
            {
                Edge edge = edgeObj as Edge;
                Curve curve = edge == null ? null : edge.GetCurve() as Curve;
                if (curve == null || !curve.IsCircle()) continue;
                double[] circle = curve.CircleParams as double[];
                if (circle == null || circle.Length < 7 || Math.Abs(circle[6] - expectedRadius) > GeomTol) continue;
                double[] circleAxis = Normalize(circle.Skip(3).Take(3).ToArray());
                if (Math.Abs(Dot(circleAxis, axis)) < 1.0 - 1e-5) continue;
                double[] delta = Sub(circle.Take(3).ToArray(), center);
                double station = Dot(delta, axis);
                if (Distance(delta, Scale(axis, station)) > GeomTol * 2) continue;
                if (!stations.Any(x => Math.Abs(x - station) <= GeomTol)) stations.Add(station);
            }
            return stations;
        }

        private static bool HasSpan(List<double> stations, double expectedSpan, double tolerance)
        {
            if (stations == null || stations.Count < 2) return false;
            double span = stations.Max() - stations.Min();
            return Math.Abs(span - expectedSpan) <= tolerance;
        }

        private static CountersinkSpec ResolveCountersink(string value)
        {
            string key = value.Trim().Replace(" ", "").ToUpperInvariant();
            if (!key.StartsWith("M")) key = "M" + key;
            switch (key)
            {
                case "M3": return new CountersinkSpec { Size = "M3", ClearanceHoleMm = 3.4, HeadDiameterMm = 6.72, AngleDeg = 90 };
                case "M4": return new CountersinkSpec { Size = "M4", ClearanceHoleMm = 4.5, HeadDiameterMm = 8.96, AngleDeg = 90 };
                case "M5": return new CountersinkSpec { Size = "M5", ClearanceHoleMm = 5.5, HeadDiameterMm = 11.20, AngleDeg = 90 };
                case "M6": return new CountersinkSpec { Size = "M6", ClearanceHoleMm = 6.6, HeadDiameterMm = 13.44, AngleDeg = 90 };
                case "M8": return new CountersinkSpec { Size = "M8", ClearanceHoleMm = 9.0, HeadDiameterMm = 17.92, AngleDeg = 90 };
                case "M10": return new CountersinkSpec { Size = "M10", ClearanceHoleMm = 11.0, HeadDiameterMm = 22.40, AngleDeg = 90 };
                case "M12": return new CountersinkSpec { Size = "M12", ClearanceHoleMm = 13.5, HeadDiameterMm = 26.88, AngleDeg = 90 };
                default: throw new ArgumentException("ISO 10642/ISO 273 标准表目前支持 M3、M4、M5、M6、M8、M10、M12。");
            }
        }

        private static Feature CreateCosmeticThread(ModelDoc2 model, Feature holeFeature, double[] center, double[] normal,
            ThreadSpec spec, double depthMm)
        {
            Edge entry = FindEntryEdge(holeFeature, center, normal, spec.TapDrillMm / 2000.0);
            model.ClearSelection2(true);
            if (!((IEntity)entry).Select4(false, null)) throw new InvalidOperationException("无法选择螺纹入口圆边以创建装饰螺纹。");
            Feature cosmetic = model.FeatureManager.InsertCosmeticThread3(
                spec.CosmeticStandard, spec.CosmeticStandardType, string.IsNullOrWhiteSpace(spec.CosmeticSize) ? spec.Size : spec.CosmeticSize,
                spec.NominalMm / 1000.0,
                (int)swCosmeticEndConditions_e.swEndConditionBlind,
                depthMm / 1000.0, spec.CosmeticNote ?? (spec.Size + " - 6H RH")) as Feature;
            model.ClearSelection2(true);
            if (cosmetic == null) throw new InvalidOperationException("InsertCosmeticThread3 没有返回装饰螺纹特征。");
            cosmetic.Name = "CosmeticThread_" + spec.Size.Replace('x', '_').Replace('.', '_') + "_Internal";
            return cosmetic;
        }

        private static Feature CreateRealThread(ModelDoc2 model, Feature holeFeature, double[] center, double[] normal,
            ThreadSpec spec, double depthMm, bool throughAll)
        {
            Edge entry = FindEntryEdge(holeFeature, center, normal, spec.TapDrillMm / 2000.0);
            IThreadFeatureData data = model.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSweepThread) as IThreadFeatureData;
            if (data == null) throw new InvalidOperationException("SolidWorks 无法初始化 ThreadFeatureData。");
            try
            {
                data.InitializeThreadData();
                model.ClearSelection2(true);
                if (!((IEntity)entry).Select2(false, 1))
                    throw new InvalidOperationException("无法按选择标记 1 选中底孔入口圆边。");
                data.Edge = entry;
                data.Type = "Metric Tap";
                data.ThreadMethod = (int)swThreadMethod_e.swThreadMethod_Cut;
                data.EndCondition = (int)(throughAll ? swThreadEndCondition_e.swThreadEndCondition_Revolutions : swThreadEndCondition_e.swThreadEndCondition_Blind);
                if (throughAll) data.Revolutions = depthMm / spec.PitchMm;
                else data.BlindDepth = depthMm / 1000.0;
                data.Size = spec.Size;
                data.PitchOverride = true;
                data.Pitch = spec.PitchMm / 1000.0;
                data.DiameterOverride = false;
                data.RightHanded = true;
                Feature real = model.FeatureManager.CreateFeature(data) as Feature;
                if (real == null) throw new InvalidOperationException("CreateFeature(ThreadFeatureData) 没有返回真实螺纹特征。");
                real.Name = "Thread_" + spec.Size.Replace('x', '_').Replace('.', '_') + "_Internal_" + (throughAll ? "Through" : "Blind");
                return real;
            }
            finally
            {
                model.ClearSelection2(true);
                try { data.ReleaseSelectionAccess(); } catch { }
            }
        }

        private static ThreadSpec ResolveThread(string value)
        {
            ThreadSpec spec;
            if (!TryResolveMetricThread(value, out spec))
                throw new ArgumentException("该公制规格/螺距不在本机 SolidWorks Hole Wizard 攻丝表中；请检查螺纹规格或显式提供经核实的底孔直径。");
            return spec;
        }

        private static Dictionary<string, ThreadSpec> BuildMetricThreadSpecs()
        {
            var result = new Dictionary<string, ThreadSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (string row in MetricTapDrillRows)
            {
                string[] fields = row.Split('|');
                double major = double.Parse(fields[1], CultureInfo.InvariantCulture);
                double pitch = double.Parse(fields[0].Substring(fields[0].IndexOf('x') + 1), CultureInfo.InvariantCulture);
                result[MetricKey(major, pitch)] = new ThreadSpec { Size = fields[0], NominalMm = major, PitchMm = pitch, TapDrillMm = double.Parse(fields[2], CultureInfo.InvariantCulture) };
            }
            return result;
        }

        private static bool TryResolveMetricThread(string value, out ThreadSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string normalized = value.Trim().Replace(" ", "").Replace('×', 'x').ToUpperInvariant();
            if (!normalized.StartsWith("M", StringComparison.Ordinal)) normalized = "M" + normalized;
            string[] parts = normalized.Substring(1).Split('x');
            decimal major, pitch;
            if (!decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out major) || major <= 0) return false;
            if (parts.Length > 2) return false;
            if (parts.Length == 2)
            {
                if (!decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out pitch) || pitch <= 0) return false;
                return MetricThreadSpecs.TryGetValue(MetricKey((double)major, (double)pitch), out spec);
            }
            spec = MetricThreadSpecs.Values.Where(x => Math.Abs(x.NominalMm - (double)major) < 1e-8)
                .OrderByDescending(x => x.PitchMm).FirstOrDefault();
            return spec != null;
        }

        private static string MetricKey(double major, double pitch)
        {
            return major.ToString("0.####", CultureInfo.InvariantCulture) + "|" + pitch.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static bool IsValidatedRealThread(ThreadSpec spec)
        {
            string[] sizes = { "M3x0.5", "M4x0.7", "M5x0.8", "M6x1.0", "M8x1.25", "M10x1.5", "M12x1.75" };
            return sizes.Any(s => string.Equals(s, spec.Size, StringComparison.OrdinalIgnoreCase));
        }

        private static ThreadSpec ResolveCosmeticThread(string standard, string threadType, string size, double tapDrillMm, double majorDiameterMm, string note)
        {
            if (string.IsNullOrWhiteSpace(size) && !string.Equals(standard, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(standard, "custom", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("标准装饰螺纹必须提供 cosmetic_size，例如 M6x1、1/4-20 UNC、G1/4 或 NPT 1/4。");
            string key = (standard ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            int code;
            switch (key)
            {
                case "ansi_inch": code = 0; break;
                case "ansi_metric": code = 1; break;
                case "bsi": code = 2; break;
                case "dme": code = 3; break;
                case "din": code = 4; break;
                case "hasco_metric": code = 5; break;
                case "helicoil_inch": code = 6; break;
                case "helicoil_metric": code = 7; break;
                case "iso": code = 8; break;
                case "jis": code = 9; break;
                case "pcs": code = 10; break;
                case "progressive": code = 11; break;
                case "superior": code = 12; break;
                case "gb": code = 13; break;
                case "ks": code = 14; break;
                case "is": code = 15; break;
                case "as": code = 16; break;
                case "none": case "custom": code = -2; break;
                default: throw new ArgumentException("cosmetic_standard 支持 SolidWorks 标准 ansi_inch、ansi_metric、bsi、dme、din、hasco_metric、helicoil_inch、helicoil_metric、iso、jis、pcs、progressive、superior、gb、ks、is、as 或 none/custom。");
            }
            ThreadSpec metric = null;
            if ((code == 1 || code == 8) && TryResolveMetricThread(size, out metric))
            {
                if (FinitePositive(tapDrillMm) && Math.Abs(tapDrillMm - metric.TapDrillMm) > 0.001)
                    throw new ArgumentException("tap_drill_diameter_mm 与本机 Hole Wizard 公制表不符；表值为 " + metric.TapDrillMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm。");
                if (FinitePositive(majorDiameterMm) && Math.Abs(majorDiameterMm - metric.NominalMm) > 0.001)
                    throw new ArgumentException("major_diameter_mm 与所选公制规格的标称直径不符。");
                tapDrillMm = metric.TapDrillMm;
                majorDiameterMm = metric.NominalMm;
                size = metric.Size;
            }
            if (!FinitePositive(tapDrillMm) || tapDrillMm > 500)
                throw new ArgumentOutOfRangeException("tapDrillDiameterMm", "必须提供正确的 tap_drill_diameter_mm，或选择本机公制 M 螺纹表中已有的规格/螺距。");
            if (!FinitePositive(majorDiameterMm) || majorDiameterMm > 500)
                throw new ArgumentOutOfRangeException("majorDiameterMm", "必须提供 major_diameter_mm，或选择本机公制 M 螺纹表中已有的规格/螺距。");
            return new ThreadSpec
            {
                Size = (size ?? "").Trim(), NominalMm = majorDiameterMm, TapDrillMm = tapDrillMm,
                CosmeticStandard = code,
                CosmeticStandardType = code == -2 ? "" : (string.IsNullOrWhiteSpace(threadType) ? "Tapped Hole" : threadType.Trim()),
                CosmeticSize = code == -2 ? "" : size.Trim(), CosmeticNote = note ?? ""
            };
        }

        internal static double GetTapDrillDiameter(string threadSize) { return ResolveThread(threadSize).TapDrillMm; }

        private static Feature AddCountersinkChamfer(ModelDoc2 model, Feature hole, double[] center, double[] normal,
            double holeDiameterMm, double countersinkDiameterMm, double includedAngleDeg)
        {
            double targetRadius = holeDiameterMm / 2000.0;
            Edge best = null;
            double bestPlaneOffset = double.MaxValue;
            object[] faces = hole.GetFaces() as object[];
            if (faces == null) throw new InvalidOperationException("无法读取孔特征的边界，不能建立沉头口。");
            foreach (object item in faces)
            {
                Face2 face = item as Face2;
                object[] edges = face == null ? null : face.GetEdges() as object[];
                if (edges == null) continue;
                foreach (object edgeObj in edges)
                {
                    Edge edge = edgeObj as Edge;
                    Curve curve = edge == null ? null : edge.GetCurve() as Curve;
                    if (curve == null || !curve.IsCircle()) continue;
                    double[] cp = curve.CircleParams as double[];
                    if (cp == null || cp.Length < 7 || Math.Abs(cp[6] - targetRadius) > GeomTol) continue;
                    double[] delta = Sub(cp.Take(3).ToArray(), center);
                    double off = Math.Abs(Dot(delta, normal));
                    double radial = Distance(delta, Scale(normal, Dot(delta, normal)));
                    if (radial > GeomTol || off > bestPlaneOffset) continue;
                    best = edge;
                    bestPlaneOffset = off;
                }
            }
            if (best == null) throw new InvalidOperationException("找不到沉头入口处的孔口圆边。");
            model.ClearSelection2(true);
            if (!((IEntity)best).Select4(false, null)) throw new InvalidOperationException("无法选择沉头孔口边。");
            double radialDepth = (countersinkDiameterMm - holeDiameterMm) / 2000.0;
            double chamferAngle = includedAngleDeg * Math.PI / 360.0;
            Feature chamfer = model.FeatureManager.InsertFeatureChamfer(4, 1, radialDepth, chamferAngle, 0, 0, 0, 0);
            model.ClearSelection2(true);
            if (chamfer == null) throw new InvalidOperationException("SolidWorks 未创建沉头孔口倒角。");
            return chamfer;
        }

        private static Edge FindEntryEdge(Feature hole, double[] center, double[] normal, double radius)
        {
            Edge best = null;
            double bestOffset = double.MaxValue;
            object[] faces = hole.GetFaces() as object[];
            if (faces == null) throw new InvalidOperationException("无法读取底孔的面以定位螺纹入口。");
            foreach (object item in faces)
            {
                Face2 face = item as Face2;
                object[] edges = face == null ? null : face.GetEdges() as object[];
                if (edges == null) continue;
                foreach (object edgeObj in edges)
                {
                    Edge edge = edgeObj as Edge;
                    Curve curve = edge == null ? null : edge.GetCurve() as Curve;
                    if (curve == null || !curve.IsCircle()) continue;
                    double[] cp = curve.CircleParams as double[];
                    if (cp == null || cp.Length < 7 || Math.Abs(cp[6] - radius) > GeomTol) continue;
                    double[] delta = Sub(cp.Take(3).ToArray(), center);
                    double offset = Math.Abs(Dot(delta, normal));
                    double radial = Distance(delta, Scale(normal, Dot(delta, normal)));
                    if (radial > GeomTol || offset >= bestOffset) continue;
                    best = edge;
                    bestOffset = offset;
                }
            }
            return best ?? throw new InvalidOperationException("没有找到与底孔轴线对应的入口圆边。");
        }

        private static bool FeatureTreeContains(ModelDoc2 model, string name)
        {
            Feature feature = model.FirstFeature() as Feature;
            int guard = 0;
            while (feature != null && guard++ < 10000)
            {
                if (FeatureTreeContains(feature, name)) return true;
                feature = feature.GetNextFeature() as Feature;
            }
            return false;
        }

        private static bool FeatureTreeContains(Feature feature, string name)
        {
            Feature child = feature.GetFirstSubFeature() as Feature;
            int guard = 0;
            while (child != null && guard++ < 10000)
            {
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase) || FeatureTreeContains(child, name)) return true;
                child = child.GetNextSubFeature() as Feature;
            }
            return string.Equals(feature.Name, name, StringComparison.OrdinalIgnoreCase);
        }

        internal static object CreateSlots(double widthMm, double lengthMm, bool fullLength, bool throughAll, double depthMm)
        {
            SldWorks app = AddinRuntime.App;
            ModelDoc2 model = app == null ? null : app.ActiveDoc as ModelDoc2;
            if (model == null) throw new InvalidOperationException("没有打开的模型。");
            if (CapturedModel == null || !SameModel(CapturedModel, model)) throw new InvalidOperationException("当前活动文档与捕获参照时不同。请清空并重新捕获参照和目标面。");
            if (SlotReferences.Count == 0) throw new InvalidOperationException("尚未收集腰型孔参照。");
            if (Target == null) throw new InvalidOperationException("尚未设置目标平面。");
            if (!FinitePositive(widthMm) || widthMm > 500 || !FinitePositive(lengthMm) || lengthMm > 10000)
                throw new ArgumentOutOfRangeException("widthMm", "槽宽和槽长必须为有限正数（mm）。");
            if (fullLength && lengthMm <= widthMm) throw new ArgumentException("按总长定义时，槽长必须大于槽宽。");
            if (!throughAll && !FinitePositive(depthMm)) throw new ArgumentOutOfRangeException("depthMm", "盲槽深度必须大于零。");

            MathUtility math = app.GetMathUtility() as MathUtility;
            if (math == null) throw new InvalidOperationException("SolidWorks 数学工具不可用。");
            bool assembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            if (model.GetType() != (int)swDocumentTypes_e.swDocPART && !assembly) throw new InvalidOperationException("此操作只支持零件或装配体文档。");
            if (assembly && Target.Component == null) throw new InvalidOperationException("目标平面不属于装配体零件组件。");

            double[] targetOrigin = MapPoint(math, Target.Component, Target.Origin);
            double[] targetNormal = Normalize(MapVector(math, Target.Component, Target.Normal));
            var slots = new List<(double[] A, double[] B)>();
            foreach (SlotReference source in SlotReferences)
            {
                double[] sourceNormal = Normalize(MapVector(math, source.Component, source.Normal));
                double[] a = IntersectPlane(MapPoint(math, source.Component, source.End1), sourceNormal, targetOrigin, targetNormal);
                double[] b = IntersectPlane(MapPoint(math, source.Component, source.End2), sourceNormal, targetOrigin, targetNormal);
                if (Distance(a, b) < 1e-6) throw new InvalidOperationException("参照腰型孔投影长度为零。");
                slots.Add((a, b));
            }

            AssemblyDoc asm = assembly ? model as AssemblyDoc : null;
            Component2 priorEditTarget = asm == null ? null : asm.GetEditTargetComponent() as Component2;
            if (assembly && priorEditTarget != null && !SameComponent(priorEditTarget, Target.Component))
                throw new InvalidOperationException("当前正在编辑另一个零件。请退出零件编辑后重试。");
            bool startedEdit = false;
            if (assembly && priorEditTarget == null)
            {
                model.ClearSelection2(true);
                if (!Target.Component.Select4(false, null, false)) throw new InvalidOperationException("无法选择目标零件组件。");
                int editErrors = 0;
                if (asm.EditPart2(false, false, ref editErrors) == 0)
                    throw new InvalidOperationException("无法进入目标组件编辑状态（SolidWorks 错误 " + editErrors.ToString(CultureInfo.InvariantCulture) + "）。");
                startedEdit = true;
            }

            var created = new List<Feature>();
            var reports = new List<object>();
            bool sketchOpen = false;
            try
            {
                foreach (var slot in slots)
                {
                    double[] axis = Normalize(Sub(slot.B, slot.A));
                    double centerDistance = fullLength ? lengthMm / 1000.0 - widthMm / 1000.0 : lengthMm / 1000.0;
                    double[] midpoint = Scale(Add(slot.A, slot.B), 0.5);
                    double[] aAsm = Sub(midpoint, Scale(axis, centerDistance / 2.0));
                    double[] bAsm = Add(midpoint, Scale(axis, centerDistance / 2.0));
                    double[] sample = midpoint;
                    model.ClearSelection2(true);
                    bool selected = model.Extension.SelectByRay(
                        sample[0] + targetNormal[0] * 0.005, sample[1] + targetNormal[1] * 0.005, sample[2] + targetNormal[2] * 0.005,
                        -targetNormal[0], -targetNormal[1], -targetNormal[2], 0.00002,
                        (int)swSelectType_e.swSelFACES, false, 0, 0);
                    if (!selected) throw new InvalidOperationException("无法在腰型孔中心处选中目标平面。");
                    if (assembly && !SameComponent(Target.Component, model.SelectionManager.GetSelectedObjectsComponent3(1, -1) as Component2))
                        throw new InvalidOperationException("腰型孔投影命中了其他组件。");
                    Face2 pickedFace = model.SelectionManager.GetSelectedObject6(1, -1) as Face2;
                    if (!MatchesTargetPlane(math, pickedFace, Target.Component, targetOrigin, targetNormal)) throw new InvalidOperationException("投影中心落在目标面范围之外。");

                    model.SketchManager.InsertSketch(true);
                    sketchOpen = true;
                    Sketch sketch = model.SketchManager.ActiveSketch;
                    if (sketch == null) throw new InvalidOperationException("未能在目标面开始草图。");
                    MathTransform modelToSketch = sketch.ModelToSketchTransform;
                    MathTransform targetInverse = Inverse(Target.Component);
                    double[] aLocal = MapPoint(math, targetInverse, aAsm);
                    double[] bLocal = MapPoint(math, targetInverse, bAsm);
                    double[] aSketch = MapPoint(math, modelToSketch, aLocal);
                    double[] bSketch = MapPoint(math, modelToSketch, bLocal);
                    SketchSlot sketchSlot = model.SketchManager.CreateSketchSlot(
                        (int)swSketchSlotCreationType_e.swSketchSlotCreationType_line,
                        (int)(fullLength ? swSketchSlotLengthType_e.swSketchSlotLengthType_FullLength : swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter),
                        widthMm / 1000.0,
                        aSketch[0], aSketch[1], aSketch[2], bSketch[0], bSketch[1], bSketch[2],
                        0, 0, 0, 1, false);
                    if (sketchSlot == null) throw new InvalidOperationException("SolidWorks 未创建腰型孔草图轮廓。");
                    model.SketchManager.InsertSketch(true);
                    sketchOpen = false;
                    Feature sketchFeature = model.FeatureByPositionReverse(0) as Feature;
                    if (sketchFeature == null) throw new InvalidOperationException("无法读取腰型孔草图特征。");
                    created.Add(sketchFeature);
                    if (!model.EditRebuild3()) throw new InvalidOperationException("腰型孔草图重建失败。");
                    model.ClearSelection2(true);
                    if (!sketchFeature.Select2(false, 0)) throw new InvalidOperationException("无法选择腰型孔草图用于切除。");
                    int end = throughAll ? (int)swEndConditions_e.swEndCondThroughAll : (int)swEndConditions_e.swEndCondBlind;
                    double cutDepth = throughAll ? 0.01 : depthMm / 1000.0;
                    Feature cut = model.FeatureManager.FeatureCut4(true, false, false, end, (int)swEndConditions_e.swEndCondBlind,
                        cutDepth, cutDepth, false, false, false, false, 0, 0, false, false, false, false, false,
                        true, true, true, true, false, (int)swStartConditions_e.swStartSketchPlane, 0, false, false);
                    model.ClearSelection2(true);
                    if (cut == null) throw new InvalidOperationException("SolidWorks 未创建腰型孔切除特征。");
                    created.Add(cut);
                    if (!model.EditRebuild3()) throw new InvalidOperationException("腰型孔切除重建失败。");
                    reports.Add(new Dictionary<string, object>
                    {
                        { "sketch", sketchFeature.Name }, { "feature", cut.Name }, { "centers_m", new[] { aAsm, bAsm } },
                        { "width_mm", widthMm }, { "length_mm", lengthMm }, { "length_definition", fullLength ? "overall" : "center_to_center" }
                    });
                }
            }
            catch (Exception ex)
            {
                if (sketchOpen) { try { model.SketchManager.InsertSketch(true); } catch { } }
                RollBack(model, created);
                throw new InvalidOperationException(ex.Message + "；本批已创建的腰型孔特征已整体回退。", ex);
            }
            finally
            {
                model.ClearSelection2(true);
                if (startedEdit) { try { asm.EditAssembly(); } catch { } }
            }

            return new Dictionary<string, object>
            {
                { "count", reports.Count }, { "features", reports }, { "width_mm", widthMm }, { "length_mm", lengthMm },
                { "length_definition", fullLength ? "overall" : "center_to_center" },
                { "end_condition", throughAll ? "through_all" : "blind" }, { "depth_mm", throughAll ? (object)null : depthMm },
                { "coordinate_space", assembly ? "assembly" : "part" }, { "geometry_verified", false }, { "position_associative", false }
            };
        }

        private static AxisReference AxisFromEntity(object entity, Component2 component, string mode)
        {
            Face2 face = entity as Face2;
            if (face != null)
            {
                Surface surface = face.GetSurface() as Surface;
                if (surface != null && surface.IsCylinder() && (mode == "圆孔面" || mode == "选面（圆柱/圆锥）"))
                {
                    double[] p = surface.CylinderParams as double[];
                    if (p != null && p.Length >= 7)
                        return new AxisReference { Component = component, Origin = p.Take(3).ToArray(), Direction = Normalize(p.Skip(3).Take(3).ToArray()), Label = mode + " 圆柱面" };
                }
                if (surface != null && surface.IsCone() && mode == "选面（圆柱/圆锥）")
                {
                    double[] p = surface.ConeParams as double[];
                    if (p != null && p.Length >= 6)
                        return new AxisReference { Component = component, Origin = p.Take(3).ToArray(), Direction = Normalize(p.Skip(3).Take(3).ToArray()), Label = mode + " 圆锥面" };
                }
            }
            Edge edge = entity as Edge;
            Curve curve = edge == null ? null : edge.GetCurve() as Curve;
            if (curve != null && curve.IsCircle() && mode == "选圆边")
            {
                double[] p = curve.CircleParams as double[];
                if (p != null && p.Length >= 7)
                    return new AxisReference { Component = component, Origin = p.Take(3).ToArray(), Direction = Normalize(p.Skip(3).Take(3).ToArray()), Label = mode + " 圆边" };
            }
            return null;
        }

        private static bool SameAxis(MathUtility math, AxisReference a, AxisReference b)
        {
            double[] p1 = MapPoint(math, a.Component, a.Origin), p2 = MapPoint(math, b.Component, b.Origin);
            double[] u1 = Normalize(MapVector(math, a.Component, a.Direction)), u2 = Normalize(MapVector(math, b.Component, b.Direction));
            double[] delta = Sub(p2, p1);
            return Math.Abs(Dot(u1, u2)) > 0.999999 && Distance(delta, Scale(u1, Dot(delta, u1))) < GeomTol;
        }

        private static bool SameSlot(SlotReference a, SlotReference b)
        {
            if (Math.Abs(a.Width - b.Width) > GeomTol) return false;
            MathUtility math = AddinRuntime.App.GetMathUtility() as MathUtility;
            if (math == null) return false;
            double[] a1 = MapPoint(math, a.Component, a.End1), a2 = MapPoint(math, a.Component, a.End2);
            double[] b1 = MapPoint(math, b.Component, b.End1), b2 = MapPoint(math, b.Component, b.End2);
            return (Distance(a1, b1) < GeomTol && Distance(a2, b2) < GeomTol) || (Distance(a1, b2) < GeomTol && Distance(a2, b1) < GeomTol);
        }

        private static double[] IntersectPlane(double[] origin, double[] direction, double[] planeOrigin, double[] planeNormal)
        {
            double denom = Dot(direction, planeNormal);
            if (Math.Abs(denom) < 1e-9) throw new InvalidOperationException("参照轴线与目标平面平行，无法求交点。");
            return Add(origin, Scale(direction, Dot(Sub(planeOrigin, origin), planeNormal) / denom));
        }

        internal static bool VerifyHoleAxis(Feature feature, double[] expectedPoint, double expectedRadius)
        {
            return VerifyHoleAxis(feature, expectedPoint, expectedRadius, null);
        }

        internal static bool VerifyHoleDirection(Feature feature, double[] expectedDirection)
        {
            object raw = feature == null ? null : feature.GetFaces();
            object[] faces = raw as object[];
            if (faces == null || expectedDirection == null || expectedDirection.Length != 3) return false;
            double[] expected = Normalize(expectedDirection);
            foreach (object item in faces)
            {
                Face2 face = item as Face2;
                Surface surface = face == null ? null : face.GetSurface() as Surface;
                if (surface == null || !surface.IsCylinder()) continue;
                double[] p = surface.CylinderParams as double[];
                if (p == null || p.Length < 6) continue;
                double[] axis = Normalize(p.Skip(3).Take(3).ToArray());
                if (Math.Abs(Dot(axis, expected)) >= 0.9999) return true;
            }
            return false;
        }

        internal static bool VerifyHoleAxis(Feature feature, double[] expectedPoint, double expectedRadius, double[] expectedDirection)
        {
            object raw = feature.GetFaces();
            object[] faces = raw as object[];
            if (faces == null) return false;
            double[] expected = expectedDirection == null ? null : Normalize(expectedDirection);
            foreach (object item in faces)
            {
                Face2 face = item as Face2;
                Surface surface = face == null ? null : face.GetSurface() as Surface;
                if (surface == null || !surface.IsCylinder()) continue;
                double[] p = surface.CylinderParams as double[];
                if (p == null || p.Length < 7) continue;
                double[] axisPoint = p.Take(3).ToArray();
                double[] axis = Normalize(p.Skip(3).Take(3).ToArray());
                if (expected != null && Math.Abs(Dot(axis, expected)) < 0.9999) continue;
                double[] delta = Sub(expectedPoint, axisPoint);
                double perpendicular = Distance(delta, Scale(axis, Dot(delta, axis)));
                if (perpendicular <= GeomTol && Math.Abs(p[6] - expectedRadius) <= GeomTol) return true;
            }
            return false;
        }

        private static void RollBack(ModelDoc2 model, IList<Feature> features)
        {
            for (int i = features.Count - 1; i >= 0; i--)
            {
                try
                {
                    model.ClearSelection2(true);
                    if (features[i].Select2(false, 0)) model.EditDelete();
                }
                catch { }
            }
            model.ClearSelection2(true);
            model.EditRebuild3();
        }

        private static bool SameComponent(Component2 a, Component2 b)
        {
            if (a == null || b == null) return a == null && b == null;
            return string.Equals(a.Name2, b.Name2, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesTargetPlane(MathUtility math, Face2 face, Component2 component, double[] targetOrigin, double[] targetNormal)
        {
            Surface surface = face == null ? null : face.GetSurface() as Surface;
            if (surface == null || !surface.IsPlane()) return false;
            double[] p = surface.PlaneParams as double[];
            if (p == null || p.Length < 6) return false;
            double[] origin = MapPoint(math, component, p.Skip(3).Take(3).ToArray());
            double[] normal = Normalize(MapVector(math, component, p.Take(3).ToArray()));
            return Math.Abs(Dot(normal, targetNormal)) > 0.999999 && Math.Abs(Dot(Sub(origin, targetOrigin), targetNormal)) < GeomTol;
        }

        private static bool SameModel(ModelDoc2 a, ModelDoc2 b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a != null && b != null && string.Equals(a.GetPathName(), b.GetPathName(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.GetTitle(), b.GetTitle(), StringComparison.OrdinalIgnoreCase);
        }

        private static double[] MapPoint(MathUtility math, Component2 component, double[] point)
        {
            if (component == null) return point.ToArray();
            MathPoint source = math.CreatePoint(point) as MathPoint;
            MathPoint result = source == null ? null : source.MultiplyTransform(component.Transform2) as MathPoint;
            return result == null ? throw new InvalidOperationException("无法变换组件内点坐标。") : (double[])result.ArrayData;
        }

        private static double[] MapPoint(MathUtility math, MathTransform transform, double[] point)
        {
            if (transform == null) return point.ToArray();
            MathPoint source = math.CreatePoint(point) as MathPoint;
            MathPoint result = source == null ? null : source.MultiplyTransform(transform) as MathPoint;
            return result == null ? throw new InvalidOperationException("无法将装配坐标转换到目标零件坐标。") : (double[])result.ArrayData;
        }

        private static double[] MapVector(MathUtility math, Component2 component, double[] vector)
        {
            if (component == null) return vector.ToArray();
            MathVector source = math.CreateVector(vector) as MathVector;
            MathVector result = source == null ? null : source.MultiplyTransform(component.Transform2) as MathVector;
            return result == null ? throw new InvalidOperationException("无法变换组件内向量坐标。") : (double[])result.ArrayData;
        }

        private static MathTransform Inverse(Component2 component)
        {
            if (component == null) return null;
            return component.Transform2.Inverse() as MathTransform;
        }

        private static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double[] Scale(double[] a, double s) { return new[] { a[0] * s, a[1] * s, a[2] * s }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double Distance(double[] a, double[] b) { double[] d = Sub(a, b); return Math.Sqrt(Dot(d, d)); }
        private static bool FinitePositive(double x) { return !double.IsNaN(x) && !double.IsInfinity(x) && x > 0; }
        private static double[] Normalize(double[] v)
        {
            double n = Math.Sqrt(Dot(v, v));
            if (double.IsNaN(n) || double.IsInfinity(n) || n < 1e-12) throw new InvalidOperationException("几何方向向量无效。");
            return Scale(v, 1.0 / n);
        }
    }
}
