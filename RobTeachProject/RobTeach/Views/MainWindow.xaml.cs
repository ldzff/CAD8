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
using RobTeach.Utils; // Added for GeometryUtils
using IxMilia.Dxf.Blocks; // Added for DxfBlock

// using netDxf.Header; // No longer needed with IxMilia.Dxf
using System.Windows.Threading; // Was for optional Dispatcher.Invoke, now used.
using System.Threading.Tasks; // Added for Task.Delay
// using System.Text.RegularExpressions; // Was for optional IP validation, not currently used.
using MahApps.Metro.Controls; // Added for MetroWindow

namespace RobTeach.Views
{
    public partial class MainWindow : MetroWindow
    {
        private static readonly List<string> _layersToIgnoreForBoundingBox = new List<string>
        {
            "DEFPOINTS", "AXES", "CONSTRUCTION", "0_REF", "REFERENCE", "DIMENSIONS", "TEXT_NOTES", "VIEWPORT"
        };

        private readonly CadService _cadService = new CadService();
        private readonly ConfigurationService _configService = new ConfigurationService();
        private readonly ModbusService _modbusService = new ModbusService();

        private DxfFile? _currentDxfDocument;
        private string? _currentDxfFilePath;
        private string? _currentLoadedConfigPath;
        private Models.Configuration _currentConfiguration;
        private bool isConfigurationDirty = false;
        private RobTeach.Models.Trajectory? _trajectoryInDetailView;

        private readonly List<DxfEntity> _selectedDxfEntities = new List<DxfEntity>();
        private readonly Dictionary<System.Windows.Shapes.Shape, DxfEntity> _wpfShapeToDxfEntityMap = new Dictionary<System.Windows.Shapes.Shape, DxfEntity>();
        private readonly Dictionary<string, DxfEntity> _dxfEntityHandleMap = new Dictionary<string, DxfEntity>();
        private readonly List<System.Windows.Shapes.Polyline> _trajectoryPreviewPolylines = new List<System.Windows.Shapes.Polyline>();
        private List<DirectionIndicator> _directionIndicators;
        private List<System.Windows.Controls.TextBlock> _orderNumberLabels = new List<System.Windows.Controls.TextBlock>();

        private ScaleTransform _scaleTransform;
        private TranslateTransform _translateTransform;
        private TransformGroup _transformGroup;
        private System.Windows.Point _panStartPoint;
        private bool _isPanning;
        private Rect _dxfBoundingBox = Rect.Empty;

        private System.Windows.Shapes.Rectangle? selectionRectangleUI = null;
        private System.Windows.Point selectionStartPoint;
        private bool isSelectingWithRect = false;

        private static readonly Brush DefaultStrokeBrush = Brushes.LightGray;
        private static readonly Brush SelectedStrokeBrush = Brushes.DodgerBlue;
        private const double DefaultStrokeThickness = 2;
        private const double SelectedStrokeThickness = 3.5;
        private const string TrajectoryPreviewTag = "TrajectoryPreview";
        private const double TrajectoryPointResolutionAngle = 15.0;


        public MainWindow()
        {
            InitializeComponent();
            AppLogger.Log("Application started.");

            var canvasBackgroundBrush = (SolidColorBrush)Resources["CanvasBackgroundBrush"];
            CadCanvas.Background = canvasBackgroundBrush;

            _directionIndicators = new List<DirectionIndicator>();

            ProductNameTextBox.Text = $"Product_{DateTime.Now:yyyyMMddHHmmss}";
            _previousProductName = ProductNameTextBox.Text;
            _currentConfiguration = new Models.Configuration();
            _currentConfiguration.ProductName = ProductNameTextBox.Text;

            _scaleTransform = new ScaleTransform(1, 1);
            _translateTransform = new TranslateTransform(0, 0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_scaleTransform);
            _transformGroup.Children.Add(_translateTransform);
            CadCanvas.RenderTransform = _transformGroup;

            CadCanvas.MouseWheel += CadCanvas_MouseWheel;
            CadCanvas.MouseDown += CadCanvas_MouseDown;
            CadCanvas.MouseMove += CadCanvas_MouseMove;
            CadCanvas.MouseUp += CadCanvas_MouseUp;

            CadCanvas.SizeChanged += CadCanvas_SizeChanged;

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

            AddPassButton.Click += AddPassButton_Click;
            RemovePassButton.Click += RemovePassButton_Click;
            RenamePassButton.Click += RenamePassButton_Click;
            SprayPassesListBox.SelectionChanged += SprayPassesListBox_SelectionChanged;

            CurrentPassTrajectoriesListBox.SelectionChanged += CurrentPassTrajectoriesListBox_SelectionChanged;
            MoveTrajectoryUpButton.Click += MoveTrajectoryUpButton_Click;
            MoveTrajectoryDownButton.Click += MoveTrajectoryDownButton_Click;

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

            TrajectoryIsReversedCheckBox.Checked += TrajectoryIsReversedCheckBox_Changed;
            TrajectoryIsReversedCheckBox.Unchecked += TrajectoryIsReversedCheckBox_Changed;
            TrajectoryRuntimeTextBox.LostFocus += TrajectoryRuntimeTextBox_LostFocus;
            ProductNameTextBox.LostFocus += ProductNameTextBox_LostFocus;
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
                if (CadCanvas.Children.Contains(label)) CadCanvas.Children.Remove(label);
            }
            _orderNumberLabels.Clear();

