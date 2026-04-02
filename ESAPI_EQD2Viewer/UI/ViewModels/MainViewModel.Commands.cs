using CommunityToolkit.Mvvm.Input;
using EQD2Viewer.Core.Models;
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
        internal void LoadIsodosePreset(string preset) => _doseOverlay.LoadPreset(preset);

        [RelayCommand]
        private void AddIsodoseLevel() => _doseOverlay.AddLevel();

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
