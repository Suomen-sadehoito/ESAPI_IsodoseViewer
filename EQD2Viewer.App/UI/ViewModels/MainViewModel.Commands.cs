using EQD2Viewer.Services.Rendering;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using CommunityToolkit.Mvvm.Input;
using EQD2Viewer.App.Services;
using System;
using System.Linq;

namespace EQD2Viewer.App.UI.ViewModels
{
    public partial class MainViewModel
    {
        [RelayCommand]
        private void JumpToHotspot()
        {
            if (!HasHotspot) return;
            int target = HotspotSliceZ;
            if (target < 0) return;
            if (target > _maxSlice) target = _maxSlice;
            CurrentSlice = target;
        }

        /// <summary>
        /// Scans the single-plan dose volume for its peak and stores the location for the
        /// "Jump to hotspot" button. Also uses dose-grid → reference-CT world projection so
        /// the reported slice matches what the user is browsing (CT slices), not the dose grid.
        /// Called once on app load and after ClearSummation.
        /// </summary>
        internal void ComputeSinglePlanHotspot()
        {
            var dose = _snapshot?.Dose;
            var ct = _snapshot?.CtImage;
            if (dose == null || ct == null) { ClearHotspot(); return; }

            var hs = HotspotFinder.FindInDoseVolume(dose);
            if (!hs.IsValid) { ClearHotspot(); return; }

            // Project dose-voxel center to world, then to CT slice index.
            double wx = dose.Origin.X + hs.PixelX * dose.XRes * dose.XDirection.X
                                       + hs.PixelY * dose.YRes * dose.YDirection.X
                                       + hs.SliceZ * dose.ZRes * dose.ZDirection.X;
            double wy = dose.Origin.Y + hs.PixelX * dose.XRes * dose.XDirection.Y
                                       + hs.PixelY * dose.YRes * dose.YDirection.Y
                                       + hs.SliceZ * dose.ZRes * dose.ZDirection.Y;
            double wz = dose.Origin.Z + hs.PixelX * dose.XRes * dose.XDirection.Z
                                       + hs.PixelY * dose.YRes * dose.YDirection.Z
                                       + hs.SliceZ * dose.ZRes * dose.ZDirection.Z;

            double dx = wx - ct.Origin.X, dy = wy - ct.Origin.Y, dz = wz - ct.Origin.Z;
            int ctSlice = (int)Math.Round(
                (dx * ct.ZDirection.X + dy * ct.ZDirection.Y + dz * ct.ZDirection.Z) / ct.ZRes);
            int ctX = (int)Math.Round(
                (dx * ct.XDirection.X + dy * ct.XDirection.Y + dz * ct.XDirection.Z) / ct.XRes);
            int ctY = (int)Math.Round(
                (dx * ct.YDirection.X + dy * ct.YDirection.Y + dz * ct.YDirection.Z) / ct.YRes);

            if (ctSlice < 0 || ctSlice > _maxSlice) { ClearHotspot(); return; }
            SetHotspot(hs.MaxGy, ctSlice, ctX, ctY);
        }
        [RelayCommand]
        private void AutoPreset()
        {
            var (level, width) = _renderingService.ComputeAutoWindow(CurrentSlice);
            WindowLevel = level;
            WindowWidth = width;
        }

        [RelayCommand]
        private void Preset(string type)
        {
            switch (type)
            {
                case "Soft": WindowLevel = 40; WindowWidth = 400; break;
                case "Lung": WindowLevel = -600; WindowWidth = 1600; break;
                case "Bone": WindowLevel = 300; WindowWidth = 1500; break;
            }
        }

        [RelayCommand]
        internal void LoadIsodosePreset(string preset)
        {
            double max = HasHotspot ? HotspotDoseGy
                       : _isSummationActive ? _lastMaxDoseGy
                       : 0;
            _doseOverlay.LoadPreset(preset, max);
        }

        [RelayCommand]
        private void AddIsodoseLevel()
        {
            _doseOverlay.AddLevel();
            _doseOverlay.SortLevels();
        }

        [RelayCommand]
        private void SortIsodoseLevels() => _doseOverlay.SortLevels();

        [RelayCommand]
        private void SetLevelColor(string param)
        {
            if (string.IsNullOrEmpty(param)) return;
            var parts = param.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int index) &&
                uint.TryParse(parts[1], out uint color) && index >= 0 && index < _doseOverlay.IsodoseLevels.Count)
            {
                _doseOverlay.IsodoseLevels[index].Color = color;
                RequestRender();
            }
        }

        [RelayCommand]
        private void RemoveIsodoseLevel(IsodoseLevel level) => _doseOverlay.RemoveLevel(level);

        [RelayCommand]
        private void ToggleAllIsodose(string visibleStr)
        {
            bool visible = visibleStr?.ToLower() == "true";
            _doseOverlay.ToggleAllVisibility(visible);
        }

        [RelayCommand]
        private void SetDisplayAlphaBeta(string valueStr)
        {
            if (double.TryParse(valueStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0)
            {
                DisplayAlphaBeta = value;
            }
        }

        [RelayCommand]
        private void CalculateEQD2()
        {
            IsEQD2Enabled = true;
            RecalculateAllDVH();
            if (_isSummationActive && _summationService != null && _summationService.HasSummedDose)
                CalculateSummationDVH(_summationService.MaxDoseGy);
        }

        [RelayCommand]
        private void ExportCSV() { if (SummaryData.Any()) ExportService.ExportSummaryToCSV(SummaryData); }

        [RelayCommand]
        private void Debug() { _debugExportService.ExportDebugLog(_snapshot, CurrentSlice); }
    }
}
