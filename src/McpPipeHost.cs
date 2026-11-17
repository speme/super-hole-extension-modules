using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CustomTaskpane
{
    internal static class AddinRuntime
    {
        internal static SldWorks App { get; private set; }

        internal static void Attach(SldWorks app) { App = app; }
        internal static void Detach() { App = null; }
    }

    /// <summary>Serializes MCP commands onto the SolidWorks task-pane UI thread.</summary>
    internal static class McpPipeHost
    {
        internal const string PipeName = "MyCustomTaskpane.Mcp.v1";
        private static readonly object Gate = new object();
        private static CancellationTokenSource _stop;
        private static NamedPipeServerStream _activePipe;
        private static Control _dispatcher;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        internal static void Start(Control dispatcher)
        {
            lock (Gate)
            {
                if (_stop != null) return;
                _dispatcher = dispatcher;
                _stop = new CancellationTokenSource();
                var token = _stop.Token;
                var thread = new Thread(() => Listen(token)) { IsBackground = true, Name = "MyCustomTaskpane MCP pipe" };
                thread.Start();
            }
        }

        internal static void Stop()
        {
            lock (Gate)
            {
                if (_stop == null) return;
                _stop.Cancel();
                try { if (_activePipe != null) _activePipe.Dispose(); } catch { }
                _activePipe = null;
                _dispatcher = null;
                _stop.Dispose();
                _stop = null;
            }
        }

        private static void Listen(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 4096, 4096, CurrentUserPipeSecurity());
                    lock (Gate) _activePipe = pipe;
                    pipe.WaitForConnection();
                    using (var reader = new System.IO.StreamReader(pipe, new System.Text.UTF8Encoding(false), false, 4096, true))
                    using (var writer = new System.IO.StreamWriter(pipe, new System.Text.UTF8Encoding(false), 4096, true) { AutoFlush = true })
                    {
                        string line = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Dictionary<string, object> request = Json.Deserialize<Dictionary<string, object>>(line);
                            Dictionary<string, object> response = Dispatch(request);
                            writer.WriteLine(Json.Serialize(response));
                        }
                    }
                }
                catch (ObjectDisposedException) { if (!token.IsCancellationRequested) Thread.Sleep(50); }
                catch (Exception ex)
                {
                    try
                    {
                        if (pipe != null && pipe.IsConnected)
                        {
                            using (var writer = new System.IO.StreamWriter(pipe, new System.Text.UTF8Encoding(false), 4096, true) { AutoFlush = true })
                                writer.WriteLine(Json.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } }));
                        }
                    }
                    catch { }
                }
                finally
                {
                    lock (Gate) if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
                    if (pipe != null) pipe.Dispose();
                }
            }
        }

        private static PipeSecurity CurrentUserPipeSecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private static Dictionary<string, object> Dispatch(Dictionary<string, object> request)
        {
            string method = Convert.ToString(request.ContainsKey("method") ? request["method"] : null, CultureInfo.InvariantCulture);
            var args = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;
            Control control;
            lock (Gate) control = _dispatcher;
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
                throw new InvalidOperationException("SolidWorks task pane is not ready.");

            object result = null;
            Exception failure = null;
            using (var done = new ManualResetEvent(false))
            {
                control.BeginInvoke(new Action(() =>
                {
                    try { result = Execute(method, args ?? new Dictionary<string, object>()); }
                    catch (Exception ex) { failure = ex; }
                    finally { done.Set(); }
                }));
                done.WaitOne();
            }
            if (failure != null) throw failure;
            return new Dictionary<string, object> { { "ok", true }, { "result", result } };
        }

        private static object Execute(string method, Dictionary<string, object> args)
        {
            SldWorks app = AddinRuntime.App;
            if (app == null) throw new InvalidOperationException("SolidWorks add-in is disconnected.");
            ModelDoc2 model = app.ActiveDoc as ModelDoc2;

            if (method == "status")
            {
                if (model == null) return new Dictionary<string, object> { { "connected", true }, { "document_open", false } };
                int type = model.GetType();
                return new Dictionary<string, object>
                {
                    { "connected", true }, { "document_open", true }, { "title", model.GetTitle() },
                    { "path", model.GetPathName() }, { "document_type", type == (int)swDocumentTypes_e.swDocPART ? "part" : type == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "drawing" }
                };
            }
            if (method == "holes_create") return CreateHoles(model, args);
            if (method == "thread_holes_create")
            {
                string threadSize = Convert.ToString(args.ContainsKey("thread_size") ? args["thread_size"] : "", CultureInfo.InvariantCulture);
                List<double[]> centers = Vectors(args, "centers_mm");
                double[] normal = Normalize(Vector(args, "face_normal"));
                string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "auto", CultureInfo.InvariantCulture);
                double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
                double threadDepthMm = args.ContainsKey("thread_depth_mm") ? Number(args, "thread_depth_mm") : 0;
                string cosmeticStandard = Convert.ToString(args.ContainsKey("cosmetic_standard") ? args["cosmetic_standard"] : "", CultureInfo.InvariantCulture);
                string cosmeticType = Convert.ToString(args.ContainsKey("cosmetic_thread_type") ? args["cosmetic_thread_type"] : "Tapped Hole", CultureInfo.InvariantCulture);
                string cosmeticSize = Convert.ToString(args.ContainsKey("cosmetic_size") ? args["cosmetic_size"] : "", CultureInfo.InvariantCulture);
                double tapDrillMm = args.ContainsKey("tap_drill_diameter_mm") ? Number(args, "tap_drill_diameter_mm") : 0;
                double majorDiameterMm = args.ContainsKey("major_diameter_mm") ? Number(args, "major_diameter_mm") : 0;
                string cosmeticNote = Convert.ToString(args.ContainsKey("cosmetic_note") ? args["cosmetic_note"] : "", CultureInfo.InvariantCulture);
                return AssemblyHoleAlignment.CreateThreadedHolesByPoints(model, threadSize, centers, normal, end, depthMm, threadDepthMm,
                    cosmeticStandard, cosmeticType, cosmeticSize, tapDrillMm, majorDiameterMm, cosmeticNote);
            }
            if (method == "assembly_capture_references")
            {
                string mode = Convert.ToString(args.ContainsKey("mode") ? args["mode"] : "圆孔面", CultureInfo.InvariantCulture);
                if (mode == "round_face") mode = "圆孔面";
                else if (mode == "axis_face") mode = "选面（圆柱/圆锥）";
                else if (mode == "circle_edge") mode = "选圆边";
                else if (mode == "slot_face")
                {
                    int slots = AssemblyHoleAlignment.CaptureSlotReferences(model);
                    return new Dictionary<string, object> { { "added", slots }, { "total", AssemblyHoleAlignment.SlotReferenceCount } };
                }
                int added = AssemblyHoleAlignment.CaptureReferences(model, mode);
                return new Dictionary<string, object> { { "added", added }, { "total", AssemblyHoleAlignment.ReferenceCount } };
            }
            if (method == "assembly_set_target")
            {
                AssemblyHoleAlignment.CaptureTarget(model);
                return new Dictionary<string, object> { { "target_set", true } };
            }
            if (method == "assembly_clear_alignment")
            {
                AssemblyHoleAlignment.Clear();
                return new Dictionary<string, object> { { "cleared", true } };
            }
            if (method == "assembly_align_holes")
            {
                double diameterMm = Number(args, "diameter_mm");
                string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "auto", CultureInfo.InvariantCulture);
                if (end != "auto" && end != "blind" && end != "through_all") throw new ArgumentException("end_condition must be 'auto', 'blind', or 'through_all'.");
                double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
                return AssemblyHoleAlignment.CreateRoundHoles(diameterMm, end == "through_all", depthMm, automaticDepth: end == "auto");
            }
            if (method == "assembly_align_countersunk_holes")
            {
                string screwSize = Convert.ToString(args.ContainsKey("screw_size") ? args["screw_size"] : "", CultureInfo.InvariantCulture);
                string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "auto", CultureInfo.InvariantCulture);
                if (end != "auto" && end != "blind" && end != "through_all") throw new ArgumentException("end_condition must be 'auto', 'blind', or 'through_all'.");
                double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
                return AssemblyHoleAlignment.CreateCountersunkHoles(screwSize, end == "through_all", depthMm, end == "auto");
            }
            if (method == "assembly_align_fastener_recess")
            {
                string recessType = Convert.ToString(args.ContainsKey("recess_type") ? args["recess_type"] : "", CultureInfo.InvariantCulture);
                string standard = Convert.ToString(args.ContainsKey("standard") ? args["standard"] : "", CultureInfo.InvariantCulture);
                string family = Convert.ToString(args.ContainsKey("fastener_family") ? args["fastener_family"] : "", CultureInfo.InvariantCulture);
                string screwSize = Convert.ToString(args.ContainsKey("screw_size") ? args["screw_size"] : "", CultureInfo.InvariantCulture);
                string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "auto", CultureInfo.InvariantCulture);
                if (end != "auto" && end != "blind" && end != "through_all") throw new ArgumentException("end_condition must be 'auto', 'blind', or 'through_all'.");
                double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
                return AssemblyHoleAlignment.CreateFastenerRecesses(recessType, standard, family, screwSize, end == "through_all", depthMm, end == "auto");
            }
            if (method == "assembly_align_threaded_holes")
            {
                string threadSize = Convert.ToString(args.ContainsKey("thread_size") ? args["thread_size"] : "", CultureInfo.InvariantCulture);
                double tapDrill = AssemblyHoleAlignment.GetTapDrillDiameter(threadSize);
                double threadDepth = args.ContainsKey("thread_depth_mm") ? Number(args, "thread_depth_mm") : 0;
                string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "auto", CultureInfo.InvariantCulture);
                if (end != "auto" && end != "blind" && end != "through_all") throw new ArgumentException("end_condition must be 'auto', 'blind', or 'through_all'.");
                double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
                bool realThread = args.ContainsKey("real_thread") && Convert.ToBoolean(args["real_thread"], CultureInfo.InvariantCulture);
                return AssemblyHoleAlignment.CreateRoundHoles(tapDrill, end == "through_all", depthMm,
                    threadSize: threadSize, threadDepthMm: threadDepth, realThread: realThread, automaticDepth: end == "auto");
            }
            if (method == "assembly_align_slots")
            {
                double widthMm = Number(args, "width_mm");
                double lengthMm = Number(args, "length_mm");
                string definition = Convert.ToString(args.ContainsKey("length_definition") ? args["length_definition"] : "overall", CultureInfo.InvariantCulture);
                if (definition != "overall" && definition != "center_to_center") throw new ArgumentException("length_definition must be 'overall' or 'center_to_center'.");
                string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "through_all", CultureInfo.InvariantCulture);
                if (end != "blind" && end != "through_all") throw new ArgumentException("end_condition must be 'blind' or 'through_all'.");
                double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
                return AssemblyHoleAlignment.CreateSlots(widthMm, lengthMm, definition == "overall", end == "through_all", depthMm);
            }
            throw new ArgumentException("Unknown MCP operation: " + method);
        }

        private static object CreateHoles(ModelDoc2 model, Dictionary<string, object> args)
        {
            if (model == null) throw new InvalidOperationException("Open a part document before creating a hole.");
            if (model.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new InvalidOperationException("Version 1 supports hole creation in a part document only.");

            double diameterMm = Number(args, "diameter_mm");
            List<double[]> centers = Vectors(args, "centers_mm");
            if (centers.Count == 0 || centers.Count > 100) throw new ArgumentException("centers_mm must contain 1 to 100 points.");
            double[] normal = Normalize(Vector(args, "face_normal"));
            string end = Convert.ToString(args.ContainsKey("end_condition") ? args["end_condition"] : "auto", CultureInfo.InvariantCulture);
            double depthMm = args.ContainsKey("depth_mm") ? Number(args, "depth_mm") : 0;
            if (!IsFinite(diameterMm) || diameterMm <= 0 || diameterMm > 500) throw new ArgumentOutOfRangeException("diameter_mm", "Use a diameter between 0 and 500 mm.");
            if (end != "auto" && end != "blind" && end != "through_all") throw new ArgumentException("end_condition must be 'auto', 'blind', or 'through_all'.");
            if (end == "blind" && (!IsFinite(depthMm) || depthMm <= 0)) throw new ArgumentOutOfRangeException("depth_mm", "Blind holes need depth_mm > 0.");

            for (int i = 0; i < centers.Count; i++)
            {
                if (centers[i].Any(v => !IsFinite(v))) throw new ArgumentException("centers_mm must contain only finite numbers.");
                for (int j = 0; j < i; j++)
                {
                    double dx = centers[i][0] - centers[j][0], dy = centers[i][1] - centers[j][1], dz = centers[i][2] - centers[j][2];
                    if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < diameterMm)
                        throw new ArgumentException("centers_mm points must be at least one hole diameter apart.");
                }
            }

            ModelDocExtension ext = model.Extension;
            FeatureManager fm = model.FeatureManager;
            short endType = (short)(end == "through_all" ? swEndConditions_e.swEndCondThroughAllBoth : swEndConditions_e.swEndCondBlind);
            string size = "Ø" + diameterMm.ToString("0.0##", CultureInfo.InvariantCulture);
            var created = new List<Feature>();
            var resolvedDepths = new List<double>();
            try
            {
                foreach (double[] center in centers)
                {
                    MaterialDepthResult resolved = end == "auto"
                        ? MaterialDepthResolver.Resolve(model, null, new[] { center[0] / 1000.0, center[1] / 1000.0, center[2] / 1000.0 }, normal)
                        : null;
                    double holeDepthM = end == "blind" ? depthMm / 1000.0 : resolved == null ? 0.01 : resolved.DepthMm / 1000.0;
                    model.ClearSelection2(true);
                    bool selected = ext.SelectByRay(center[0] / 1000.0, center[1] / 1000.0, center[2] / 1000.0,
                        -normal[0], -normal[1], -normal[2], 0.00001, (int)swSelectType_e.swSelFACES, false, 0, 0);
                    if (!selected) throw new InvalidOperationException("Could not select a face at one of centers_mm along face_normal. Confirm every point lies on a planar face.");
                    Face2 entryFace = model.SelectionManager.GetSelectedObject6(1, -1) as Face2;
                    Surface entrySurface = entryFace == null ? null : entryFace.GetSurface() as Surface;
                    double[] entryPlane = entrySurface == null ? null : entrySurface.PlaneParams as double[];
                    double entryFaceSense = entryFace != null && entryFace.FaceInSurfaceSense() ? -1.0 : 1.0;
                    if (entrySurface == null || !entrySurface.IsPlane() || entryPlane == null || entryPlane.Length < 6
                        || Dot(Normalize(entryPlane.Take(3).Select(v => v * entryFaceSense).ToArray()), normal) < 0.9999)
                        throw new InvalidOperationException("The selected entry face is not planar or is not perpendicular to face_normal; no hole was created.");

                    Feature feature = fm.HoleWizard5(
                        (int)swWzdGeneralHoleTypes_e.swWzdHole,
                        (int)swWzdHoleStandards_e.swStandardAnsiMetric,
                        (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricDrillSizes,
                        size, endType, diameterMm / 1000.0, holeDepthM, -1,
                        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
                        "", false, true, true, true, true, false);
                    model.ClearSelection2(true);
                    if (feature == null) throw new InvalidOperationException("HoleWizard5 did not create a feature for one of centers_mm.");
                    created.Add(feature);
                    if (resolved != null) resolvedDepths.Add(resolved.DepthMm);
                    if (!model.EditRebuild3()) throw new InvalidOperationException("Rebuild failed while creating the hole batch.");
                    if (!AssemblyHoleAlignment.VerifyHoleDirection(feature, normal))
                        throw new InvalidOperationException("Created hole axis is not parallel to the selected face normal; the batch was rolled back.");
                }
            }
            catch (Exception ex)
            {
                foreach (Feature feature in created.AsEnumerable().Reverse())
                {
                    try { feature.Select2(false, 0); model.EditDelete(); }
                    catch { }
                    finally { model.ClearSelection2(true); }
                }
                throw new InvalidOperationException(ex.Message + " Previously created holes in this batch were rolled back.", ex);
            }
            return new Dictionary<string, object>
            {
                { "features", created.Select(f => new Dictionary<string, object> { { "name", f.Name }, { "type", f.GetTypeName2() } }).ToArray() },
                { "count", created.Count }, { "diameter_mm", diameterMm }, { "centers_mm", centers },
                { "end_condition", end }, { "depth_mm", end == "blind" ? (object)depthMm : null },
                { "resolved_depths_mm", end == "auto" ? (object)resolvedDepths : null },
                { "rebuild_ok", true }, { "geometry_verified", false }
            };
        }

        private static double Number(Dictionary<string, object> args, string key)
        {
            object value;
            if (!args.TryGetValue(key, out value) || value == null) throw new ArgumentException("Missing required parameter: " + key);
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static double[] Vector(Dictionary<string, object> args, string key)
        {
            object value;
            var items = args.TryGetValue(key, out value) ? value as object[] : null;
            if (items == null || items.Length != 3) throw new ArgumentException(key + " must be a three-number array.");
            return new[] { Convert.ToDouble(items[0], CultureInfo.InvariantCulture), Convert.ToDouble(items[1], CultureInfo.InvariantCulture), Convert.ToDouble(items[2], CultureInfo.InvariantCulture) };
        }

        private static List<double[]> Vectors(Dictionary<string, object> args, string key)
        {
            object value;
            var items = args.TryGetValue(key, out value) ? value as object[] : null;
            if (items == null) throw new ArgumentException(key + " must be an array of three-number arrays.");
            var result = new List<double[]>();
            foreach (object item in items)
            {
                var point = item as object[];
                if (point == null || point.Length != 3) throw new ArgumentException(key + " must be an array of three-number arrays.");
                result.Add(new[] { Convert.ToDouble(point[0], CultureInfo.InvariantCulture), Convert.ToDouble(point[1], CultureInfo.InvariantCulture), Convert.ToDouble(point[2], CultureInfo.InvariantCulture) });
            }
            return result;
        }

        private static double[] Normalize(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (!IsFinite(length) || length < 1e-12) throw new ArgumentException("face_normal must be a non-zero vector.");
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        private static bool IsFinite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
    }
}
