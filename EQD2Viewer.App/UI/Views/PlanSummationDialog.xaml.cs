using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace EQD2Viewer.App.UI.Views
{
    public partial class PlanSummationDialog : Window, INotifyPropertyChanged
    {
        private readonly List<CourseData> _courses;
        private readonly List<RegistrationData> _registrations;
        private readonly PlanData _currentPlan;
        private readonly IRegistrationService? _registrationService;
        private readonly ISummationDataLoader? _summationDataLoader;
        private List<RegistrationInfo> _allRegistrations;
        private CancellationTokenSource? _regCts;

        public ObservableCollection<PlanRowItem> PlanRows { get; } = new ObservableCollection<PlanRowItem>();
        public SummationConfig? ResultConfig { get; private set; }

        private bool _isAnyCalculating;
        public bool IsAnyCalculating
        {
            get => _isAnyCalculating;
            set { _isAnyCalculating = value; OnPropertyChanged(); }
        }

        public PlanSummationDialog(
            List<CourseData> courses, 
            List<RegistrationData> registrations, 
            PlanData currentPlan,
            IRegistrationService? registrationService = null,
            ISummationDataLoader? summationDataLoader = null)
        {
            InitializeComponent();
            DataContext = this;

            _courses = courses ?? new List<CourseData>();
            _registrations = registrations ?? new List<RegistrationData>();
            _currentPlan = currentPlan;
            _registrationService = registrationService;
            _summationDataLoader = summationDataLoader;
            _allRegistrations = IndexAllRegistrations();
            
            PopulatePlans();
            PlanGrid.ItemsSource = PlanRows;
        }

        private List<RegistrationInfo> IndexAllRegistrations()
        {
            var list = new List<RegistrationInfo>();
            foreach (var reg in _registrations)
            {
                try
                {
                    list.Add(new RegistrationInfo
                    {
                        Id = reg.Id,
                        SourceFOR = reg.SourceFOR ?? "",
                        RegisteredFOR = reg.RegisteredFOR ?? "",
                        DateStr = reg.CreationDateTime?.ToString("d") ?? ""
                    });
                }
                catch { }
            }
            return list;
        }

        private List<RegistrationOption> FilterRegistrationsForPair(string planFOR, string referenceFOR)
        {
            var options = new List<RegistrationOption>
            {
                new RegistrationOption { Id = "", DisplayName = "None — same CT as reference" }
            };
            if (string.IsNullOrEmpty(planFOR) || string.IsNullOrEmpty(referenceFOR)) return options;
            if (string.Equals(planFOR, referenceFOR, StringComparison.OrdinalIgnoreCase)) return options;

            foreach (var reg in _allRegistrations)
            {
                bool planIsSource = string.Equals(reg.SourceFOR, planFOR, StringComparison.OrdinalIgnoreCase);
                bool planIsTarget = string.Equals(reg.RegisteredFOR, planFOR, StringComparison.OrdinalIgnoreCase);
                bool refIsSource = string.Equals(reg.SourceFOR, referenceFOR, StringComparison.OrdinalIgnoreCase);
                bool refIsTarget = string.Equals(reg.RegisteredFOR, referenceFOR, StringComparison.OrdinalIgnoreCase);

                if (planIsSource && refIsTarget)
                    options.Add(new RegistrationOption { Id = reg.Id, DisplayName = $"{reg.Id}  [plan \u2192 ref]  ({reg.DateStr})" });
                else if (refIsSource && planIsTarget)
                    options.Add(new RegistrationOption { Id = reg.Id, DisplayName = $"{reg.Id}  [ref \u2192 plan]  ({reg.DateStr})" });
            }
            return options;
        }

        private string _lastDebugReport = "";

        private void RebuildAllRegistrationLists()
        {
            var refRow = PlanRows.FirstOrDefault(r => r.IsReference);
            string referenceFOR = refRow?.ImageFOR ?? "";

            var dbg = new System.Text.StringBuilder();
            dbg.AppendLine("EQD2 VIEWER — REGISTRATION DEBUG REPORT");
            dbg.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            dbg.AppendLine($"Reference FOR: {referenceFOR}");
            dbg.AppendLine($"Total registrations: {_allRegistrations.Count}");
            dbg.AppendLine();

            foreach (var row in PlanRows)
            {
                if (row.IsReference)
                {
                    row.RelevantRegistrations = new List<RegistrationOption>();
                    row.SelectedRegistrationId = "";
                    dbg.AppendLine($"{row.CourseId}/{row.PlanId}: REFERENCE");
                    continue;
                }

                var newList = FilterRegistrationsForPair(row.ImageFOR, referenceFOR);
                string currentSelection = row.SelectedRegistrationId;
                bool selectionStillValid = newList.Any(o => o.Id == currentSelection);
                row.RelevantRegistrations = newList;
                if (!selectionStillValid) row.SelectedRegistrationId = "";

                int matchCount = newList.Count - 1;
                dbg.AppendLine($"{row.CourseId}/{row.PlanId}: FOR={row.ImageFOR}, {matchCount} registrations");
            }

            _lastDebugReport = dbg.ToString();
            if (TbDebugInfo != null) TbDebugInfo.Text = _lastDebugReport;
        }

        private void CopyDebug_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastDebugReport))
            {
                try { Clipboard.SetText(_lastDebugReport); MessageBox.Show("Copied to clipboard."); }
                catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
            }
        }

        private async void CalculateDir_Click(object sender, RoutedEventArgs e)
        {
            SimpleLogger.Info("[DIR] Calculate DIR button clicked");

            if (_registrationService == null)
            {
                SimpleLogger.Warning("[DIR] _registrationService is null — ITK module not loaded");
                MessageBox.Show(
                    "DIR module is not loaded.\n\n" +
                    "EQD2Viewer.Registration.ITK.dll was not found next to the application, " +
                    "or SimpleITK native DLLs are missing.\n\n" +
                    "Build with the Release-WithITK configuration and deploy all 5 DLLs " +
                    "from BuildOutput\\02_Eclipse_With_ITK\\ to the Eclipse scripts folder.",
                    "DIR unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Global lock: disallow starting a second DIR while one is already running. SimpleITK
            // doesn't tolerate two simultaneous ImageRegistrationMethod.Execute calls well and
            // the UI can get confused when two async awaits race for the same row fields.
            if (IsAnyCalculating)
            {
                SimpleLogger.Info("[DIR] Ignored — another DIR is already running.");
                MessageBox.Show(
                    "Another DIR registration is already running.\n\n" +
                    "Wait for it to finish (watch the progress bar) or click Cancel first.",
                    "DIR busy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is not PlanRowItem row)
            {
                SimpleLogger.Warning("[DIR] Sender DataContext is not PlanRowItem");
                return;
            }

            var refRow = PlanRows.FirstOrDefault(r => r.IsReference);
            if (refRow == null)
            {
                SimpleLogger.Info("[DIR] No reference plan selected");
                MessageBox.Show("Select a reference plan first (tick the 'Ref' radio button on one row).",
                    "Registration setup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ReferenceEquals(row, refRow))
            {
                MessageBox.Show("Cannot register the reference plan to itself.",
                    "Registration setup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SimpleLogger.Info($"[DIR] Registering {row.CourseId}/{row.PlanId} onto reference {refRow.CourseId}/{refRow.PlanId}");

            var refPs = _courses.SelectMany(c => c.Plans).FirstOrDefault(p => p.CourseId == refRow.CourseId && p.PlanId == refRow.PlanId);
            var movPs = _courses.SelectMany(c => c.Plans).FirstOrDefault(p => p.CourseId == row.CourseId && p.PlanId == row.PlanId);

            if (refPs == null || movPs == null)
            {
                SimpleLogger.Warning($"[DIR] Plan lookup failed — refPs={refPs != null}, movPs={movPs != null}");
                MessageBox.Show("Plan metadata not available for registration.",
                    "DIR error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_summationDataLoader == null)
            {
                SimpleLogger.Warning("[DIR] _summationDataLoader is null — running in a mode without CT access");
                MessageBox.Show(
                    "CT volume access is not available in this mode.\n\n" +
                    "DIR requires the ESAPI-backed data loader. This feature only works inside Eclipse, " +
                    "not in the standalone DevRunner.",
                    "DIR unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Give immediate visual feedback — registration may take 30 s or longer.
            row.IsCalculating = true;
            IsAnyCalculating = true;
            row.DirStatus = "Loading CT volumes…";
            if (PbRegistration != null) PbRegistration.Value = 0;
            TbRegistrationStatus.Text = $"Loading CT volumes for {refRow.PlanId} and {row.PlanId}…";

            // Yield so the UI paints the "Loading…" state before we start heavy work.
            await Task.Yield();

            VolumeData? refCt = null, movCt = null;
            try
            {
                refCt = PS_LoadCt(refPs);
                movCt = PS_LoadCt(movPs);
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("[DIR] CT load threw", ex);
            }

            if (refCt == null || movCt == null)
            {
                SimpleLogger.Warning($"[DIR] CT load returned null — refCt={refCt != null}, movCt={movCt != null}");
                row.IsCalculating = false;
                IsAnyCalculating = false;
                row.DirStatus = "CT load failed";
                TbRegistrationStatus.Text = "";
                MessageBox.Show(
                    "Could not load the CT volume(s) required for registration.\n\n" +
                    "Check that both plans have valid image data in Eclipse.",
                    "DIR error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _regCts?.Cancel();
            _regCts = new CancellationTokenSource();
            var ct = _regCts.Token;

            row.DirStatus = "Registering…";
            TbRegistrationStatus.Text = $"Running SimpleITK B-spline on {refCt.XSize}×{refCt.YSize}×{refCt.ZSize} volumes…";
            string opLabel = $"{row.CourseId}/{row.PlanId} onto {refRow.CourseId}/{refRow.PlanId}";
            SimpleLogger.Info($"[DIR] TASK STARTED — {opLabel} | fixed={refCt.XSize}x{refCt.YSize}x{refCt.ZSize}, moving={movCt.XSize}x{movCt.YSize}x{movCt.ZSize}");

            // FOV diagnostic runs in microseconds and prints *before* the expensive
            // registration starts, so a user who spots a bad overlap can press Cancel
            // within seconds rather than waiting out a failed registration.
            try
            {
                var fov = VolumeOverlapAnalyzer.Analyze(refCt, movCt);
                SimpleLogger.Info("[DIR] " + fov.FormatSummary());
                if (fov.Verdict == VolumeOverlapVerdict.Fail)
                    SimpleLogger.Warning("[DIR] FOV overlap < 50% — registration very likely to fail. Consider cancelling.");
                else if (fov.Verdict == VolumeOverlapVerdict.Warning)
                    SimpleLogger.Warning("[DIR] FOV overlap < 70% — borderline. Check the report before trusting the result.");
            }
            catch (Exception fex)
            {
                SimpleLogger.Warning($"[DIR] FOV diagnostic failed: {fex.GetType().Name}: {fex.Message}");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string endReason = "unknown";
            try
            {
                var progress = new Progress<int>(p => { if (PbRegistration != null) PbRegistration.Value = p; });
                var field = await _registrationService.RegisterAsync(refCt, movCt, progress, ct);

                if (field != null)
                {
                    row.DeformationField = field;
                    row.DirStatus = "DIR calculated";
                    endReason = $"SUCCESS — DVF {field.XSize}x{field.YSize}x{field.ZSize}";

                    // TG-132 style QA analysis on a background thread; report hits the log.
                    // Fire-and-forget so the user is not blocked while it scans the field.
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var report = DeformationFieldAnalyzer.Analyze(field);
                            SimpleLogger.Info("[DIR] Quality report:" + Environment.NewLine + report.FormatSummary());
                        }
                        catch (Exception qex)
                        {
                            SimpleLogger.Warning($"[DIR] Quality analysis failed: {qex.GetType().Name}: {qex.Message}");
                        }
                    });
                }
                else
                {
                    SimpleLogger.Warning("[DIR] RegisterAsync returned null");
                    row.DirStatus = "DIR failed";
                    endReason = "NULL-RESULT (registration returned no field)";
                    MessageBox.Show("Registration completed but returned no field. Check the log.",
                        "DIR error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                row.DirStatus = "Cancelled";
                endReason = "CANCELLED";
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("[DIR] RegisterAsync threw", ex);
                row.DirStatus = "DIR failed";
                endReason = $"EXCEPTION — {ex.GetType().Name}: {ex.Message}";
                MessageBox.Show($"Registration failed:\n\n{ex.Message}\n\nSee EQD2Viewer.log for details.",
                    "DIR error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                sw.Stop();
                row.IsCalculating = false;
                IsAnyCalculating = false;
                if (TbRegistrationStatus != null) TbRegistrationStatus.Text = "";
                SimpleLogger.Info($"[DIR] TASK ENDED — {opLabel} | {endReason} | elapsed {sw.Elapsed.TotalSeconds:F1}s");
            }
        }

        private void CancelRegistration_Click(object sender, RoutedEventArgs e)
        {
            _regCts?.Cancel();
        }

        private VolumeData? PS_LoadCt(PlanSummaryData ps)
        {
            try { return _summationDataLoader?.LoadCtVolume(ps.CourseId, ps.PlanId); }
            catch { return null; }
        }

        private void PopulatePlans()
        {
            foreach (var course in _courses)
            {
                if (course.Plans == null) continue;
                foreach (var plan in course.Plans)
                {
                    if (!plan.HasDose) continue;

                    bool isCurrentPlan = _currentPlan != null
                        && plan.PlanId == _currentPlan.Id
                        && plan.CourseId == _currentPlan.CourseId;

                    var row = new PlanRowItem
                    {
                        CourseId = course.Id,
                        PlanId = plan.PlanId,
                        ImageId = plan.ImageId ?? "",
                        ImageFOR = plan.ImageFOR ?? "",
                        TotalDoseGy = plan.TotalDoseGy,
                        NumberOfFractions = plan.NumberOfFractions,
                        PlanNormalization = plan.PlanNormalization,
                        IsIncluded = isCurrentPlan,
                        IsReference = isCurrentPlan,
                        SelectedRegistrationId = "",
                        Weight = 1.0,
                        RelevantRegistrations = new List<RegistrationOption>(),
                        IsDirCalculationEnabled = _registrationService != null,
                        // Make the disabled state visible: reading "DIR not loaded" is clearer than a
                        // greyed-out button with no explanation.
                        DirStatus = _registrationService != null ? "Not calculated" : "DIR module not loaded"
                    };
                    row.PropertyChanged += OnRowPropertyChanged;
                    PlanRows.Add(row);
                }
            }
            RebuildAllRegistrationLists();
        }

        private void OnRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlanRowItem.IsReference)) RebuildAllRegistrationLists();
        }

        private void Compute_Click(object sender, RoutedEventArgs e)
        {
            var includedPlans = PlanRows.Where(p => p.IsIncluded).ToList();
            if (includedPlans.Count < 2)
            {
                MessageBox.Show(
                    "Select at least two plans to sum.\n\n" +
                    "Tick the 'Include' checkbox on the rows you want to combine.",
                    "Summation setup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!includedPlans.Any(p => p.IsReference))
            {
                MessageBox.Show(
                    "No reference plan selected.\n\n" +
                    "Click the 'Ref' radio button on the plan whose CT grid the sum should use.",
                    "Summation setup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (includedPlans.Count(p => p.IsReference) > 1)
            {
                MessageBox.Show("Only one reference plan may be selected at a time.",
                    "Summation setup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var p in includedPlans)
                if (p.NumberOfFractions <= 0)
                {
                    MessageBox.Show(
                        $"Plan '{p.CourseId} / {p.PlanId}' has an invalid fraction count.\n\n" +
                        "Edit the 'Fx' column and enter a positive integer (the plan's delivered fractions).",
                        "Summation setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

            var refPlan = includedPlans.First(p => p.IsReference);
            foreach (var p in includedPlans.Where(p => !p.IsReference))
            {
                bool sameFOR = !string.IsNullOrEmpty(p.ImageFOR) && !string.IsNullOrEmpty(refPlan.ImageFOR)
                    && string.Equals(p.ImageFOR, refPlan.ImageFOR, StringComparison.OrdinalIgnoreCase);
                if (!sameFOR && string.IsNullOrEmpty(p.SelectedRegistrationId) && !p.HasDeformationField)
                {
                    if (MessageBox.Show(
                            $"Plan '{p.CourseId} / {p.PlanId}' is on a different CT than the reference " +
                            "and has no registration or DIR selected.\n\n" +
                            "Doses from this plan will be sampled at matching voxel indices — this is only " +
                            "accurate if the CTs are already spatially aligned.\n\n" +
                            "Continue anyway?",
                            "Missing spatial mapping", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                        return;
                }
            }

            // α/β: strict parse + explicit feedback on invalid input instead of silent default.
            string raw = (TbAlphaBeta.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double alphaBeta) || alphaBeta <= 0)
            {
                MessageBox.Show(
                    $"'{TbAlphaBeta.Text}' is not a valid α/β value.\n\n" +
                    "Enter a positive number (e.g. 3.0 for late-responding OARs, 10.0 for tumour targets).",
                    "Invalid α/β", MessageBoxButton.OK, MessageBoxImage.Warning);
                TbAlphaBeta.Focus();
                TbAlphaBeta.SelectAll();
                return;
            }

            ResultConfig = new SummationConfig
            {
                Method = RbEQD2.IsChecked == true ? SummationMethod.EQD2 : SummationMethod.Physical,
                GlobalAlphaBeta = alphaBeta,
                Plans = includedPlans.Select(p => new SummationPlanEntry
                {
                    DisplayLabel = $"{p.CourseId} / {p.PlanId}",
                    CourseId = p.CourseId,
                    PlanId = p.PlanId,
                    NumberOfFractions = p.NumberOfFractions,
                    TotalDoseGy = p.TotalDoseGy,
                    PlanNormalization = double.IsNaN(p.PlanNormalization) || p.PlanNormalization <= 0 ? 100.0 : p.PlanNormalization,
                    IsReference = p.IsReference,
                    RegistrationId = p.IsReference ? "" : (p.SelectedRegistrationId ?? ""),
                    Weight = p.Weight,
                    DeformationField = p.DeformationField
                }).ToList()
            };
            DialogResult = true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PlanRowItem : INotifyPropertyChanged
    {
        private bool _isIncluded, _isReference;
        private int _numberOfFractions;
        private double _weight = 1.0;
        private string _selectedRegistrationId = "";
        private string _dirStatus = "Not Calculated";
        private bool _isCalculating;
        private bool _isDirCalculationEnabled;
        private DeformationField? _deformationField;
        private List<RegistrationOption> _relevantRegistrations = new List<RegistrationOption>();

        public string CourseId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ImageId { get; set; } = "";
        public string ImageFOR { get; set; } = "";
        public double TotalDoseGy { get; set; }
        public double PlanNormalization { get; set; }

        public string ShortFOR => string.IsNullOrEmpty(ImageFOR) ? "—"
            : (ImageFOR.Length > 8 ? ".." + ImageFOR.Substring(ImageFOR.Length - 8) : ImageFOR);

        public List<RegistrationOption> RelevantRegistrations
        { get => _relevantRegistrations; set { _relevantRegistrations = value; OnPropertyChanged(); } }
        public bool IsIncluded { get => _isIncluded; set { _isIncluded = value; OnPropertyChanged(); } }
        public bool IsReference
        {
            get => _isReference;
            set { if (_isReference != value) { _isReference = value; OnPropertyChanged(); if (value) SelectedRegistrationId = ""; } }
        }
        public int NumberOfFractions { get => _numberOfFractions; set { _numberOfFractions = value; OnPropertyChanged(); } }
        public double Weight { get => _weight; set { _weight = value; OnPropertyChanged(); } }
        public string SelectedRegistrationId { get => _selectedRegistrationId; set { _selectedRegistrationId = value; OnPropertyChanged(); } }

        public string DirStatus { get => _dirStatus; set { _dirStatus = value; OnPropertyChanged(); } }
        public bool IsCalculating { get => _isCalculating; set { _isCalculating = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDirCalculationEnabled)); } }
        public bool IsDirCalculationEnabled { get => _isDirCalculationEnabled && !IsCalculating; set { _isDirCalculationEnabled = value; OnPropertyChanged(); } }
        
        public bool HasDeformationField => _deformationField != null;
        public DeformationField? DeformationField 
        { 
            get => _deformationField; 
            set { if (SetProperty(ref _deformationField, value)) OnPropertyChanged(nameof(HasDeformationField)); } 
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class RegistrationOption { public string Id { get; set; } = ""; public string DisplayName { get; set; } = ""; }
    internal class RegistrationInfo { public string Id { get; set; } = ""; public string SourceFOR { get; set; } = ""; public string RegisteredFOR { get; set; } = ""; public string DateStr { get; set; } = ""; }
}
