using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Interfaces;
using ESAPI_EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Calculations;

namespace ESAPI_EQD2Viewer.Services
{
    public class ImageRenderingService : IImageRenderingService
    {
        private int _width;
        private int _height;

        private int[][,] _ctCache;
        private int[][,] _doseCache;

        private double _doseRawScale;
        private double _doseRawOffset;
        private double _doseUnitToGyFactor;
        private bool _doseScalingReady;

        private int _huOffset;
        private bool _disposed;

        // ── Cached geometry for ESAPI-free rendering ──
        private VolumeGeometry _ctGeo;
        private VolumeGeometry _doseGeo;

        public void Initialize(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            _width = width;
            _height = height;
        }

        public void PreloadData(VolumeData ctImage, DoseVolumeData dose)
        {
            if (ctImage != null)
            {
                _ctCache = ctImage.Voxels;
                _huOffset = ctImage.HuOffset;
                _ctGeo = ctImage.Geometry;
            }
            if (dose != null)
            {
                _doseCache = dose.Voxels;
                _doseRawScale = dose.Scaling.RawScale;
                _doseRawOffset = dose.Scaling.RawOffset;
                _doseUnitToGyFactor = dose.Scaling.UnitToGy;
                _doseScalingReady = true;
                _doseGeo = dose.Geometry;
            }
        }

        // ================================================================
        // Shared Helper
        // ================================================================
        private static void AssertBitmapCompatible(WriteableBitmap bmp, int width, int height)
        {
            Debug.Assert(bmp.PixelWidth == width && bmp.PixelHeight == height,
                $"Bitmap size mismatch: expected {width}x{height}, got {bmp.PixelWidth}x{bmp.PixelHeight}");
            Debug.Assert(bmp.BackBufferStride >= width * 4,
                $"Stride too small: {bmp.BackBufferStride} < {width * 4}");
        }

        private double[] BuildCtResolutionDoseMap(DoseGridData g)
        {
            int w = _width, h = _height;
            double[] map = new double[w * h];
            for (int py = 0; py < h; py++)
            {
                double rxBase = g.BaseX + py * g.DxPerPy;
                double ryBase = g.BaseY + py * g.DyPerPy;
                for (int px = 0; px < w; px++)
                    map[py * w + px] = ImageUtils.BilinearSample(g.DoseGyGrid, g.DoseWidth, g.DoseHeight,
                        rxBase + px * g.DxPerPx, ryBase + px * g.DyPerPx);
            }
            return map;
        }

        private class DoseGridData
        {
            public double[,] DoseGyGrid;
            public int DoseWidth, DoseHeight, DoseSlice;
            public double ReferenceDoseGy, MaxDoseInSlice;
            public double BaseX, BaseY, DxPerPx, DxPerPy, DyPerPx, DyPerPy;
            public bool IsEQD2;
            public string StatusText;
            public bool IsValid => DoseGyGrid != null;
        }

        // ================================================================
        // Rendering (Uses Cached Geometry)
        // ================================================================

