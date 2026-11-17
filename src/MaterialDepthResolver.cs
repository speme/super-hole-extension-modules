using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomTaskpane
{
    internal sealed class MaterialDepthResult
    {
        internal double DepthMm;
        internal string StopReason;
        internal int IntersectedBodies;
    }

    /// <summary>
    /// Resolves an unspecified cut depth from solid-body ray intervals. Contiguous
    /// plate bodies are treated as one stack. If a body is crossed more than once
    /// (a hollow section such as a tube), the cut stops at its first inner wall.
    /// </summary>
    internal static class MaterialDepthResolver
    {
        private const double EntryLiftM = 0.002;
        private const double ContactGapToleranceM = 0.0005;
        private const double HitRadiusM = 0.00001;
        private const double HitOffsetM = 0.00002;

        private sealed class Event
        {
            internal double Distance;
            internal bool Enter;
        }

        private sealed class Interval
        {
            internal int BodyIndex;
            internal double Start;
            internal double End;
        }

        internal static MaterialDepthResult Resolve(ModelDoc2 contextModel, Component2 component,
            double[] entryPoint, double[] outwardNormal)
        {
            if (contextModel == null) throw new ArgumentNullException("contextModel");
            if (entryPoint == null || entryPoint.Length != 3) throw new ArgumentException("入口点必须是三维坐标。", "entryPoint");
            if (outwardNormal == null || outwardNormal.Length != 3) throw new ArgumentException("入口面法向必须是三维向量。", "outwardNormal");

            ModelDoc2 partModel = component == null ? contextModel : component.GetModelDoc2() as ModelDoc2;
            if (partModel == null || partModel.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new InvalidOperationException("自动深度目前只支持零件文档或装配体中当前目标组件对应的零件。");
            PartDoc part = partModel as PartDoc;
            object rawBodies = part == null ? null : part.GetBodies2((int)swBodyType_e.swSolidBody, false);
            object[] rawBodyArray = rawBodies as object[];
            if (rawBodyArray == null || rawBodyArray.Length == 0) throw new InvalidOperationException("自动深度无法读取目标零件的实体体。 ");
            Body2[] bodies = rawBodyArray.Cast<Body2>().ToArray();

            double norm = Math.Sqrt(outwardNormal[0] * outwardNormal[0] + outwardNormal[1] * outwardNormal[1] + outwardNormal[2] * outwardNormal[2]);
            if (norm < 1e-12) throw new InvalidOperationException("目标面法向无效，无法计算自动深度。");
            double[] inward = new[] { -outwardNormal[0] / norm, -outwardNormal[1] / norm, -outwardNormal[2] / norm };
            double[] rayOrigin = new[]
            {
                entryPoint[0] + outwardNormal[0] / norm * EntryLiftM,
                entryPoint[1] + outwardNormal[1] / norm * EntryLiftM,
                entryPoint[2] + outwardNormal[2] / norm * EntryLiftM
            };

            int options = (int)(swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT | swRayPtsOpts_e.swRayPtsOptsNORMALS);
            int count = partModel.Extension.RayIntersections(bodies, rayOrigin, inward, options, HitRadiusM, HitOffsetM, true);
            if (count <= 0) throw new InvalidOperationException("自动深度射线没有命中实体；请确认孔位落在目标实体表面。");
            double[] hits = partModel.GetRayIntersectionsPoints() as double[];
            const int stride = 9; // body, ray, type, point XYZ, normal XYZ
            if (hits == null || hits.Length < count * stride)
                throw new InvalidOperationException("SolidWorks 未返回完整的自动深度射线交点数据。");

            var eventsByBody = new Dictionary<int, List<Event>>();
            for (int i = 0; i < count; i++)
            {
                int k = i * stride;
                int bodyIndex = (int)Math.Round(hits[k]);
                int rayIndex = (int)Math.Round(hits[k + 1]);
                int type = (int)Math.Round(hits[k + 2]);
                if (rayIndex != 0) continue;
                bool enter = (type & (int)swRayPtsResults_e.swRayPtsResultsENTER) != 0;
                bool exit = (type & (int)swRayPtsResults_e.swRayPtsResultsEXIT) != 0;
                if (enter == exit) continue;
                double distance = (hits[k + 3] - rayOrigin[0]) * inward[0]
                    + (hits[k + 4] - rayOrigin[1]) * inward[1]
                    + (hits[k + 5] - rayOrigin[2]) * inward[2];
                if (distance < EntryLiftM - ContactGapToleranceM) continue;
                List<Event> events;
                if (!eventsByBody.TryGetValue(bodyIndex, out events)) eventsByBody[bodyIndex] = events = new List<Event>();
                events.Add(new Event { Distance = distance, Enter = enter });
            }

            var intervals = new List<Interval>();
            foreach (KeyValuePair<int, List<Event>> pair in eventsByBody)
            {
                List<Event> events = pair.Value.OrderBy(e => e.Distance).ToList();
                double? open = null;
                foreach (Event item in events)
                {
                    if (item.Enter)
                    {
                        if (!open.HasValue) open = item.Distance;
                    }
                    else if (open.HasValue && item.Distance > open.Value + HitOffsetM)
                    {
                        intervals.Add(new Interval { BodyIndex = pair.Key, Start = open.Value, End = item.Distance });
                        open = null;
                    }
                }
            }
            if (intervals.Count == 0) throw new InvalidOperationException("射线交点未形成有效实体区间；为避免错误贯穿，自动深度已停止。");

            // Multiple disjoint intervals in one solid body are the geometric
            // signature of crossing a hollow section along this ray. Stop at its
            // nearest inner surface, after any earlier plate layers.
            var hollow = intervals.GroupBy(i => i.BodyIndex)
                .Where(g => g.Count() > 1)
                .Select(g => g.OrderBy(i => i.Start).First())
                .OrderBy(i => i.Start)
                .FirstOrDefault();
            double stopDistance;
            string reason;
            if (hollow != null)
            {
                stopDistance = hollow.End;
                reason = "tube_inner_wall";
            }
            else
            {
                List<Interval> ordered = intervals.OrderBy(i => i.Start).ToList();
                Interval first = ordered.FirstOrDefault(i => i.Start <= EntryLiftM + ContactGapToleranceM && i.End > EntryLiftM);
                if (first == null) throw new InvalidOperationException("射线未从目标入口面进入实体；自动深度已停止。");
                stopDistance = first.End;
                foreach (Interval next in ordered.Where(i => i.Start > first.Start))
                {
                    if (next.Start > stopDistance + ContactGapToleranceM) break;
                    if (next.End > stopDistance) stopDistance = next.End;
                }
                reason = "contiguous_plate_stack";
            }

            double depthMm = (stopDistance - EntryLiftM) * 1000.0;
            if (double.IsNaN(depthMm) || double.IsInfinity(depthMm) || depthMm <= 0 || depthMm > 500)
                throw new InvalidOperationException("自动计算出的深度不在 0 到 500 mm 的有效范围内：" + depthMm.ToString("0.###") + " mm。");
            return new MaterialDepthResult { DepthMm = depthMm, StopReason = reason, IntersectedBodies = intervals.Select(i => i.BodyIndex).Distinct().Count() };
        }
    }
}
