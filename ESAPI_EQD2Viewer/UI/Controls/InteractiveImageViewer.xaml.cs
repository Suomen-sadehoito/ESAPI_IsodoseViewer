using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ESAPI_EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.UI.Controls
{
    public partial class InteractiveImageViewer : UserControl
    {
        private bool _isPanning;
        private bool _isWindowing;
        private Point _panStartPoint;
        private Point _windowingStartPoint;
        private double _initialWindowLevel;
        private double _initialWindowWidth;
        private DateTime _lastRenderTime = DateTime.MinValue;

        #region Dependency Properties

        public static readonly DependencyProperty CtImageSourceProperty =
            DependencyProperty.Register(nameof(CtImageSource), typeof(ImageSource), typeof(InteractiveImageViewer));

        public ImageSource CtImageSource
        {
            get => (ImageSource)GetValue(CtImageSourceProperty);
            set => SetValue(CtImageSourceProperty, value);
        }

        public static readonly DependencyProperty DoseImageSourceProperty =
            DependencyProperty.Register(nameof(DoseImageSource), typeof(ImageSource), typeof(InteractiveImageViewer));

        public ImageSource DoseImageSource
        {
            get => (ImageSource)GetValue(DoseImageSourceProperty);
            set => SetValue(DoseImageSourceProperty, value);
        }

        public static readonly DependencyProperty CurrentSliceProperty =
            DependencyProperty.Register(nameof(CurrentSlice), typeof(int), typeof(InteractiveImageViewer),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public int CurrentSlice
        {
            get => (int)GetValue(CurrentSliceProperty);
            set => SetValue(CurrentSliceProperty, value);
        }

        public static readonly DependencyProperty MaxSliceProperty =
            DependencyProperty.Register(nameof(MaxSlice), typeof(int), typeof(InteractiveImageViewer));

        public int MaxSlice
        {
            get => (int)GetValue(MaxSliceProperty);
            set => SetValue(MaxSliceProperty, value);
        }

        public static readonly DependencyProperty WindowLevelProperty =
            DependencyProperty.Register(nameof(WindowLevel), typeof(double), typeof(InteractiveImageViewer),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public double WindowLevel
        {
            get => (double)GetValue(WindowLevelProperty);
            set => SetValue(WindowLevelProperty, value);
        }

        public static readonly DependencyProperty WindowWidthProperty =
            DependencyProperty.Register(nameof(WindowWidth), typeof(double), typeof(InteractiveImageViewer),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public double WindowWidth
        {
            get => (double)GetValue(WindowWidthProperty);
            set => SetValue(WindowWidthProperty, value);
        }

        /// <summary>
        /// Vector isodose contour lines (Line mode). Bound to ItemsControl in XAML.
        /// Each item contains a frozen StreamGeometry that renders as a WPF Path element.
        /// </summary>
        public static readonly DependencyProperty ContourLinesProperty =
            DependencyProperty.Register(nameof(ContourLines),
                typeof(ObservableCollection<IsodoseContourData>),
                typeof(InteractiveImageViewer),
                new PropertyMetadata(null));

        public ObservableCollection<IsodoseContourData> ContourLines
        {
            get => (ObservableCollection<IsodoseContourData>)GetValue(ContourLinesProperty);
            set => SetValue(ContourLinesProperty, value);
        }

        #endregion

        public InteractiveImageViewer()
        {
            InitializeComponent();
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double zoomFactor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
                double newScale = Math.Max(0.1, Math.Min(10.0, ImageScale.ScaleX * zoomFactor));
                ImageScale.ScaleX = newScale;
                ImageScale.ScaleY = newScale;
            }
            else
            {
                int sliceDelta = e.Delta > 0 ? 1 : -1;
                int newSlice = CurrentSlice + sliceDelta;
                if (newSlice >= 0 && newSlice <= MaxSlice)
                    CurrentSlice = newSlice;
            }
            e.Handled = true;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(this);
                e.Handled = true;
            }
            else if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isWindowing = true;
                _windowingStartPoint = e.GetPosition(this);
                _initialWindowLevel = WindowLevel;
                _initialWindowWidth = WindowWidth;
                e.Handled = true;
            }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning && e.MiddleButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(this);
                Vector diff = currentPoint - _panStartPoint;
                ImageTranslate.X += diff.X;
                ImageTranslate.Y += diff.Y;
                _panStartPoint = currentPoint;
                e.Handled = true;
            }
            else if (_isWindowing && e.LeftButton == MouseButtonState.Pressed)
            {
                if ((DateTime.Now - _lastRenderTime).TotalMilliseconds > 33)
                {
                    Point currentPoint = e.GetPosition(this);
                    Vector diff = currentPoint - _windowingStartPoint;
                    WindowWidth = Math.Max(1.0, _initialWindowWidth + (diff.X * 2.0));
                    WindowLevel = _initialWindowLevel - (diff.Y * 2.0);
                    _lastRenderTime = DateTime.Now;
                }
                e.Handled = true;
            }
            else
            {
                _isPanning = false;
                _isWindowing = false;
            }
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isWindowing && e.ChangedButton == MouseButton.Left)
            {
                Point currentPoint = e.GetPosition(this);
                Vector diff = currentPoint - _windowingStartPoint;
                WindowWidth = Math.Max(1.0, _initialWindowWidth + (diff.X * 2.0));
                WindowLevel = _initialWindowLevel - (diff.Y * 2.0);
            }
            _isPanning = false;
            _isWindowing = false;
        }
    }
}