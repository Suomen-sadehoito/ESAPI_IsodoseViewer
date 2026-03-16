using System;
using System.Collections.Generic;
using System.Windows;

namespace ESAPI_EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Marching squares algorithm for generating smooth isodose contour polylines.
    /// Operates on a CT-resolution dose map and outputs polylines in CT pixel coordinates.
    /// These polylines are rendered as WPF vector Path elements → perfect at any zoom level.
    /// </summary>
    public static class MarchingSquares
    {
        /// <summary>
        /// Generates contour polylines for a single dose threshold.
        /// </summary>
        /// <param name="field">Flat array of dose values at CT pixel resolution [y * width + x]</param>
        /// <param name="width">CT image width in pixels</param>
        /// <param name="height">CT image height in pixels</param>
        /// <param name="threshold">Dose threshold in Gy</param>
        /// <returns>List of polylines, each a list of Point in CT pixel coordinates</returns>
        public static List<List<Point>> GenerateContours(double[] field, int width, int height, double threshold)
        {
            if (field == null || field.Length == 0 || width < 2 || height < 2)
                return new List<List<Point>>();

            // ================================================================
            // Pass 1: Generate line segments from marching squares cells.
            //
            // Corner layout per cell:
            //   TL (x,y) ---- TR (x+1,y)
            //    |                |
            //   BL (x,y+1) -- BR (x+1,y+1)
            //
            // Case index: TL=bit3, TR=bit2, BR=bit1, BL=bit0
            // 1 = above threshold, 0 = below
            // ================================================================
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

                    // Fully inside or fully outside — no contour
                    if (c == 0 || c == 15) continue;

                    // Interpolate crossing points on each edge
                    // (only computed edges that are actually used, but compiler optimizes unused ones away)
                    Point top = LerpEdge(x, y, x + 1, y, tl, tr, threshold);
                    Point right = LerpEdge(x + 1, y, x + 1, y + 1, tr, br, threshold);
                    Point bottom = LerpEdge(x, y + 1, x + 1, y + 1, bl, br, threshold);
                    Point left = LerpEdge(x, y, x, y + 1, tl, bl, threshold);

                    switch (c)
                    {
                        case 1: segments.Add(new Segment(left, bottom)); break;
                        case 2: segments.Add(new Segment(bottom, right)); break;
                        case 3: segments.Add(new Segment(left, right)); break;
                        case 4: segments.Add(new Segment(top, right)); break;
                        case 5: // Saddle: TR and BL above
                            if ((tl + tr + br + bl) / 4.0 >= threshold)
                            { segments.Add(new Segment(top, left)); segments.Add(new Segment(bottom, right)); }
                            else
                            { segments.Add(new Segment(top, right)); segments.Add(new Segment(left, bottom)); }
                            break;
                        case 6: segments.Add(new Segment(top, bottom)); break;
                        case 7: segments.Add(new Segment(top, left)); break;
                        case 8: segments.Add(new Segment(top, left)); break;
                        case 9: segments.Add(new Segment(top, bottom)); break;
                        case 10: // Saddle: TL and BR above
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

            // ================================================================
            // Pass 2: Chain line segments into polylines.
            //
            // Adjacent marching squares cells produce segments that share
            // endpoints exactly (same edge, same interpolation → bit-identical).
            // We chain these into longer polylines for:
            //   - Fewer WPF Path figures = better rendering performance
            //   - Smooth line joins instead of disconnected segments
            // ================================================================
            return ChainSegments(segments);
        }

        /// <summary>
        /// Interpolates the contour crossing point along a cell edge.
        /// </summary>
        private static Point LerpEdge(int x1, int y1, int x2, int y2,
            double v1, double v2, double threshold)
        {
            double denom = v2 - v1;
            double t = (denom != 0) ? (threshold - v1) / denom : 0.5;
            // Clamp to [0,1] for robustness
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return new Point(x1 + t * (x2 - x1), y1 + t * (y2 - y1));
        }

        #region Segment Chaining

        private struct Segment
        {
            public Point A, B;
            public Segment(Point a, Point b) { A = a; B = b; }
        }

        /// <summary>
        /// Spatial hash key for a point. Quantized to 1/1000 pixel for exact matching
        /// of shared edge points between adjacent marching squares cells.
        /// </summary>
        private static long PointKey(Point p)
        {
            // Quantize to 1/1000 pixel — adjacent cells produce identical edge points
            long x = (long)(p.X * 1000.0 + 0.5);
            long y = (long)(p.Y * 1000.0 + 0.5);
            return x * 100000000L + y;
        }

        private static List<List<Point>> ChainSegments(List<Segment> segments)
        {
            if (segments.Count == 0)
                return new List<List<Point>>();

            // Build adjacency: point hash → list of segment indices touching that point
            var adjacency = new Dictionary<long, List<int>>(segments.Count * 2);

            for (int i = 0; i < segments.Count; i++)
            {
                long keyA = PointKey(segments[i].A);
                long keyB = PointKey(segments[i].B);

                if (!adjacency.TryGetValue(keyA, out var listA))
                {
                    listA = new List<int>(4);
                    adjacency[keyA] = listA;
                }
                listA.Add(i);

                if (!adjacency.TryGetValue(keyB, out var listB))
                {
                    listB = new List<int>(4);
                    adjacency[keyB] = listB;
                }
                listB.Add(i);
            }

            var used = new bool[segments.Count];
            var chains = new List<List<Point>>();

            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;

                // Start a new chain with this segment
                var chain = new List<Point>(32);
                chain.Add(segments[i].A);
                chain.Add(segments[i].B);

                // Extend forward from chain's last point
                ExtendChain(chain, false, segments, adjacency, used);

                // Extend backward from chain's first point
                ExtendChain(chain, true, segments, adjacency, used);

                if (chain.Count >= 2)
                    chains.Add(chain);
            }

            return chains;
        }

        /// <summary>
        /// Extends a chain by finding connected segments at one end.
        /// </summary>
        private static void ExtendChain(List<Point> chain, bool fromStart,
            List<Segment> segments, Dictionary<long, List<int>> adjacency, bool[] used)
        {
            while (true)
            {
                Point endpoint = fromStart ? chain[0] : chain[chain.Count - 1];
                long key = PointKey(endpoint);

                if (!adjacency.TryGetValue(key, out var neighbors))
                    break;

                // Find first unused neighbor
                int nextIdx = -1;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    if (!used[neighbors[i]])
                    {
                        nextIdx = neighbors[i];
                        break;
                    }
                }

                if (nextIdx < 0) break;
                used[nextIdx] = true;

                // Determine which end of the neighbor segment connects to our endpoint
                var seg = segments[nextIdx];
                Point newPoint;

                if (PointKey(seg.A) == key)
                    newPoint = seg.B;
                else
                    newPoint = seg.A;

                if (fromStart)
                    chain.Insert(0, newPoint);
                else
                    chain.Add(newPoint);
            }
        }

        #endregion
    }
}