        private DoseGridData PrepareDoseGrid(int currentSlice,
            double planTotalDoseGy, double planNormalization, EQD2Settings eqd2Settings)
        {
            var result = new DoseGridData();

            if (_doseCache == null || !_doseScalingReady || _ctGeo == null || _doseGeo == null)
            { result.StatusText = "No dose available."; return result; }

            double prescriptionGy = planTotalDoseGy;
            double normalization = planNormalization;
            if (double.IsNaN(normalization) || normalization <= 0) normalization = 100.0;
            else if (normalization < DomainConstants.NormalizationFractionThreshold) normalization *= 100.0;

            double referenceDoseGy = prescriptionGy * (normalization / 100.0);
            if (referenceDoseGy < DomainConstants.MinReferenceDoseGy)
                referenceDoseGy = prescriptionGy;

            bool eqd2Active = eqd2Settings != null && eqd2Settings.IsEnabled
                              && eqd2Settings.NumberOfFractions > 0 && eqd2Settings.AlphaBeta > 0;

            double eqd2QuadFactor = 0, eqd2LinFactor = 1.0;
            if (eqd2Active)
            {
                referenceDoseGy = EQD2Calculator.ToEQD2(referenceDoseGy,
                    eqd2Settings.NumberOfFractions, eqd2Settings.AlphaBeta);
                EQD2Calculator.GetVoxelScalingFactors(eqd2Settings.NumberOfFractions,
                    eqd2Settings.AlphaBeta, out eqd2QuadFactor, out eqd2LinFactor);
            }

            result.ReferenceDoseGy = referenceDoseGy;
            result.IsEQD2 = eqd2Active;

            // CT slice → dose slice mapping using cached geometry
            var ctO = _ctGeo.Origin;
            var ctZ = _ctGeo.ZDirection;
            Vec3 ctPlaneCenter = ctO + ctZ * (currentSlice * _ctGeo.ZRes);
            Vec3 relToDose = ctPlaneCenter - _doseGeo.Origin;
            int doseSlice = (int)Math.Round(relToDose.Dot(_doseGeo.ZDirection) / _doseGeo.ZRes);

            if (doseSlice < 0 || doseSlice >= _doseGeo.ZSize)
            { result.StatusText = $"CT Z: {currentSlice} | Dose Z: {doseSlice} (Out of range)"; return result; }

            result.DoseSlice = doseSlice;
            int dx = _doseGeo.XSize, dy = _doseGeo.YSize;
            int[,] doseBuffer = _doseCache[doseSlice];

            double maxDose = 0;
            double[,] doseGyGrid = new double[dx, dy];
            for (int y = 0; y < dy; y++)
                for (int x = 0; x < dx; x++)
                {
                    double dGy = (doseBuffer[x, y] * _doseRawScale + _doseRawOffset) * _doseUnitToGyFactor;
                    if (eqd2Active) dGy = EQD2Calculator.ToEQD2Fast(dGy, eqd2QuadFactor, eqd2LinFactor);
                    doseGyGrid[x, y] = dGy;
                    if (dGy > maxDose) maxDose = dGy;
                }

            result.DoseGyGrid = doseGyGrid;
            result.DoseWidth = dx;
            result.DoseHeight = dy;
            result.MaxDoseInSlice = maxDose;

            // Coordinate mapping: CT pixel → dose voxel
            Vec3 ctBase = ctO + _ctGeo.ZDirection * (currentSlice * _ctGeo.ZRes);
            Vec3 baseDiff = ctBase - _doseGeo.Origin;
            result.BaseX = baseDiff.Dot(_doseGeo.XDirection) / _doseGeo.XRes;
            result.BaseY = baseDiff.Dot(_doseGeo.YDirection) / _doseGeo.YRes;
            result.DxPerPx = _ctGeo.XRes * _ctGeo.XDirection.Dot(_doseGeo.XDirection) / _doseGeo.XRes;
            result.DxPerPy = _ctGeo.YRes * _ctGeo.YDirection.Dot(_doseGeo.XDirection) / _doseGeo.XRes;
            result.DyPerPx = _ctGeo.XRes * _ctGeo.XDirection.Dot(_doseGeo.YDirection) / _doseGeo.YRes;
            result.DyPerPy = _ctGeo.YRes * _ctGeo.YDirection.Dot(_doseGeo.YDirection) / _doseGeo.YRes;

            result.StatusText = $"CT Z: {currentSlice} | Dose Z: {doseSlice} | " +
                                $"Max: {maxDose:F2} Gy | Ref: {referenceDoseGy:F2} Gy";
            return result;
        }

        public unsafe void RenderCtImage(WriteableBitmap targetBitmap, int currentSlice,
            double windowLevel, double windowWidth)
        {
            if (_ctCache == null || currentSlice < 0 || currentSlice >= _ctCache.Length) return;

            int[,] currentCtSlice = _ctCache[currentSlice];
            if (currentCtSlice.GetLength(0) != _width || currentCtSlice.GetLength(1) != _height) return;

            AssertBitmapCompatible(targetBitmap, _width, _height);

            targetBitmap.Lock();
            try
            {
                byte* pBackBuffer = (byte*)targetBitmap.BackBuffer;
                int stride = targetBitmap.BackBufferStride;
                double huMin = windowLevel - (windowWidth / 2.0);
                double factor = (windowWidth > 0) ? 255.0 / windowWidth : 0;
                int huOffset = _huOffset;

                for (int y = 0; y < _height; y++)
                {
                    uint* pRow = (uint*)(pBackBuffer + y * stride);
                    for (int x = 0; x < _width; x++)
                    {
                        int hu = currentCtSlice[x, y] - huOffset;
                        double valDouble = (hu - huMin) * factor;
                        byte val = (byte)(valDouble < 0 ? 0 : (valDouble > 255 ? 255 : valDouble));
                        pRow[x] = (0xFFu << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                    }
                }
                targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally { targetBitmap.Unlock(); }
        }

        public unsafe string RenderDoseImage(WriteableBitmap targetBitmap,
            int currentSlice, double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            DoseDisplayMode displayMode, double colorwashOpacity, double colorwashMinPercent,
            EQD2Settings eqd2Settings)
        {
            AssertBitmapCompatible(targetBitmap, _width, _height);

            targetBitmap.Lock();
            try
            {
                int doseStride = targetBitmap.BackBufferStride;
                byte* pDoseBuffer = (byte*)targetBitmap.BackBuffer;
                for (int i = 0; i < _height * doseStride; i++) pDoseBuffer[i] = 0;

                if (displayMode == DoseDisplayMode.Line)
                {
                    targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                    return "";
                }

                var grid = PrepareDoseGrid(currentSlice,
                    planTotalDoseGy, planNormalization, eqd2Settings);

                if (!grid.IsValid)
                { targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height)); return grid.StatusText; }

                double[] ctDoseMap = BuildCtResolutionDoseMap(grid);

                switch (displayMode)
                {
                    case DoseDisplayMode.Fill:
                        RenderFillMode(pDoseBuffer, doseStride, ctDoseMap, grid.ReferenceDoseGy, levels);
                        break;
                    case DoseDisplayMode.Colorwash:
                        byte cwAlpha = (byte)(Math.Max(0, Math.Min(1, colorwashOpacity)) * 255);
                        RenderColorwashMode(pDoseBuffer, doseStride, ctDoseMap, grid.ReferenceDoseGy, cwAlpha, colorwashMinPercent);
                        break;
                }

                targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                string label = grid.IsEQD2 ? "EQD2" : "Physical";
                return $"[{label} {displayMode}] {grid.StatusText}";
            }
            finally { targetBitmap.Unlock(); }
        }

