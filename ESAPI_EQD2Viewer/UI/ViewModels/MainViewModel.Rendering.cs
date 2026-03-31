using ESAPI_EQD2Viewer.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using EQD2Viewer.Core.Logging;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>
        /// Coalesces rapid property changes into a single render dispatch.
        /// Uses Interlocked.CompareExchange as a lock-free "dirty" flag.
        /// </summary>
        internal void RequestRender()
        {
            if (Interlocked.CompareExchange(ref _renderPendingFlag, 1, 0) != 0) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _renderPendingFlag, 0);
                RenderScene();
            }), DispatcherPriority.Render);
        }

        /// <summary>
        /// Master render method — orchestrates CT, overlay, structures, and dose rendering.
        /// Guard: skips if already rendering (prevents re-entrant calls).
        /// </summary>
        private void RenderScene()
        {
            if (_disposed) return;
            if (_isSnapshotMode && _snapshot?.CtImage == null) return;
            if (!_isSnapshotMode && _context?.Image == null) return;

            if (_isRendering) return;
            _isRendering = true;
            try
            {
                if (_isSnapshotMode)
                {
                    _renderingService.RenderCtImage(CtImageSource, CurrentSlice, WindowLevel, WindowWidth);
                    RenderStructureContoursFromSnapshot();
                }
                else
                {
                    _renderingService.RenderCtImage(_context.Image, CtImageSource, CurrentSlice, WindowLevel, WindowWidth);
                    RenderRegistrationOverlay();
                    RenderStructureContours();
                }

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

        /// <summary>
        /// Renders dose overlay for a single plan (non-summation mode).
        /// Uses DisplayAlphaBeta for EQD2 visualization — NOT for DVH.
        /// </summary>
        private void RenderSinglePlanDose()
        {
            double planTotalDoseGy = GetPrescriptionGy();

            double planNormalization = _isSnapshotMode
                ? (_snapshot?.ActivePlan?.PlanNormalization ?? 100.0)
                : (_plan?.PlanNormalizationValue ?? 100.0);

            // Display α/β is for visualization only
            EQD2Settings eqd2 = _isEQD2Enabled
                ? new EQD2Settings { IsEnabled = true, AlphaBeta = _displayAlphaBeta, NumberOfFractions = _numberOfFractions }
                : null;

            if (_isSnapshotMode)
            {
                // Use cached-geometry render methods
                if (_doseDisplayMode == DoseDisplayMode.Line)
                {
                    _renderingService.RenderDoseImage(null, null, DoseImageSource, CurrentSlice,
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

                    StatusText = _renderingService.RenderDoseImage(null, null, DoseImageSource, CurrentSlice,
                        planTotalDoseGy, planNormalization, _isodoseLevelArray,
                        _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);
                }
            }
            else
            {
                // Original ESAPI-based rendering (unchanged)
                if (_doseDisplayMode == DoseDisplayMode.Line)
                {
                    _renderingService.RenderDoseImage(_context.Image, _plan?.Dose, DoseImageSource, CurrentSlice,
                        planTotalDoseGy, planNormalization, _isodoseLevelArray,
                        _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);

                    var result = _renderingService.GenerateVectorContours(_context.Image, _plan?.Dose, CurrentSlice,
                        planTotalDoseGy, planNormalization, _isodoseLevelArray, eqd2);

                    ContourLines = new ObservableCollection<IsodoseContourData>(result.Contours);
                    StatusText = result.StatusText ?? "";
                }
                else
                {
                    if (_contourLines?.Count > 0) ContourLines = new ObservableCollection<IsodoseContourData>();

                    StatusText = _renderingService.RenderDoseImage(_context.Image, _plan?.Dose, DoseImageSource, CurrentSlice,
                        planTotalDoseGy, planNormalization, _isodoseLevelArray,
                        _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);
                }
            }
        }

        private void RenderStructureContours()
        {
            if (_showStructureContours && _visibleStructures.Count > 0)
            {
                var contours = _renderingService.GenerateStructureContours(_context.Image, CurrentSlice, _visibleStructures);
                StructureContourLines = new ObservableCollection<StructureContourData>(contours);
            }
            else if (_structureContourLines?.Count > 0)
                StructureContourLines = new ObservableCollection<StructureContourData>();
        }

        private void RenderStructureContoursFromSnapshot()
        {
            if (_showStructureContours && _snapshot?.Structures?.Count > 0)
            {
                var selectedStructures = _snapshot.Structures
                    .Where(s => _visibleStructures.Any(vs => vs.Id == s.Id) || _dvhCache.Any(d => d.Structure.Id == s.Id));

                var contours = _renderingService.GenerateStructureContours(CurrentSlice, selectedStructures);
                StructureContourLines = new ObservableCollection<StructureContourData>(contours);
            }
            else if (_structureContourLines?.Count > 0)
                StructureContourLines = new ObservableCollection<StructureContourData>();
        }

        private unsafe void RenderRegistrationOverlay()
        {
            int w = _context.Image.XSize, h = _context.Image.YSize;
            var bmp = OverlayImageSource;

            // Early return before Lock() if overlay is off — avoids unnecessary lock overhead
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

        /// <summary>
        /// Updates the dose readout text at the cursor position.
        /// Called from InteractiveImageViewer on mouse move.
        /// Single source of truth — the DoseCursorText property drives the UI.
        /// </summary>
        public void UpdateDoseCursor(int pixelX, int pixelY)
        {
            if (!_isSnapshotMode && _plan?.Dose == null && !_isSummationActive)
            { DoseCursorText = ""; return; }

            double doseGy;
            if (_isSummationActive && _summationService != null && _summationService.HasSummedDose)
            {
                double[] slice = _summationService.GetSummedSlice(CurrentSlice);

                // Get width and height based on mode
                int w = _isSnapshotMode ? _snapshot.CtImage.XSize : _context.Image.XSize;
                int h = _isSnapshotMode ? _snapshot.CtImage.YSize : _context.Image.YSize;

                if (slice == null || pixelX < 0 || pixelX >= w || pixelY < 0 || pixelY >= h)
                { DoseCursorText = ""; return; }

                doseGy = slice[pixelY * w + pixelX];
            }
            else if (_isSnapshotMode)
            {
                EQD2Settings eqd2 = _isEQD2Enabled
                    ? new EQD2Settings { IsEnabled = true, AlphaBeta = _displayAlphaBeta, NumberOfFractions = _numberOfFractions }
                    : null;

                doseGy = _renderingService.GetDoseAtPixel(CurrentSlice, pixelX, pixelY, eqd2);
            }
            else
            {
                // Single-plan: use DisplayAlphaBeta for cursor readout (consistent with display)
                EQD2Settings eqd2 = _isEQD2Enabled
                    ? new EQD2Settings { IsEnabled = true, AlphaBeta = _displayAlphaBeta, NumberOfFractions = _numberOfFractions }
                    : null;

                doseGy = _renderingService.GetDoseAtPixel(_context.Image, _plan?.Dose, CurrentSlice, pixelX, pixelY, eqd2);
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