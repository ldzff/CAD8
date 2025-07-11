using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// Explicitly using System.Windows.Shapes.Shape to avoid ambiguity
// using System.Windows.Shapes; // This line can be removed if all Shape usages are qualified
using RobTeach.Views; // Added for DirectionIndicator
using Microsoft.Win32;
using RobTeach.Services;
using RobTeach.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IxMilia.Dxf; // Required for DxfFile
using IxMilia.Dxf.Entities;
using System.Diagnostics; // Added for Debug.WriteLine
using System.IO;
using System.Text; // Added for Encoding
using RobTeach.Utils; // Added for GeometryUtils AND TrajectoryUtils
using IxMilia.Dxf.Blocks; // Added for DxfBlock

// using netDxf.Header; // No longer needed with IxMilia.Dxf
using System.Windows.Threading; // Was for optional Dispatcher.Invoke, now used.
using System.Threading.Tasks; // Added for Task.Delay
// using System.Text.RegularExpressions; // Was for optional IP validation, not currently used.
using MahApps.Metro.Controls; // Added for MetroWindow

namespace RobTeach.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. This is the main window of the RobTeach application,
    /// handling UI events, displaying CAD data, managing configurations, and initiating Modbus communication.
    /// </summary>
    public partial class MainWindow : MetroWindow // Changed from Window to MetroWindow
    {
        private static readonly List<string> _layersToIgnoreForBoundingBox = new List<string>
        {
            "DEFPOINTS", // Standard non-plotting layer
            "AXES",
            "CONSTRUCTION",
            "0_REF",
            "REFERENCE",
            "DIMENSIONS",
            "TEXT_NOTES",
            "VIEWPORT"
            // Add more common non-geometry layer names if known
        };

        // Services used by the MainWindow
        private readonly CadService _cadService = new CadService();
        private readonly ConfigurationService _configService = new ConfigurationService();
        private readonly ModbusService _modbusService = new ModbusService();

        // Current state variables
        private DxfFile? _currentDxfDocument; // Holds the currently loaded DXF document object.
        private string? _currentDxfFilePath;      // Path to the currently loaded DXF file.
        private string? _currentLoadedConfigPath; // Path to the last successfully loaded configuration file.
        private Models.Configuration _currentConfiguration; // The active configuration, either loaded or built from selections.
        private bool isConfigurationDirty = false;
        private RobTeach.Models.Trajectory? _trajectoryInDetailView; // Made nullable

        // Collections for managing DXF entities and their WPF shape representations
        private readonly List<DxfEntity> _selectedDxfEntities = new List<DxfEntity>(); // Stores original DXF entities selected by the user.
        // Qualified System.Windows.Shapes.Shape for dictionary key
        private readonly Dictionary<System.Windows.Shapes.Shape, DxfEntity> _wpfShapeToDxfEntityMap = new Dictionary<System.Windows.Shapes.Shape, DxfEntity>(); // Changed to DxfEntity
        private readonly Dictionary<string, DxfEntity> _dxfEntityHandleMap = new Dictionary<string, DxfEntity>(); // Maps DXF entity handles to entities for quick lookup when loading configs.
        private readonly List<System.Windows.Shapes.Polyline> _trajectoryPreviewPolylines = new List<System.Windows.Shapes.Polyline>(); // Keeps track of trajectory preview polylines for easy removal.
        private List<DirectionIndicator> _directionIndicators; // Field for the direction indicator arrow
        private List<System.Windows.Controls.TextBlock> _orderNumberLabels = new List<System.Windows.Controls.TextBlock>();

        // Fields for CAD Canvas Zoom/Pan functionality
        private ScaleTransform _scaleTransform;         // Handles scaling (zoom) of the canvas content.
        private TranslateTransform _translateTransform; // Handles translation (pan) of the canvas content.
        private TransformGroup _transformGroup;         // Combines scale and translate transforms.
        private System.Windows.Point _panStartPoint;    // Qualified: Stores the starting point of a mouse pan operation.
        private bool _isPanning;                        // Flag indicating if a pan operation is currently in progress.
        private Rect _dxfBoundingBox = Rect.Empty;      // Stores the calculated bounding box of the entire loaded DXF document.

        // Fields for Marquee Selection
        private System.Windows.Shapes.Rectangle? selectionRectangleUI = null; // The visual rectangle for selection
        private System.Windows.Point selectionStartPoint;                   // Start point of the selection rectangle
        private bool isSelectingWithRect = false;                         // Flag indicating if marquee selection is active

        // Styling constants for visual feedback
        private static readonly Brush DefaultStrokeBrush = Brushes.LightGray; // Default color for CAD shapes.
        private static readonly Brush SelectedStrokeBrush = Brushes.DodgerBlue;   // Color for selected CAD shapes.
        private const double DefaultStrokeThickness = 2;                          // Default stroke thickness.
        private const double SelectedStrokeThickness = 3.5;                       // Thickness for selected shapes and trajectories.
        private const string TrajectoryPreviewTag = "TrajectoryPreview";          // Tag for identifying trajectory polylines on canvas (not actively used for removal yet).
        private const double TrajectoryPointResolutionAngle = 15.0; // Default resolution for discretizing arcs/circles.


        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// Sets up default values, initializes transformation objects for the canvas,
        /// and attaches necessary mouse event handlers for canvas interaction.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            AppLogger.Log("Application started."); // Log application start

            // Ensure canvas background color is set
            var canvasBackgroundBrush = (SolidColorBrush)Resources["CanvasBackgroundBrush"];
            CadCanvas.Background = canvasBackgroundBrush;

            _directionIndicators = new List<DirectionIndicator>();

            // Initialize product name with a timestamp to ensure uniqueness for new configurations.
            ProductNameTextBox.Text = $"Product_{DateTime.Now:yyyyMMddHHmmss}";
            _previousProductName = ProductNameTextBox.Text; // Initialize for LostFocus tracking
            _currentConfiguration = new Models.Configuration();
            _currentConfiguration.ProductName = ProductNameTextBox.Text;

            // Setup transformations for the CAD canvas
            _scaleTransform = new ScaleTransform(1, 1);
            _translateTransform = new TranslateTransform(0, 0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_scaleTransform);    // Apply scaling first
            _transformGroup.Children.Add(_translateTransform); // Then apply translation
            CadCanvas.RenderTransform = _transformGroup;

            // Attach mouse event handlers for canvas zoom and pan
            CadCanvas.MouseWheel += CadCanvas_MouseWheel;
            CadCanvas.MouseDown += CadCanvas_MouseDown; // For initiating pan
            CadCanvas.MouseMove += CadCanvas_MouseMove; // For active panning
            CadCanvas.MouseUp += CadCanvas_MouseUp;     // For ending pan

            // Attach event handler for canvas resize
            CadCanvas.SizeChanged += CadCanvas_SizeChanged;

            // Initialize Spray Pass Management
            _selectedDxfEntities.Clear();
            _wpfShapeToDxfEntityMap.Clear();

            if (_currentConfiguration.SprayPasses == null || !_currentConfiguration.SprayPasses.Any())
            {
                _currentConfiguration.SprayPasses = new List<SprayPass> { new SprayPass { PassName = "Default Pass 1" } };
                _currentConfiguration.CurrentPassIndex = 0;
            }
            else if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
            {
                _currentConfiguration.CurrentPassIndex = 0;
            }

            SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
            if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < SprayPassesListBox.Items.Count)
            {
                SprayPassesListBox.SelectedIndex = _currentConfiguration.CurrentPassIndex;
            }

            // Attach new event handlers
            AddPassButton.Click += AddPassButton_Click;
            RemovePassButton.Click += RemovePassButton_Click;
            RenamePassButton.Click += RenamePassButton_Click;
            SprayPassesListBox.SelectionChanged += SprayPassesListBox_SelectionChanged;

            CurrentPassTrajectoriesListBox.SelectionChanged += CurrentPassTrajectoriesListBox_SelectionChanged;
            MoveTrajectoryUpButton.Click += MoveTrajectoryUpButton_Click;
            MoveTrajectoryDownButton.Click += MoveTrajectoryDownButton_Click;

            // Event handlers for the new six checkboxes
            TrajectoryUpperNozzleEnabledCheckBox.Checked += TrajectoryUpperNozzleEnabledCheckBox_Changed;
            TrajectoryUpperNozzleEnabledCheckBox.Unchecked += TrajectoryUpperNozzleEnabledCheckBox_Changed;
            TrajectoryUpperNozzleGasOnCheckBox.Checked += TrajectoryUpperNozzleGasOnCheckBox_Changed;
            TrajectoryUpperNozzleGasOnCheckBox.Unchecked += TrajectoryUpperNozzleGasOnCheckBox_Changed;
            TrajectoryUpperNozzleLiquidOnCheckBox.Checked += TrajectoryUpperNozzleLiquidOnCheckBox_Changed;
            TrajectoryUpperNozzleLiquidOnCheckBox.Unchecked += TrajectoryUpperNozzleLiquidOnCheckBox_Changed;

            TrajectoryLowerNozzleEnabledCheckBox.Checked += TrajectoryLowerNozzleEnabledCheckBox_Changed;
            TrajectoryLowerNozzleEnabledCheckBox.Unchecked += TrajectoryLowerNozzleEnabledCheckBox_Changed;
            TrajectoryLowerNozzleGasOnCheckBox.Checked += TrajectoryLowerNozzleGasOnCheckBox_Changed;
            TrajectoryLowerNozzleGasOnCheckBox.Unchecked += TrajectoryLowerNozzleGasOnCheckBox_Changed;
            TrajectoryLowerNozzleLiquidOnCheckBox.Checked += TrajectoryLowerNozzleLiquidOnCheckBox_Changed;
            TrajectoryLowerNozzleLiquidOnCheckBox.Unchecked += TrajectoryLowerNozzleLiquidOnCheckBox_Changed;

            // Event handler for TrajectoryIsReversedCheckBox
            TrajectoryIsReversedCheckBox.Checked += TrajectoryIsReversedCheckBox_Changed;
            TrajectoryIsReversedCheckBox.Unchecked += TrajectoryIsReversedCheckBox_Changed;

            // Event handler for TrajectoryRuntimeTextBox
            TrajectoryRuntimeTextBox.LostFocus += TrajectoryRuntimeTextBox_LostFocus;

            // Event handler for ProductNameTextBox LostFocus
            ProductNameTextBox.LostFocus += ProductNameTextBox_LostFocus;

            // Test Run Button
            StartTestRunButton.IsEnabled = false;
            StartTestRunButton.Click += StartTestRunButton_Click;

            RefreshCurrentPassTrajectoriesListBox();
            UpdateSelectedTrajectoryDetailUI();
            RefreshCadCanvasHighlights();
        }


        private void RefreshCadCanvasHighlights()
        {
            if (_currentConfiguration == null || CadCanvas == null) return;

            HashSet<DxfEntity> entitiesInCurrentPass = new HashSet<DxfEntity>();
            if (_currentConfiguration.CurrentPassIndex >= 0 &&
                _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                if (currentPass != null && currentPass.Trajectories != null)
                {
                    foreach (var trajectory in currentPass.Trajectories)
                    {
                        if (trajectory.OriginalDxfEntity != null)
                        {
                            entitiesInCurrentPass.Add(trajectory.OriginalDxfEntity);
                        }
                    }
                }
            }

            foreach (var wpfShape in _wpfShapeToDxfEntityMap.Keys)
            {
                if (_wpfShapeToDxfEntityMap.TryGetValue(wpfShape, out DxfEntity associatedEntity))
                {
                    if (entitiesInCurrentPass.Contains(associatedEntity))
                    {
                        wpfShape.Stroke = SelectedStrokeBrush;
                        wpfShape.StrokeThickness = SelectedStrokeThickness;
                    }
                    else
                    {
                        wpfShape.Stroke = DefaultStrokeBrush;
                        wpfShape.StrokeThickness = DefaultStrokeThickness;
                    }
                }
                else
                {
                    wpfShape.Stroke = DefaultStrokeBrush;
                    wpfShape.StrokeThickness = DefaultStrokeThickness;
                }
            }
        }


        private void AddPassButton_Click(object sender, RoutedEventArgs e)
        {
            int passCount = _currentConfiguration.SprayPasses.Count;
            var newPass = new SprayPass { PassName = $"Pass {passCount + 1}" };
            _currentConfiguration.SprayPasses.Add(newPass);

            SprayPassesListBox.ItemsSource = null;
            SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
            SprayPassesListBox.SelectedItem = newPass;
            AppLogger.Log($"Spray pass added: '{newPass.PassName}'.");
            isConfigurationDirty = true;
            UpdateDirectionIndicator();
            UpdateOrderNumberLabels();
        }

        private void RemovePassButton_Click(object sender, RoutedEventArgs e)
        {
            if (SprayPassesListBox.SelectedItem is SprayPass selectedPass)
            {
                if (_currentConfiguration.SprayPasses.Count <= 1)
                {
                    string msg = "Cannot remove the last spray pass.";
                    AppLogger.Log(msg, LogLevel.Warning);
                    MessageBox.Show(msg, "Action Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _currentConfiguration.SprayPasses.Remove(selectedPass);

                SprayPassesListBox.ItemsSource = null;
                SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
                if (_currentConfiguration.SprayPasses.Any())
                {
                    _currentConfiguration.CurrentPassIndex = 0;
                    SprayPassesListBox.SelectedIndex = 0;
                }
                else
                {
                    _currentConfiguration.CurrentPassIndex = -1;
                }
                AppLogger.Log($"Spray pass removed: '{selectedPass.PassName}'. New current pass index: {_currentConfiguration.CurrentPassIndex}");
                RefreshCurrentPassTrajectoriesListBox();
                isConfigurationDirty = true;
                UpdateDirectionIndicator();
                UpdateOrderNumberLabels();
            }
        }

        private void UpdateOrderNumberLabels()
        {
            foreach (var label in _orderNumberLabels)
            {
                if (CadCanvas.Children.Contains(label))
                {
                    CadCanvas.Children.Remove(label);
                }
            }
            _orderNumberLabels.Clear();

            if (_currentConfiguration == null ||
                _currentConfiguration.CurrentPassIndex < 0 ||
                _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count ||
                _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories == null)
            {
                return;
            }

            var currentPassTrajectories = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories;

            for (int i = 0; i < currentPassTrajectories.Count; i++)
            {
                var selectedTrajectory = currentPassTrajectories[i];

                if (selectedTrajectory.Points == null || !selectedTrajectory.Points.Any())
                {
                    continue;
                }

                TextBlock orderLabel = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 10,
                    Foreground = Brushes.DarkSlateBlue,
                    Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 180)),
                    Padding = new Thickness(2, 0, 2, 0),
                };

                Point anchorPoint;
                if (selectedTrajectory.PrimitiveType == "Line" && selectedTrajectory.Points.Count >= 2)
                {
                    Point p_start = selectedTrajectory.Points[0];
                    Point p_end = selectedTrajectory.Points[selectedTrajectory.Points.Count - 1];
                    anchorPoint = new Point((p_start.X + p_end.X) / 2, (p_start.Y + p_end.Y) / 2);
                }
                else
                {
                    int midIndex = selectedTrajectory.Points.Count / 2;
                    anchorPoint = selectedTrajectory.Points[midIndex];
                }

                double offsetX = 5;
                double offsetY = -15;

                Canvas.SetLeft(orderLabel, anchorPoint.X + offsetX);
                Canvas.SetTop(orderLabel, anchorPoint.Y + offsetY);
                Panel.SetZIndex(orderLabel, 100);

                CadCanvas.Children.Add(orderLabel);
                _orderNumberLabels.Add(orderLabel);
            }
        }

        private void RenamePassButton_Click(object sender, RoutedEventArgs e)
        {
            if (SprayPassesListBox.SelectedItem is SprayPass selectedPass)
            {
                string oldPassName = selectedPass.PassName;
                string newPassName = selectedPass.PassName + "_Renamed";
                selectedPass.PassName = newPassName;

                AppLogger.Log($"Spray pass renamed from '{oldPassName}' to '{newPassName}'.");

                SprayPassesListBox.ItemsSource = null;
                SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
                SprayPassesListBox.SelectedItem = selectedPass;
                isConfigurationDirty = true;
            }
        }

        private void SprayPassesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SprayPassesListBox.SelectedIndex >= 0)
            {
                _currentConfiguration.CurrentPassIndex = SprayPassesListBox.SelectedIndex;
            }
            else if (!_currentConfiguration.SprayPasses.Any())
            {
                 _currentConfiguration.CurrentPassIndex = -1;
            }

            RefreshCurrentPassTrajectoriesListBox();
            UpdateSelectedTrajectoryDetailUI();
            RefreshCadCanvasHighlights();
            UpdateDirectionIndicator();
            UpdateOrderNumberLabels();
        }

        private void RefreshCurrentPassTrajectoriesListBox()
        {
            CurrentPassTrajectoriesListBox.ItemsSource = null;
            if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                CurrentPassTrajectoriesListBox.ItemsSource = currentPass.Trajectories;
            }
            UpdateSelectedTrajectoryDetailUI();
        }

        private void CurrentPassTrajectoriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Trace.WriteLine("++++ CurrentPassTrajectoriesListBox_SelectionChanged Fired ++++");
            Trace.Flush();
            UpdateSelectedTrajectoryDetailUI();
            UpdateDirectionIndicator();
    RefreshCadCanvasHighlights();
        }

        private void UpdateDirectionIndicator()
        {
            foreach (var indicator in _directionIndicators)
            {
                if (CadCanvas.Children.Contains(indicator))
                {
                    CadCanvas.Children.Remove(indicator);
                }
            }
            _directionIndicators.Clear();

            if (_currentConfiguration == null ||
                _currentConfiguration.CurrentPassIndex < 0 ||
                _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count ||
                _currentConfiguration.SprayPasses == null)
            {
                return;
            }

            var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
            if (currentPass == null || currentPass.Trajectories == null)
            {
                return;
            }

            const double fixedArrowLineLength = 8.0;
            Trajectory? actuallySelectedItem = CurrentPassTrajectoriesListBox.SelectedItem as Trajectory;

            foreach (var trajectoryInLoop in currentPass.Trajectories)
            {
                if (trajectoryInLoop.Points == null || !trajectoryInLoop.Points.Any())
                {
                    continue;
                }

                var newIndicator = new DirectionIndicator
                {
                    Color = (trajectoryInLoop == actuallySelectedItem) ? SelectedStrokeBrush : DefaultStrokeBrush,
                    ArrowheadSize = 8,
                    StrokeThickness = 1.5
                };

                List<System.Windows.Point> points = trajectoryInLoop.Points;
                Point arrowStartPoint = new Point();
                Point arrowEndPoint = new Point();
                bool addIndicator = false;

                switch (trajectoryInLoop.PrimitiveType)
                {
                    case "Line":
                        if (points.Count >= 2)
                        {
                            Point p_start = points[0];
                            Point p_end = points[points.Count - 1];
                            Point midPoint = new Point((p_start.X + p_end.X) / 2, (p_start.Y + p_end.Y) / 2);
                            Vector direction = p_end - p_start;

                            if (direction.Length > 0)
                            {
                                direction.Normalize();
                                arrowStartPoint = midPoint - direction * (fixedArrowLineLength / 2.0);
                                arrowEndPoint = midPoint + direction * (fixedArrowLineLength / 2.0);
                                addIndicator = true;
                            }
                        }
                        break;
                    case "Arc":
                        if (points.Count >= 2)
                        {
                            Point p0 = points[0];
                            Point p1 = points[1];
                            Vector direction = p1 - p0;

                            if (direction.Length > 0.001)
                            {
                                direction.Normalize();
                                arrowStartPoint = p0 - direction * (fixedArrowLineLength / 2.0);
                                arrowEndPoint = p0 + direction * (fixedArrowLineLength / 2.0);
                                addIndicator = true;
                            }
                        }
                        break;
                    case "Circle":
                        if (points.Count >= 2)
                        {
                            Point p0 = points[0];
                            Point p1 = points[1];
                            Vector direction = p1 - p0;

                            if (direction.Length > 0)
                            {
                                direction.Normalize();
                                arrowStartPoint = p0 - direction * (fixedArrowLineLength / 2.0);
                                arrowEndPoint = p0 + direction * (fixedArrowLineLength / 2.0);
                                addIndicator = true;
                            }
                        }
                        break;
                    default:
                        break;
                }

                if (addIndicator && arrowStartPoint != arrowEndPoint)
                {
                    newIndicator.StartPoint = arrowStartPoint;
                    newIndicator.EndPoint = arrowEndPoint;
                    System.Windows.Controls.Panel.SetZIndex(newIndicator, 99);
                    CadCanvas.Children.Add(newIndicator);
                    _directionIndicators.Add(newIndicator);
                }
            }
        }

        private void MoveTrajectoryUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count) return;
            var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
            var selectedIndex = CurrentPassTrajectoriesListBox.SelectedIndex;

            if (selectedIndex > 0 && currentPass.Trajectories.Count > selectedIndex)
            {
                var itemToMove = currentPass.Trajectories[selectedIndex];
                currentPass.Trajectories.RemoveAt(selectedIndex);
                currentPass.Trajectories.Insert(selectedIndex - 1, itemToMove);

                CurrentPassTrajectoriesListBox.ItemsSource = null;
                CurrentPassTrajectoriesListBox.ItemsSource = currentPass.Trajectories;
                CurrentPassTrajectoriesListBox.SelectedIndex = selectedIndex - 1;
                AppLogger.Log($"Trajectory moved up in pass '{currentPass.PassName}': '{itemToMove.ToString()}' to index {selectedIndex - 1}.");
                isConfigurationDirty = true;
                RefreshCadCanvasHighlights();
                UpdateDirectionIndicator();
                UpdateOrderNumberLabels();
            }
        }

        private void MoveTrajectoryDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count) return;
            var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
            var selectedIndex = CurrentPassTrajectoriesListBox.SelectedIndex;

            if (selectedIndex >= 0 && selectedIndex < currentPass.Trajectories.Count - 1)
            {
                var itemToMove = currentPass.Trajectories[selectedIndex];
                currentPass.Trajectories.RemoveAt(selectedIndex);
                currentPass.Trajectories.Insert(selectedIndex + 1, itemToMove);

                CurrentPassTrajectoriesListBox.ItemsSource = null;
                CurrentPassTrajectoriesListBox.ItemsSource = currentPass.Trajectories;
                CurrentPassTrajectoriesListBox.SelectedIndex = selectedIndex + 1;
                AppLogger.Log($"Trajectory moved down in pass '{currentPass.PassName}': '{itemToMove.ToString()}' to index {selectedIndex + 1}.");
                isConfigurationDirty = true;
                RefreshCadCanvasHighlights();
                UpdateDirectionIndicator();
                UpdateOrderNumberLabels();
            }
        }

        private void UpdateSelectedTrajectoryDetailUI()
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                _trajectoryInDetailView = selectedTrajectory;

                TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = true;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsEnabled = true;
                TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = true;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsEnabled = true;

                TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = selectedTrajectory.UpperNozzleLiquidOn;
                TrajectoryUpperNozzleGasOnCheckBox.IsChecked = selectedTrajectory.UpperNozzleGasOn;

                TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = selectedTrajectory.LowerNozzleLiquidOn;
                TrajectoryLowerNozzleGasOnCheckBox.IsChecked = selectedTrajectory.LowerNozzleGasOn;

                if (TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked == true)
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsChecked = true;
                    selectedTrajectory.UpperNozzleGasOn = true;
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = false;
                }
                else
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = true;
                }

                if (TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked == true)
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsChecked = true;
                    selectedTrajectory.LowerNozzleGasOn = true;
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = false;
                }
                else
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = true;
                }

                TrajectoryIsReversedCheckBox.IsChecked = selectedTrajectory.IsReversed;

                if (selectedTrajectory.PrimitiveType == "Line" || selectedTrajectory.PrimitiveType == "Arc")
                {
                    TrajectoryIsReversedCheckBox.Visibility = Visibility.Visible;
                }
                else
                {
                    TrajectoryIsReversedCheckBox.Visibility = Visibility.Collapsed;
                }

                LineHeightControlsPanel.Visibility = selectedTrajectory.PrimitiveType == "Line" ? Visibility.Visible : Visibility.Collapsed;
                ArcHeightControlsPanel.Visibility = selectedTrajectory.PrimitiveType == "Arc" ? Visibility.Visible : Visibility.Collapsed;
                CircleHeightControlsPanel.Visibility = selectedTrajectory.PrimitiveType == "Circle" ? Visibility.Visible : Visibility.Collapsed;

                if (selectedTrajectory.PrimitiveType == "Line")
                {
                    LineStartZTextBox.Text = selectedTrajectory.LineStartPoint.Z.ToString("F3");
                    LineEndZTextBox.Text = selectedTrajectory.LineEndPoint.Z.ToString("F3");
                }
                else if (selectedTrajectory.PrimitiveType == "Arc")
                {
                    if (selectedTrajectory.ArcPoint1 != null)
                    {
                        ArcCenterZTextBox.Text = selectedTrajectory.ArcPoint1.Coordinates.Z.ToString("F3");
                    }
                    else
                    {
                        ArcCenterZTextBox.Text = string.Empty;
                    }
                }
                else if (selectedTrajectory.PrimitiveType == "Circle")
                {
                    CircleCenterZTextBox.Text = selectedTrajectory.CirclePoint1.Coordinates.Z.ToString("F3");
                }

                LineStartZTextBox.Tag = selectedTrajectory;
                LineEndZTextBox.Tag = selectedTrajectory;
                ArcCenterZTextBox.Tag = selectedTrajectory;
                CircleCenterZTextBox.Tag = selectedTrajectory;

                TrajectoryRuntimeTextBox.IsEnabled = true;
                TrajectoryRuntimeTextBox.Text = selectedTrajectory.Runtime.ToString("F3");
                TrajectoryRuntimeTextBox.Tag = selectedTrajectory;
            }
            else
            {
                _trajectoryInDetailView = null;

                TrajectoryUpperNozzleEnabledCheckBox.IsEnabled = false;
                TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = false;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsEnabled = false;
                TrajectoryLowerNozzleEnabledCheckBox.IsEnabled = false;
                TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = false;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsEnabled = false;

                TrajectoryUpperNozzleEnabledCheckBox.IsChecked = false;
                TrajectoryUpperNozzleGasOnCheckBox.IsChecked = false;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = false;
                TrajectoryLowerNozzleEnabledCheckBox.IsChecked = false;
                TrajectoryLowerNozzleGasOnCheckBox.IsChecked = false;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = false;

                TrajectoryIsReversedCheckBox.Visibility = Visibility.Collapsed;
                TrajectoryIsReversedCheckBox.IsChecked = false;
                LineHeightControlsPanel.Visibility = Visibility.Collapsed;
                ArcHeightControlsPanel.Visibility = Visibility.Collapsed;
                CircleHeightControlsPanel.Visibility = Visibility.Collapsed;
                LineStartZTextBox.Text = string.Empty;
                LineEndZTextBox.Text = string.Empty;
                ArcCenterZTextBox.Text = string.Empty;
                CircleCenterZTextBox.Text = string.Empty;

                LineStartZTextBox.Tag = null;
                LineEndZTextBox.Tag = null;
                ArcCenterZTextBox.Tag = null;
                CircleCenterZTextBox.Tag = null;

                TrajectoryRuntimeTextBox.IsEnabled = false;
                TrajectoryRuntimeTextBox.Text = string.Empty;
                TrajectoryRuntimeTextBox.Tag = null;
            }
        }


        private void TrajectoryRuntimeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TrajectoryRuntimeTextBox.Tag is Trajectory selectedTrajectory && selectedTrajectory != null)
            {
                if (double.TryParse(TrajectoryRuntimeTextBox.Text, out double newRuntime))
                {
                    double minRuntime = TrajectoryUtils.CalculateMinRuntime(selectedTrajectory);
                    if (newRuntime >= minRuntime)
                    {
                        if (selectedTrajectory.Runtime != newRuntime)
                        {
                            double oldRuntime = selectedTrajectory.Runtime;
                            selectedTrajectory.Runtime = newRuntime;
                            AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' Runtime changed from {oldRuntime:F3}s to {newRuntime:F3}s in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                            isConfigurationDirty = true;
                        }
                    }
                    else
                    {
                        string msg = $"Runtime cannot be less than the minimum calculated value: {minRuntime:F3} s.";
                        AppLogger.Log(msg, LogLevel.Warning);
                        MessageBox.Show(msg, "Invalid Runtime", MessageBoxButton.OK, MessageBoxImage.Warning);
                        TrajectoryRuntimeTextBox.Text = selectedTrajectory.Runtime.ToString("F3");
                    }
                }
                else
                {
                    string msg = "Invalid runtime value. Please enter a valid number.";
                    AppLogger.Log(msg, LogLevel.Error);
                    MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    TrajectoryRuntimeTextBox.Text = selectedTrajectory.Runtime.ToString("F3");
                }
            }
        }


        private void TrajectoryIsReversedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool isReversedNow = TrajectoryIsReversedCheckBox.IsChecked ?? false;
                if (selectedTrajectory.IsReversed != isReversedNow)
                {
                    selectedTrajectory.IsReversed = isReversedNow;
                    AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' IsReversed changed to: {isReversedNow} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;

                    if (selectedTrajectory.PrimitiveType == "Line")
                    {
                        var tempPoint = selectedTrajectory.LineStartPoint;
                        selectedTrajectory.LineStartPoint = selectedTrajectory.LineEndPoint;
                        selectedTrajectory.LineEndPoint = tempPoint;
                    }
                    else if (selectedTrajectory.PrimitiveType == "Arc")
                    {
                        var tempArcPoint = selectedTrajectory.ArcPoint1;
                        selectedTrajectory.ArcPoint1 = selectedTrajectory.ArcPoint3;
                        selectedTrajectory.ArcPoint3 = tempArcPoint;
                    }

                    TrajectoryUtils.GenerateDisplayPoints(selectedTrajectory, TrajectoryPointResolutionAngle);
                    CurrentPassTrajectoriesListBox.Items.Refresh();
                    RefreshCadCanvasHighlights();
                    UpdateDirectionIndicator();
                }
            }
        }

        // The method PopulateTrajectoryPoints has been moved to TrajectoryUtils.GenerateDisplayPoints.
        // All calls to PopulateTrajectoryPoints(trajectory) should be replaced with
        // TrajectoryUtils.GenerateDisplayPoints(trajectory, TrajectoryPointResolutionAngle).

        private void TrajectoryUpperNozzleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
            }
        }

        private void TrajectoryUpperNozzleGasOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool newValue = TrajectoryUpperNozzleGasOnCheckBox.IsChecked ?? false;
                if (selectedTrajectory.UpperNozzleGasOn != newValue)
                {
                    selectedTrajectory.UpperNozzleGasOn = newValue;
                    AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' UpperNozzleGasOn changed to: {newValue} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                }
            }
        }

        private void TrajectoryUpperNozzleLiquidOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool isLiquidOn = TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked ?? false;
                bool gasStateChangedByLogic = false;

                if (selectedTrajectory.UpperNozzleLiquidOn != isLiquidOn)
                {
                    selectedTrajectory.UpperNozzleLiquidOn = isLiquidOn;
                    AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' UpperNozzleLiquidOn changed to: {isLiquidOn} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                }

                if (isLiquidOn)
                {
                    if (!selectedTrajectory.UpperNozzleGasOn)
                    {
                        selectedTrajectory.UpperNozzleGasOn = true;
                        TrajectoryUpperNozzleGasOnCheckBox.IsChecked = true;
                        AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' UpperNozzleGasOn automatically set to: true due to LiquidOn in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                        gasStateChangedByLogic = true;
                        isConfigurationDirty = true;
                    }
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = false;
                }
                else
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = true;
                }
            }
        }

        private void TrajectoryLowerNozzleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
            }
        }

        private void TrajectoryLowerNozzleGasOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool newValue = TrajectoryLowerNozzleGasOnCheckBox.IsChecked ?? false;
                if (selectedTrajectory.LowerNozzleGasOn != newValue)
                {
                    selectedTrajectory.LowerNozzleGasOn = newValue;
                    AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' LowerNozzleGasOn changed to: {newValue} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                }
            }
        }

        private void TrajectoryLowerNozzleLiquidOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool isLiquidOn = TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked ?? false;
                bool gasStateChangedByLogic = false;

                if (selectedTrajectory.LowerNozzleLiquidOn != isLiquidOn)
                {
                    selectedTrajectory.LowerNozzleLiquidOn = isLiquidOn;
                    AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' LowerNozzleLiquidOn changed to: {isLiquidOn} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                }

                if (isLiquidOn)
                {
                    if (!selectedTrajectory.LowerNozzleGasOn)
                    {
                        selectedTrajectory.LowerNozzleGasOn = true;
                        TrajectoryLowerNozzleGasOnCheckBox.IsChecked = true;
                        AppLogger.Log($"Trajectory '{selectedTrajectory.ToString()}' LowerNozzleGasOn automatically set to: true due to LiquidOn in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                        gasStateChangedByLogic = true;
                        isConfigurationDirty = true;
                    }
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = false;
                }
                else
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = true;
                }
            }
        }

        private void ProductNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (this.IsLoaded)
            {
                isConfigurationDirty = true;
            }
        }

        private string _previousProductName = "";

        private void ProductNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded && ProductNameTextBox.Text != _previousProductName)
            {
                AppLogger.Log($"Product name changed from '{_previousProductName}' to '{ProductNameTextBox.Text}'.");
                _previousProductName = ProductNameTextBox.Text;
                isConfigurationDirty = true;
            }
        }

    private void UpdateLineStartZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Line")
        {
            if (double.TryParse(LineStartZTextBox.Text, out double newZ))
            {
                if (_trajectoryInDetailView.LineStartPoint.Z != newZ)
                {
                    double oldZ = _trajectoryInDetailView.LineStartPoint.Z;
                    _trajectoryInDetailView.LineStartPoint = new DxfPoint(
                        _trajectoryInDetailView.LineStartPoint.X,
                        _trajectoryInDetailView.LineStartPoint.Y,
                        newZ);
                    AppLogger.Log($"Trajectory '{_trajectoryInDetailView.ToString()}' LineStartPoint.Z changed from {oldZ:F3} to {newZ:F3} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                    CurrentPassTrajectoriesListBox.Items.Refresh();
                }
            }
            else
            {
                string msg = "Invalid Start Z value. Please enter a valid number.";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LineStartZTextBox.Text = _trajectoryInDetailView.LineStartPoint.Z.ToString("F3");
            }
        }
    }

    private void LineStartZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineStartZFromTextBox();
    }

    private void UpdateLineEndZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Line")
        {
            if (double.TryParse(LineEndZTextBox.Text, out double newZ))
            {
                if (_trajectoryInDetailView.LineEndPoint.Z != newZ)
                {
                    double oldZ = _trajectoryInDetailView.LineEndPoint.Z;
                    _trajectoryInDetailView.LineEndPoint = new DxfPoint(
                        _trajectoryInDetailView.LineEndPoint.X,
                        _trajectoryInDetailView.LineEndPoint.Y,
                        newZ);
                    AppLogger.Log($"Trajectory '{_trajectoryInDetailView.ToString()}' LineEndPoint.Z changed from {oldZ:F3} to {newZ:F3} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                    CurrentPassTrajectoriesListBox.Items.Refresh();
                }
            }
            else
            {
                string msg = "Invalid End Z value. Please enter a valid number.";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LineEndZTextBox.Text = _trajectoryInDetailView.LineEndPoint.Z.ToString("F3");
            }
        }
    }

    private void LineEndZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineEndZFromTextBox();
    }

    private void UpdateArcCenterZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Arc")
        {
            if (double.TryParse(ArcCenterZTextBox.Text, out double newZ))
            {
                bool changed = false;
                if (_trajectoryInDetailView.ArcPoint1 != null && _trajectoryInDetailView.ArcPoint1.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.ArcPoint1.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.ArcPoint1.Coordinates.X,
                        _trajectoryInDetailView.ArcPoint1.Coordinates.Y,
                        newZ);
                    changed = true;
                }
                if (_trajectoryInDetailView.ArcPoint2 != null && _trajectoryInDetailView.ArcPoint2.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.ArcPoint2.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.ArcPoint2.Coordinates.X,
                        _trajectoryInDetailView.ArcPoint2.Coordinates.Y,
                        newZ);
                    changed = true;
                }
                if (_trajectoryInDetailView.ArcPoint3 != null && _trajectoryInDetailView.ArcPoint3.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.ArcPoint3.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.ArcPoint3.Coordinates.X,
                        _trajectoryInDetailView.ArcPoint3.Coordinates.Y,
                        newZ);
                    changed = true;
                }

                if (changed)
                {
                    AppLogger.Log($"Trajectory '{_trajectoryInDetailView.ToString()}' Arc points Z (P1, P2, P3) set to {newZ:F3} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                    TrajectoryUtils.GenerateDisplayPoints(_trajectoryInDetailView, TrajectoryPointResolutionAngle);
                    CurrentPassTrajectoriesListBox.Items.Refresh();
                }
            }
            else
            {
                string msg = "Invalid Arc Z value. Please enter a valid number.";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                if(_trajectoryInDetailView.ArcPoint1 != null)
                    ArcCenterZTextBox.Text = _trajectoryInDetailView.ArcPoint1.Coordinates.Z.ToString("F3");
                else
                    ArcCenterZTextBox.Text = "0.000";
            }
        }
    }

    private void ArcCenterZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateArcCenterZFromTextBox();
    }

    private void UpdateCircleCenterZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Circle")
        {
            if (double.TryParse(CircleCenterZTextBox.Text, out double newZ))
            {
                bool changed = false;
                if (_trajectoryInDetailView.CirclePoint1.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.CirclePoint1.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.CirclePoint1.Coordinates.X,
                        _trajectoryInDetailView.CirclePoint1.Coordinates.Y, newZ);
                    changed = true;
                }
                if (_trajectoryInDetailView.CirclePoint2.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.CirclePoint2.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.CirclePoint2.Coordinates.X,
                        _trajectoryInDetailView.CirclePoint2.Coordinates.Y, newZ);
                    changed = true;
                }
                if (_trajectoryInDetailView.CirclePoint3.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.CirclePoint3.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.CirclePoint3.Coordinates.X,
                        _trajectoryInDetailView.CirclePoint3.Coordinates.Y, newZ);
                    changed = true;
                }

                if (changed)
                {
                    AppLogger.Log($"Trajectory '{_trajectoryInDetailView.ToString()}' Circle points Z (P1, P2, P3) set to {newZ:F3} in pass '{_currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].PassName}'.");
                    isConfigurationDirty = true;
                    TrajectoryUtils.GenerateDisplayPoints(_trajectoryInDetailView, TrajectoryPointResolutionAngle);
                    CurrentPassTrajectoriesListBox.Items.Refresh();
                }
            }
            else
            {
                string msg = "Invalid Circle Z value. Please enter a valid number.";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CircleCenterZTextBox.Text = _trajectoryInDetailView.CirclePoint1.Coordinates.Z.ToString("F3");
            }
        }
    }

    private void CircleCenterZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCircleCenterZFromTextBox();
    }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            AppLogger.Log("Application closing.");
            _modbusService.Disconnect();
        }

        private void CadCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Debug.WriteLine($"[DEBUG] CadCanvas_SizeChanged: NewSize=({e.NewSize.Width}, {e.NewSize.Height}), DXF Loaded={_currentDxfDocument != null}");
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0 && _currentDxfDocument != null)
            {
                PerformFitToView();
            }
        }

        private void DrawDxfEntities()
        {
            if (_currentDxfDocument == null)
            {
                AppLogger.Log("DrawDxfEntities: No DXF document loaded.", LogLevel.Warning);
                return;
            }

            AppLogger.Log("=== Drawing DXF Entities ===", LogLevel.Info);
            foreach (var entity in _currentDxfDocument.Entities)
            {
                System.Windows.Shapes.Shape? shape = null;

                if (entity is DxfLine line)
                {
                    var wpfLine = new System.Windows.Shapes.Line
                    {
                        X1 = line.P1.X,
                        Y1 = line.P1.Y,
                        X2 = line.P2.X,
                        Y2 = line.P2.Y,
                        Stroke = DefaultStrokeBrush,
                        StrokeThickness = DefaultStrokeThickness
                    };
                    shape = wpfLine;
                    AppLogger.Log($"Drawing Line: ({line.P1.X:F2}, {line.P1.Y:F2}) to ({line.P2.X:F2}, {line.P2.Y:F2})", LogLevel.Info);
                }
                else if (entity is DxfCircle circle)
                {
                    var wpfEllipse = new System.Windows.Shapes.Ellipse
                    {
                        Width = circle.Radius * 2,
                        Height = circle.Radius * 2,
                        Stroke = DefaultStrokeBrush,
                        StrokeThickness = DefaultStrokeThickness,
                        Fill = null
                    };
                    Canvas.SetLeft(wpfEllipse, circle.Center.X - circle.Radius);
                    Canvas.SetTop(wpfEllipse, circle.Center.Y - circle.Radius);
                    shape = wpfEllipse;
                    AppLogger.Log($"Drawing Circle: Center({circle.Center.X:F2}, {circle.Center.Y:F2}), Radius={circle.Radius:F2}", LogLevel.Info);
                }
                else if (entity is DxfArc arc)
                {
                    var pathGeometry = new PathGeometry();
                    var pathFigure = new PathFigure();
                    
                    double startAngle = arc.StartAngle * Math.PI / 180.0;
                    double endAngle = arc.EndAngle * Math.PI / 180.0;
                    
                    double startX = arc.Center.X + arc.Radius * Math.Cos(startAngle);
                    double startY = arc.Center.Y + arc.Radius * Math.Sin(startAngle);
                    pathFigure.StartPoint = new Point(startX, startY);

                    var arcSegment = new ArcSegment
                    {
                        Point = new Point(
                            arc.Center.X + arc.Radius * Math.Cos(endAngle),
                            arc.Center.Y + arc.Radius * Math.Sin(endAngle)),
                        Size = new Size(arc.Radius, arc.Radius),
                        IsLargeArc = Math.Abs(endAngle - startAngle) > Math.PI,
                        SweepDirection = endAngle > startAngle ? SweepDirection.Clockwise : SweepDirection.Counterclockwise
                    };

                    pathFigure.Segments.Add(arcSegment);
                    pathGeometry.Figures.Add(pathFigure);

                    var path = new System.Windows.Shapes.Path
                    {
                        Data = pathGeometry,
                        Stroke = DefaultStrokeBrush,
                        StrokeThickness = DefaultStrokeThickness
                    };
                    shape = path;
                    AppLogger.Log($"Drawing Arc: Center({arc.Center.X:F2}, {arc.Center.Y:F2}), Radius={arc.Radius:F2}, Angles={arc.StartAngle:F2} to {arc.EndAngle:F2}", LogLevel.Info);
                }
                else if (entity is DxfLwPolyline polyline)
                {
                    var points = new PointCollection();
                    foreach (var vertex in polyline.Vertices)
                    {
                        points.Add(new Point(vertex.X, vertex.Y));
                    }

                    if (polyline.IsClosed && points.Count > 0)
                    {
                        points.Add(points[0]);
                    }

                    var wpfPolyline = new System.Windows.Shapes.Polyline
                    {
                        Points = points,
                        Stroke = DefaultStrokeBrush,
                        StrokeThickness = DefaultStrokeThickness
                    };
                    shape = wpfPolyline;
                    AppLogger.Log($"Drawing Polyline: {polyline.Vertices.Count} vertices, Closed={polyline.IsClosed}", LogLevel.Info);
                }

                if (shape != null)
                {
                    string entityId = Guid.NewGuid().ToString();
                    shape.Tag = entityId;
                    shape.MouseLeftButtonDown += OnCadEntityClicked;
                    CadCanvas.Children.Add(shape);
                    _wpfShapeToDxfEntityMap[shape] = entity;
                    _dxfEntityHandleMap[entityId] = entity;
                }
            }

            AppLogger.Log($"Total entities drawn: {CadCanvas.Children.Count}", LogLevel.Info);
        }

        private void LoadDxfButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*",
                    FilterIndex = 1
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    _currentDxfFilePath = openFileDialog.FileName;
                    _currentDxfDocument = DxfFile.Load(_currentDxfFilePath);

                    AppLogger.Log("=== Loaded DXF Entities ===", LogLevel.Info);
                    foreach (var entity in _currentDxfDocument.Entities)
                    {
                        if (entity is DxfLine line)
                        {
                            AppLogger.Log($"Line: Start({line.P1.X:F2}, {line.P1.Y:F2}) to End({line.P2.X:F2}, {line.P2.Y:F2})", LogLevel.Info);
                        }
                        else if (entity is DxfCircle circle)
                        {
                            AppLogger.Log($"Circle: Center({circle.Center.X:F2}, {circle.Center.Y:F2}), Radius={circle.Radius:F2}", LogLevel.Info);
                        }
                        else if (entity is DxfLwPolyline polyline_dxf) // Renamed to avoid conflict
                        {
                            AppLogger.Log($"Polyline: {polyline_dxf.Vertices.Count} vertices:", LogLevel.Info);
                            foreach (var vertex in polyline_dxf.Vertices)
                            {
                                AppLogger.Log($"  - Point({vertex.X:F2}, {vertex.Y:F2})", LogLevel.Info);
                            }
                        }
                    }

                    _dxfBoundingBox = GetDxfBoundingBox(_currentDxfDocument);
                    AppLogger.Log($"=== DXF Bounding Box ===", LogLevel.Info);
                    AppLogger.Log($"X: {_dxfBoundingBox.X:F2} to {_dxfBoundingBox.X + _dxfBoundingBox.Width:F2}", LogLevel.Info);
                    AppLogger.Log($"Y: {_dxfBoundingBox.Y:F2} to {_dxfBoundingBox.Y + _dxfBoundingBox.Height:F2}", LogLevel.Info);
                    AppLogger.Log($"Width: {_dxfBoundingBox.Width:F2}, Height: {_dxfBoundingBox.Height:F2}", LogLevel.Info);

                    CadCanvas.Children.Clear();
                    _wpfShapeToDxfEntityMap.Clear();
                    _dxfEntityHandleMap.Clear();

                    DrawDxfEntities();
                    PerformFitToView();
                    
                    AppLogger.Log($"=== Canvas Information ===", LogLevel.Info);
                    AppLogger.Log($"Canvas Size: {CadCanvas.ActualWidth:F2} x {CadCanvas.ActualHeight:F2}", LogLevel.Info);
                    AppLogger.Log($"Final Scale: ({_scaleTransform.ScaleX:F2}, {_scaleTransform.ScaleY:F2})", LogLevel.Info);
                    AppLogger.Log($"Final Translation: ({_translateTransform.X:F2}, {_translateTransform.Y:F2})", LogLevel.Info);

                    StatusTextBlock.Text = $"Loaded: {System.IO.Path.GetFileName(_currentDxfFilePath)}";
                    isConfigurationDirty = true;
                }
            }
            catch (Exception ex)
            {
                HandleError(ex, "loading DXF file");
            }
        }

        private void PerformFitToView()
        {
            if (_dxfBoundingBox == null || CadCanvas == null) return;

            double canvasWidth = CadCanvas.ActualWidth;
            double canvasHeight = CadCanvas.ActualHeight;

            AppLogger.Log("=== PerformFitToView Started ===", LogLevel.Info);
            AppLogger.Log($"Canvas Size: {canvasWidth:F2} x {canvasHeight:F2}", LogLevel.Info);
            AppLogger.Log($"DXF Bounds: X({_dxfBoundingBox.Left:F2} to {_dxfBoundingBox.Right:F2}), Y({_dxfBoundingBox.Bottom:F2} to {_dxfBoundingBox.Top:F2})", LogLevel.Info);

            double scaleX = canvasWidth / _dxfBoundingBox.Width;
            double scaleY = canvasHeight / _dxfBoundingBox.Height;
            double scale = Math.Min(scaleX, scaleY);

            AppLogger.Log("Scale Calculation:", LogLevel.Info);
            AppLogger.Log($"  ScaleX (from width): {scaleX:F4}", LogLevel.Info);
            AppLogger.Log($"  ScaleY (from height): {scaleY:F4}", LogLevel.Info);
            AppLogger.Log($"  Final scale: {scale:F4}", LogLevel.Info);

            double scaledContentWidth = _dxfBoundingBox.Width * scale;
            double scaledContentHeight = _dxfBoundingBox.Height * scale;
            AppLogger.Log($"Scaled content size: {scaledContentWidth:F2} x {scaledContentHeight:F2}", LogLevel.Info);

            double translateX = (canvasWidth - scaledContentWidth) / 2 - (_dxfBoundingBox.Left * scale);
            
            double canvasCenterY = canvasHeight / 2;
            double contentCenterY = (_dxfBoundingBox.Top + _dxfBoundingBox.Bottom) / 2;
            double translateY = canvasCenterY + (contentCenterY * scale);

            AppLogger.Log("Translation Calculation:", LogLevel.Info);
            AppLogger.Log($"  TranslateX = {translateX:F2}", LogLevel.Info);
            AppLogger.Log($"  TranslateY = {translateY:F2}", LogLevel.Info);
            AppLogger.Log($"  Canvas Center Y = {canvasCenterY:F2}", LogLevel.Info);
            AppLogger.Log($"  Content Center Y = {contentCenterY:F2}", LogLevel.Info);

            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(new ScaleTransform(scale, -scale));
            transformGroup.Children.Add(new TranslateTransform(translateX, translateY));

            AppLogger.Log("=== Final Transform State ===", LogLevel.Info);
            AppLogger.Log($"Scale: ({scale:F4}, {-scale:F4})", LogLevel.Info);
            AppLogger.Log($"Translation: ({translateX:F2}, {translateY:F2})", LogLevel.Info);

            double viewportLeft = -translateX / scale;
            double viewportRight = (canvasWidth - translateX) / scale;
            double viewportBottom = -(translateY + canvasHeight) / scale;
            double viewportTop = -translateY / scale;

            AppLogger.Log("=== Viewport in DXF Coordinates ===", LogLevel.Info);
            AppLogger.Log($"X range: {viewportLeft:F2} to {viewportRight:F2}", LogLevel.Info);
            AppLogger.Log($"Y range: {viewportBottom:F2} to {viewportTop:F2}", LogLevel.Info);

            AppLogger.Log("=== Canvas Information ===", LogLevel.Info);
            AppLogger.Log($"Canvas Size: {canvasWidth:F2} x {canvasHeight:F2}", LogLevel.Info);
            AppLogger.Log($"Final Scale: ({scale:F2}, {-scale:F2})", LogLevel.Info);
            AppLogger.Log($"Final Translation: ({translateX:F2}, {translateY:F2})", LogLevel.Info);

            foreach (UIElement element in CadCanvas.Children)
            {
                element.RenderTransform = transformGroup;
            }
        }

        private void OnCadEntityClicked(object sender, MouseButtonEventArgs e)
        {
            Trace.WriteLine("++++ OnCadEntityClicked Fired ++++");
            Trace.Flush();
            Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Sender is {sender?.GetType().Name}");
            Trajectory trajectoryToSelect = null;

            if (sender is System.Windows.Shapes.Shape clickedShape && _wpfShapeToDxfEntityMap.TryGetValue(clickedShape, out DxfEntity? dxfEntity))
            {
                Trace.WriteLine($"  -- Checking sender type: {sender?.GetType().Name ?? "null"}, IsShape: true, Map contains key: true");
                Trace.Flush();
                Trace.WriteLine("  -- Condition (sender is Shape AND _wpfShapeToDxfEntityMap contains key) MET");
                Trace.Flush();

                Trace.WriteLine($"  -- Retrieved dxfEntity: {dxfEntity?.GetType().Name ?? "null"}");
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Retrieved DxfEntity: {dxfEntity?.GetType().Name}");
                Trace.Flush();

                Point clickPosCanvas = e.GetPosition(CadCanvas);
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Click position on Canvas = {clickPosCanvas}");
                Point clickPosDxf = _transformGroup.Inverse.Transform(clickPosCanvas);
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Click position transformed to DXF Coords = {clickPosDxf}");

                Rect entityDxfBounds = GetDxfEntityRect(dxfEntity);
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: DXF Entity Bounds = {entityDxfBounds}");
                if (entityDxfBounds != Rect.Empty)
                {
                    Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Does transformed click fall within entity bounds? {entityDxfBounds.Contains(clickPosDxf)}");
                }
                
                Trace.WriteLine($"  -- Checking current pass index: {_currentConfiguration.CurrentPassIndex}, SprayPasses count: {_currentConfiguration.SprayPasses?.Count ?? 0}");
                Trace.Flush();
                if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
                {
                    Trace.WriteLine("  -- Current pass index invalid, returning.");
                    Trace.Flush();
                    string msg = "Please select or create a spray pass first.";
                    AppLogger.Log(msg, LogLevel.Info);
                    MessageBox.Show(msg, "No Active Pass", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];

                var existingTrajectory = currentPass.Trajectories.FirstOrDefault(t => t.OriginalDxfEntity == dxfEntity);

                if (existingTrajectory != null)
                {
                    Trace.WriteLine("  -- Existing trajectory found. Selecting it.");
                    Trace.Flush();
                    trajectoryToSelect = existingTrajectory;
                }
                else
                {
                    Trace.WriteLine("  -- No existing trajectory. Creating and adding new one.");
                    Trace.Flush();
                    var newTrajectory = new Trajectory
                    {
                        OriginalDxfEntity = dxfEntity,
                        EntityType = dxfEntity.GetType().Name,
                        IsReversed = false
                    };

                    switch (dxfEntity)
                    {
                        case DxfLine line:
                            newTrajectory.PrimitiveType = "Line";
                            double p1DistSq = line.P1.X * line.P1.X + line.P1.Y * line.P1.Y + line.P1.Z * line.P1.Z;
                            double p2DistSq = line.P2.X * line.P2.X + line.P2.Y * line.P2.Y + line.P2.Z * line.P2.Z;
                            if (p1DistSq <= p2DistSq)
                            {
                                newTrajectory.LineStartPoint = line.P1;
                                newTrajectory.LineEndPoint = line.P2;
                            }
                            else
                            {
                                newTrajectory.LineStartPoint = line.P2;
                                newTrajectory.LineEndPoint = line.P1;
                            }
                            break;
                        case DxfArc arc:
                            newTrajectory.PrimitiveType = "Arc";
                            double startRad = arc.StartAngle * Math.PI / 180.0;
                            double endRad = arc.EndAngle * Math.PI / 180.0;

                            newTrajectory.ArcPoint1.Coordinates = new DxfPoint(
                                arc.Center.X + arc.Radius * Math.Cos(startRad),
                                arc.Center.Y + arc.Radius * Math.Sin(startRad),
                                arc.Center.Z
                            );

                            newTrajectory.ArcPoint3.Coordinates = new DxfPoint(
                                arc.Center.X + arc.Radius * Math.Cos(endRad),
                                arc.Center.Y + arc.Radius * Math.Sin(endRad),
                                arc.Center.Z
                            );

                            if (endRad < startRad)
                            {
                                endRad += 2 * Math.PI;
                            }
                            double midRad = (startRad + endRad) / 2.0;
                            newTrajectory.ArcPoint2.Coordinates = new DxfPoint(
                                arc.Center.X + arc.Radius * Math.Cos(midRad),
                                arc.Center.Y + arc.Radius * Math.Sin(midRad),
                                arc.Center.Z
                            );
                            break;
                        case DxfCircle circle:
                            newTrajectory.PrimitiveType = "Circle";

                            DxfVector normal_calc = circle.Normal.Normalize();
                            DxfPoint center_calc = circle.Center;
                            double radius_calc = circle.Radius;

                            DxfVector localXAxis;
                            double arbThreshold = 1.0 / 64.0;

                            if (Math.Abs(normal_calc.X) < arbThreshold && Math.Abs(normal_calc.Y) < arbThreshold)
                            {
                                localXAxis = (new DxfVector(0, 1, 0)).Cross(normal_calc).Normalize();
                            }
                            else
                            {
                                localXAxis = (DxfVector.ZAxis).Cross(normal_calc).Normalize();
                            }
                            DxfVector localYAxis = normal_calc.Cross(localXAxis).Normalize();

                            newTrajectory.CirclePoint1.Coordinates = new DxfPoint(
                                center_calc.X + localXAxis.X * radius_calc,
                                center_calc.Y + localXAxis.Y * radius_calc,
                                center_calc.Z + localXAxis.Z * radius_calc);

                            double angle120 = 2.0 * Math.PI / 3.0;
                            double cos120 = Math.Cos(angle120);
                            double sin120 = Math.Sin(angle120);
                            newTrajectory.CirclePoint2.Coordinates = new DxfPoint(
                                center_calc.X + (localXAxis.X * cos120 + localYAxis.X * sin120) * radius_calc,
                                center_calc.Y + (localXAxis.Y * cos120 + localYAxis.Y * sin120) * radius_calc,
                                center_calc.Z + (localXAxis.Z * cos120 + localYAxis.Z * sin120) * radius_calc);

                            double angle240 = 4.0 * Math.PI / 3.0;
                            double cos240 = Math.Cos(angle240);
                            double sin240 = Math.Sin(angle240);
                            newTrajectory.CirclePoint3.Coordinates = new DxfPoint(
                                center_calc.X + (localXAxis.X * cos240 + localYAxis.X * sin240) * radius_calc,
                                center_calc.Y + (localXAxis.Y * cos240 + localYAxis.Y * sin240) * radius_calc,
                                center_calc.Z + (localXAxis.Z * cos240 + localYAxis.Z * sin240) * radius_calc);

                            newTrajectory.OriginalCircleCenter = center_calc;
                            newTrajectory.OriginalCircleRadius = radius_calc;
                            newTrajectory.OriginalCircleNormal = normal_calc;

                            break;
                        default:
                            newTrajectory.PrimitiveType = dxfEntity.GetType().Name;
                            break;
                    }
                    TrajectoryUtils.GenerateDisplayPoints(newTrajectory, TrajectoryPointResolutionAngle);
                    newTrajectory.Runtime = TrajectoryUtils.CalculateMinRuntime(newTrajectory);
                    currentPass.Trajectories.Add(newTrajectory);
                    AppLogger.Log($"Trajectory added to pass '{currentPass.PassName}': Type '{newTrajectory.PrimitiveType}', EntityHandle '{newTrajectory.OriginalEntityHandle}'.", LogLevel.Info);
                    isConfigurationDirty = true;
                    trajectoryToSelect = newTrajectory;
                }

                RefreshCurrentPassTrajectoriesListBox();

                if (trajectoryToSelect != null)
                {
                    CurrentPassTrajectoriesListBox.SelectedItem = trajectoryToSelect;
                    Trace.WriteLine($"  -- Explicitly set CurrentPassTrajectoriesListBox.SelectedItem to: {trajectoryToSelect.ToString()}");
                }
                else if (existingTrajectory != null)
                {
                    Trace.WriteLine($"  -- Trajectory deselected. CurrentPassTrajectoriesListBox.SelectedItem is now: {CurrentPassTrajectoriesListBox.SelectedItem?.ToString() ?? "null"}");
                }
                Trace.Flush();

                RefreshCadCanvasHighlights();
                StatusTextBlock.Text = $"Selected {currentPass.Trajectories.Count} trajectories in {currentPass.PassName}.";

                Trace.WriteLine("  -- About to call UpdateDirectionIndicator from OnCadEntityClicked");
                Trace.Flush();
                UpdateDirectionIndicator();
                UpdateOrderNumberLabels();
            }
            else
            {
                Trace.WriteLine("  -- Condition (sender is Shape AND _wpfShapeToDxfEntityMap contains key) FAILED");
                Trace.Flush();
            }
        }

        private void UpdateTrajectoryPreview()
        {
            foreach (var polyline in _trajectoryPreviewPolylines)
            {
                CadCanvas.Children.Remove(polyline);
            }
            _trajectoryPreviewPolylines.Clear();

            foreach (var entity in _selectedDxfEntities)
            {
                List<System.Windows.Point> points = new List<System.Windows.Point>();
                
                switch (entity)
                {
                    case DxfLine line:
                        points = _cadService.ConvertLineToPoints(line);
                        break;
                    case DxfArc arc:
                        points = _cadService.ConvertArcToPoints(arc, TrajectoryPointResolutionAngle);
                        break;
                    case DxfCircle circle:
                        points = _cadService.ConvertCircleToPoints(circle, TrajectoryPointResolutionAngle);
                        break;
                }

                if (points.Count > 1)
                {
                    var polyline = new System.Windows.Shapes.Polyline
                    {
                        Points = new System.Windows.Media.PointCollection(points),
                        Stroke = Brushes.Red,
                        StrokeThickness = SelectedStrokeThickness,
                        StrokeDashArray = new System.Windows.Media.DoubleCollection { 5, 3 },
                        Tag = TrajectoryPreviewTag
                    };
                    
                    _trajectoryPreviewPolylines.Add(polyline);
                    CadCanvas.Children.Add(polyline);
                }
            }
        }

        private Models.Configuration CreateConfigurationFromCurrentState(bool forSaving = false)
        {
            _currentConfiguration.ProductName = ProductNameTextBox.Text;
            return _currentConfiguration;
        }
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            bool success = PerformSaveOperation();
            if (success)
            {
                isConfigurationDirty = false;
            }
            else
            {
            }
        }
        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            bool canProceed = PromptAndTrySaveChanges();
            if (!canProceed)
            {
                StatusTextBlock.Text = "Load configuration cancelled due to unsaved changes.";
                return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog {
                Filter = "Config files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Load Configuration File"
            };
            string initialDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "RobTeachProject", "RobTeach", "Configurations"));
            if (!Directory.Exists(initialDir)) initialDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configurations");
            openFileDialog.InitialDirectory = initialDir;

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    _currentConfiguration = _configService.LoadConfiguration(openFileDialog.FileName);
                    if (_currentConfiguration == null)
                    {
                        string msg = "Failed to load configuration file. The file might be corrupt or not a valid configuration.";
                        AppLogger.Log($"{msg} Path: {openFileDialog.FileName}", LogLevel.Error);
                        MessageBox.Show(msg, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        StatusTextBlock.Text = "Error: Failed to deserialize configuration.";
                        _currentConfiguration = new Models.Configuration { ProductName = $"Product_{DateTime.Now:yyyyMMddHHmmss}" };
                    }
                    else
                    {
                    }

                    ProductNameTextBox.Text = _currentConfiguration.ProductName;

                    ModbusIpAddressTextBox.Text = _currentConfiguration.ModbusIpAddress;
                    ModbusPortTextBox.Text = _currentConfiguration.ModbusPort.ToString();

                    if (_currentConfiguration.CanvasState != null)
                    {
                        _scaleTransform.ScaleX = _currentConfiguration.CanvasState.ScaleX;
                        _scaleTransform.ScaleY = _currentConfiguration.CanvasState.ScaleY;
                        _translateTransform.X = _currentConfiguration.CanvasState.TranslateX;
                        _translateTransform.Y = _currentConfiguration.CanvasState.TranslateY;
                    }

                    if (!string.IsNullOrEmpty(_currentConfiguration.DxfFileContent))
                    {
                        CadCanvas.Children.Clear();
                        _wpfShapeToDxfEntityMap.Clear();
                        _trajectoryPreviewPolylines.Clear();
                        _selectedDxfEntities.Clear();
                        _dxfEntityHandleMap.Clear();
                        _currentDxfDocument = null;
                        _dxfBoundingBox = Rect.Empty;
                        _currentDxfFilePath = "(Embedded DXF from project file)";

                        try
                        {
                            using (var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(_currentConfiguration.DxfFileContent)))
                            {
                                _currentDxfDocument = DxfFile.Load(memoryStream);
                            }

                            if (_currentDxfDocument != null)
                            {
                                List<System.Windows.Shapes.Shape> wpfShapes = _cadService.GetWpfShapesFromDxf(_currentDxfDocument);
                                int shapeIndex = 0;
                                int entityIndex = 0;
                                foreach(var entity in _currentDxfDocument.Entities)
                                {
                                    if (shapeIndex < wpfShapes.Count)
                                    {
                                        var wpfShape = wpfShapes[shapeIndex];
                                        if (wpfShape != null)
                                        {
                                            wpfShape.Stroke = DefaultStrokeBrush;
                                            wpfShape.StrokeThickness = DefaultStrokeThickness;
                                            wpfShape.MouseLeftButtonDown += OnCadEntityClicked;
                                            _wpfShapeToDxfEntityMap[wpfShape] = entity;
                                            CadCanvas.Children.Add(wpfShape);
                                        }
                                        else
                                        {
                                        }
                                    }
                                    else
                                    {
                                    }
                                    shapeIndex++;
                                    entityIndex++;
                                }
                                if (wpfShapes.Count != _currentDxfDocument.Entities.Count())
                                {
                                }
                                _dxfBoundingBox = GetDxfBoundingBox(_currentDxfDocument);
                                PerformFitToView();
                                StatusTextBlock.Text = "Loaded embedded DXF and configuration from project file.";
                            }
                            else
                            {
                                StatusTextBlock.Text = "Project file loaded, but embedded DXF content was invalid or empty.";
                                _currentDxfDocument = null;
                            }
                        }
                        catch (Exception dxfEx)
                        {
                            StatusTextBlock.Text = "Project file loaded, but failed to load embedded DXF content.";
                            string msg = "Failed to load embedded DXF content from the project file. It might be corrupt.";
                            AppLogger.Log(msg, dxfEx, LogLevel.Error);
                            MessageBox.Show($"{msg}\nError: {dxfEx.Message}", "DXF Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            _currentDxfDocument = null;
                            CadCanvas.Children.Clear();
                            _wpfShapeToDxfEntityMap.Clear();
                        }
                    }
                    else
                    {
                        CadCanvas.Children.Clear();
                        _wpfShapeToDxfEntityMap.Clear();
                        _trajectoryPreviewPolylines.Clear();
                        _selectedDxfEntities.Clear();
                        _dxfEntityHandleMap.Clear();
                        _currentDxfDocument = null;
                        _currentDxfFilePath = null;
                        _dxfBoundingBox = Rect.Empty;
                        PerformFitToView();
                        StatusTextBlock.Text = "Configuration loaded (no embedded DXF).";
                    }

                    if (_currentConfiguration.SprayPasses == null || !_currentConfiguration.SprayPasses.Any())
                    {
                        _currentConfiguration.SprayPasses = new List<SprayPass> { new SprayPass { PassName = "Default Pass 1" } };
                        _currentConfiguration.CurrentPassIndex = 0;
                    }
                    else if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
                    {
                        _currentConfiguration.CurrentPassIndex = _currentConfiguration.SprayPasses.Any() ? 0 : -1;
                    }

                    SprayPassesListBox.ItemsSource = null;
                    SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
                    if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < SprayPassesListBox.Items.Count)
                    {
                         SprayPassesListBox.SelectedIndex = _currentConfiguration.CurrentPassIndex;
                    }
                    else if (SprayPassesListBox.Items.Count > 0)
                    {
                        SprayPassesListBox.SelectedIndex = 0;
                        _currentConfiguration.CurrentPassIndex = 0;
                    }

                    RefreshCurrentPassTrajectoriesListBox();

                    if (_currentConfiguration.SelectedTrajectoryIndexInCurrentPass >= 0 &&
                        _currentConfiguration.SelectedTrajectoryIndexInCurrentPass < CurrentPassTrajectoriesListBox.Items.Count)
                    {
                        CurrentPassTrajectoriesListBox.SelectedIndex = _currentConfiguration.SelectedTrajectoryIndexInCurrentPass;
                    }

                    if (!string.IsNullOrEmpty(_currentConfiguration.DxfFileContent) && _currentDxfDocument != null)
                    {
                        ReconcileTrajectoryEntities(_currentConfiguration, _currentDxfDocument);
                    }

                    if (_currentConfiguration != null && _currentConfiguration.SprayPasses != null)
                    {
                        foreach (var pass in _currentConfiguration.SprayPasses)
                        {
                            if (pass.Trajectories != null)
                            {
                                foreach (var trajectory in pass.Trajectories)
                                {
                                    TrajectoryUtils.GenerateDisplayPoints(trajectory, TrajectoryPointResolutionAngle);
                                }
                            }
                        }
                    }

                    UpdateSelectedTrajectoryDetailUI();
                    RefreshCadCanvasHighlights();
                    UpdateDirectionIndicator();
                    UpdateOrderNumberLabels();

                    isConfigurationDirty = false;
                    StatusTextBlock.Text = $"Configuration loaded from {Path.GetFileName(openFileDialog.FileName)}";
                    AppLogger.Log($"Successfully loaded configuration: {Path.GetFileName(openFileDialog.FileName)}");
                    _currentLoadedConfigPath = openFileDialog.FileName;
                    StartTestRunButton.IsEnabled = false;
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "Error loading configuration.";
                    string msg = $"Failed to load configuration from {openFileDialog.FileName}";
                    AppLogger.Log(msg, ex, LogLevel.Error);
                    MessageBox.Show($"{msg}: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _currentConfiguration = new Models.Configuration { ProductName = $"Product_{DateTime.Now:yyyyMMddHHmmss}" };
                    ProductNameTextBox.Text = _currentConfiguration.ProductName;
                    UpdateSelectedTrajectoryDetailUI();
                    isConfigurationDirty = false;
                    UpdateDirectionIndicator();
                    UpdateOrderNumberLabels();
                }
            }
            else
            {
                StatusTextBlock.Text = "Load configuration cancelled.";
                AppLogger.Log("Configuration loading cancelled by user.");
            }
        }
        private void ModbusConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string ipAddress = ModbusIpAddressTextBox.Text;
            string portString = ModbusPortTextBox.Text;

            if (string.IsNullOrEmpty(ipAddress))
            {
                string msg = "IP address cannot be empty.";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(portString, out int port) || port < 1 || port > 65535)
            {
                string msg = "Invalid port number. Please enter a number between 1 and 65535.";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppLogger.Log($"Attempting Modbus connection to {ipAddress}:{port}.");
            ModbusResponse response = _modbusService.Connect(ipAddress, port);
            ModbusStatusTextBlock.Text = response.Message;

            if (response.Success)
            {
                AppLogger.Log($"Modbus connected successfully to {ipAddress}:{port}. Message: {response.Message}");
                ModbusStatusIndicatorEllipse.Fill = Brushes.Green;
                ModbusConnectButton.IsEnabled = false;
                ModbusDisconnectButton.IsEnabled = true;
                SendToRobotButton.IsEnabled = true;
                StatusTextBlock.Text = "Successfully connected to Modbus server.";
            }
            else
            {
                AppLogger.Log($"Modbus connection failed to {ipAddress}:{port}. Message: {response.Message}", LogLevel.Error);
                ModbusStatusIndicatorEllipse.Fill = Brushes.Red;
                StatusTextBlock.Text = "Failed to connect to Modbus server.";
                StartTestRunButton.IsEnabled = false;
            }
        }

        private void ModbusDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _modbusService.Disconnect();
            AppLogger.Log("Modbus disconnected by user.");
            ModbusStatusTextBlock.Text = "Disconnected";
            ModbusStatusIndicatorEllipse.Fill = Brushes.Red;
            ModbusConnectButton.IsEnabled = true;
            ModbusDisconnectButton.IsEnabled = false;
            SendToRobotButton.IsEnabled = false;
            StartTestRunButton.IsEnabled = false;
            StatusTextBlock.Text = "Disconnected from Modbus server.";
        }

        private void SendToRobotButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_modbusService.IsConnected)
            {
                string msg = "Not connected to Modbus server. Please connect first.";
                AppLogger.Log(msg, LogLevel.Warning);
                MessageBox.Show(msg, "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ushort robotStatusAddress = 1000;
            ModbusReadInt16Result statusResult = _modbusService.ReadHoldingRegisterInt16(robotStatusAddress);

            if (!statusResult.Success)
            {
                string msg = $"无法读取机械臂状态: {statusResult.Message}";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Modbus 读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_currentConfiguration == null || _currentConfiguration.SprayPasses == null || !_currentConfiguration.SprayPasses.Any())
            {
            }
            else
            {
                foreach (var pass in _currentConfiguration.SprayPasses)
                {
                    if (pass.Trajectories == null || !pass.Trajectories.Any())
                    {
                        string msg = $"Spray pass '{pass.PassName}' contains no primitives. Please add primitives to all configured passes or remove empty ones before sending.";
                        AppLogger.Log(msg, LogLevel.Warning);
                        MessageBox.Show(msg, "Empty Spray Pass", MessageBoxButton.OK, MessageBoxImage.Warning);
                        StatusTextBlock.Text = $"Sending aborted: Spray pass '{pass.PassName}' is empty.";
                        return;
                    }
                }
            }

            short robotStatus = statusResult.Value;

            if (robotStatus == 0)
            {
                string msg = "当前机械臂处于工作状态";
                AppLogger.Log(msg, LogLevel.Warning);
                MessageBox.Show(msg, "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else if (robotStatus == 2)
            {
                string msg = "当前机械臂错误";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            else if (robotStatus != 1)
            {
                string msg = $"未知的机械臂状态: {robotStatus}";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "状态未知", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppLogger.Log("Send to Robot initiated. Robot status is Ready.");

            string tempFilePath = string.Empty;
            try
            {
                tempFilePath = WriteSendDataToTempFile(_currentConfiguration);
                AppLogger.Log($"Robot data successfully written to temporary file: {tempFilePath}");
                StatusTextBlock.Text = $"Data for robot written to {tempFilePath}";
            }
            catch (Exception ex)
            {
                string msg = "Error writing data to temporary file";
                AppLogger.Log(msg, ex, LogLevel.Error);
                MessageBox.Show($"{msg}: {ex.Message}", "File Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "Error writing data to temp file. Sending aborted.";
                return;
            }

            if (_currentConfiguration.CurrentPassIndex >= 0 &&
                _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                SprayPass currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                if (currentPass.Trajectories != null)
                {
                    foreach (var trajectory in currentPass.Trajectories)
                    {
                        TrajectoryUtils.GenerateDisplayPoints(trajectory, TrajectoryPointResolutionAngle);
                    }
                }
            }
            else if (_currentConfiguration.SprayPasses == null || _currentConfiguration.SprayPasses.Count == 0)
            {
                string msg = "No spray passes in the current configuration to send.";
                AppLogger.Log(msg, LogLevel.Warning);
                MessageBox.Show(msg, "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else
            {
                string msg = $"Cannot send: Invalid current spray pass index ({_currentConfiguration.CurrentPassIndex}).";
                AppLogger.Log(msg, LogLevel.Warning);
                MessageBox.Show(msg, "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _currentConfiguration.ProductName = ProductNameTextBox.Text;
            ModbusResponse response = _modbusService.SendConfiguration(_currentConfiguration);

            if (response.Success)
            {
                AppLogger.Log($"Configuration successfully sent to robot. Modbus response: {response.Message}");
                StatusTextBlock.Text = "Configuration successfully sent to robot.";
                ModbusStatusTextBlock.Text = response.Message;
                StartTestRunButton.IsEnabled = true;
            }
            else
            {
                AppLogger.Log($"Failed to send configuration to robot. Modbus response: {response.Message}", LogLevel.Error);
                StatusTextBlock.Text = $"Failed to send configuration: {response.Message}";
                ModbusStatusTextBlock.Text = response.Message;
                StartTestRunButton.IsEnabled = false;
            }
        }

        private Rect GetDxfBoundingBox(DxfFile dxfDoc)
        {
            AppLogger.Log("GetDxfBoundingBox: Method started.", LogLevel.Info);
            if (dxfDoc == null)
            {
                AppLogger.Log("GetDxfBoundingBox: dxfDoc is null. Returning Rect.Empty.", LogLevel.Warning);
                return Rect.Empty;
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool hasValidBounds = false;

            if (dxfDoc.Entities != null && dxfDoc.Entities.Any())
            {
                AppLogger.Log($"GetDxfBoundingBox: Processing {dxfDoc.Entities.Count()} entities.", LogLevel.Info);

                foreach (var entity in dxfDoc.Entities)
                {
                    if (entity == null) continue;

                    var bounds = CalculateEntityBoundsSimple(entity);
                    if (!bounds.IsEmpty)
                    {
                        minX = Math.Min(minX, bounds.X);
                        minY = Math.Min(minY, bounds.Y);
                        maxX = Math.Max(maxX, bounds.X + bounds.Width);
                        maxY = Math.Max(maxY, bounds.Y + bounds.Height);
                        hasValidBounds = true;
                        AppLogger.Log($"Entity bounds: X={bounds.X:F2}, Y={bounds.Y:F2}, W={bounds.Width:F2}, H={bounds.Height:F2}", LogLevel.Info);
                    }
                }
            }

            if (!hasValidBounds)
            {
                AppLogger.Log("GetDxfBoundingBox: No valid bounds found. Returning Rect.Empty.", LogLevel.Warning);
                return Rect.Empty;
            }

            minX = Math.Min(minX, 0);
            minY = Math.Min(minY, 0);
            maxX = Math.Max(maxX, 0);
            maxY = Math.Max(maxY, 0);

            var result = new Rect(minX, minY, maxX - minX, maxY - minY);
            AppLogger.Log($"Final bounding box: X={result.X:F2}, Y={result.Y:F2}, W={result.Width:F2}, H={result.Height:F2}", LogLevel.Info);
            return result;
        }

        private Rect CalculateEntityBoundsSimple(DxfEntity entity)
        {
            AppLogger.Log($"CalculateEntityBoundsSimple: Processing entity of type {entity.GetType().Name}", LogLevel.Info);
            
            if (entity is DxfLine line)
            {
                AppLogger.Log($"Line: ({line.P1.X:F2}, {line.P1.Y:F2}) to ({line.P2.X:F2}, {line.P2.Y:F2})", LogLevel.Info);
                double minX = Math.Min(line.P1.X, line.P2.X);
                double minY = Math.Min(line.P1.Y, line.P2.Y);
                double maxX = Math.Max(line.P1.X, line.P2.X);
                double maxY = Math.Max(line.P1.Y, line.P2.Y);
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
            else if (entity is DxfCircle circle)
            {
                AppLogger.Log($"Circle: Center({circle.Center.X:F2}, {circle.Center.Y:F2}), Radius:{circle.Radius:F2}", LogLevel.Info);
                double minX = circle.Center.X - circle.Radius;
                double minY = circle.Center.Y - circle.Radius;
                double width = circle.Radius * 2;
                double height = circle.Radius * 2;
                return new Rect(minX, minY, width, height);
            }
            else if (entity is DxfArc arc)
            {
                AppLogger.Log($"Arc: Center({arc.Center.X:F2}, {arc.Center.Y:F2}), Radius:{arc.Radius:F2}, Start:{arc.StartAngle:F2}, End:{arc.EndAngle:F2}", LogLevel.Info);
                var startPoint = new Point(
                    arc.Center.X + arc.Radius * Math.Cos(arc.StartAngle * Math.PI / 180.0),
                    arc.Center.Y + arc.Radius * Math.Sin(arc.StartAngle * Math.PI / 180.0));
                var endPoint = new Point(
                    arc.Center.X + arc.Radius * Math.Cos(arc.EndAngle * Math.PI / 180.0),
                    arc.Center.Y + arc.Radius * Math.Sin(arc.EndAngle * Math.PI / 180.0));

                double minX = Math.Min(startPoint.X, endPoint.X);
                double minY = Math.Min(startPoint.Y, endPoint.Y);
                double maxX = Math.Max(startPoint.X, endPoint.X);
                double maxY = Math.Max(startPoint.Y, endPoint.Y);

                double startAngle_norm = arc.StartAngle; // Renamed to avoid conflict
                double endAngle_norm = arc.EndAngle; // Renamed to avoid conflict
                if (endAngle_norm < startAngle_norm) endAngle_norm += 360;

                for (int angle_check = 0; angle_check < 360; angle_check += 90) // Renamed to avoid conflict
                {
                    double normalizedAngle = angle_check;
                    while (normalizedAngle < startAngle_norm) normalizedAngle += 360;
                    if (normalizedAngle >= startAngle_norm && normalizedAngle <= endAngle_norm)
                    {
                        double rad = angle_check * Math.PI / 180.0;
                        double x = arc.Center.X + arc.Radius * Math.Cos(rad);
                        double y = arc.Center.Y + arc.Radius * Math.Sin(rad);
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }

                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
            else if (entity is DxfLwPolyline lwPolyline && lwPolyline.Vertices.Any())
            {
                AppLogger.Log($"LwPolyline: {lwPolyline.Vertices.Count} vertices, Closed:{lwPolyline.IsClosed}", LogLevel.Info);
                double minX = lwPolyline.Vertices[0].X;
                double minY = lwPolyline.Vertices[0].Y;
                double maxX = minX;
                double maxY = minY;

                foreach (var vertex in lwPolyline.Vertices)
                {
                    minX = Math.Min(minX, vertex.X);
                    minY = Math.Min(minY, vertex.Y);
                    maxX = Math.Max(maxX, vertex.X);
                    maxY = Math.Max(maxY, vertex.Y);
                }

                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
            else if (entity is DxfInsert insert)
            {
                AppLogger.Log($"Insert: Name={insert.Name}, Location=({insert.Location.X:F2}, {insert.Location.Y:F2}), Scale=({insert.XScaleFactor:F2}, {insert.YScaleFactor:F2}), Rotation={insert.Rotation:F2}", LogLevel.Info);
                return GetDxfInsertBounds(insert);
            }

            AppLogger.Log($"Unsupported entity type: {entity.GetType().Name}", LogLevel.Warning);
            return Rect.Empty;
        }

        private void CadCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (CadCanvas == null || _scaleTransform == null || _translateTransform == null)
            {
                AppLogger.Log("CadCanvas_MouseWheel: Canvas or transforms not initialized, skipping zoom.", LogLevel.Warning);
                return;
            }

            Point mousePos = e.GetPosition(CadCanvas);

            double zoomFactor = 1.1;
            double scaleChange;

            if (e.Delta > 0)
            {
                scaleChange = zoomFactor;
            }
            else
            {
                scaleChange = 1.0 / zoomFactor;
            }

            double newScaleX = _scaleTransform.ScaleX * scaleChange;
            double newScaleY = _scaleTransform.ScaleY * scaleChange;

            double minScale = 0.05;
            double maxScale = 20.0;
            if (Math.Abs(newScaleX) < minScale || Math.Abs(newScaleX) > maxScale)
            {
                AppLogger.Log($"CadCanvas_MouseWheel: Zoom scale {newScaleX:F4} out of limits [{minScale}, {maxScale}]. No zoom applied.", LogLevel.Debug);
                return;
            }

            AppLogger.Log($"CadCanvas_MouseWheel: MousePos=({mousePos.X:F2},{mousePos.Y:F2}), Delta={e.Delta}, OldScale=({_scaleTransform.ScaleX:F4},{_scaleTransform.ScaleY:F4}), ScaleChange={scaleChange:F4}", LogLevel.Debug);

            Point worldPointBeforeZoom = new Point(
                (mousePos.X - _translateTransform.X) / _scaleTransform.ScaleX,
                (mousePos.Y - _translateTransform.Y) / _scaleTransform.ScaleY
            );
            AppLogger.Log($"CadCanvas_MouseWheel: WorldPointUnderMouse (BeforeZoom)=({worldPointBeforeZoom.X:F3},{worldPointBeforeZoom.Y:F3})", LogLevel.Debug);


            _scaleTransform.ScaleX = newScaleX;
            _scaleTransform.ScaleY = newScaleY;
            AppLogger.Log($"CadCanvas_MouseWheel: NewScale=({_scaleTransform.ScaleX:F4},{_scaleTransform.ScaleY:F4})", LogLevel.Debug);

            double newTranslateX = mousePos.X - (worldPointBeforeZoom.X * _scaleTransform.ScaleX);
            double newTranslateY = mousePos.Y - (worldPointBeforeZoom.Y * _scaleTransform.ScaleY);

            _translateTransform.X = newTranslateX;
            _translateTransform.Y = newTranslateY;
            AppLogger.Log($"CadCanvas_MouseWheel: NewTranslate=({_translateTransform.X:F3},{_translateTransform.Y:F3})", LogLevel.Debug);

            StatusTextBlock.Text = $"Zoom: {Math.Abs(_scaleTransform.ScaleX * 100):F1}%";
            isConfigurationDirty = true;
            e.Handled = true;
        }

        private void CadCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed ||
                (e.LeftButton == MouseButtonState.Pressed && Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) )
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(this);
                CadCanvas.CaptureMouse();
                StatusTextBlock.Text = "Panning...";
                e.Handled = true;
            }
            else if (e.LeftButton == MouseButtonState.Pressed && e.Source == CadCanvas)
            {
                isSelectingWithRect = true;
                selectionStartPoint = e.GetPosition(CadCanvas);

                if (selectionRectangleUI != null && CadCanvas.Children.Contains(selectionRectangleUI))
                {
                    CadCanvas.Children.Remove(selectionRectangleUI);
                }
                selectionRectangleUI = null;

                selectionRectangleUI = new System.Windows.Shapes.Rectangle
                {
                    Stroke = Brushes.DodgerBlue,
                    StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 255))
                };

                Canvas.SetLeft(selectionRectangleUI, selectionStartPoint.X);
                Canvas.SetTop(selectionRectangleUI, selectionStartPoint.Y);
                selectionRectangleUI.Width = 0;
                selectionRectangleUI.Height = 0;

                CadCanvas.Children.Add(selectionRectangleUI);

                CadCanvas.CaptureMouse();
                StatusTextBlock.Text = "Defining selection area...";
                e.Handled = true;
            }
        }
        private void CadCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                System.Windows.Point currentPanPoint = e.GetPosition(this);
                Vector panDelta = currentPanPoint - _panStartPoint;
                _translateTransform.X += panDelta.X;
                _translateTransform.Y += panDelta.Y;
                _panStartPoint = currentPanPoint;
                e.Handled = true;
            }
            else if (isSelectingWithRect && selectionRectangleUI != null)
            {
                System.Windows.Point currentMousePos = e.GetPosition(CadCanvas);

                double x = Math.Min(selectionStartPoint.X, currentMousePos.X);
                double y = Math.Min(selectionStartPoint.Y, currentMousePos.Y);
                double width = Math.Abs(selectionStartPoint.X - currentMousePos.X);
                double height = Math.Abs(selectionStartPoint.Y - currentMousePos.Y);

                Canvas.SetLeft(selectionRectangleUI, x);
                Canvas.SetTop(selectionRectangleUI, y);
                selectionRectangleUI.Width = width;
                selectionRectangleUI.Height = height;

                e.Handled = true;
            }
        }
        private void CadCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                CadCanvas.ReleaseMouseCapture();
                StatusTextBlock.Text = "Pan complete.";
                e.Handled = true;
            }
            else if (isSelectingWithRect)
            {
                isSelectingWithRect = false;
                CadCanvas.ReleaseMouseCapture();

                if (selectionRectangleUI == null)
                {
                    e.Handled = true;
                    return;
                }

                Rect finalSelectionRect = new Rect(
                    Canvas.GetLeft(selectionRectangleUI),
                    Canvas.GetTop(selectionRectangleUI),
                    selectionRectangleUI.Width,
                    selectionRectangleUI.Height);

                CadCanvas.Children.Remove(selectionRectangleUI);
                selectionRectangleUI = null;

                StatusTextBlock.Text = "Selection processed.";
                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee): finalSelectionRect (Canvas UI Coords) = {finalSelectionRect}");
                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee): _scaleTransform=({_scaleTransform.ScaleX},{_scaleTransform.ScaleY}), _translateTransform=({_translateTransform.X},{_translateTransform.Y})");

                const double clickThreshold = 5.0;
                if (finalSelectionRect.Width < clickThreshold && finalSelectionRect.Height < clickThreshold)
                {
                    e.Handled = true;
                    return;
                }

                if (_currentConfiguration == null || _currentConfiguration.CurrentPassIndex < 0 ||
                    _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count ||
                    _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories == null)
                {
                    string msg = "Please select or create a spray pass first to add entities.";
                    AppLogger.Log(msg, LogLevel.Info);
                    MessageBox.Show(msg, "No Active Pass", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Handled = true;
                    return;
                }
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                bool selectionStateChanged = false;
                List<DxfEntity> marqueeHitEntities = new List<DxfEntity>();

                RectangleGeometry selectionGeometry = new RectangleGeometry(finalSelectionRect);
                GeometryHitTestParameters parameters = new GeometryHitTestParameters(selectionGeometry);

                HitTestResultCallback hitTestCallback = (HitTestResult result) =>
                {
                    if (result is GeometryHitTestResult geometryResult)
                    {
                        if (geometryResult.VisualHit is System.Windows.Shapes.Shape hitShape)
                        {
                            if (_wpfShapeToDxfEntityMap.TryGetValue(hitShape, out DxfEntity? hitEntity) && hitEntity != null)
                            {
                                if (!marqueeHitEntities.Contains(hitEntity))
                                {
                                    marqueeHitEntities.Add(hitEntity);
                                    Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee HitTest): Hit DxfEntity {hitEntity.GetType().Name}");
                                }
                            }
                        }
                    }
                    return HitTestResultBehavior.Continue;
                };

                VisualTreeHelper.HitTest(CadCanvas, null, hitTestCallback, parameters);

                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee HitTest): Found {marqueeHitEntities.Count} entities in marquee.");

                bool isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp: ShiftPressed={isShiftPressed}, Marquee Hits={marqueeHitEntities.Count}");

                if (isShiftPressed)
                {
                    int itemsDeselectedCount = 0;
                    for (int i = currentPass.Trajectories.Count - 1; i >= 0; i--)
                    {
                        Trajectory trajectory = currentPass.Trajectories[i];
                        if (marqueeHitEntities.Contains(trajectory.OriginalDxfEntity))
                        {
                            Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Shift+Marquee): Deselecting {trajectory.OriginalDxfEntity.GetType().Name}");
                            currentPass.Trajectories.RemoveAt(i);
                            itemsDeselectedCount++;
                        }
                    }
                    if (itemsDeselectedCount > 0)
                    {
                        AppLogger.Log($"Marquee deselection: {itemsDeselectedCount} trajectories removed from pass '{currentPass.PassName}'.");
                        selectionStateChanged = true;
                    }
                }
                else
                {
                    int itemsAddedCount = 0;
                    List<string> addedTrajectoryInfo = new List<string>();

                    foreach (DxfEntity? hitDxfEntity in marqueeHitEntities)
                    {
                        if (hitDxfEntity == null) continue;

                        bool alreadySelected = currentPass.Trajectories.Any(t => t.OriginalDxfEntity != null && AreEntitiesGeometricallyEquivalent(t.OriginalDxfEntity, hitDxfEntity));
                        if (!alreadySelected)
                        {
                            Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Normal Marquee - Additive): Adding {hitDxfEntity.GetType().Name} as it's not geometrically equivalent to any existing selected trajectory's entity.");
                            var newTrajectory = new Trajectory
                            {
                                OriginalDxfEntity = hitDxfEntity,
                                EntityType = hitDxfEntity.GetType().Name,
                                IsReversed = false
                            };
                            switch (hitDxfEntity)
                            {
                                case DxfLine line:
                                    newTrajectory.PrimitiveType = "Line";
                                    newTrajectory.LineStartPoint = line.P1;
                                    newTrajectory.LineEndPoint = line.P2;
                                    break;
                                case DxfArc arc:
                                    newTrajectory.PrimitiveType = "Arc";
                                    double startRadMarquee = arc.StartAngle * Math.PI / 180.0;
                                    double endRadMarquee = arc.EndAngle * Math.PI / 180.0;
                                    newTrajectory.ArcPoint1.Coordinates = new DxfPoint(
                                        arc.Center.X + arc.Radius * Math.Cos(startRadMarquee),
                                        arc.Center.Y + arc.Radius * Math.Sin(startRadMarquee),
                                        arc.Center.Z);
                                    newTrajectory.ArcPoint3.Coordinates = new DxfPoint(
                                        arc.Center.X + arc.Radius * Math.Cos(endRadMarquee),
                                        arc.Center.Y + arc.Radius * Math.Sin(endRadMarquee),
                                        arc.Center.Z);
                                    if (endRadMarquee < startRadMarquee) endRadMarquee += 2 * Math.PI;
                                    double midRadMarquee = (startRadMarquee + endRadMarquee) / 2.0;
                                    newTrajectory.ArcPoint2.Coordinates = new DxfPoint(
                                        arc.Center.X + arc.Radius * Math.Cos(midRadMarquee),
                                        arc.Center.Y + arc.Radius * Math.Sin(midRadMarquee),
                                        arc.Center.Z);
                                    break;
                                case DxfCircle circle:
                                    newTrajectory.PrimitiveType = "Circle";

                                    DxfVector marquee_normal = circle.Normal.Normalize();
                                    DxfPoint marquee_center = circle.Center;
                                    double marquee_radius = circle.Radius;
                                    DxfVector marquee_localXAxis;
                                    double marquee_arbThreshold = 1.0 / 64.0;

                                    if (Math.Abs(marquee_normal.X) < marquee_arbThreshold && Math.Abs(marquee_normal.Y) < marquee_arbThreshold)
                                    {
                                        marquee_localXAxis = (new DxfVector(0, 1, 0)).Cross(marquee_normal).Normalize();
                                    }
                                    else
                                    {
                                        marquee_localXAxis = (DxfVector.ZAxis).Cross(marquee_normal).Normalize();
                                    }
                                    DxfVector marquee_localYAxis = marquee_normal.Cross(marquee_localXAxis).Normalize();

                                    newTrajectory.CirclePoint1.Coordinates = new DxfPoint(
                                        marquee_center.X + marquee_localXAxis.X * marquee_radius,
                                        marquee_center.Y + marquee_localXAxis.Y * marquee_radius,
                                        marquee_center.Z + marquee_localXAxis.Z * marquee_radius);

                                    double marquee_angle120 = 2.0 * Math.PI / 3.0;
                                    double marquee_cos120 = Math.Cos(marquee_angle120);
                                    double marquee_sin120 = Math.Sin(marquee_angle120);
                                    DxfVector marquee_dirP2_unscaled = new DxfVector(
                                        marquee_localXAxis.X * marquee_cos120 + marquee_localYAxis.X * marquee_sin120,
                                        marquee_localXAxis.Y * marquee_cos120 + marquee_localYAxis.Y * marquee_sin120,
                                        marquee_localXAxis.Z * marquee_cos120 + marquee_localYAxis.Z * marquee_sin120);
                                    newTrajectory.CirclePoint2.Coordinates = new DxfPoint(
                                        marquee_center.X + marquee_dirP2_unscaled.X * marquee_radius,
                                        marquee_center.Y + marquee_dirP2_unscaled.Y * marquee_radius,
                                        marquee_center.Z + marquee_dirP2_unscaled.Z * marquee_radius);

                                    double marquee_angle240 = 4.0 * Math.PI / 3.0;
                                    double marquee_cos240 = Math.Cos(marquee_angle240);
                                    double marquee_sin240 = Math.Sin(marquee_angle240);
                                    DxfVector marquee_dirP3_unscaled = new DxfVector(
                                        marquee_localXAxis.X * marquee_cos240 + marquee_localYAxis.X * marquee_sin240,
                                        marquee_localXAxis.Y * marquee_cos240 + marquee_localYAxis.Y * marquee_sin240,
                                        marquee_localXAxis.Z * marquee_cos240 + marquee_localYAxis.Z * marquee_sin240);
                                    newTrajectory.CirclePoint3.Coordinates = new DxfPoint(
                                        marquee_center.X + marquee_dirP3_unscaled.X * marquee_radius,
                                        marquee_center.Y + marquee_dirP3_unscaled.Y * marquee_radius,
                                        marquee_center.Z + marquee_dirP3_unscaled.Z * marquee_radius);

                                    newTrajectory.OriginalCircleCenter = marquee_center;
                                    newTrajectory.OriginalCircleRadius = marquee_radius;
                                    newTrajectory.OriginalCircleNormal = marquee_normal;
                                    break;
                                default:
                                    newTrajectory.PrimitiveType = hitDxfEntity.GetType().Name;
                                    break;
                            }
                            TrajectoryUtils.GenerateDisplayPoints(newTrajectory, TrajectoryPointResolutionAngle);
                            newTrajectory.Runtime = TrajectoryUtils.CalculateMinRuntime(newTrajectory);
                            currentPass.Trajectories.Add(newTrajectory);
                            addedTrajectoryInfo.Add($"Type '{newTrajectory.PrimitiveType}', EntityHandle '{newTrajectory.OriginalEntityHandle}'");
                            itemsAddedCount++;
                        }
                    }
                    if (itemsAddedCount > 0)
                    {
                        AppLogger.Log($"Marquee selection: {itemsAddedCount} trajectories added to pass '{currentPass.PassName}'. Details: {string.Join("; ", addedTrajectoryInfo)}");
                        selectionStateChanged = true;
                    }
                }

                if (selectionStateChanged)
                {
                    Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee): Selection state changed. Refreshing UI.");
                    isConfigurationDirty = true;
                    RefreshCurrentPassTrajectoriesListBox();
                    RefreshCadCanvasHighlights();
                    UpdateDirectionIndicator();
                    UpdateOrderNumberLabels();
                    StatusTextBlock.Text = $"Selection updated in {currentPass.PassName}. Total: {currentPass.Trajectories.Count}.";
                }
                e.Handled = true;
            }
        }

        private bool PerformSaveOperation()
        {
            UpdateLineStartZFromTextBox();
            UpdateLineEndZFromTextBox();
            UpdateArcCenterZFromTextBox();
            UpdateCircleCenterZFromTextBox();

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "Config files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Save Configuration File",
                FileName = $"{ProductNameTextBox.Text}.json"
            };

            string initialDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "RobTeachProject", "RobTeach", "Configurations"));
            if (!Directory.Exists(initialDir))
            {
                initialDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configurations");
            }
            if (!Directory.Exists(initialDir))
            {
                try
                {
                    Directory.CreateDirectory(initialDir);
                }
                catch (Exception ex)
                {
                    string msg = "Error creating configurations directory.";
                    AppLogger.Log(msg, ex, LogLevel.Error);
                    StatusTextBlock.Text = $"{msg} {ex.Message}";
                    initialDir = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            saveFileDialog.InitialDirectory = initialDir;

            if (saveFileDialog.ShowDialog() == true)
            {
                _currentConfiguration.ProductName = ProductNameTextBox.Text;

                string dxfContentForSaving = string.Empty;
                if (!string.IsNullOrEmpty(_currentDxfFilePath) && File.Exists(_currentDxfFilePath))
                {
                    try
                    {
                        dxfContentForSaving = File.ReadAllText(_currentDxfFilePath);
                    }
                    catch (Exception ex)
                    {
                        string msg = $"Could not read DXF file content from '{_currentDxfFilePath}' for saving.";
                        AppLogger.Log(msg, ex, LogLevel.Warning);
                        MessageBox.Show($"Warning: {msg} {ex.Message}", "File Read Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (_currentDxfFilePath != "(Embedded DXF from project file)" && !string.IsNullOrEmpty(_currentConfiguration.DxfFileContent)) {
                            dxfContentForSaving = _currentConfiguration.DxfFileContent;
                        } else {
                            dxfContentForSaving = string.Empty;
                        }
                    }
                }
                else if (_currentDxfFilePath == "(Embedded DXF from project file)" && !string.IsNullOrEmpty(_currentConfiguration.DxfFileContent))
                {
                    dxfContentForSaving = _currentConfiguration.DxfFileContent;
                }
                else
                {
                    if (!string.IsNullOrEmpty(_currentConfiguration.DxfFileContent)) {
                        dxfContentForSaving = _currentConfiguration.DxfFileContent;
                    } else {
                        dxfContentForSaving = string.Empty;
                    }
                }
                _currentConfiguration.DxfFileContent = dxfContentForSaving;

                _currentConfiguration.ModbusIpAddress = ModbusIpAddressTextBox.Text;
                if (int.TryParse(ModbusPortTextBox.Text, out int parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
                {
                    _currentConfiguration.ModbusPort = parsedPort;
                }
                else
                {
                    _currentConfiguration.ModbusPort = 502;
                }

                _currentConfiguration.CanvasState ??= new CanvasViewSettings();
                _currentConfiguration.CanvasState.ScaleX = _scaleTransform.ScaleX;
                _currentConfiguration.CanvasState.ScaleY = _scaleTransform.ScaleY;
                _currentConfiguration.CanvasState.TranslateX = _translateTransform.X;
                _currentConfiguration.CanvasState.TranslateY = _translateTransform.Y;

                _currentConfiguration.SelectedTrajectoryIndexInCurrentPass = CurrentPassTrajectoriesListBox.SelectedIndex;

                Configuration configToSave = new Configuration
                {
                    ProductName = _currentConfiguration.ProductName,
                    CurrentPassIndex = _currentConfiguration.CurrentPassIndex,
                    DxfFileContent = _currentConfiguration.DxfFileContent,
                    ModbusIpAddress = _currentConfiguration.ModbusIpAddress,
                    ModbusPort = _currentConfiguration.ModbusPort,
                    CanvasState = _currentConfiguration.CanvasState,
                    SelectedTrajectoryIndexInCurrentPass = _currentConfiguration.SelectedTrajectoryIndexInCurrentPass,
                    SprayPasses = new List<SprayPass>()
                };

                if (_currentConfiguration.SprayPasses != null)
                {
                    foreach (var originalPass in _currentConfiguration.SprayPasses)
                    {
                        if (originalPass.Trajectories != null && originalPass.Trajectories.Any())
                        {
                            SprayPass passToSave = new SprayPass
                            {
                                PassName = originalPass.PassName
                            };
                            passToSave.Trajectories = new List<Trajectory>(originalPass.Trajectories);

                            configToSave.SprayPasses.Add(passToSave);
                        }
                    }
                }

                try
                {
                    _configService.SaveConfiguration(configToSave, saveFileDialog.FileName);
                    StatusTextBlock.Text = $"Configuration saved to {Path.GetFileName(saveFileDialog.FileName)}";
                    AppLogger.Log($"Configuration saved to: {saveFileDialog.FileName}");
                    _currentLoadedConfigPath = saveFileDialog.FileName;
                    return true;
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "Error saving configuration.";
                    string msg = $"Failed to save configuration to {saveFileDialog.FileName}";
                    AppLogger.Log(msg, ex, LogLevel.Error);
                    MessageBox.Show($"{msg}: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            else
            {
                StatusTextBlock.Text = "Save configuration cancelled.";
                AppLogger.Log("Save configuration cancelled by user.");
                return false;
            }
        }

        private DxfPoint TransformPoint(DxfPoint point, DxfPoint location, double xScale, double yScale, double rotationDegrees)
        {
            double scaledX = point.X * xScale;
            double scaledY = point.Y * yScale;

            double rotationRadians = rotationDegrees * Math.PI / 180.0;
            double cosR = Math.Cos(rotationRadians);
            double sinR = Math.Sin(rotationRadians);

            double rotatedX = scaledX * cosR - scaledY * sinR;
            double rotatedY = scaledX * sinR + scaledY * cosR;

            return new DxfPoint(rotatedX + location.X, rotatedY + location.Y, point.Z * 1.0 + location.Z);
        }

        private (double minX, double minY, double maxX, double maxY)? GetTransformedBounds(
            (double minX, double minY, double maxX, double maxY) localBounds,
            DxfPoint location, double xScale, double yScale, double rotationDegrees)
        {
            DxfPoint c1 = new DxfPoint(localBounds.minX, localBounds.minY, 0);
            DxfPoint c2 = new DxfPoint(localBounds.maxX, localBounds.minY, 0);
            DxfPoint c3 = new DxfPoint(localBounds.minX, localBounds.maxY, 0);
            DxfPoint c4 = new DxfPoint(localBounds.maxX, localBounds.maxY, 0);

            DxfPoint t1 = TransformPoint(c1, location, xScale, yScale, rotationDegrees);
            DxfPoint t2 = TransformPoint(c2, location, xScale, yScale, rotationDegrees);
            DxfPoint t3 = TransformPoint(c3, location, xScale, yScale, rotationDegrees);
            DxfPoint t4 = TransformPoint(c4, location, xScale, yScale, rotationDegrees);

            double resultMinX = Math.Min(Math.Min(t1.X, t2.X), Math.Min(t3.X, t4.X));
            double resultMinY = Math.Min(Math.Min(t1.Y, t2.Y), Math.Min(t3.Y, t4.Y));
            double resultMaxX = Math.Max(Math.Max(t1.X, t2.X), Math.Max(t3.X, t4.X));
            double resultMaxY = Math.Max(Math.Max(t1.Y, t2.Y), Math.Max(t3.Y, t4.Y));
            return (resultMinX, resultMinY, resultMaxX, resultMaxY);
        }


        private bool PromptAndTrySaveChanges()
        {
            if (!isConfigurationDirty)
            {
                return true;
            }

            MessageBoxResult result = MessageBox.Show(
                "You have unsaved changes. Would you like to save the current configuration?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    bool saveSuccess = PerformSaveOperation();
                    if (saveSuccess)
                    {
                        isConfigurationDirty = false;
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                case MessageBoxResult.No:
                    return true;
                case MessageBoxResult.Cancel:
                    return false;
                default:
                    return false;
            }
        }


        private void HandleError(Exception ex, string action) { /* ... (No change) ... */ }


        private bool PointEquals(DxfPoint p1, DxfPoint p2, double tolerance = 0.001)
        {
            return Math.Abs(p1.X - p2.X) < tolerance &&
                   Math.Abs(p1.Y - p2.Y) < tolerance &&
                   Math.Abs(p1.Z - p2.Z) < tolerance;
        }

        private bool AreEntitiesGeometricallyEquivalent(DxfEntity entity1, DxfEntity entity2, double tolerance = 0.001)
        {
            if (entity1 == null || entity2 == null)
            {
                Debug.WriteLineIf(entity1 == null || entity2 == null, $"[DEBUG] AreEntitiesGeometricallyEquivalent: One or both entities are null. Entity1: {(entity1 == null ? "null" : entity1.GetType().Name)}, Entity2: {(entity2 == null ? "null" : entity2.GetType().Name)}");
                return false;
            }
            if (entity1.GetType() != entity2.GetType())
            {
                Debug.WriteLine($"[DEBUG] AreEntitiesGeometricallyEquivalent: Entity types differ: {entity1.GetType().Name} vs {entity2.GetType().Name}");
                return false;
            }

            Debug.WriteLine($"[DEBUG] AreEntitiesGeometricallyEquivalent: Comparing two {entity1.GetType().Name}");

            switch (entity1)
            {
                case DxfLine line1 when entity2 is DxfLine line2:
                    bool p1p1 = PointEquals(line1.P1, line2.P1, tolerance);
                    bool p2p2 = PointEquals(line1.P2, line2.P2, tolerance);
                    bool p1p2 = PointEquals(line1.P1, line2.P2, tolerance);
                    bool p2p1 = PointEquals(line1.P2, line2.P1, tolerance);
                    Debug.WriteLine($"[DEBUG] LineCompare: L1P1={line1.P1}, L1P2={line1.P2} | L2P1={line2.P1}, L2P2={line2.P2}");
                    Debug.WriteLine($"[DEBUG] LineCompare: (P1s match: {p1p1}, P2s match: {p2p2}) OR (P1-L2P2 match: {p1p2}, P2-L2P1 match: {p2p1})");
                    return (p1p1 && p2p2) || (p1p2 && p2p1);

                case DxfCircle circle1 when entity2 is DxfCircle circle2:
                    bool centerMatch = PointEquals(circle1.Center, circle2.Center, tolerance);
                    bool radiusMatch = Math.Abs(circle1.Radius - circle2.Radius) < tolerance;
                    Debug.WriteLine($"[DEBUG] CircleCompare: C1=({circle1.Center}, R={circle1.Radius}) | C2=({circle2.Center}, R={circle2.Radius})");
                    Debug.WriteLine($"[DEBUG] CircleCompare: CenterMatch={centerMatch}, RadiusMatch={radiusMatch}");
                    return centerMatch && radiusMatch;

                case DxfArc arc1 when entity2 is DxfArc arc2:
                    double normalizedStartAngle1 = (arc1.StartAngle % 360 + 360) % 360;
                    double normalizedEndAngle1 = (arc1.EndAngle % 360 + 360) % 360;
                    double normalizedStartAngle2 = (arc2.StartAngle % 360 + 360) % 360;
                    double normalizedEndAngle2 = (arc2.EndAngle % 360 + 360) % 360;

                    bool arcCenterMatch = PointEquals(arc1.Center, arc2.Center, tolerance);
                    bool arcRadiusMatch = Math.Abs(arc1.Radius - arc2.Radius) < tolerance;
                    bool arcStartAngleMatch = Math.Abs(normalizedStartAngle1 - normalizedStartAngle2) < tolerance || Math.Abs(normalizedStartAngle1 - normalizedStartAngle2 - 360) < tolerance || Math.Abs(normalizedStartAngle1 - normalizedStartAngle2 + 360) < tolerance;
                    bool arcEndAngleMatch = Math.Abs(normalizedEndAngle1 - normalizedEndAngle2) < tolerance || Math.Abs(normalizedEndAngle1 - normalizedEndAngle2 - 360) < tolerance || Math.Abs(normalizedEndAngle1 - normalizedEndAngle2 + 360) < tolerance;

                    Debug.WriteLine($"[DEBUG] ArcCompare: A1=C({arc1.Center}),R({arc1.Radius}),SA({arc1.StartAngle}),EA({arc1.EndAngle})");
                    Debug.WriteLine($"[DEBUG] ArcCompare: A2=C({arc2.Center}),R({arc2.Radius}),SA({arc2.StartAngle}),EA({arc2.EndAngle})");
                    Debug.WriteLine($"[DEBUG] ArcCompare: NormA1=SA({normalizedStartAngle1}),EA({normalizedEndAngle1}) | NormA2=SA({normalizedStartAngle2}),EA({normalizedEndAngle2})");
                    Debug.WriteLine($"[DEBUG] ArcCompare: CenterMatch={arcCenterMatch}, RadiusMatch={arcRadiusMatch}, StartAngleMatch={arcStartAngleMatch}, EndAngleMatch={arcEndAngleMatch}");
                    return arcCenterMatch && arcRadiusMatch && arcStartAngleMatch && arcEndAngleMatch;

                case DxfLwPolyline poly1 when entity2 is DxfLwPolyline poly2:
                    Debug.WriteLine($"[DEBUG] LWPolylineCompare: VCount1={poly1.Vertices.Count}, VCount2={poly2.Vertices.Count}, Closed1={poly1.IsClosed}, Closed2={poly2.IsClosed}");
                    if (poly1.Vertices.Count != poly2.Vertices.Count || poly1.IsClosed != poly2.IsClosed) return false;
                    for(int i=0; i < poly1.Vertices.Count; i++)
                    {
                        var v1 = poly1.Vertices[i];
                        var v2 = poly2.Vertices[i];
                        bool xyMatch = PointEquals(new DxfPoint(v1.X, v1.Y, 0), new DxfPoint(v2.X, v2.Y, 0), tolerance);
                        bool bulgeMatch = Math.Abs(v1.Bulge - v2.Bulge) < tolerance;
                        Debug.WriteLine($"[DEBUG] LWPolylineCompare: V{i} P1=({v1.X},{v1.Y},B={v1.Bulge}) | P2=({v2.X},{v2.Y},B={v2.Bulge}) | XYMatch={xyMatch}, BulgeMatch={bulgeMatch}");
                        if (!xyMatch || !bulgeMatch)
                        {
                            return false;
                        }
                    }
                    return true;
                default:
                    Debug.WriteLine($"[WARNING] AreEntitiesGeometricallyEquivalent: Unhandled entity type {entity1.GetType().Name} for comparison.");
                    return false;
            }
        }

        private void ReconcileTrajectoryEntities(Models.Configuration config, DxfFile? currentDoc)
        {
            if (config == null || currentDoc == null || config.SprayPasses == null || !currentDoc.Entities.Any())
            {
                Debug.WriteLine("[DEBUG] ReconcileTrajectoryEntities: Skipping reconciliation due to null config, doc, passes, or empty document entities.");
                return;
            }

            Debug.WriteLine($"[DEBUG] ReconcileTrajectoryEntities: Starting. Document has {currentDoc.Entities.Count()} entities.");

            List<DxfEntity> availableDocEntities = new List<DxfEntity>(currentDoc.Entities);

            foreach (var pass in config.SprayPasses)
            {
                if (pass.Trajectories == null)
                {
                    continue;
                }

                for (int i = 0; i < pass.Trajectories.Count; i++)
                {
                    var trajectory = pass.Trajectories[i];
                    if (trajectory.OriginalDxfEntity == null)
                    {
                        Debug.WriteLine($"[DEBUG] ReconcileTrajectoryEntities: Trajectory {i} in pass '{pass.PassName}' has null OriginalDxfEntity.");
                        continue;
                    }

                    DxfEntity? matchedEntity = null;
                    int matchedEntityIndexInAvailableList = -1;

                    for (int j = 0; j < availableDocEntities.Count; j++)
                    {
                        if (AreEntitiesGeometricallyEquivalent(trajectory.OriginalDxfEntity, availableDocEntities[j]))
                        {
                            matchedEntity = availableDocEntities[j];
                            matchedEntityIndexInAvailableList = j;
                            break;
                        }
                    }

                    if (matchedEntity != null)
                    {
                        trajectory.OriginalDxfEntity = matchedEntity;
                        Debug.WriteLine($"[DEBUG] ReconcileTrajectoryEntities: Reconciled trajectory entity: {matchedEntity.GetType().Name} (Index in availableDocEntities was {matchedEntityIndexInAvailableList}, not removing).");
                    }
                    else
                    {
                        Debug.WriteLine($"[WARNING] ReconcileTrajectoryEntities: Could not find a matching live entity for deserialized {trajectory.OriginalDxfEntity.GetType().Name}.");
                    }
                }
            }
            Debug.WriteLine("[DEBUG] ReconcileTrajectoryEntities: Finished.");
        }

        private Rect GetArcSegmentBoundsFromBulge(Point p1, Point p2, double bulge)
        {
            if (Math.Abs(bulge) < 1e-9)
            {
                return new Rect(p1, p2);
            }

            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double chordLengthSquared = dx * dx + dy * dy;
            double chordLength = Math.Sqrt(chordLengthSquared);

            if (chordLength < 1e-9)
            {
                return new Rect(p1, p2);
            }

            double chordAngle = Math.Atan2(dy, dx);

            double includedAngle = 4.0 * Math.Atan(bulge);

            double radius = chordLength / (2.0 * Math.Sin(includedAngle / 2.0));
            if (double.IsInfinity(radius) || double.IsNaN(radius)) return new Rect(p1, p2);

            Point midPointChord = new Point(p1.X + dx / 2.0, p1.Y + dy / 2.0);

            double h = radius * Math.Cos(includedAngle / 2.0);

            double perpDx = -dy / chordLength;
            double perpDy = dx / chordLength;

            double offsetFactor = h * Math.Sign(bulge);

            Point center = new Point(
                midPointChord.X - offsetFactor * (dy / chordLength),
                midPointChord.Y + offsetFactor * (dx / chordLength)
            );

            double startAngle = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            double endAngle = Math.Atan2(p2.Y - center.Y, p2.Y - center.Y);

            if (bulge < 0)
            {
                if (endAngle > startAngle)
                    startAngle += 2 * Math.PI;
            }
            else
            {
                if (endAngle < startAngle)
                    endAngle += 2 * Math.PI;
            }

            double minX = Math.Min(p1.X, p2.X);
            double minY = Math.Min(p1.Y, p2.Y);
            double maxX = Math.Max(p1.X, p2.X);
            double maxY = Math.Max(p1.Y, p2.Y);

            Action<double> checkAngle = (angle) =>
            {
                bool angleInSweep;
                if (bulge > 0)
                {
                    double normalizedAngle = angle;
                    while (normalizedAngle < startAngle) normalizedAngle += 2 * Math.PI;
                    angleInSweep = normalizedAngle >= startAngle && normalizedAngle <= endAngle;
                }
                else
                {
                    double normalizedAngle = angle;
                    while (normalizedAngle > startAngle) normalizedAngle -= 2 * Math.PI;
                    angleInSweep = normalizedAngle <= startAngle && normalizedAngle >= endAngle;
                }

                if (angleInSweep)
                {
                    double x = center.X + radius * Math.Cos(angle);
                    double y = center.Y + radius * Math.Sin(angle);
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            };

            checkAngle(0);
            checkAngle(Math.PI / 2.0);
            checkAngle(Math.PI);
            checkAngle(3.0 * Math.PI / 2.0);

            if (minX > maxX || minY > maxY) return Rect.Empty;

            return new Rect(new Point(minX, minY), new Point(maxX, maxY));
        }

        private Rect GetDxfEntityRect(DxfEntity entity)
        {
            if (entity == null)
                return Rect.Empty;

            var bounds = CalculateEntityBoundsSimple(entity);
            if (!bounds.IsEmpty)
            {
                return bounds;
            }
            return Rect.Empty;
        }

        private void WritePointData(StreamWriter writer, DxfPoint point, float rx = 0f, float ry = 0f, float rz = 0f)
        {
            writer.WriteLine(((float)point.X).ToString("F3"));
            writer.WriteLine(((float)point.Y).ToString("F3"));
            writer.WriteLine(((float)point.Z).ToString("F3"));
            writer.WriteLine(rx.ToString("F3"));
            writer.WriteLine(ry.ToString("F3"));
            writer.WriteLine(rz.ToString("F3"));
        }

        private void WriteTrajectoryPointWithAnglesData(StreamWriter writer, TrajectoryPointWithAngles point)
        {
            writer.WriteLine(((float)point.Coordinates.X).ToString("F3"));
            writer.WriteLine(((float)point.Coordinates.Y).ToString("F3"));
            writer.WriteLine(((float)point.Coordinates.Z).ToString("F3"));
            writer.WriteLine(((float)point.Rx).ToString("F3"));
            writer.WriteLine(((float)point.Ry).ToString("F3"));
            writer.WriteLine(((float)point.Rz).ToString("F3"));
        }


        private string WriteSendDataToTempFile(Models.Configuration config)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string logDirectory = Path.Combine(baseDirectory, "log");

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            string dataFileName = $"RobTeach_SendData_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
            string dataFilePath = Path.Combine(logDirectory, dataFileName);

            using (StreamWriter writer = new StreamWriter(dataFilePath))
            {
                writer.WriteLine(((float)config.SprayPasses.Count).ToString("F3"));

                int passIndex = 0;
                foreach (var pass in config.SprayPasses)
                {
                    passIndex++;

                    writer.WriteLine(((float)pass.Trajectories.Count).ToString("F3"));

                    int primitiveIndexInPass = 0;
                    foreach (var trajectory in pass.Trajectories)
                    {
                        primitiveIndexInPass++;

                        writer.WriteLine(((float)primitiveIndexInPass).ToString("F3"));

                        float primitiveType = 0.0f;
                        if (trajectory.PrimitiveType == "Line") primitiveType = 1.0f;
                        else if (trajectory.PrimitiveType == "Circle") primitiveType = 2.0f;
                        else if (trajectory.PrimitiveType == "Arc") primitiveType = 3.0f;
                        writer.WriteLine(primitiveType.ToString("F3"));

                        writer.WriteLine((trajectory.UpperNozzleGasOn ? 11.0f : 10.0f).ToString("F3"));
                        writer.WriteLine((trajectory.UpperNozzleLiquidOn ? 12.0f : 10.0f).ToString("F3"));
                        writer.WriteLine((trajectory.LowerNozzleGasOn ? 21.0f : 20.0f).ToString("F3"));
                        writer.WriteLine((trajectory.LowerNozzleLiquidOn ? 22.0f : 20.0f).ToString("F3"));

                        double lengthInMeters = TrajectoryUtils.CalculateTrajectoryLength(trajectory);
                        double currentRuntime = trajectory.Runtime;
                        float speedForRobot = 0.0f;

                        if (lengthInMeters > 0.00001)
                        {
                            if (currentRuntime > 0.00001)
                            {
                                speedForRobot = (float)(lengthInMeters / currentRuntime);
                            }
                        }
                        writer.WriteLine(speedForRobot.ToString("F3"));

                        if (trajectory.PrimitiveType == "Line")
                        {
                            WritePointData(writer, trajectory.LineStartPoint);
                            WritePointData(writer, trajectory.LineEndPoint);
                        }
                        else if (trajectory.PrimitiveType == "Arc")
                        {
                            if (trajectory.ArcPoint1 == null || trajectory.ArcPoint2 == null || trajectory.ArcPoint3 == null)
                            {
                                for(int i=0; i < 3 * 6; i++) writer.WriteLine(0.0f.ToString("F3"));
                            }
                            else
                            {
                                WriteTrajectoryPointWithAnglesData(writer, trajectory.ArcPoint1);
                                WriteTrajectoryPointWithAnglesData(writer, trajectory.ArcPoint2);
                                WriteTrajectoryPointWithAnglesData(writer, trajectory.ArcPoint3);
                            }
                        }
                        else if (trajectory.PrimitiveType == "Circle")
                        {
                             if (trajectory.CirclePoint1 == null || trajectory.OriginalCircleCenter == null || trajectory.CirclePoint3 == null)
                             {
                                for(int i=0; i < 3 * 6; i++) writer.WriteLine(0.0f.ToString("F3"));
                             }
                             else
                             {
                                WriteTrajectoryPointWithAnglesData(writer, trajectory.CirclePoint1);
                                WriteTrajectoryPointWithAnglesData(writer, trajectory.CirclePoint2);
                                WriteTrajectoryPointWithAnglesData(writer, trajectory.CirclePoint3);
                             }
                        }
                        else
                        {
                             for(int i=0; i < 2 * 6; i++) writer.WriteLine(0.0f.ToString("F3"));
                        }

                        writer.WriteLine(0.0f.ToString("F3"));
                        writer.WriteLine(0.0f.ToString("F3"));
                        writer.WriteLine(0.0f.ToString("F3"));
                    }
                }
            }
            return dataFilePath;
        }

        private void StartTestRunButton_Click(object sender, RoutedEventArgs e)
        {
            string selectedSpeedModeName = "Slow";
            if (StandardSpeedRadioButton.IsChecked == true)
            {
                selectedSpeedModeName = "Standard";
            }

            string confirmationMessage = $"The robot will start a test run in '{selectedSpeedModeName} Speed' mode. Please ensure the robot's workspace is clear of any obstructions or personnel.\n\nDo you want to proceed?";
            MessageBoxResult confirmResult = MessageBox.Show(confirmationMessage, "Confirm Test Run", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirmResult == MessageBoxResult.No)
            {
                AppLogger.Log("Test run cancelled by user at confirmation dialog.");
                StatusTextBlock.Text = "Test run cancelled by user.";
                return;
            }

            AppLogger.Log($"User confirmed test run initiation ({selectedSpeedModeName} speed).");

            if (!_modbusService.IsConnected)
            {
                string msg = "Not connected to Modbus server. Please connect first to start a test run.";
                AppLogger.Log(msg, LogLevel.Warning);
                MessageBox.Show(msg, "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ushort robotStatusAddress = 1000;
            ModbusReadInt16Result statusResult = _modbusService.ReadHoldingRegisterInt16(robotStatusAddress);

            if (!statusResult.Success)
            {
                string msg = $"Failed to read robot status for test run: {statusResult.Message}";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Modbus Read Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            short robotStatus = statusResult.Value;
            if (robotStatus != 1)
            {
                string msg = $"Cannot start test run: Robot is not in a stopped/ready state (Current status at {robotStatusAddress}: {robotStatus}).";
                AppLogger.Log(msg, LogLevel.Warning);
                MessageBox.Show(msg, "Robot Not Ready", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ushort speedModeAddress = 1001;
            int speedModeValue = 11;
            string speedModeName = "Slow";

            if (StandardSpeedRadioButton.IsChecked == true)
            {
                speedModeValue = 22;
                speedModeName = "Standard";
            }

            AppLogger.Log($"Setting speed mode for Test Run to {speedModeName} (Value: {speedModeValue}) at address {speedModeAddress}.");
            ModbusResponse speedSetResponse = _modbusService.WriteSingleShortRegister(speedModeAddress, (short)speedModeValue);

            if (!speedSetResponse.Success)
            {
                string msg = $"Failed to set speed mode on robot for test run: {speedSetResponse.Message}";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Modbus Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            System.Threading.Thread.Sleep(50);

            ushort triggerAddress = 1002;
            short triggerValue = 33;
            AppLogger.Log($"Triggering Test Run (Value: {triggerValue}) at address {triggerAddress}.");
            ModbusResponse triggerResponse = _modbusService.WriteSingleShortRegister(triggerAddress, triggerValue);

            if (!triggerResponse.Success)
            {
                string msg = $"Failed to trigger test run on robot: {triggerResponse.Message}";
                AppLogger.Log(msg, LogLevel.Error);
                MessageBox.Show(msg, "Modbus Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                AppLogger.Log($"Test run ({speedModeName} speed) initiated successfully.");
                StatusTextBlock.Text = $"Test run ({speedModeName} speed) initiated.";
                MessageBox.Show($"Test run ({speedModeName} speed) initiated successfully.", "Test Run Started", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private Rect GetTransformedBounds(Rect originalBounds, DxfPoint insertPoint, double scaleX, double scaleY, double rotationDegrees)
        {
            if (originalBounds.IsEmpty)
            {
                return Rect.Empty;
            }

            var transform = new Matrix();
            
            transform.Scale(scaleX, scaleY);
            transform.Rotate(rotationDegrees);
            transform.Translate(insertPoint.X, insertPoint.Y);

            var points = new[]
            {
                new System.Windows.Point(originalBounds.Left, originalBounds.Top),
                new System.Windows.Point(originalBounds.Right, originalBounds.Top),
                new System.Windows.Point(originalBounds.Right, originalBounds.Bottom),
                new System.Windows.Point(originalBounds.Left, originalBounds.Bottom)
            };

            transform.Transform(points);

            double minX = points.Min(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxX = points.Max(p => p.X);
            double maxY = points.Max(p => p.Y);

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private void FitToViewButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Log("[USER ACTION] FitToViewButton_Click called.", LogLevel.Debug);
            PerformFitToView();
        }

        private Rect GetDxfInsertBounds(DxfInsert insert)
        {
            if (_currentDxfDocument == null)
            {
                AppLogger.Log($"GetDxfInsertBounds: DxfInsert '{insert.Name}' - _currentDxfDocument is null. Cannot resolve block. Using insertion point.", LogLevel.Warning);
                return new Rect(insert.Location.X, insert.Location.Y, 0, 0);
            }

            DxfBlock? block = _currentDxfDocument.Blocks.FirstOrDefault(b => b.Name == insert.Name);
            if (block == null || !block.Entities.Any())
            {
                AppLogger.Log($"GetDxfInsertBounds: DxfInsert '{insert.Name}' - Block not found or empty. Using insertion point.", LogLevel.Debug);
                return new Rect(insert.Location.X, insert.Location.Y, 0, 0);
            }

            Rect blockBounds = Rect.Empty;
            bool hasValidBounds = false;

            foreach (DxfEntity entityInBlock in block.Entities)
            {
                if (entityInBlock == null) continue;

                var entityBounds = CalculateEntityBoundsSimple(entityInBlock);
                if (!entityBounds.IsEmpty)
                {
                    if (!hasValidBounds)
                    {
                        blockBounds = entityBounds;
                        hasValidBounds = true;
                    }
                    else
                    {
                        blockBounds.Union(entityBounds);
                    }
                }
            }

            if (!hasValidBounds)
            {
                AppLogger.Log($"GetDxfInsertBounds: DxfInsert '{insert.Name}' - No valid entity bounds within block. Using insertion point.", LogLevel.Debug);
                return new Rect(insert.Location.X, insert.Location.Y, 0, 0);
            }

            return GetTransformedBounds(blockBounds, insert.Location, insert.XScaleFactor, insert.YScaleFactor, insert.Rotation);
        }

        private void UpdateShapesOnCanvas(List<System.Windows.Shapes.Shape?> shapes)
        {
            if (shapes == null)
            {
                AppLogger.Log("[MainWindow] UpdateShapesOnCanvas: shapes list is null.", LogLevel.Warning);
                return;
            }

            AppLogger.Log($"[MainWindow] UpdateShapesOnCanvas: Processing {shapes.Count} shapes.", LogLevel.Debug);
            var nonNullShapes = shapes.Where(s => s != null).Select(s => s!).ToList();
            AppLogger.Log($"[MainWindow] UpdateShapesOnCanvas: Found {nonNullShapes.Count} non-null shapes.", LogLevel.Debug);

            int entityIndex = 0;
            foreach (var shape in nonNullShapes)
            {
                shape.Stroke = DefaultStrokeBrush;
                shape.StrokeThickness = DefaultStrokeThickness;
                if (shape is System.Windows.Shapes.Path path)
                {
                    path.Fill = Brushes.Transparent;
                }

                if (_currentDxfDocument != null && entityIndex < _currentDxfDocument.Entities.Count())
                {
                    var entity = _currentDxfDocument.Entities.ElementAt(entityIndex);
                    shape.MouseLeftButtonDown += OnCadEntityClicked;
                    _wpfShapeToDxfEntityMap[shape] = entity;
                }
                entityIndex++;
            }

            CadCanvas.Children.Clear();
            foreach (var shape in nonNullShapes)
            {
                CadCanvas.Children.Add(shape);
            }

            CadCanvas.UpdateLayout();
            CadCanvas.InvalidateVisual();

            if (nonNullShapes.Any())
            {
                PerformFitToView();
            }
        }
    }
}