        public ContourGenerationResult GenerateVectorContours(int currentSlice,
            double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            EQD2Settings eqd2Settings)
        {
            var result = new ContourGenerationResult { Contours = new List<IsodoseContourData>() };

            var grid = PrepareDoseGrid(currentSlice,
                planTotalDoseGy, planNormalization, eqd2Settings);

            if (!grid.IsValid || levels == null || levels.Length == 0)
            { result.StatusText = grid.StatusText ?? "No data"; return result; }

            double[] ctDoseMap = BuildCtResolutionDoseMap(grid);
            int w = _width, h = _height;

            for (int i = 0; i < levels.Length; i++)
            {
                if (!levels[i].IsVisible) continue;

                double thresholdGy = grid.ReferenceDoseGy * levels[i].Fraction;
                var polylines = MarchingSquares.GenerateContours(ctDoseMap, w, h, thresholdGy);
                if (polylines.Count == 0) continue;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    foreach (var chain in polylines)
                    {
                        if (chain.Count < 2) continue;
                        ctx.BeginFigure(new System.Windows.Point(chain[0].X, chain[0].Y), false, false);
                        for (int j = 1; j < chain.Count; j++)
                            ctx.LineTo(new System.Windows.Point(chain[j].X, chain[j].Y), true, false);
                    }
                }
                geometry.Freeze();

                uint c = levels[i].Color;
                var brush = new SolidColorBrush(Color.FromRgb(
                    (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF)));
                brush.Freeze();

                result.Contours.Add(new IsodoseContourData
                { Geometry = geometry, Stroke = brush, StrokeThickness = 1.0 });
            }

            string label = grid.IsEQD2 ? "EQD2" : "Physical";
            result.StatusText = $"[{label} Line] {grid.StatusText}";
            return result;
        }

