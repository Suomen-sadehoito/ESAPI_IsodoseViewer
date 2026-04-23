using EQD2Viewer.Services.Rendering;
using EQD2Viewer.App.UI.ViewModels;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace EQD2Viewer.App.UI.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            Closed += (s, e) => viewModel?.Dispose();
        }

        private void SelectStructures_Click(object sender, RoutedEventArgs e)
        {
            var structures = _viewModel.AvailableStructures;
            if (structures == null || structures.Count == 0)
            {
                MessageBox.Show("No structures available.",
                    "EQD2 Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new StructureSelectionDialog(structures);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && dialog.SelectedStructures.Any())
            {
                _viewModel.AddStructuresForDVH(dialog.SelectedStructures);
            }
        }

        // ════════════════════════════════════════════════════════
        // ISODOSE ROW ACTIONS
        // ════════════════════════════════════════════════════════

        private static IsodoseLevel? LevelFromSender(object sender)
            => (sender as FrameworkElement)?.DataContext as IsodoseLevel;

        /// <summary>Left-click swatch cycles the built-in palette (fast iteration).</summary>
        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (LevelFromSender(sender) is not IsodoseLevel level) return;
            uint[] palette = IsodoseLevel.ColorPalette;
            int idx = -1;
            for (int i = 0; i < palette.Length; i++)
                if (palette[i] == level.Color) { idx = i; break; }
            level.Color = palette[(idx + 1) % palette.Length];
        }

        /// <summary>Right-click → "Pick color…" opens the Windows common color dialog.</summary>
        private void ColorSwatch_PickCustom(object sender, RoutedEventArgs e)
        {
            if (LevelFromSender(sender) is not IsodoseLevel level) return;
            var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(
                    (int)((level.Color >> 16) & 0xFF),
                    (int)((level.Color >> 8) & 0xFF),
                    (int)(level.Color & 0xFF))
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                level.Color = 0xFF000000u
                    | ((uint)dlg.Color.R << 16)
                    | ((uint)dlg.Color.G << 8)
                    | (uint)dlg.Color.B;
            }
        }

        /// <summary>Right-click → "Reset to default" snaps the color back to a palette entry.</summary>
        private void ColorSwatch_ResetDefault(object sender, RoutedEventArgs e)
        {
            if (LevelFromSender(sender) is not IsodoseLevel level) return;
            // Map by rank: highest-threshold level gets palette[0] (red), next gets palette[1], etc.
            var list = _viewModel.IsodoseLevels.OrderByDescending(l =>
                _viewModel.IsAbsoluteMode ? l.AbsoluteDoseGy : l.Fraction).ToList();
            int rank = list.IndexOf(level);
            uint[] palette = IsodoseLevel.ColorPalette;
            level.Color = palette[(rank < 0 ? 0 : rank) % palette.Length];
        }

        /// <summary>Commit edit on Enter — WPF default would wait for focus loss.</summary>
        private void IsodoseValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && sender is TextBox tb2)
            {
                tb2.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        /// <summary>On value commit, trigger a re-sort so the grid stays ordered by threshold.</summary>
        private void IsodoseValue_LostFocus(object sender, RoutedEventArgs e)
        {
            _viewModel.SortIsodoseLevelsCommand.Execute(null);
        }

        private void IsodoseDelete_Click(object sender, RoutedEventArgs e)
        {
            if (LevelFromSender(sender) is IsodoseLevel level)
                _viewModel.RemoveIsodoseLevelCommand.Execute(level);
        }

        private void IsodoseSolo_Click(object sender, RoutedEventArgs e)
        {
            if (LevelFromSender(sender) is not IsodoseLevel target) return;
            foreach (var l in _viewModel.IsodoseLevels)
                l.IsVisible = (l == target);
        }
    }
}
