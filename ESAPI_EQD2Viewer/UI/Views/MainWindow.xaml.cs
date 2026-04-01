using ESAPI_EQD2Viewer.UI.ViewModels;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Data;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;

namespace ESAPI_EQD2Viewer.UI.Views
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
            var snapshot = _viewModel._snapshot;
            if (snapshot?.Structures == null || snapshot.Structures.Count == 0)
            {
                MessageBox.Show("No structures available.",
                    "EQD2 Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new StructureSelectionDialog(snapshot.Structures);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && dialog.SelectedStructures.Any())
            {
                _viewModel.AddStructuresForDVH(dialog.SelectedStructures);
            }
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle rect && rect.DataContext is IsodoseLevel level)
            {
                uint[] palette = IsodoseLevel.ColorPalette;
                uint current = level.Color;
                int idx = -1;
                for (int i = 0; i < palette.Length; i++)
                    if (palette[i] == current) { idx = i; break; }
                int next = (idx + 1) % palette.Length;
                level.Color = palette[next];
            }
        }
    }
}