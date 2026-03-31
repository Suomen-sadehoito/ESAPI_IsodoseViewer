using CommunityToolkit.Mvvm.Input;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Services;
using System.Linq;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        [RelayCommand]
        private void AutoPreset() { WindowLevel = 40; WindowWidth = 400; }

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
            IsodoseLevel[] levels;
            switch (preset)
            {
                case "Eclipse": levels = IsodoseLevel.GetEclipseDefaults(); IsodosePresetName = "Eclipse (10)"; CurrentIsodoseMode = IsodoseMode.Relative; break;
                case "Minimal": levels = IsodoseLevel.GetMinimalSet(); IsodosePresetName = "Minimal (3)"; CurrentIsodoseMode = IsodoseMode.Relative; break;
                case "Default": levels = IsodoseLevel.GetDefaults(); IsodosePresetName = "Default (4)"; CurrentIsodoseMode = IsodoseMode.Relative; break;
                case "ReIrradiation": levels = IsodoseLevel.GetReIrradiationPreset(); IsodosePresetName = "Re-irradiation"; CurrentIsodoseMode = IsodoseMode.Absolute; break;
                case "Stereotactic": levels = IsodoseLevel.GetStereotacticPreset(); IsodosePresetName = "Stereotactic"; CurrentIsodoseMode = IsodoseMode.Absolute; break;
                case "Palliative": levels = IsodoseLevel.GetPalliativePreset(); IsodosePresetName = "Palliative"; CurrentIsodoseMode = IsodoseMode.Absolute; break;
                default: levels = IsodoseLevel.GetDefaults(); IsodosePresetName = "Default (4)"; break;
            }
            IsodoseLevels.Clear();
            foreach (var l in levels) IsodoseLevels.Add(l);
            RebuildIsodoseArray();
            UpdateIsodoseLabels();
            RequestRender();
        }

        [RelayCommand]
        private void AddIsodoseLevel()
        {
            var newLevel = _isodoseMode == IsodoseMode.Absolute
                ? new IsodoseLevel(0, 25, "25.0 Gy", 0xFF9900FF)
                : new IsodoseLevel(0.60, "60%", 0xFF9900FF);
            newLevel.PropertyChanged += OnIsodoseLevelChanged;
            IsodoseLevels.Add(newLevel);
            RebuildIsodoseArray();
            UpdateIsodoseLabels();
            RequestRender();
        }

        [RelayCommand]
        private void SetLevelColor(string param)
        {
            if (string.IsNullOrEmpty(param)) return;
            var parts = param.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int index) &&
                uint.TryParse(parts[1], out uint color) && index >= 0 && index < IsodoseLevels.Count)
            {
                IsodoseLevels[index].Color = color;
                RequestRender();
            }
        }

        [RelayCommand]
        private void RemoveIsodoseLevel(IsodoseLevel level)
        {
            if (level != null && IsodoseLevels.Contains(level))
            {
                level.PropertyChanged -= OnIsodoseLevelChanged;
                IsodoseLevels.Remove(level);
                RebuildIsodoseArray();
                RequestRender();
            }
        }

        [RelayCommand]
        private void ToggleAllIsodose(string visibleStr)
        {
            bool visible = visibleStr?.ToLower() == "true";
            foreach (var level in IsodoseLevels) level.IsVisible = visible;
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
