using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using System.Windows;
using EQD2Viewer.Core.Logging;

namespace ESAPI_EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Rasterizes structure contour polygons into per-slice bitmasks for fast DVH computation.
    /// 
    /// APPROACH:
    /// Phase 1 (UI thread): Extract contour polygons from ESAPI Structure objects,
    ///   convert world coordinates to CT pixel coordinates, store as plain arrays.
    /// Phase 2 (any thread): For each DVH calculation, scan-line rasterize the polygon
    ///   to determine which CT pixels are inside the structure on each slice.
    /// 
    /// This is dramatically faster than calling Structure.IsPointInsideSegment() per voxel.
    /// </summary>
    public static class StructureRasterizer
    {
        /// <summary>
        /// Rasterizes a polygon (in CT pixel coordinates) to a bitmask.
        /// Uses scan-line ray-casting: for each row, find X-intersections with polygon edges,
        /// then fill between pairs.
        /// </summary>
        /// <param name="polygonPoints">Polygon vertices in CT pixel coordinates (closed loop)</param>
        /// <param name="width">CT image width in pixels</param>
        /// <param name="height">CT image height in pixels</param>
        /// <returns>Boolean mask [width * height], true = inside structure</returns>
        public static bool[] RasterizePolygon(Point2D[] polygonPoints, int width, int height)
        {
            bool[] mask = new bool[width * height];
            if (polygonPoints == null || polygonPoints.Length < 3)
                return mask;

            // Find bounding box to limit scan range
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < polygonPoints.Length; i++)
            {
                if (polygonPoints[i].Y < minY) minY = polygonPoints[i].Y;
                if (polygonPoints[i].Y > maxY) maxY = polygonPoints[i].Y;
            }

            int yStart = Math.Max(0, (int)minY);
            int yEnd = Math.Min(height - 1, (int)Math.Ceiling(maxY));

            int n = polygonPoints.Length;
            var xIntersections = new List<double>(16);

            for (int y = yStart; y <= yEnd; y++)
            {
                double scanY = y + 0.5; // Center of pixel row
                xIntersections.Clear();

                // Find all X-intersections of scan line with polygon edges
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    double y1 = polygonPoints[i].Y, y2 = polygonPoints[j].Y;

                    // Skip horizontal edges and edges that don't cross scanY
                    if ((y1 < scanY && y2 < scanY) || (y1 >= scanY && y2 >= scanY))
                        continue;
                    if (Math.Abs(y2 - y1) < 1e-10)
                        continue;

                    double x1 = polygonPoints[i].X, x2 = polygonPoints[j].X;
                    double t = (scanY - y1) / (y2 - y1);
                    double xIntersect = x1 + t * (x2 - x1);
                    xIntersections.Add(xIntersect);
                }

                // Sort intersections and fill between pairs
                xIntersections.Sort();
                for (int k = 0; k + 1 < xIntersections.Count; k += 2)
                {
                    int xStart = Math.Max(0, (int)Math.Ceiling(xIntersections[k]));
                    int xEnd = Math.Min(width - 1, (int)Math.Floor(xIntersections[k + 1]));
                    int rowOffset = y * width;

                    for (int x = xStart; x <= xEnd; x++)
                        mask[rowOffset + x] = true;
                }
            }

            return mask;
        }

        /// <summary>
        /// Combines multiple contour polygon masks for the same slice using XOR (handles holes).
        /// ESAPI structures can have multiple contours per slice (outer boundary + holes).
        /// </summary>
        public static bool[] CombineContourMasks(List<bool[]> masks, int width, int height)
        {
            if (masks == null || masks.Count == 0)
                return new bool[width * height];

            if (masks.Count == 1)
                return masks[0];

            bool[] combined = new bool[width * height];
            foreach (var mask in masks)
            {
                for (int i = 0; i < combined.Length; i++)
                {
                    if (mask[i])
                        combined[i] = !combined[i]; // XOR for hole handling
                }
            }

            return combined;
        }

        /// <summary>
        /// Converts ESAPI world-coordinate contour points to CT pixel coordinates.
        /// </summary>
        /// <param name="worldPoints">Contour vertices in mm (DICOM patient coordinates)</param>
        /// <param name="imageOriginX">CT image origin X in mm</param>
        /// <param name="imageOriginY">CT image origin Y in mm</param>
        /// <param name="pixelSpacingX">CT pixel spacing X in mm</param>
        /// <param name="pixelSpacingY">CT pixel spacing Y in mm</param>
        /// <param name="xDirX">CT X-direction X component</param>
        /// <param name="xDirY">CT X-direction Y component</param>
        /// <param name="yDirX">CT Y-direction X component</param>
        /// <param name="yDirY">CT Y-direction Y component</param>
        /// <returns>Points in CT pixel coordinates</returns>
        public static Point2D[] WorldToPixel(
            double[][] worldPoints,
            double imageOriginX, double imageOriginY, double imageOriginZ,
            double pixelSpacingX, double pixelSpacingY,
            double xDirX, double xDirY, double xDirZ,
            double yDirX, double yDirY, double yDirZ)
        {
            if (worldPoints == null) return null;

            var pixels = new Point2D[worldPoints.Length];
            for (int i = 0; i < worldPoints.Length; i++)
            {
                double dx = worldPoints[i][0] - imageOriginX;
                double dy = worldPoints[i][1] - imageOriginY;
                double dz = worldPoints[i][2] - imageOriginZ;

                // Project onto image plane axes
                double px = (dx * xDirX + dy * xDirY + dz * xDirZ) / pixelSpacingX;
                double py = (dx * yDirX + dy * yDirY + dz * yDirZ) / pixelSpacingY;

                pixels[i] = new Point2D(px, py);
            }

            return pixels;
        }
    }
}
