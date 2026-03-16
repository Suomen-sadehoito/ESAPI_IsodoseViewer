using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPI_EQD2Viewer.Core.Interfaces;
using ESAPI_EQD2Viewer.Core.Extensions;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Calculations;

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

        public void Initialize(int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");

            _width = width;
            _height = height;
        }

        public void PreloadData(Image ctImage, Dose dose, double prescriptionDoseGy)
        {
            if (ctImage != null)
            {
                _ctCache = new int[ctImage.ZSize][,];
                for (int z = 0; z < ctImage.ZSize; z++)
                {
                    _ctCache[z] = new int[ctImage.XSize, ctImage.YSize];
                    ctImage.GetVoxels(z, _ctCache[z]);
                }
                _huOffset = DetermineHuOffset(ctImage);
            }

            if (dose != null)
            {
                _doseCache = new int[dose.ZSize][,];
                for (int z = 0; z < dose.ZSize; z++)
                {
                    _doseCache[z] = new int[dose.XSize, dose.YSize];
                    dose.GetVoxels(z, _doseCache[z]);
                }

                DoseValue dv0 = dose.VoxelToDoseValue(0);
                DoseValue dvRef = dose.VoxelToDoseValue(10000);

                _doseRawScale = (dvRef.Dose - dv0.Dose) / 10000.0;
                _doseRawOffset = dv0.Dose;

                if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                    _doseUnitToGyFactor = prescriptionDoseGy / 100.0;
                else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                    _doseUnitToGyFactor = 0.01;
                else
                    _doseUnitToGyFactor = 1.0;

                _doseScalingReady = true;
            }
        }

        private int DetermineHuOffset(Image ctImage)
        {
            int midSlice = ctImage.ZSize / 2;
            if (_ctCache == null || midSlice < 0 || midSlice >= _ctCache.Length)
                return 0;

            int[,] slice = _ctCache[midSlice];
            int xSize = ctImage.XSize;
            int ySize = ctImage.YSize;
            int step = 8;
            int countAboveThreshold = 0;
            int totalSamples = 0;

            for (int y = 0; y < ySize; y += step)
                for (int x = 0; x < xSize; x += step)
                {
                    totalSamples++;
                    if (slice[x, y] > 30000) countAboveThreshold++;
                }

            return (totalSamples > 0 && countAboveThreshold > totalSamples / 2) ? 32768 : 0;
        }

        public unsafe void RenderCtImage(Image ctImage, WriteableBitmap targetBitmap, int currentSlice,
            double windowLevel, double windowWidth)
        {
            if (_ctCache == null || currentSlice < 0 || currentSlice >= _ctCache.Length) return;

            int[,] currentCtSlice = _ctCache[currentSlice];
            if (currentCtSlice.GetLength(0) != _width || currentCtSlice.GetLength(1) != _height) return;

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

        #region Shared: Dose Grid Computation

        private DoseGridData PrepareDoseGrid(Image ctImage, Dose dose, int currentSlice,
            double planTotalDoseGy, double planNormalization, EQD2Settings eqd2Settings)
        {
            var result = new DoseGridData();

            if (dose == null || _doseCache == null || !_doseScalingReady)
            { result.StatusText = "No dose available."; return result; }

            double prescriptionGy = planTotalDoseGy;
            double normalization = planNormalization;
            if (double.IsNaN(normalization) || normalization <= 0) normalization = 100.0;
            else if (normalization < 5.0) normalization *= 100.0;

            double referenceDoseGy = prescriptionGy * (normalization / 100.0);
            if (referenceDoseGy < 0.1) referenceDoseGy = prescriptionGy;

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

            VVector ctPlaneCenterWorld = ctImage.Origin + ctImage.ZDirection * (currentSlice * ctImage.ZRes);
            VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
            int doseSlice = (int)Math.Round(relativeToDoseOrigin.Dot(dose.ZDirection) / dose.ZRes);

            if (doseSlice < 0 || doseSlice >= dose.ZSize)
            { result.StatusText = $"CT Z: {currentSlice} | Dose Z: {doseSlice} (Out of range)"; return result; }

            result.DoseSlice = doseSlice;
            int dx = dose.XSize, dy = dose.YSize;
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

            VVector ctBase = ctImage.Origin + ctImage.ZDirection * (currentSlice * ctImage.ZRes);
            VVector baseDiff = ctBase - dose.Origin;
            result.BaseX = baseDiff.Dot(dose.XDirection) / dose.XRes;
            result.BaseY = baseDiff.Dot(dose.YDirection) / dose.YRes;
            result.DxPerPx = ctImage.XRes * ctImage.XDirection.Dot(dose.XDirection) / dose.XRes;
            result.DxPerPy = ctImage.YRes * ctImage.YDirection.Dot(dose.XDirection) / dose.XRes;
            result.DyPerPx = ctImage.XRes * ctImage.XDirection.Dot(dose.YDirection) / dose.YRes;
            result.DyPerPy = ctImage.YRes * ctImage.YDirection.Dot(dose.YDirection) / dose.YRes;

            string label = eqd2Active ? "EQD2" : "Physical";
            result.StatusText = $"CT Z: {currentSlice} | Dose Z: {doseSlice} | " +
                                $"Max: {maxDose:F2} Gy | Ref: {referenceDoseGy:F2} Gy";
            return result;
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
                    map[py * w + px] = BilinearSample(g.DoseGyGrid, g.DoseWidth, g.DoseHeight,
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

        #endregion

        #region RenderDoseImage (Fill & Colorwash bitmap; Line mode clears only)

        public unsafe string RenderDoseImage(Image ctImage, Dose dose, WriteableBitmap targetBitmap,
            int currentSlice, double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            DoseDisplayMode displayMode = DoseDisplayMode.Line,
            double colorwashOpacity = 0.5, double colorwashMinPercent = 0.1,
            EQD2Settings eqd2Settings = null)
        {
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

                var grid = PrepareDoseGrid(ctImage, dose, currentSlice,
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

        #endregion

        #region Vector Contours (Line mode — marching squares)

        public ContourGenerationResult GenerateVectorContours(Image ctImage, Dose dose, int currentSlice,
            double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            EQD2Settings eqd2Settings = null)
        {
            var result = new ContourGenerationResult { Contours = new List<IsodoseContourData>() };

            var grid = PrepareDoseGrid(ctImage, dose, currentSlice,
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
                        ctx.BeginFigure(chain[0], false, false);
                        for (int j = 1; j < chain.Count; j++)
                            ctx.LineTo(chain[j], true, false);
                    }
                }
                geometry.Freeze();

                uint c = levels[i].Color;
                var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                    (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF)));
                brush.Freeze();

                result.Contours.Add(new IsodoseContourData
                {
                    Geometry = geometry,
                    Stroke = brush,
                    StrokeThickness = 1.0
                });
            }

            string label = grid.IsEQD2 ? "EQD2" : "Physical";
            result.StatusText = $"[{label} Line] {grid.StatusText}";
            return result;
        }

        #endregion

        #region Bilinear Interpolation

        private static double BilinearSample(double[,] grid, int gw, int gh, double fx, double fy)
        {
            if (fx < 0 || fy < 0 || fx >= gw - 1 || fy >= gh - 1)
            {
                int nx = (int)Math.Round(fx), ny = (int)Math.Round(fy);
                return (nx >= 0 && nx < gw && ny >= 0 && ny < gh) ? grid[nx, ny] : 0;
            }
            int x0 = (int)fx, y0 = (int)fy;
            double tx = fx - x0, ty = fy - y0;
            return grid[x0, y0] * (1 - tx) * (1 - ty) + grid[x0 + 1, y0] * tx * (1 - ty)
                 + grid[x0, y0 + 1] * (1 - tx) * ty + grid[x0 + 1, y0 + 1] * tx * ty;
        }

        #endregion

        #region Fill Mode — CT-resolution

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

        #endregion

        #region Colorwash Mode — CT-resolution

        private unsafe void RenderColorwashMode(byte* pBuffer, int stride, double[] ctDoseMap,
            double refDoseGy, byte alpha, double minPercent)
        {
            double minGy = refDoseGy * minPercent, maxGy = refDoseGy * 1.15;
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
                    row[px] = JetColormap(f, alpha);
                }
            }
        }

        private static uint JetColormap(double t, byte a)
        {
            double r, g, b;
            if (t < 0.125) { r = 0; g = 0; b = 0.5 + t * 4.0; }
            else if (t < 0.375) { r = 0; g = (t - 0.125) * 4.0; b = 1.0; }
            else if (t < 0.625) { r = (t - 0.375) * 4.0; g = 1.0; b = 1.0 - (t - 0.375) * 4.0; }
            else if (t < 0.875) { r = 1.0; g = 1.0 - (t - 0.625) * 4.0; b = 0; }
            else { r = 1.0 - (t - 0.875) * 4.0; g = 0; b = 0; }
            byte rb = (byte)(Clamp01(r) * 255), gb = (byte)(Clamp01(g) * 255), bb = (byte)(Clamp01(b) * 255);
            return ((uint)a << 24) | ((uint)rb << 16) | ((uint)gb << 8) | bb;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        #endregion

        #region GetDoseAtPixel

        public double GetDoseAtPixel(Image ctImage, Dose dose, int currentSlice, int pixelX, int pixelY,
            EQD2Settings eqd2Settings = null)
        {
            if (dose == null || _doseCache == null || !_doseScalingReady) return double.NaN;
            if (pixelX < 0 || pixelX >= _width || pixelY < 0 || pixelY >= _height) return double.NaN;

            VVector worldPos = ctImage.Origin + ctImage.XDirection * (pixelX * ctImage.XRes)
                             + ctImage.YDirection * (pixelY * ctImage.YRes) + ctImage.ZDirection * (currentSlice * ctImage.ZRes);
            VVector diff = worldPos - dose.Origin;
            int dx = (int)Math.Round(diff.Dot(dose.XDirection) / dose.XRes);
            int dy = (int)Math.Round(diff.Dot(dose.YDirection) / dose.YRes);
            int dz = (int)Math.Round(diff.Dot(dose.ZDirection) / dose.ZRes);

            if (dx < 0 || dx >= dose.XSize || dy < 0 || dy >= dose.YSize || dz < 0 || dz >= dose.ZSize)
                return double.NaN;

            double dGy = (_doseCache[dz][dx, dy] * _doseRawScale + _doseRawOffset) * _doseUnitToGyFactor;
            if (eqd2Settings != null && eqd2Settings.IsEnabled && eqd2Settings.NumberOfFractions > 0 && eqd2Settings.AlphaBeta > 0)
                dGy = EQD2Calculator.ToEQD2(dGy, eqd2Settings.NumberOfFractions, eqd2Settings.AlphaBeta);
            return dGy;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ctCache = null;
            _doseCache = null;
        }
    }
}