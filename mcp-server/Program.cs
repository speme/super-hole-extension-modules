using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;

namespace MyCustomTaskpane.McpServer
{
    internal static class Program
    {
        private const string PipeName = "MyCustomTaskpane.Mcp.v1";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                object id = null;
                try
                {
                    var request = Json.Deserialize<Dictionary<string, object>>(line);
                    if (request.ContainsKey("id")) id = request["id"];
                    string method = Convert.ToString(request.ContainsKey("method") ? request["method"] : null, CultureInfo.InvariantCulture);
                    if (method.StartsWith("notifications/", StringComparison.Ordinal)) continue;
                    Dictionary<string, object> response = Handle(method, request);
                    if (id != null) Write(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", response } });
                }
                catch (Exception ex)
                {
                    if (id != null) Write(new Dictionary<string, object>
                    {
                        { "jsonrpc", "2.0" }, { "id", id },
                        { "error", new Dictionary<string, object> { { "code", -32000 }, { "message", ex.Message } } }
                    });
                }
            }
            return 0;
        }

        private static Dictionary<string, object> Handle(string method, Dictionary<string, object> request)
        {
            if (method == "initialize")
            {
                var p = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;
                object version = p != null && p.ContainsKey("protocolVersion") ? p["protocolVersion"] : "2025-03-26";
                return new Dictionary<string, object>
                {
                    { "protocolVersion", version }, { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                    { "serverInfo", new Dictionary<string, object> { { "name", "my-custom-taskpane" }, { "version", "0.1.0" } } },
                    { "instructions", "Tools operate on the active SolidWorks session through the enabled MyCustomTaskpane add-in. Coordinates and lengths are in millimetres. Use holes_create or thread_holes_create for coordinate-based work in a part; thread_holes_create creates cosmetic thread lines only. Capture round references with assembly_capture_references, capture a target plane with assembly_set_target, then call assembly_align_holes, assembly_align_threaded_holes, assembly_align_fastener_recess, or assembly_align_slots for alignment workflows." }
                };
            }
            if (method == "ping") return new Dictionary<string, object>();
            if (method == "shutdown") return new Dictionary<string, object>();
            if (method == "tools/list") return new Dictionary<string, object> { { "tools", Tools() } };
            if (method == "tools/call")
            {
                var p = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;
                if (p == null) throw new ArgumentException("Missing params.");
                string name = Convert.ToString(p.ContainsKey("name") ? p["name"] : null, CultureInfo.InvariantCulture);
                var args = p.ContainsKey("arguments") ? p["arguments"] as Dictionary<string, object> : new Dictionary<string, object>();
                if (name != "sw_status" && name != "holes_create" && name != "thread_holes_create" && name != "assembly_capture_references" && name != "assembly_set_target" && name != "assembly_clear_alignment" && name != "assembly_align_holes" && name != "assembly_align_slots" && name != "assembly_align_threaded_holes" && name != "assembly_align_countersunk_holes" && name != "assembly_align_fastener_recess") throw new ArgumentException("Unknown tool: " + name);
                var pipeResult = CallAddin(name == "sw_status" ? "status" : name, args ?? new Dictionary<string, object>());
                bool ok = pipeResult.ContainsKey("ok") && Convert.ToBoolean(pipeResult["ok"], CultureInfo.InvariantCulture);
                if (!ok) return new Dictionary<string, object> { { "content", new object[] { Text(pipeResult.ContainsKey("error") ? pipeResult["error"] : "Add-in command failed.") } }, { "isError", true } };
                return new Dictionary<string, object> { { "content", new object[] { Text(Json.Serialize(pipeResult["result"])) } } };
            }
            throw new ArgumentException("Unsupported MCP method: " + method);
        }

        private static object[] Tools()
        {
            return new object[]
            {
                new Dictionary<string, object>
                {
                    { "name", "sw_status" }, { "description", "Read the connected SolidWorks session and active document." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", true }, { "destructiveHint", false }, { "idempotentHint", true }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } } }
                },
                new Dictionary<string, object>
                {
                    { "name", "holes_create" },
                    { "description", "Create simple drilled holes in one part. end_condition defaults to auto: ray-cast all solid bodies in this same part, continue through touching plate bodies, and stop at the near inner wall when a ray crosses a hollow tube body. This does not cut separate assembly components. face_normal must point outward from a planar entry face; the selected face and resulting cylindrical hole axis are checked against it, and a parallel-to-face result is rolled back. Use blind with depth_mm for an explicit depth, or through_all only when the entire target body is intended to be cut. The batch rolls back if a point fails." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "diameter_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 } } },
                                    { "centers_mm", new Dictionary<string, object> { { "type", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", VectorSchema("Hole center in model millimetres.") } } },
                                    { "face_normal", VectorSchema("Outward normal of the planar entry face; normalized by the add-in.") },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "auto", "blind", "through_all" } }, { "default", "auto" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind holes. Omit for automatic same-part plate-stack / tube-inner-wall depth." } } }
                                }
                            },
                            { "required", new string[] { "diameter_mm", "centers_mm", "face_normal" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "thread_holes_create" },
                    { "description", "Create a tap-drill bore and SolidWorks cosmetic thread annotation at drawing/model coordinates in one part. Cosmetic threads accept the installed SolidWorks standards and any size/thread type accepted by its local library. Provide verified tap_drill_diameter_mm and major_diameter_mm for the selected thread form; this public package does not redistribute SolidWorks Toolbox thread data. real/modelled thread geometry is not created by this tool." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "thread_size", new Dictionary<string, object> { { "type", "string" }, { "description", "M size and optional pitch for a consuming add-in that has configured a compatible metric tap-drill table. Omit when using general cosmetic_* fields and explicitly supplied dimensions." } } },
                                    { "cosmetic_standard", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "ansi_inch", "ansi_metric", "bsi", "dme", "din", "hasco_metric", "helicoil_inch", "helicoil_metric", "iso", "jis", "pcs", "progressive", "superior", "gb", "ks", "is", "as", "none", "custom" } }, { "description", "SolidWorks cosmetic thread standard. Standard availability depends on the installed SolidWorks version/library." } } },
                                    { "cosmetic_thread_type", new Dictionary<string, object> { { "type", "string" }, { "default", "Tapped Hole" }, { "description", "Exact thread type name accepted by the local SolidWorks standard, such as Tapped Hole or Machine Threads." } } },
                                    { "cosmetic_size", new Dictionary<string, object> { { "type", "string" }, { "description", "Exact size name used by the chosen SolidWorks standard, for example 1/4-20 UNC, G1/4, NPT 1/4 or M6x1." } } },
                                    { "tap_drill_diameter_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Verified bore diameter for this thread form in mm; optional for ISO/ANSI Metric M sizes found in the local tap-drill table." } } },
                                    { "major_diameter_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Nominal major diameter in mm; optional for table-backed ISO/ANSI Metric M sizes." } } },
                                    { "cosmetic_note", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional custom callout for cosmetic_standard none/custom." } } },
                                    { "centers_mm", new Dictionary<string, object> { { "type", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", VectorSchema("Thread-hole center in model millimetres.") } } },
                                    { "face_normal", VectorSchema("Outward normal of the common planar entry face; normalized by the add-in.") },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "auto", "blind", "through_all" } }, { "default", "auto" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind holes." } } },
                                    { "thread_depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Optional for auto; defaults to the resolved tap-drill depth. Required with explicit blind or through_all end conditions." } } }
                                }
                            },
                            { "required", new string[] { "centers_mm", "face_normal" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_capture_references" },
                    { "description", "Capture the currently selected reference hole geometry. Select cylindrical faces, or circular edges when mode is circle_edge. Modes: round_face, axis_face (cylinder or cone), circle_edge." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", false }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "mode", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "round_face", "axis_face", "circle_edge", "slot_face" } }, { "default", "round_face" } } }
                                }
                            }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_set_target" },
                    { "description", "Capture the currently selected planar face as the target plane. In an assembly, select a face on the target component." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", false }, { "idempotentHint", true }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } } }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_clear_alignment" },
                    { "description", "Clear captured reference axes and target plane." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", false }, { "idempotentHint", true }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } } }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_align_holes" },
                    { "description", "Create holes by intersecting captured axes with the target plane. In an assembly, edits only the selected target component in context; it does not cut separate assembly components. end_condition defaults to auto and ray-casts all solid bodies of that same part: touching plates are traversed as a stack, while a hollow tube body stops at its near inner wall. blind requires depth_mm; through_all explicitly cuts the target body completely. Checks that the generated hole axis is normal to the target face, plus its position and radius, and rolls back on failure." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "diameter_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 } } },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "auto", "blind", "through_all" } }, { "default", "auto" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind holes." } } }
                                }
                            },
                            { "required", new string[] { "diameter_mm" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_align_threaded_holes" },
                    { "description", "Create metric tap-drill holes and cosmetic threads on captured axes; the consuming add-in must provide its metric tap-drill table. end_condition defaults to auto: within the target part, pass through contiguous plate bodies and stop the hole and thread at the near inner wall of a hollow tube body. Other components in an assembly are not cut. The created hole axis is checked against the target face normal before adding a thread. Set real_thread=true for the separately validated M3/M4/M5/M6/M8/M10/M12 coarse real-thread subset; other real-thread requests are rejected before geometry is created. thread_depth_mm is optional in auto mode and then matches the resolved hole depth." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "thread_size", new Dictionary<string, object> { { "type", "string" }, { "description", "M size and optional pitch, resolved using the consuming add-in's configured metric tap-drill table." } } },
                                    { "thread_depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Optional; defaults to the resolved automatic hole depth when end_condition is auto." } } },
                                    { "real_thread", new Dictionary<string, object> { { "type", "boolean" }, { "default", false }, { "description", "When true, create real thread geometry; only validated M3/M4/M5/M6/M8/M10/M12 coarse sizes are accepted. Other sizes roll back before creating geometry." } } },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "auto", "blind", "through_all" } }, { "default", "auto" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind tap-drill holes." } } }
                                }
                            },
                            { "required", new string[] { "thread_size" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_align_fastener_recess" },
                    { "description", "Create standard Hole Wizard counterbore or countersink clearances on captured axes. Depth defaults to auto for the base bore: contiguous solid bodies in the same part are traversed, and a hollow tube stops at its near inner wall; separate assembly components are not cut. Blind requires depth_mm. ISO metric, ANSI UNC/UNF, and BSI BSW/BSF sizes use the installed Hole Wizard database." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "recess_type", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "countersink", "counterbore" } } } },
                                    { "standard", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "iso_metric", "ansi_unc_82", "ansi_unf_82", "ansi_unc_100", "ansi_unf_100", "bsi_bsw", "bsi_bsf" } }, { "description", "UNC/UNF select US ANSI inch series; BSW/BSF select British BSI inch series. Hole geometry comes from the matching SolidWorks Hole Wizard tables." } } },
                                    { "fastener_family", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "socket_flat", "machine_flat", "socket_cap", "hex_head" } }, { "description", "Countersink: socket_flat or machine_flat. Counterbore: socket_cap or hex_head. The selected family must match recess_type." } } },
                                    { "screw_size", new Dictionary<string, object> { { "type", "string" }, { "description", "Examples: M6, M8, #8, #10, 1/4, 3/8. Exact availability depends on the installed SolidWorks Hole Wizard database." } } },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "auto", "blind", "through_all" } }, { "default", "auto" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind holes." } } }
                                }
                            },
                            { "required", new string[] { "recess_type", "standard", "fastener_family", "screw_size" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_align_countersunk_holes" },
                    { "description", "Create countersunk clearance holes on captured axes using ISO 273 normal-fit diameter and ISO 10642/DIN 7991 head dimensions. Default depth is auto for same-part plate stacks and stops at a hollow tube's near inner wall; this does not cut separate assembly components." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "screw_size", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "M3", "M4", "M5", "M6", "M8", "M10", "M12" } } } },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "auto", "blind", "through_all" } }, { "default", "auto" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind holes." } } }
                                }
                            },
                            { "required", new string[] { "screw_size" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "name", "assembly_align_slots" },
                    { "description", "Create a batch of slot cut features from previously captured planar slot faces. The two end-arc centers are mapped to the target plane, then used as a center-line slot profile. length_definition is overall length or center-to-center. Rebuild is required; the current path does not independently verify slot B-rep geometry or associative linkage." },
                    { "annotations", new Dictionary<string, object> { { "readOnlyHint", false }, { "destructiveHint", true }, { "idempotentHint", false }, { "openWorldHint", false } } },
                    { "inputSchema", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "width_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 } } },
                                    { "length_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 } } },
                                    { "length_definition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "overall", "center_to_center" } }, { "default", "overall" } } },
                                    { "end_condition", new Dictionary<string, object> { { "type", "string" }, { "enum", new string[] { "blind", "through_all" } }, { "default", "through_all" } } },
                                    { "depth_mm", new Dictionary<string, object> { { "type", "number" }, { "minimum", 0.001 }, { "description", "Required for blind slots." } } }
                                }
                            },
                            { "required", new string[] { "width_mm", "length_mm" } }
                        }
                    }
                }
            };
        }

        private static Dictionary<string, object> VectorSchema(string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" }, { "description", description }, { "minItems", 3 }, { "maxItems", 3 },
                { "items", new Dictionary<string, object> { { "type", "number" } } }
            };
        }

        private static Dictionary<string, object> CallAddin(string method, Dictionary<string, object> args)
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(5000);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                {
                    writer.WriteLine(Json.Serialize(new Dictionary<string, object> { { "method", method }, { "params", args } }));
                    string response = reader.ReadLine();
                    if (response == null) throw new IOException("The add-in pipe closed without a response.");
                    return Json.Deserialize<Dictionary<string, object>>(response);
                }
            }
        }

        private static Dictionary<string, object> Text(object value)
        {
            return new Dictionary<string, object> { { "type", "text" }, { "text", Convert.ToString(value, CultureInfo.InvariantCulture) } };
        }

        private static void Write(Dictionary<string, object> message)
        {
            Console.WriteLine(Json.Serialize(message));
            Console.Out.Flush();
        }
    }
}