            if (_currentConfiguration?.SprayPasses == null || _currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count || _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories == null)
            {
                return;
            }
            var currentPassTrajectories = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories;
            for (int i = 0; i < currentPassTrajectories.Count; i++)
            {
                var selectedTrajectory = currentPassTrajectories[i];
                if (selectedTrajectory.Points == null || !selectedTrajectory.Points.Any()) continue;

                TextBlock orderLabel = new TextBlock { Text = (i + 1).ToString(), FontSize = 10, Foreground = Brushes.DarkSlateBlue, Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 180)), Padding = new Thickness(2, 0, 2, 0) };
                Point anchorPoint = (selectedTrajectory.PrimitiveType == "Line" && selectedTrajectory.Points.Count >= 2) ? new Point((selectedTrajectory.Points[0].X + selectedTrajectory.Points.Last().X) / 2, (selectedTrajectory.Points[0].Y + selectedTrajectory.Points.Last().Y) / 2) : selectedTrajectory.Points[selectedTrajectory.Points.Count / 2];
                Canvas.SetLeft(orderLabel, anchorPoint.X + 5); Canvas.SetTop(orderLabel, anchorPoint.Y - 15); Panel.SetZIndex(orderLabel, 100);
                CadCanvas.Children.Add(orderLabel); _orderNumberLabels.Add(orderLabel);
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
            if (SprayPassesListBox.SelectedIndex >= 0) _currentConfiguration.CurrentPassIndex = SprayPassesListBox.SelectedIndex;
            else if (!_currentConfiguration.SprayPasses.Any()) _currentConfiguration.CurrentPassIndex = -1;
            RefreshCurrentPassTrajectoriesListBox(); UpdateSelectedTrajectoryDetailUI(); RefreshCadCanvasHighlights(); UpdateDirectionIndicator(); UpdateOrderNumberLabels();
        }

        private void RefreshCurrentPassTrajectoriesListBox()
        {
            CurrentPassTrajectoriesListBox.ItemsSource = null;
            if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                CurrentPassTrajectoriesListBox.ItemsSource = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories;
            }
            UpdateSelectedTrajectoryDetailUI();
        }

        private void CurrentPassTrajectoriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppLogger.Log($"CurrentPassTrajectoriesListBox_SelectionChanged: SelectedItem='{CurrentPassTrajectoriesListBox.SelectedItem?.ToString() ?? "null"}'", LogLevel.Debug);
            UpdateSelectedTrajectoryDetailUI();
            UpdateDirectionIndicator();
            RefreshCadCanvasHighlights();
        }

        // CONSOLIDATED MOUSE EVENT HANDLERS WITH ENHANCED LOGGING START HERE

        private void CadCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Point rawPos = e.GetPosition(CadCanvas);
            Point dxfPos = (_transformGroup?.Inverse != null) ? _transformGroup.Inverse.Transform(rawPos) : new Point(double.NaN, double.NaN);

            AppLogger.Log($"CadCanvas_MouseDown: START. RawPos=({rawPos.X:F2},{rawPos.Y:F2}), DxfPos=({dxfPos.X:F2},{dxfPos.Y:F2}), Source={e.Source?.GetType().Name}, Button={e.ChangedButton}, ClickCount={e.ClickCount}", LogLevel.Debug);

            bool isCtrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            AppLogger.Log($"CadCanvas_MouseDown: ButtonStates: Left={e.LeftButton}, Middle={e.MiddleButton}, Right={e.RightButton}. CtrlPressed={isCtrlPressed}.", LogLevel.Debug);

            if (e.MiddleButton == MouseButtonState.Pressed || (e.LeftButton == MouseButtonState.Pressed && isCtrlPressed))
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(this);
                CadCanvas.CaptureMouse();
                StatusTextBlock.Text = "Panning...";
                AppLogger.Log($"CadCanvas_MouseDown: Pan mode INITIATED. _isPanning={_isPanning}. Mouse captured.", LogLevel.Debug);
                e.Handled = true;
            }
            else if (e.LeftButton == MouseButtonState.Pressed && e.Source == CadCanvas)
            {
                isSelectingWithRect = true;
                selectionStartPoint = rawPos;

                if (selectionRectangleUI != null && CadCanvas.Children.Contains(selectionRectangleUI))
                {
                    CadCanvas.Children.Remove(selectionRectangleUI);
                    AppLogger.Log("CadCanvas_MouseDown: Existing selectionRectangleUI removed.", LogLevel.Debug);
                }
                selectionRectangleUI = null;

                selectionRectangleUI = new System.Windows.Shapes.Rectangle
                {
                    Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 255))
                };
                AppLogger.Log($"CadCanvas_MouseDown: selectionRectangleUI created. isSelectingWithRect={isSelectingWithRect}.", LogLevel.Debug);

                Canvas.SetLeft(selectionRectangleUI, selectionStartPoint.X);
                Canvas.SetTop(selectionRectangleUI, selectionStartPoint.Y);
                selectionRectangleUI.Width = 0; selectionRectangleUI.Height = 0;

                CadCanvas.Children.Add(selectionRectangleUI);
                AppLogger.Log($"CadCanvas_MouseDown: selectionRectangleUI added to CadCanvas.Children at ({selectionStartPoint.X:F2},{selectionStartPoint.Y:F2}).", LogLevel.Debug);

                CadCanvas.CaptureMouse();
                StatusTextBlock.Text = "Defining selection area...";
                AppLogger.Log($"CadCanvas_MouseDown: Marquee selection mode INITIATED. Mouse captured.", LogLevel.Debug);
                e.Handled = true;
            }
            else
            {
                AppLogger.Log($"CadCanvas_MouseDown: Event not handled for pan or marquee start. Source was {e.Source?.GetType().Name} (expected CadCanvas for marquee). Button: {e.ChangedButton}", LogLevel.Debug);
            }
            AppLogger.Log($"CadCanvas_MouseDown: END. _isPanning={_isPanning}, isSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);
        }

        private void CadCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point currentRawMousePos = e.GetPosition(CadCanvas);
            AppLogger.Log($"CadCanvas_MouseMove: Fired. RawPos=({currentRawMousePos.X:F2},{currentRawMousePos.Y:F2}), IsPanning={_isPanning}, IsSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);

            if (_isPanning)
            {
                System.Windows.Point currentPanPoint = e.GetPosition(this);
                Vector panDelta = currentPanPoint - _panStartPoint;
                _translateTransform.X += panDelta.X;
                _translateTransform.Y += panDelta.Y;
                _panStartPoint = currentPanPoint;
                AppLogger.Log($"CadCanvas_MouseMove: Panning. New TranslateTransform=({_translateTransform.X:F2},{_translateTransform.Y:F2})", LogLevel.Debug);
                e.Handled = true;
            }
            else if (isSelectingWithRect && selectionRectangleUI != null)
            {
                AppLogger.Log($"CadCanvas_MouseMove (Marquee Update): selectionStartPoint=({selectionStartPoint.X:F2},{selectionStartPoint.Y:F2}), currentRawMousePos=({currentRawMousePos.X:F2},{currentRawMousePos.Y:F2})", LogLevel.Debug);

                double x = Math.Min(selectionStartPoint.X, currentRawMousePos.X);
                double y = Math.Min(selectionStartPoint.Y, currentRawMousePos.Y);
                double width = Math.Abs(selectionStartPoint.X - currentRawMousePos.X);
                double height = Math.Abs(selectionStartPoint.Y - currentRawMousePos.Y);
                AppLogger.Log($"CadCanvas_MouseMove (Marquee Update): Calculated Rect Coords for UI - X={x:F2}, Y={y:F2}, W={width:F2}, H={height:F2}", LogLevel.Debug);

                Canvas.SetLeft(selectionRectangleUI, x);
                Canvas.SetTop(selectionRectangleUI, y);
                selectionRectangleUI.Width = width;
                selectionRectangleUI.Height = height;
                AppLogger.Log($"CadCanvas_MouseMove (Marquee Update): selectionRectangleUI properties set.", LogLevel.Debug);
                e.Handled = true;
            }
        }

        private void CadCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Point rawMousePos = e.GetPosition(CadCanvas);
            Point dxfMousePos = (_transformGroup?.Inverse != null) ? _transformGroup.Inverse.Transform(rawMousePos) : new Point(double.NaN, double.NaN);
            AppLogger.Log($"CadCanvas_MouseUp: Fired. RawPos=({rawMousePos.X:F2},{rawMousePos.Y:F2}), DxfPos=({dxfMousePos.X:F2},{dxfMousePos.Y:F2}), Button={e.ChangedButton}", LogLevel.Debug);
            AppLogger.Log($"CadCanvas_MouseUp: Initial states - _isPanning={_isPanning}, isSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);

            if (_isPanning)
            {
                _isPanning = false;
                CadCanvas.ReleaseMouseCapture();
                StatusTextBlock.Text = "Pan complete.";
                AppLogger.Log($"CadCanvas_MouseUp: Pan mode COMPLETED. _isPanning={_isPanning}. Mouse capture released.", LogLevel.Debug);
                e.Handled = true;
            }
            else if (isSelectingWithRect)
            {
                bool wasSelectingWithRect = isSelectingWithRect; // Capture state before reset
                isSelectingWithRect = false; // Reset state first
                CadCanvas.ReleaseMouseCapture();
                AppLogger.Log($"CadCanvas_MouseUp: Marquee mode was active (wasSelectingWithRect={wasSelectingWithRect}), now isSelectingWithRect={isSelectingWithRect}. Mouse capture released.", LogLevel.Debug);


                if (selectionRectangleUI == null)
                {
                    AppLogger.Log("CadCanvas_MouseUp (Marquee): selectionRectangleUI is null, though wasSelectingWithRect was true. Aborting marquee.", LogLevel.Warning);
                    e.Handled = true;
                    return;
                }

                Rect finalSelectionRect = new Rect(
                    Canvas.GetLeft(selectionRectangleUI), Canvas.GetTop(selectionRectangleUI),
                    selectionRectangleUI.Width, selectionRectangleUI.Height);
                AppLogger.Log($"CadCanvas_MouseUp (Marquee): finalSelectionRect (UI Coords before removal) = X:{finalSelectionRect.X:F2}, Y:{finalSelectionRect.Y:F2}, W:{finalSelectionRect.Width:F2}, H:{finalSelectionRect.Height:F2}", LogLevel.Debug);

                CadCanvas.Children.Remove(selectionRectangleUI);
                selectionRectangleUI = null;
                AppLogger.Log($"CadCanvas_MouseUp (Marquee): selectionRectangleUI removed from canvas and nulled.", LogLevel.Debug);

                StatusTextBlock.Text = "Selection processed.";

                const double clickThreshold = 5.0;
                if (finalSelectionRect.Width < clickThreshold && finalSelectionRect.Height < clickThreshold)
                {
                    AppLogger.Log($"CadCanvas_MouseUp (Marquee): Drag was below click threshold (W:{finalSelectionRect.Width:F2}, H:{finalSelectionRect.Height:F2}). No marquee selection performed.", LogLevel.Debug);
                    e.Handled = true;
                    return;
                }

                if (_currentConfiguration == null || _currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count || _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories == null)
                {
                    AppLogger.Log("CadCanvas_MouseUp (Marquee): No active/valid spray pass for selection. Aborting.", LogLevel.Warning);
                    MessageBox.Show("Please select or create a spray pass first to add entities.", "No Active Pass", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Handled = true;
                    return;
                }
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                bool selectionStateChanged = false;
                List<DxfEntity> marqueeHitEntities = new List<DxfEntity>();

                RectangleGeometry selectionGeometry = new RectangleGeometry(finalSelectionRect);
                GeometryHitTestParameters parameters = new GeometryHitTestParameters(selectionGeometry);

                HitTestResultCallback hitTestCallback = (HitTestResult result) => { /* ... existing callback ... */ return HitTestResultBehavior.Continue; };
                VisualTreeHelper.HitTest(CadCanvas, null, hitTestCallback, parameters);
                AppLogger.Log($"CadCanvas_MouseUp (Marquee HitTest): VisualTreeHelper.HitTest completed. Found {marqueeHitEntities.Count} entities intersecting marquee rectangle.", LogLevel.Debug);

                bool isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                AppLogger.Log($"CadCanvas_MouseUp (Marquee): ShiftPressed={isShiftPressed}, Marquee Hits={marqueeHitEntities.Count}", LogLevel.Debug);

                // ... (rest of existing marquee selection logic for adding/removing trajectories) ...
                // (This part is complex and assumed to be functionally okay if hit entities are correct)
                 if (isShiftPressed)
                {
                    int itemsDeselectedCount = 0;
                    for (int i = currentPass.Trajectories.Count - 1; i >= 0; i--)
                    {
                        Trajectory trajectory = currentPass.Trajectories[i];
                        if (marqueeHitEntities.Contains(trajectory.OriginalDxfEntity))
                        {
                            currentPass.Trajectories.RemoveAt(i); itemsDeselectedCount++;
                        }
                    }
                    if (itemsDeselectedCount > 0) { AppLogger.Log($"Marquee deselection (Shift): {itemsDeselectedCount} trajectories removed.", LogLevel.Info); selectionStateChanged = true; }
                }
                else
                {
                    int itemsAddedCount = 0; List<string> addedTrajectoryInfo = new List<string>();
                    foreach (DxfEntity? hitDxfEntity in marqueeHitEntities) { /* ... existing add logic ... */ }
                    if (itemsAddedCount > 0) { AppLogger.Log($"Marquee selection (Additive): {itemsAddedCount} new trajectories added.", LogLevel.Info); selectionStateChanged = true; }
                }

                if (selectionStateChanged)
                {
                    AppLogger.Log($"CadCanvas_MouseUp (Marquee): Selection state changed. Refreshing UI elements.", LogLevel.Debug);
                    isConfigurationDirty = true; RefreshCurrentPassTrajectoriesListBox(); RefreshCadCanvasHighlights(); UpdateDirectionIndicator(); UpdateOrderNumberLabels();
                    StatusTextBlock.Text = $"Selection updated in {currentPass.PassName}. Total: {currentPass.Trajectories.Count}.";
                }
                e.Handled = true;
            }
            else
            {
                 AppLogger.Log($"CadCanvas_MouseUp: Event not handled for pan or marquee completion.", LogLevel.Debug);
            }
             AppLogger.Log($"CadCanvas_MouseUp: END. _isPanning={_isPanning}, isSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);
        }

        private void OnCadEntityClicked(object sender, MouseButtonEventArgs e)
        {
            AppLogger.Log($"OnCadEntityClicked: START. Sender Type: {sender?.GetType().Name}", LogLevel.Debug);
            if (sender is System.Windows.Shapes.Shape clickedShapeForLog)
            {
                string? tag = clickedShapeForLog.Tag as string; // Assuming Tag is string (GUID)
                AppLogger.Log($"OnCadEntityClicked: Clicked shape Tag: {tag ?? "N/A"}. Fill: {clickedShapeForLog.Fill}, Stroke: {clickedShapeForLog.Stroke}", LogLevel.Debug);
            }

            Point clickPosCanvasLocal = e.GetPosition(CadCanvas);
            Point clickPosDxf = new Point(double.NaN, double.NaN);
            if (_transformGroup != null && _transformGroup.Inverse != null)
            {
                clickPosDxf = _transformGroup.Inverse.Transform(clickPosCanvasLocal);
            }
            AppLogger.Log($"OnCadEntityClicked: RawPos=({clickPosCanvasLocal.X:F2},{clickPosCanvasLocal.Y:F2}), DxfPos=({clickPosDxf.X:F2},{clickPosDxf.Y:F2}), Button={e.ChangedButton}, ClickCount={e.ClickCount}", LogLevel.Debug);

            Trajectory trajectoryToSelect = null;

            if (sender is System.Windows.Shapes.Shape clickedShape && _wpfShapeToDxfEntityMap.TryGetValue(clickedShape, out DxfEntity? dxfEntity))
            {
                AppLogger.Log($"OnCadEntityClicked: Shape successfully mapped to DxfEntity Type: {dxfEntity?.GetType().Name}", LogLevel.Debug);
                // ... (rest of existing OnCadEntityClicked logic for adding/selecting trajectory) ...
                // (This part is complex and assumed to be functionally okay if this handler is reached correctly)
            }
            else
            {
                 AppLogger.Log($"OnCadEntityClicked: Clicked sender was not a recognized Shape in _wpfShapeToDxfEntityMap or mapping failed. Sender: {sender?.GetType().Name}", LogLevel.Warning);
            }
            AppLogger.Log("OnCadEntityClicked: END.", LogLevel.Debug);
        }
        // ... (rest of the file from UpdateTrajectoryPreview onwards)
        // ... (Make sure to copy the ENTIRE rest of the file from here)

        // NOTE: The following is a placeholder for the rest of the file.
        // The actual `overwrite_file_with_block` tool requires the *complete* new file content.
        // I will just show the changed methods here for brevity in this description,
        // but the tool call will use the full file content with these integrated changes.
    }
}
// Placeholder for the rest of the file's content to be included in the actual overwrite operation.
// The ... (rest of existing OnCadEntityClicked logic for adding/selecting trajectory) ...
// and ... (rest of existing marquee selection logic for adding/removing trajectories) ...
// sections in CadCanvas_MouseUp and OnCadEntityClicked would be preserved from the read file content.
// Also, the sections from UpdateTrajectoryPreview() to the end of the class and namespace
// would be preserved.
// The key is that the `overwrite_file_with_block` receives the *entire new content*.

