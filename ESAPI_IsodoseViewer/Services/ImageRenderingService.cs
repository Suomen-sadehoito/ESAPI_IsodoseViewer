using System;
using System.Windows;
using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPI_IsodoseViewer.Helpers;

namespace ESAPI_IsodoseViewer.Services
{
    public class ImageRenderingService : IImageRenderingService
    {
        private int _width;
        private int _height;

        // Caches for storing all slices in memory to improve performance
        private int[][,] _ctCache;
        private int[][,] _doseCache;

        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public void PreloadData(Image ctImage, Dose dose)
        {
            if (ctImage != null)
            {
                _ctCache = new int[ctImage.ZSize][,];
                for (int z = 0; z < ctImage.ZSize; z++)
                {
                    _ctCache[z] = new int[ctImage.XSize, ctImage.YSize];
                    ctImage.GetVoxels(z, _ctCache[z]);
                }
            }

            if (dose != null)
            {
                _doseCache = new int[dose.ZSize][,];
                for (int z = 0; z < dose.ZSize; z++)
                {
                    _doseCache[z] = new int[dose.XSize, dose.YSize];
                    dose.GetVoxels(z, _doseCache[z]);
                }
            }
        }

        public unsafe void RenderCtImage(Image ctImage, WriteableBitmap targetBitmap, int currentSlice, double windowLevel, double windowWidth)
        {
            // Ensure cache is loaded and slice is within bounds
            if (_ctCache == null || currentSlice < 0 || currentSlice >= _ctCache.Length) return;

            int[,] currentCtSlice = _ctCache[currentSlice];

            targetBitmap.Lock();
            byte* pBackBuffer = (byte*)targetBitmap.BackBuffer;
            int stride = targetBitmap.BackBufferStride;

            double huMin = windowLevel - (windowWidth / 2.0);
            double factor = 255.0 / windowWidth;

            // Auto-detect offset based on center pixel
            int rawCenter = currentCtSlice[_width / 2, _height / 2];
            int huOffset = (rawCenter > 30000) ? 32768 : 0;

            for (int y = 0; y < _height; y++)
            {
                uint* pRow = (uint*)(pBackBuffer + y * stride);
                for (int x = 0; x < _width; x++)
                {
                    int hu = currentCtSlice[x, y] - huOffset;
                    double valDouble = (hu - huMin) * factor;
                    byte val = (byte)(valDouble < 0 ? 0 : (valDouble > 255 ? 255 : valDouble));

                    // BGRA format
                    pRow[x] = (0xFFu << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                }
            }

            targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            targetBitmap.Unlock();
        }

        public unsafe string RenderDoseImage(Image ctImage, Dose dose, WriteableBitmap targetBitmap, int currentSlice, double planTotalDose, double planNormalization)
        {
            targetBitmap.Lock();
            int doseStride = targetBitmap.BackBufferStride;
            byte* pDoseBuffer = (byte*)targetBitmap.BackBuffer;

            // Clear dose buffer
            for (int i = 0; i < _height * doseStride; i++) pDoseBuffer[i] = 0;

            if (dose == null || _doseCache == null)
            {
                targetBitmap.Unlock();
                return "No dose available.";
            }

            double prescriptionGy = planTotalDose;
            double normalization = planNormalization;
            if (double.IsNaN(normalization) || normalization <= 0) normalization = 100.0;
            else if (normalization < 5.0) normalization *= 100.0;

            double referenceDoseGy = prescriptionGy * (normalization / 100.0);
            if (referenceDoseGy < 0.1) referenceDoseGy = prescriptionGy;

            // Dose Z lookup
            VVector ctPlaneCenterWorld = ctImage.Origin + ctImage.ZDirection * (currentSlice * ctImage.ZRes);
            VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
            int doseSlice = (int)Math.Round(relativeToDoseOrigin.Dot(dose.ZDirection) / dose.ZRes);

            if (doseSlice < 0 || doseSlice >= dose.ZSize)
            {
                targetBitmap.Unlock();
                return $"CT Z: {currentSlice} | Dose Z: {doseSlice} (Out of range)";
            }

            // Retrieve dose slice from cache instead of calling GetVoxels
            int dx = dose.XSize;
            int dy = dose.YSize;
            int[,] doseBuffer = _doseCache[doseSlice];

            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(10000);
            double rawScale = (dvRef.Dose - dv0.Dose) / 10000.0;
            double rawOffset = dv0.Dose;
            double unitToGyFactor = (dvRef.Unit == DoseValue.DoseUnit.Percent) ? prescriptionGy / 100.0 :
                                    (dvRef.Unit == DoseValue.DoseUnit.cGy) ? 0.01 : 1.0;

            var levels = new[] {
                new { Pct = 1.07, Color = 0xFFFF0000 },
                new { Pct = 0.95, Color = 0xFF00FF00 },
                new { Pct = 0.80, Color = 0xFF00FFFF },
                new { Pct = 0.50, Color = 0xFF0000FF }
            };

            double maxDoseInSlice = 0;
            double scaleX = dose.XRes / ctImage.XRes;
            double scaleY = dose.YRes / ctImage.YRes;

            for (int y = 0; y < dy; y++)
            {
                for (int x = 0; x < dx; x++)
                {
                    double valInUnits = doseBuffer[x, y] * rawScale + rawOffset;
                    double dGy = valInUnits * unitToGyFactor;
                    if (dGy > maxDoseInSlice) maxDoseInSlice = dGy;

                    uint color = 0;
                    foreach (var level in levels)
                    {
                        if (dGy >= referenceDoseGy * level.Pct)
                        {
                            color = level.Color;
                            break;
                        }
                    }

                    if (color != 0)
                    {
                        color = (color & 0x00FFFFFF) | (0x4C000000); // Set alpha to ~0.3

                        VVector worldPos = dose.Origin +
                                           dose.XDirection * (x * dose.XRes) +
                                           dose.YDirection * (y * dose.YRes) +
                                           dose.ZDirection * (doseSlice * dose.ZRes);

                        VVector diff = worldPos - ctImage.Origin;
                        double px = diff.Dot(ctImage.XDirection) / ctImage.XRes;
                        double py = diff.Dot(ctImage.YDirection) / ctImage.YRes;

                        int startX = (int)(px - scaleX / 2.0);
                        int startY = (int)(py - scaleY / 2.0);
                        int endX = (int)(px + scaleX / 2.0);
                        int endY = (int)(py + scaleY / 2.0);

                        for (int pyImg = startY; pyImg < endY; pyImg++)
                        {
                            if (pyImg < 0 || pyImg >= _height) continue;
                            uint* row = (uint*)(pDoseBuffer + pyImg * doseStride);

                            for (int pxImg = startX; pxImg < endX; pxImg++)
                            {
                                if (pxImg >= 0 && pxImg < _width)
                                {
                                    row[pxImg] = color;
                                }
                            }
                        }
                    }
                }
            }

            targetBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            targetBitmap.Unlock();

            return $"CT Z: {currentSlice} | Dose Z: {doseSlice} | Max: {maxDoseInSlice:F2} Gy | Ref: {referenceDoseGy:F2} Gy";
        }
    }
}