        public List<StructureContourData> GenerateStructureContours(int currentSlice,
            IEnumerable<StructureData> structures)
        {
            var result = new List<StructureContourData>();
            if (structures == null || _ctGeo == null) return result;

            foreach (var structure in structures)
            {
                if (!structure.HasMesh) continue;

                if (!structure.ContoursBySlice.TryGetValue(currentSlice, out var sliceContours)) continue;
                if (sliceContours == null || sliceContours.Count == 0) continue;

                try
                {
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        foreach (var contour in sliceContours)
                        {
                            if (contour.Length < 3) continue;

                            var pixels = StructureRasterizer.WorldToPixel(contour,
                                _ctGeo.Origin.X, _ctGeo.Origin.Y, _ctGeo.Origin.Z,
                                _ctGeo.XRes, _ctGeo.YRes,
                                _ctGeo.XDirection.X, _ctGeo.XDirection.Y, _ctGeo.XDirection.Z,
                                _ctGeo.YDirection.X, _ctGeo.YDirection.Y, _ctGeo.YDirection.Z);

                            if (pixels == null || pixels.Length < 3) continue;

                            ctx.BeginFigure(new System.Windows.Point(pixels[0].X, pixels[0].Y), false, true);
                            for (int i = 1; i < pixels.Length; i++)
                                ctx.LineTo(new System.Windows.Point(pixels[i].X, pixels[i].Y), true, false);
                        }
                    }
                    geometry.Freeze();

                    var brush = new SolidColorBrush(Color.FromArgb(structure.ColorA, structure.ColorR, structure.ColorG, structure.ColorB));
                    brush.Freeze();

                    var contourData = new StructureContourData
                    {
                        Geometry = geometry,
                        Stroke = brush,
                        StrokeThickness = RenderConstants.StructureContourThickness,
                        StructureId = structure.Id
                    };

                    if (structure.DicomType == "SUPPORT" || structure.DicomType == "EXTERNAL")
                    {
                        contourData.StrokeDashArray = new DoubleCollection { 4, 2 };
                        contourData.StrokeDashArray.Freeze();
                    }

                    result.Add(contourData);
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"Could not render structure '{structure.Id}': {ex.Message}");
                }
            }
            return result;
        }

        public double GetDoseAtPixel(int currentSlice, int pixelX, int pixelY,
            EQD2Settings eqd2Settings)
        {
            if (_doseCache == null || !_doseScalingReady || _ctGeo == null || _doseGeo == null)
                return double.NaN;
            if (pixelX < 0 || pixelX >= _width || pixelY < 0 || pixelY >= _height)
                return double.NaN;

            Vec3 worldPos = _ctGeo.Origin
                + _ctGeo.XDirection * (pixelX * _ctGeo.XRes)
                + _ctGeo.YDirection * (pixelY * _ctGeo.YRes)
                + _ctGeo.ZDirection * (currentSlice * _ctGeo.ZRes);

            Vec3 diff = worldPos - _doseGeo.Origin;
            int dx = (int)Math.Round(diff.Dot(_doseGeo.XDirection) / _doseGeo.XRes);
            int dy = (int)Math.Round(diff.Dot(_doseGeo.YDirection) / _doseGeo.YRes);
            int dz = (int)Math.Round(diff.Dot(_doseGeo.ZDirection) / _doseGeo.ZRes);

            if (dx < 0 || dx >= _doseGeo.XSize || dy < 0 || dy >= _doseGeo.YSize
                || dz < 0 || dz >= _doseGeo.ZSize)
                return double.NaN;

            double dGy = (_doseCache[dz][dx, dy] * _doseRawScale + _doseRawOffset) * _doseUnitToGyFactor;

            if (eqd2Settings != null && eqd2Settings.IsEnabled
                && eqd2Settings.NumberOfFractions > 0 && eqd2Settings.AlphaBeta > 0)
                dGy = EQD2Calculator.ToEQD2(dGy, eqd2Settings.NumberOfFractions, eqd2Settings.AlphaBeta);

            return dGy;
        }

        // ================================================================
        // Rendering Implementations (Fill & Colorwash)
        // ================================================================

        private unsafe void RenderFillMode(byte* pBuffer, int stride, double[] ctDoseMap,
            double refDoseGy, IsodoseLevel[] levels)
        {
            if (levels == null || levels.Length == 0) return;

            int vc = 0;
            for (int i = 0; i < levels.Length; i++) if (levels[i].IsVisible) vc++;
            if (vc == 0) return;

            double[] thr = new double[vc];
            uint[] col = new uint[vc];
            int vi = 0;
            for (int i = 0; i < levels.Length; i++)
            {
                if (!levels[i].IsVisible) continue;
                thr[vi] = refDoseGy * levels[i].Fraction;
                col[vi] = (levels[i].Color & 0x00FFFFFF) | ((uint)levels[i].Alpha << 24);
                vi++;
            }

            int w = _width, h = _height;
            for (int py = 0; py < h; py++)
            {
                uint* row = (uint*)(pBuffer + py * stride);
                int ro = py * w;
                for (int px = 0; px < w; px++)
                {
                    double d = ctDoseMap[ro + px];
                    if (d <= 0) continue;
                    for (int li = 0; li < vc; li++)
                        if (d >= thr[li]) { row[px] = col[li]; break; }
                }
            }
        }

        private unsafe void RenderColorwashMode(byte* pBuffer, int stride, double[] ctDoseMap,
            double refDoseGy, byte alpha, double minPercent)
        {
            double minGy = refDoseGy * minPercent;
            double maxGy = refDoseGy * RenderConstants.ColorwashMaxFraction;
            double range = maxGy - minGy;
            if (range <= 0) return;

            int w = _width, h = _height;
            for (int py = 0; py < h; py++)
            {
                uint* row = (uint*)(pBuffer + py * stride);
                int ro = py * w;
                for (int px = 0; px < w; px++)
                {
                    double d = ctDoseMap[ro + px];
                    if (d < minGy) continue;
                    double f = (d - minGy) / range;
                    if (f > 1.0) f = 1.0;
                    row[px] = ColorMaps.Jet(f, alpha);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ctCache = null;
            _doseCache = null;
            _ctGeo = null;
            _doseGeo = null;
        }
    }
}