// For the actual tool call, I would reconstruct the full file content:
// 1. Start of file (usings, namespace, class declaration up to MainWindow constructor) from read content.
// 2. Modified MainWindow constructor (if any changes were made there - none in this step).
// 3. Methods from RefreshCadCanvasHighlights up to just before CadCanvas_MouseDown from read content.
// 4. The NEW fully logged CadCanvas_MouseDown.
// 5. The NEW fully logged CadCanvas_MouseMove.
// 6. The NEW fully logged CadCanvas_MouseUp (integrating existing selection logic).
// 7. The NEW fully logged OnCadEntityClicked (integrating existing selection logic).
// 8. The rest of the methods from UpdateTrajectoryPreview to the end of the file from read content.
```

Since the file is very large and the `overwrite_file_with_block` requires the full content, and I need to be careful merging the new logging into the existing complex logic of these four mouse event handlers, I will do this in parts for clarity of what's being changed in each handler, then combine.

**Changes for `CadCanvas_MouseDown` (Conceptual - will be part of one overwrite):**
*   Add `AppLogger.Log($"CadCanvas_MouseDown: START. RawPos=({rawPos.X:F2},{rawPos.Y:F2}), DxfPos=({dxfPos.X:F2},{dxfPos.Y:F2}), Source={e.Source?.GetType().Name}, Button={e.ChangedButton}, ClickCount={e.ClickCount}", LogLevel.Debug);` at the top.
*   Add `AppLogger.Log($"CadCanvas_MouseDown: ButtonStates: Left={e.LeftButton}, Middle={e.MiddleButton}, Right={e.RightButton}. CtrlPressed={isCtrlPressed}.", LogLevel.Debug);`
*   Inside pan initiation: `AppLogger.Log($"CadCanvas_MouseDown: Pan mode INITIATED. _isPanning={_isPanning}. Mouse captured.", LogLevel.Debug);`
*   Inside marquee initiation:
    *   After removing old rectangle: `AppLogger.Log("CadCanvas_MouseDown: Existing selectionRectangleUI removed.", LogLevel.Debug);`
    *   After creating new rectangle: `AppLogger.Log($"CadCanvas_MouseDown: selectionRectangleUI created. isSelectingWithRect={isSelectingWithRect}.", LogLevel.Debug);`
    *   After adding to children: `AppLogger.Log($"CadCanvas_MouseDown: selectionRectangleUI added to CadCanvas.Children at ({selectionStartPoint.X:F2},{selectionStartPoint.Y:F2}).", LogLevel.Debug);`
    *   After capture: `AppLogger.Log($"CadCanvas_MouseDown: Marquee selection mode INITIATED. Mouse captured.", LogLevel.Debug);`
*   In the `else` for not handled: `AppLogger.Log($"CadCanvas_MouseDown: Event not handled for pan or marquee start. Source was {e.Source?.GetType().Name}, expecting CadCanvas for marquee. Button: {e.ChangedButton}", LogLevel.Debug);`
*   At the end: `AppLogger.Log($"CadCanvas_MouseDown: END. _isPanning={_isPanning}, isSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);`

**Changes for `CadCanvas_MouseMove` (Conceptual):**
*   (Already mostly done in previous step, just ensure it's robust)
    *   `AppLogger.Log($"CadCanvas_MouseMove: Fired. RawPos=({currentRawMousePos.X:F2},{currentRawMousePos.Y:F2}), IsPanning={_isPanning}, IsSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);`
    *   Inside panning: `AppLogger.Log($"CadCanvas_MouseMove: Panning. New TranslateTransform=({_translateTransform.X:F2},{_translateTransform.Y:F2})", LogLevel.Debug);`
    *   Inside marquee update: `AppLogger.Log($"CadCanvas_MouseMove (Marquee Update): selectionStartPoint=..., currentRawMousePos=...", LogLevel.Debug);`, `AppLogger.Log($"CadCanvas_MouseMove (Marquee Update): Calculated Rect Coords for UI - X=..., Y=..., W=..., H=...", LogLevel.Debug);`, `AppLogger.Log($"CadCanvas_MouseMove (Marquee Update): selectionRectangleUI properties set.", LogLevel.Debug);`

**Changes for `CadCanvas_MouseUp` (Conceptual):**
*   `AppLogger.Log($"CadCanvas_MouseUp: Fired. RawPos=..., DxfPos=..., Button={e.ChangedButton}", LogLevel.Debug);`
*   `AppLogger.Log($"CadCanvas_MouseUp: Initial states - _isPanning={_isPanning}, isSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);`
*   Inside panning completion: `AppLogger.Log($"CadCanvas_MouseUp: Pan mode COMPLETED. _isPanning={_isPanning}. Mouse capture released.", LogLevel.Debug);`
*   Inside marquee completion:
    *   Capture `wasSelectingWithRect` and log: `AppLogger.Log($"CadCanvas_MouseUp: Marquee mode was active (wasSelectingWithRect={wasSelectingWithRect}), now isSelectingWithRect={isSelectingWithRect}. Mouse capture released.", LogLevel.Debug);`
    *   Log `finalSelectionRect` details.
    *   Log if drag was below threshold.
    *   Log if no active pass.
    *   Log count of marquee hit entities.
    *   Log if selection state changed.
*   In the `else` for not handled: `AppLogger.Log($"CadCanvas_MouseUp: Event not handled for pan or marquee completion.", LogLevel.Debug);`
*   At the end: `AppLogger.Log($"CadCanvas_MouseUp: END. _isPanning={_isPanning}, isSelectingWithRect={isSelectingWithRect}", LogLevel.Debug);`

**Changes for `OnCadEntityClicked` (Conceptual):**
*   `AppLogger.Log($"OnCadEntityClicked: START. Sender Type: {sender?.GetType().Name}", LogLevel.Debug);`
*   Log sender tag, fill, stroke.
*   Log raw/DXF mouse pos, button, click count.
*   If mapped to DxfEntity: `AppLogger.Log($"OnCadEntityClicked: Shape successfully mapped to DxfEntity Type: {dxfEntity?.GetType().Name}", LogLevel.Debug);`
*   Else (mapping failed): `AppLogger.Log($"OnCadEntityClicked: Clicked sender was not a recognized Shape in _wpfShapeToDxfEntityMap or mapping failed. Sender: {sender?.GetType().Name}", LogLevel.Warning);`
*   At the end: `AppLogger.Log("OnCadEntityClicked: END.", LogLevel.Debug);`

I will now construct the full file content with these logging statements carefully integrated. This will be a large `overwrite_file_with_block` call.
