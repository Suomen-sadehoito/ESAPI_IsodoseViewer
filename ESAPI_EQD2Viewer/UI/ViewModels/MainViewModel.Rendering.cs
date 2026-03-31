using ESAPI_EQD2Viewer.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Logging;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        internal void RequestRender()
        {
            if (Interlocked.CompareExchange(ref _renderPendingFlag, 1, 0) != 0) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _renderPendingFlag, 0);
                RenderScene();
            }), DispatcherPriority.Render);
        }

        private void RenderScene()
        {
            if (_disposed) return;
            if (_snapshot?.CtImage == null) return;

            if (_isRendering) return;
            _isRendering = true;
            try
            {
                _renderingService.RenderCtImage(CtImageSource, CurrentSlice, WindowLevel, WindowWidth);
                RenderStructureContours();
                RenderRegistrationOverlay();

                if (_isSummationActive && _summationService != null && _summationService.HasSummedDose)
                    RenderSummationScene();
                else
                    RenderSinglePlanDose();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("RenderScene failed", ex);
            }
            finally { _isRendering = false; }
        }

        private void RenderSinglePlanDose()
        {
            double planTotalDoseGy = GetPrescriptionGy();
            double planNormalization = _snapshot?.ActivePlan?.PlanNormalization ?? 100.0;

            EQD2Settings eqd2 = _isEQD2Enabled
                ? new EQD2Settings { IsEnabled = true, AlphaBeta = _displayAlphaBeta, NumberOfFractions = _numberOfFractions }
                : null;

            if (_doseDisplayMode == DoseDisplayMode.Line)
            {
                _renderingService.RenderDoseImage(DoseImageSource, CurrentSlice,
                    planTotalDoseGy, planNormalization, _isodoseLevelArray,
                    _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);

                var result = _renderingService.GenerateVectorContours(CurrentSlice,
                    planTotalDoseGy, planNormalization, _isodoseLevelArray, eqd2);

                ContourLines = new ObservableCollection<IsodoseContourData>(result.Contours);
                StatusText = result.StatusText ?? "";
            }
            else
            {
                if (_contourLines?.Count > 0) ContourLines = new ObservableCollection<IsodoseContourData>();

                StatusText = _renderingService.RenderDoseImage(DoseImageSource, CurrentSlice,
                    planTotalDoseGy, planNormalization, _isodoseLevelArray,
                    _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);
            }
        }

        private void RenderStructureContours()
        {
            if (_showStructureContours && _snapshot?.Structures?.Count > 0)
            {
                var selectedStructures = _snapshot.Structures
                    .Where(s => _visibleStructureIds.Contains(s.Id) || _dvhCache.Any(d => d.Structure.Id == s.Id));

                var contours = _renderingService.GenerateStructureContours(CurrentSlice, selectedStructures);
                StructureContourLines = new ObservableCollection<StructureContourData>(contours);
            }
            else if (_structureContourLines?.Count > 0)
                StructureContourLines = new ObservableCollection<StructureContourData>();
        }

        private unsafe void RenderRegistrationOverlay()
        {
            int w = _snapshot.CtImage.XSize, h = _snapshot.CtImage.YSize;
            var bmp = OverlayImageSource;

            if (_overlayMode == OverlayMode.Off || _summationService == null || !_isSummationActive)
            {
                bmp.Lock();
                try
                {
                    byte* p = (byte*)bmp.BackBuffer;
                    for (int i = 0; i < h * bmp.BackBufferStride; i++) p[i] = 0;
                    bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
                }
                finally { bmp.Unlock(); }
                return;
            }

            int[] secondaryCt = _summationService.GetRegisteredCtSlice(_selectedOverlayPlanLabel, CurrentSlice);

            bmp.Lock();
            try
            {
                byte* p = (byte*)bmp.BackBuffer;
                int stride = bmp.BackBufferStride;
                for (int i = 0; i < h * stride; i++) p[i] = 0;

                if (secondaryCt == null || secondaryCt.Length != w * h)
                { bmp.AddDirtyRect(new Int32Rect(0, 0, w, h)); return; }

                double wl = WindowLevel, ww = WindowWidth;
                double huMin = wl - (ww / 2.0);
                double factor = (ww > 0) ? 255.0 / ww : 0;

                if (_overlayMode == OverlayMode.Checkerboard)
                {
                    int bs = RenderConstants.CheckerboardBlockSize;
                    for (int py = 0; py < h; py++)
                    {
                        uint* row = (uint*)(p + py * stride);
                        int rb = (py / bs) & 1;
                        for (int px = 0; px < w; px++)
                        {
                            if ((rb ^ ((px / bs) & 1)) == 0) continue;
                            double valD = (secondaryCt[py * w + px] - huMin) * factor;
                            byte val = (byte)(valD < 0 ? 0 : (valD > 255 ? 255 : valD));
                            row[px] = (0xFFu << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                        }
                    }
                }
                else if (_overlayMode == OverlayMode.Blend)
                {
                    byte alpha = (byte)(Math.Max(0, Math.Min(1, _overlayOpacity)) * 255);
                    for (int py = 0; py < h; py++)
                    {
                        uint* row = (uint*)(p + py * stride);
                        for (int px = 0; px < w; px++)
                        {
                            double valD = (secondaryCt[py * w + px] - huMin) * factor;
                            byte val = (byte)(valD < 0 ? 0 : (valD > 255 ? 255 : valD));
                            row[px] = ((uint)alpha << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                        }
                    }
                }
                bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally { bmp.Unlock(); }
        }

        public void UpdateDoseCursor(int pixelX, int pixelY)
        {
            if (_snapshot?.Dose == null && !_isSummationActive)
            { DoseCursorText = ""; return; }

            double doseGy;
            if (_isSummationActive && _summationService != null && _summationService.HasSummedDose)
            {
                double[] slice = _summationService.GetSummedSlice(CurrentSlice);
                int w = _snapshot.CtImage.XSize;
                int h = _snapshot.CtImage.YSize;

                if (slice == null || pixelX < 0 || pixelX >= w || pixelY < 0 || pixelY >= h)
                { DoseCursorText = ""; return; }

                doseGy = slice[pixelY * w + pixelX];
            }
            else
            {
                EQD2Settings eqd2 = _isEQD2Enabled
                    ? new EQD2Settings { IsEnabled = true, AlphaBeta = _displayAlphaBeta, NumberOfFractions = _numberOfFractions }
                    : null;

                doseGy = _renderingService.GetDoseAtPixel(CurrentSlice, pixelX, pixelY, eqd2);
            }

            if (double.IsNaN(doseGy) || doseGy <= 0) DoseCursorText = "";
            else
            {
                string label = (_isSummationActive || _isEQD2Enabled) ? "EQD2" : "Phys";
                DoseCursorText = $"{label}: {doseGy:F2} Gy  ({pixelX}, {pixelY})";
            }
        }
    }
}
