using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using ESAPI_EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Marching squares algorithm for generating smooth isodose contour polylines.
    /// Operates on a CT-resolution dose map and outputs polylines in CT pixel coordinates.
    /// </summary>
    public static class MarchingSquares
    {
        public static List<List<Point2D>> GenerateContours(double[] field, int width, int height, double threshold)
        {
            if (field == null || field.Length == 0 || width < 2 || height < 2)
                return new List<List<Point2D>>();

            var segments = new List<Segment>();

            for (int y = 0; y < height - 1; y++)
            {
                int rowOffset = y * width;
                int nextRowOffset = (y + 1) * width;

                for (int x = 0; x < width - 1; x++)
                {
                    double tl = field[rowOffset + x];
                    double tr = field[rowOffset + x + 1];
                    double br = field[nextRowOffset + x + 1];
                    double bl = field[nextRowOffset + x];

                    int c = 0;
                    if (tl >= threshold) c |= 8;
                    if (tr >= threshold) c |= 4;
                    if (br >= threshold) c |= 2;
                    if (bl >= threshold) c |= 1;

                    if (c == 0 || c == 15) continue;

                    Point2D top = LerpEdge(x, y, x + 1, y, tl, tr, threshold);
                    Point2D right = LerpEdge(x + 1, y, x + 1, y + 1, tr, br, threshold);
                    Point2D bottom = LerpEdge(x, y + 1, x + 1, y + 1, bl, br, threshold);
                    Point2D left = LerpEdge(x, y, x, y + 1, tl, bl, threshold);

                    switch (c)
                    {
                        case 1: segments.Add(new Segment(left, bottom)); break;
                        case 2: segments.Add(new Segment(bottom, right)); break;
                        case 3: segments.Add(new Segment(left, right)); break;
                        case 4: segments.Add(new Segment(top, right)); break;
                        case 5:
                            if ((tl + tr + br + bl) / 4.0 >= threshold)
                            { segments.Add(new Segment(top, left)); segments.Add(new Segment(bottom, right)); }
                            else
                            { segments.Add(new Segment(top, right)); segments.Add(new Segment(left, bottom)); }
                            break;
                        case 6: segments.Add(new Segment(top, bottom)); break;
                        case 7: segments.Add(new Segment(top, left)); break;
                        case 8: segments.Add(new Segment(top, left)); break;
                        case 9: segments.Add(new Segment(top, bottom)); break;
                        case 10:
                            if ((tl + tr + br + bl) / 4.0 >= threshold)
                            { segments.Add(new Segment(top, right)); segments.Add(new Segment(left, bottom)); }
                            else
                            { segments.Add(new Segment(top, left)); segments.Add(new Segment(bottom, right)); }
                            break;
                        case 11: segments.Add(new Segment(top, right)); break;
                        case 12: segments.Add(new Segment(left, right)); break;
                        case 13: segments.Add(new Segment(bottom, right)); break;
                        case 14: segments.Add(new Segment(left, bottom)); break;
                    }
                }
            }

            return ChainSegments(segments);
        }

        private static Point2D LerpEdge(int x1, int y1, int x2, int y2,
            double v1, double v2, double threshold)
        {
            double denom = v2 - v1;
            double t = (denom != 0) ? (threshold - v1) / denom : 0.5;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return new Point2D(x1 + t * (x2 - x1), y1 + t * (y2 - y1));
        }

        #region Segment Chaining

        private struct Segment
        {
            public Point2D A, B;
            public Segment(Point2D a, Point2D b) { A = a; B = b; }
        }

        private static long PointKey(Point2D p)
        {
            long x = (long)(p.X * DomainConstants.PointQuantization + 0.5);
            long y = (long)(p.Y * DomainConstants.PointQuantization + 0.5);
            return x * DomainConstants.PointHashMultiplier + y;
        }

        private static List<List<Point2D>> ChainSegments(List<Segment> segments)
        {
            if (segments.Count == 0)
                return new List<List<Point2D>>();

            var adjacency = new Dictionary<long, List<int>>(segments.Count * 2);

            for (int i = 0; i < segments.Count; i++)
            {
                long keyA = PointKey(segments[i].A);
                long keyB = PointKey(segments[i].B);

                if (!adjacency.TryGetValue(keyA, out var listA))
                { listA = new List<int>(4); adjacency[keyA] = listA; }
                listA.Add(i);

                if (!adjacency.TryGetValue(keyB, out var listB))
                { listB = new List<int>(4); adjacency[keyB] = listB; }
                listB.Add(i);
            }

            var used = new bool[segments.Count];
            var chains = new List<List<Point2D>>();

            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;

                var chain = new List<Point2D>(32);
                chain.Add(segments[i].A);
                chain.Add(segments[i].B);

                ExtendChain(chain, false, segments, adjacency, used);
                ExtendChain(chain, true, segments, adjacency, used);

                if (chain.Count >= 2)
                    chains.Add(chain);
            }

            return chains;
        }

        private static void ExtendChain(List<Point2D> chain, bool fromStart,
            List<Segment> segments, Dictionary<long, List<int>> adjacency, bool[] used)
        {
            while (true)
            {
                Point2D endpoint = fromStart ? chain[0] : chain[chain.Count - 1];
                long key = PointKey(endpoint);

                if (!adjacency.TryGetValue(key, out var neighbors))
                    break;

                int nextIdx = -1;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    if (!used[neighbors[i]]) { nextIdx = neighbors[i]; break; }
                }

                if (nextIdx < 0) break;
                used[nextIdx] = true;

                var seg = segments[nextIdx];
                Point2D newPoint = (PointKey(seg.A) == key) ? seg.B : seg.A;

                if (fromStart) chain.Insert(0, newPoint);
                else chain.Add(newPoint);
            }
        }

        #endregion
    }
}
