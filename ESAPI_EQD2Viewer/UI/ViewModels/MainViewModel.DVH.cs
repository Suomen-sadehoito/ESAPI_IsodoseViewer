using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Services;
using OxyPlot;
using OxyPlot.Series;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>
        /// Adds structures for DVH display using DTO data from the snapshot.
        /// </summary>
        public void AddStructuresForDVH(IEnumerable<StructureData> structures)
        {
            if (structures == null) return;
            string planId = _snapshot?.ActivePlan?.Id ?? "";

            foreach (var structure in structures)
            {
                if (_dvhCache.Any(c => c.Structure.Id == structure.Id)) continue;

                // Find pre-computed DVH curve from snapshot
                var dvhCurve = _snapshot?.DvhCurves?.FirstOrDefault(d => d.StructureId == structure.Id);
                if (dvhCurve == null) continue;

                _dvhCache.Add(new DVHCacheEntry { PlanId = planId, Structure = structure, DvhCurve = dvhCurve });

                if (!_visibleStructureIds.Contains(structure.Id))
                    _visibleStructureIds.Add(structure.Id);

                double defaultAB = (structure.DicomType == "PTV" || structure.DicomType == "CTV" || structure.DicomType == "GTV")
                    ? 10.0 : 3.0;

                var settingItem = new StructureAlphaBetaItem(structure, defaultAB);
                settingItem.PropertyChanged += OnStructureSettingChanged;
                StructureSettings.Add(settingItem);

                SummaryData.Add(((DVHService)_dvhService).BuildPhysicalSummaryFromCurve(dvhCurve, planId));

                var color = OxyColor.FromArgb(structure.ColorA, structure.ColorR, structure.ColorG, structure.ColorB);
                var series = new LineSeries
                {
                    Title = $"{structure.Id} ({planId})",
                    Tag = $"Physical_{planId}_{structure.Id}",
                    Color = color, StrokeThickness = 2
                };
                if (dvhCurve.Curve != null)
                    series.Points.AddRange(dvhCurve.Curve.Select(p => new DataPoint(p[0], p[1])));
                PlotModel.Series.Add(series);
            }

            if (_isEQD2Enabled) RecalculateAllDVH();
            ShowStructureContours = true;
            RefreshPlot();
            RequestRender();
        }

        public void ClearDVH()
        {
            _dvhCache.Clear();
            _visibleStructureIds.Clear();
            StructureSettings.Clear();
            PlotModel.Series.Clear();
            SummaryData.Clear();
            RefreshPlot();
            RequestRender();
        }

        internal void RecalculateAllDVH()
        {
            var oldSeries = PlotModel.Series.Where(s => (s.Tag as string)?.StartsWith("EQD2_") ?? false).ToList();
            foreach (var s in oldSeries) PlotModel.Series.Remove(s);
            var oldSummaries = SummaryData.Where(s => s.Type == "EQD2").ToList();
            foreach (var s in oldSummaries) SummaryData.Remove(s);

            if (!_isEQD2Enabled) { RefreshPlot(); return; }

            foreach (var entry in _dvhCache)
            {
                var setting = StructureSettings.FirstOrDefault(s => s.Structure.Id == entry.Structure.Id);
                double alphaBeta = setting?.AlphaBeta ?? 3.0;

                SummaryData.Add(((DVHService)_dvhService).BuildEQD2SummaryFromCurve(
                    entry.DvhCurve, entry.PlanId, _numberOfFractions, alphaBeta, _meanMethod));

                DoseVolumePoint[] curveInGy = null;
                if (entry.DvhCurve.Curve != null)
                    curveInGy = entry.DvhCurve.Curve.Select(p => new DoseVolumePoint(p[0], p[1])).ToArray();

                var eqd2Curve = curveInGy != null
                    ? EQD2Calculator.ConvertCurveToEQD2(curveInGy, _numberOfFractions, alphaBeta)
                    : new DoseVolumePoint[0];

                var color = OxyColor.FromArgb(entry.Structure.ColorA, entry.Structure.ColorR,
                    entry.Structure.ColorG, entry.Structure.ColorB);

                var eqd2Series = new LineSeries
                {
                    Title = $"{entry.Structure.Id} EQD2 (α/β={alphaBeta:F1})",
                    LineStyle = LineStyle.Dash,
                    Tag = $"EQD2_{entry.PlanId}_{entry.Structure.Id}",
                    Color = color, StrokeThickness = 2
                };
                eqd2Series.Points.AddRange(eqd2Curve.Select(p => new DataPoint(p.DoseGy, p.VolumePercent)));
                PlotModel.Series.Add(eqd2Series);
            }
            RefreshPlot();
        }

        private void OnStructureSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StructureAlphaBetaItem.AlphaBeta) && _isEQD2Enabled)
                RecalculateAllDVH();
        }

        internal void UpdatePlotVisibility()
        {
            foreach (var series in PlotModel.Series)
                if (series.Tag is string tag)
                    series.IsVisible = (tag.StartsWith("Physical_") && _showPhysicalDVH) ||
                                       (tag.StartsWith("EQD2_") && _showEQD2DVH) ||
                                       tag.StartsWith("Summation_");
            PlotModel.InvalidatePlot(true);
        }

        internal void RefreshPlot() { UpdatePlotVisibility(); PlotModel.InvalidatePlot(true); }
    }
}
