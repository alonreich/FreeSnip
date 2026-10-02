using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Brushes = Avalonia.Media.Brushes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using freesnip.native.foundation;
using freesnip.foundation.core;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.foundation.interfaces.Ocr;
using freesnip.editor.helpers;
using freesnip.editor.Services;
using freesnip.editor.Services.Tools;
using freesnip.foundation.IniFile;
using freesnip.helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using AvaloniaPoint = Avalonia.Point;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaControl = Avalonia.Controls.Control;
using AvaloniaSize = Avalonia.Size;

namespace freesnip.editor.forms
{
    public enum EditorTool
    {
        None, Arrow, Text, FreeDraw, Highlight, Blur, Counter, Line, Emoji, Rectangle, Ellipse, Crop
    }

    /// <summary>Shape the crop box is locked to. Chosen from the menu under the Crop button.</summary>
    public enum CropMode
    {
        Regular,
        KeepRatio,
        Square,
        Wide,
        Tall,
        VerticalStrip,
        HorizontalStrip
    }

    public partial class ImageEditorWindow : Window, IToolContextBridge
    {
        private static readonly Avalonia.Input.Cursor HandCursor = new(StandardCursorType.Hand);
        private static readonly Avalonia.Input.Cursor ArrowCursor = new(StandardCursorType.Arrow);
        private static readonly Avalonia.Input.Cursor CrossCursor = new(StandardCursorType.Cross);
        private static readonly Avalonia.Input.Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
        
        
        private static readonly Avalonia.Input.Cursor PanHeldCursor = new(StandardCursorType.SizeAll);
        private static readonly Avalonia.Input.Cursor TopLeftCursor = new(StandardCursorType.TopLeftCorner);
        private static readonly Avalonia.Input.Cursor TopRightCursor = new(StandardCursorType.TopRightCorner);
        private static readonly Avalonia.Input.Cursor IbeamCursor = new(StandardCursorType.Ibeam);
        private static readonly IBrush SnapGuideBrush = new SolidColorBrush(AvaloniaColor.FromArgb(135, 0, 191, 255));
        private static readonly IBrush SnapDotDimBrush = new SolidColorBrush(AvaloniaColor.FromArgb(170, 205, 210, 215));
        private static readonly IBrush SnapDotBorderBrush = new SolidColorBrush(AvaloniaColor.FromArgb(210, 240, 245, 255));
        private static readonly IBrush OcrSelectedFillBrush = OcrOverlayController.OcrSelectedFillBrush;
        private static readonly IBrush OcrSelectedStrokeBrush = OcrOverlayController.OcrSelectedStrokeBrush;
        private static readonly IBrush OcrUnselectedFillBrush = OcrOverlayController.OcrUnselectedFillBrush;
        private static readonly IBrush OcrUnselectedStrokeBrush = OcrOverlayController.OcrUnselectedStrokeBrush;
        private static long _pixelateCounter = 0;
        private static long _highlightCounter = 0;

        private readonly EditorDocument _document = new();
        private ImageSharpImage _image => _document.Image;
        private object _imageLock => _document.SyncRoot;
        private Bitmap _displayBitmap;
        private static int _cascadeOffset = 0;
        private static readonly log4net.ILog Log = LogHelper.GetLogger(typeof(ImageEditorWindow));
        
        private Dictionary<EditorTool, IBrush> _toolBrushes = new Dictionary<EditorTool, IBrush>();
        private Dictionary<EditorTool, double> _toolFontSizes = new Dictionary<EditorTool, double>();
        private Dictionary<EditorTool, double> _toolThicknesses = new Dictionary<EditorTool, double>();
        private Dictionary<EditorTool, bool> _toolFillModes = new Dictionary<EditorTool, bool>();
        private double _currentThickness = 3.0;
        private bool _isFillMode = false;
        private AvaloniaColor _lastSelectedColor = AvaloniaColor.Parse("#007ACC");
        
        private static readonly IBrush HighlightBrushBase = new SolidColorBrush(AvaloniaColor.Parse("#60FFFF00"));
        private EditorTool _currentTool = EditorTool.None;
        private readonly Dictionary<EditorTool, IToolHandler> _toolHandlers = new();
        private IToolHandler _activeToolHandler;
        private readonly ToolHandlerContext _toolContext;
        private AvaloniaPoint _startPoint;
        private bool _isDrawing;
        private Canvas _canvas;
        private Avalonia.Controls.Image _imageControl;
        private TextBlock _dimensionText;
        private TextBlock _titleDimensionText;
        private TextBlock _modeText;
        private Button _undoBtn;
        private Button _redoBtn;
        private Button _deleteBtn;
        private int _counterValue = 1;
        private string _pendingEmoji = "😊";
        private readonly EditorHistoryManager _historyManager;
        private const int MaxStackSize = EditorHistoryManager.MaxStackSize;
        
        
        private const double OverscrollPadding = EditorZoomEngine.OverscrollPadding;
        private const double ZoomStepMultiplier = EditorZoomEngine.ZoomStepMultiplier;
        private const double MinZoomFactor = EditorZoomEngine.MinZoomFactor;
        private const double MaxZoomFactor = EditorZoomEngine.MaxZoomFactor;

        private Avalonia.Controls.Shapes.Polyline _activePolyline;
        private readonly EditorZoomEngine _zoomEngine;
        private double _zoomFactor
        {
            get => _zoomEngine.ZoomFactor;
            set => _zoomEngine.ZoomFactor = value;
        }
        private double _targetZoom
        {
            get => _zoomEngine.TargetZoom;
            set => _zoomEngine.TargetZoom = value;
        }
        private Avalonia.Point? _zoomFocalViewportPoint => _zoomEngine.FocalViewportPoint;

        private Avalonia.Controls.Shapes.Rectangle _selectionIndicator;
        private Avalonia.Controls.Shapes.Ellipse _circleSelectionIndicator;
        private Avalonia.Controls.Shapes.Polygon _lineSelectionIndicator;
        private Avalonia.Controls.Shapes.Polygon _arrowSelectionIndicator;
        private List<Avalonia.Controls.Shapes.Rectangle> _resizeHandles = new List<Avalonia.Controls.Shapes.Rectangle>();
        private AvaloniaControl _selectedControl;
        private AvaloniaPoint _dragLastPoint;
        private double _dragUnsnappedLeft;
        private double _dragUnsnappedTop;
        
        
        private double _resizeUnsnappedLeft;
        private double _resizeUnsnappedTop;
        private double _resizeUnsnappedWidth;
        private double _resizeUnsnappedHeight;
        private bool _isDraggingSelected;
        private bool _isResizing;
        private int _resizeHandleIndex = -1;
        private bool _dragUndoCaptured;
        private bool _resizeUndoCaptured;
        private int _isBusy;
        private AvaloniaControl _previewControl;

        private Border _contextToolbar;
        private Button _contextDuplicateButton;
        private Button _contextDeleteButton;
        private Button _contextApplyButton;
        private Avalonia.Controls.Shapes.Path _contextDuplicateIcon;
        private Avalonia.Controls.Shapes.Path _contextDeleteIcon;
        private Avalonia.Controls.Shapes.Path _contextApplyIcon;
        private Avalonia.Controls.Shapes.Rectangle _contextApplySeparator;
        private Avalonia.Controls.Shapes.Rectangle _contextDuplicateSeparator;

        private ScrollViewer _editorScrollViewer;
        private Control _editorContentRoot;
        private Grid _mainGrid;
        private Panel _scrollContentPanel;
        private Panel _zoomContainer;
        private Border _snipBorder;
        private Canvas _overlayCanvas;
        private Button _zoomText;

        private Button _cropTool;
        private StackPanel _cropModeButtons;

        private StackPanel _leftToolbar;
        private StackPanel _topToolbarStackPanel;
        private Border _bottomToolbarBorder;
        private ScrollViewer _bottomToolbarScrollViewer;
        private Button _clearAllBtn;
        private Button _snapToggleBtn;
        private Button _emojiTool;
        private ItemsControl _emojiGrid;

        private ColorView _colorPickerView;
        private Button _customColorBtn;
        private TextBox _hexInput;
        private Border _hexPreviewChip;
        private Border _currentColorPreview;
        private TextBlock _currentColorHex;
        private StackPanel _colorPresetsPanel;

        private StackPanel _thicknessPanel;
        private StackPanel _thicknessChoicesPanel;
        private TextBlock _thicknessLabel;
        private WrapPanel _thicknessFlyoutPanel;
        private Slider _thicknessFlyoutSlider;
        private TextBlock _thicknessFlyoutValueText;
        private Avalonia.Controls.Primitives.Popup _lineSizePopup;
        private bool _syncingThicknessSlider;
        private bool _sliderUndoPending;
        private Button _fillToggleBtn;
        private TextBlock _fillToggleText;
        private Button _resetCounterBtn;
        private StackPanel _counterAdjustPanel;

        private Button _ocrButton;
        private TextBlock _ocrButtonText;
        private Border _ocrContextToolbar;
        private Button _ocrContextCopyButton;
        private Button _ocrContextOpenLinkButton;
        private Button _ocrContextEmailButton;

        private Grid _closePromptOverlay;
        private TextBlock _closePromptMessage;
        private Button _closePromptSaveButton;
        private Button _closePromptDiscardButton;
        private Button _closePromptCancelButton;

        private readonly System.Collections.Generic.List<DispatcherTimer> _activeHaloTimers = new();

        private Canvas _startHandle;
        private Canvas _endHandle;
        private bool _isDraggingStartPoint;
        private bool _isDraggingEndPoint;

        private bool _isSpaceDown;
        private bool _isPanning;
        private AvaloniaPoint _panStartPoint;
        private Avalonia.Vector _panStartOffset;

        private bool _isRubberBanding;
        private AvaloniaPoint _rubberBandStart;
        private Avalonia.Controls.Shapes.Rectangle _rubberBandVisual;

        private Slider _inlinePixelateSlider;
        private TextBlock _pixelatePercentText;
        private StackPanel _pixelateToolbarSection;
        private Canvas _pixelateStrengthHandle;
        private bool _isDraggingStrengthHandle;

        private int _lastPixelateStrength = PixelateStrengthDefault;
        private int _currentZIndex = 5000;
        private Avalonia.Controls.Shapes.Rectangle _hoverIndicator;
        private Avalonia.Controls.Shapes.Ellipse _circleHoverIndicator;
        private Avalonia.Controls.Shapes.Polygon _lineHoverIndicator;
        private Avalonia.Controls.Shapes.Polygon _arrowHoverIndicator;
        private Canvas _snapDotsLayer;
        private Canvas _snapHaloLayer;
        private AvaloniaPoint? _lastSnappedTargetPoint;
        private readonly List<AvaloniaPoint> _snapDotTargets = new List<AvaloniaPoint>();
        private readonly List<AvaloniaPoint> _recipientSnapDotTargets = new List<AvaloniaPoint>();
        private readonly List<AvaloniaPoint> _heldSnapDotTargets = new List<AvaloniaPoint>();
        private AvaloniaControl _closestSnapRecipient;
        private Rect _closestSnapRecipientBounds;
        private Border _vectorInfoPopup;
        private TextBlock _vectorInfoText;
        private AvaloniaControl _hoveredControl;
        private const int PixelateStrengthMin = 2;
        private const int PixelateStrengthMax = 29;
        private const int PixelateStrengthDefault = 10;
        private const double VectorHitTolerance = 12.0;
        private const int FreeSnipFrameThickness = 3;

        
        private static readonly Avalonia.Input.Cursor NoneCursor = new(StandardCursorType.None);
        private Canvas _toolGhostLayer;
        private AvaloniaControl _toolGhost;
        private bool _ghostSettingsDirty = true;
        private readonly ScaleTransform _ghostIconScale = new ScaleTransform(1, 1);

        
        private Canvas _snapGuideLayer;
        private readonly List<Avalonia.Controls.Shapes.Line> _snapGuideLines = new List<Avalonia.Controls.Shapes.Line>();
        private readonly List<SnapGuideInfo> _activeSnapGuides = new List<SnapGuideInfo>();


        private int _rectDrawDirX;
        private int _rectDrawDirY;

        private bool _zoomViewportHooked;

        private readonly struct SnapGuideInfo
        {
            public readonly bool Horizontal; 
            public readonly double Coordinate;
            public readonly double Start;
            public readonly double End;

            public SnapGuideInfo(bool horizontal, double coordinate, double start, double end)
            {
                Horizontal = horizontal;
                Coordinate = coordinate;
                Start = start;
                End = end;
            }
        }

        public ImageEditorWindow()
        {
            _zoomEngine = new EditorZoomEngine(ApplyZoomAndFocalOffset);
            _historyManager = new EditorHistoryManager(DisposeAnnotationResources);
            _toolContext = new ToolHandlerContext(this);
            _toolHandlers[EditorTool.None] = new NoneToolHandler();
            _toolHandlers[EditorTool.Rectangle] = new RectangleToolHandler();
            _toolHandlers[EditorTool.Ellipse] = new EllipseToolHandler();
            _toolHandlers[EditorTool.Line] = new LineToolHandler();
            _toolHandlers[EditorTool.Arrow] = new ArrowToolHandler();
            _toolHandlers[EditorTool.FreeDraw] = new FreeDrawToolHandler();
            _toolHandlers[EditorTool.Highlight] = new HighlightToolHandler();
            _toolHandlers[EditorTool.Blur] = new BlurToolHandler();
            _toolHandlers[EditorTool.Text] = new TextToolHandler();
            _toolHandlers[EditorTool.Counter] = new CounterToolHandler();
            _toolHandlers[EditorTool.Emoji] = new EmojiToolHandler();
            _toolHandlers[EditorTool.Crop] = new CropToolHandler();
            _activeToolHandler = _toolHandlers[EditorTool.None];
            InitializeComponent();
            CacheControls();
            freesnip.foundation.core.UiLayoutDirection.Apply(this);
            if (freesnip.foundation.core.UiLayoutDirection.IsRightToLeft)
            {
                if (_emojiTool?.Flyout is Flyout emojiFlyout)
                {
                    emojiFlyout.Placement = PlacementMode.Left;
                }
            }

            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (config.IsFirstLaunch)
            {
                config.IsFirstLaunch = false;
                IniConfig.Save();
            }

            _lastPixelateStrength = NormalizePixelateStrength(config.LastPixelateStrength);
            config.LastPixelateStrength = _lastPixelateStrength;
            
            if (_inlinePixelateSlider != null) 
            {
                _inlinePixelateSlider.Minimum = PixelateStrengthMin;
                _inlinePixelateSlider.Maximum = PixelateStrengthMax;
                _inlinePixelateSlider.Value = _lastPixelateStrength;
                _inlinePixelateSlider.PropertyChanged += OnPixelateSliderChanged;
            }

            InitializeToolDefaults();
            UpdateModeStatus();
            UpdateContextToolbarHotkeyTooltips();
            RefreshColorPresetsPanel();
            InitializeCustomColorFlyout();
            InitializeCropModes();
            UpdateSnapToggleVisual();
            PopulateEmojiGrid();

            this.Cursor = ArrowCursor;
            if (_canvas != null)
            {
                _canvas.Cursor = HandCursor;
                _canvas.PointerEntered += (s, e) =>
                {
                    if (!_isDrawing && !_isDraggingSelected && !_isResizing && !_isPanning)
                    {
                        SetEditorCursor(_currentTool == EditorTool.None ? (GetUserAnnotations().Count == 0 ? HandCursor : CrossCursor) : CrossCursor);
                    }
                };
                _canvas.PointerExited += (s, e) =>
                {
                    if (!_isDrawing && !_isDraggingSelected && !_isResizing && !_isDraggingStartPoint && !_isDraggingEndPoint && !_isPanning && !_isRubberBanding)
                    {
                        this.Cursor = ArrowCursor;
                        UpdateHoverIndicator(null);
                        HideToolGhost();
                        HideSnapGuides();
                    }
                };
            }

            Closed += OnWindowClosed;
            Opened += OnWindowOpened;
            Closing += OnWindowClosing;
        }

        private void CacheControls()
        {
            _canvas = this.FindControl<Canvas>("AnnotationCanvas");
            _imageControl = this.FindControl<Avalonia.Controls.Image>("EditorImage");
            _dimensionText = this.FindControl<TextBlock>("DimensionText");
            _titleDimensionText = this.FindControl<TextBlock>("TitleDimensionText");
            _modeText = this.FindControl<TextBlock>("ModeText");
            _undoBtn = this.FindControl<Button>("UndoButton");
            _redoBtn = this.FindControl<Button>("RedoButton");
            _deleteBtn = this.FindControl<Button>("DeleteButton");
            _contextToolbar = this.FindControl<Border>("ContextToolbar");
            _startHandle = this.FindControl<Canvas>("StartPointHandle");
            _endHandle = this.FindControl<Canvas>("EndPointHandle");

            _inlinePixelateSlider = this.FindControl<Slider>("InlinePixelateSlider");
            _pixelatePercentText = this.FindControl<TextBlock>("PixelatePercentText");
            _pixelateToolbarSection = this.FindControl<StackPanel>("PixelateToolbarSection");
            _pixelateStrengthHandle = this.FindControl<Canvas>("PixelateStrengthHandle");

            _contextDuplicateButton = this.FindControl<Button>("ContextDuplicateButton");
            _contextDeleteButton = this.FindControl<Button>("ContextDeleteButton");
            _contextApplyButton = this.FindControl<Button>("ContextApplyButton");
            _contextDuplicateIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("ContextDuplicateIcon");
            _contextDeleteIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("ContextDeleteIcon");
            _contextApplyIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("ContextApplyIcon");
            _contextApplySeparator = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("ContextApplySeparator");
            _contextDuplicateSeparator = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("ContextDuplicateSeparator");

            _editorScrollViewer = this.FindControl<ScrollViewer>("EditorScrollViewer");
            _editorContentRoot = this.FindControl<Control>("EditorContentRoot");
            _mainGrid = this.FindControl<Grid>("MainGrid");
            _scrollContentPanel = this.FindControl<Panel>("ScrollContentPanel");
            _zoomContainer = this.FindControl<Panel>("ZoomContainer");
            _snipBorder = this.FindControl<Border>("SnipBorder");
            _overlayCanvas = this.FindControl<Canvas>("OverlayCanvas");
            _zoomText = this.FindControl<Button>("ZoomText");

            _cropTool = this.FindControl<Button>("CropTool");
            _cropModePopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("CropModePopup");
            _cropModeDescription = this.FindControl<TextBlock>("CropModeDescription");
            _cropModeButtons = this.FindControl<StackPanel>("CropModeButtons");

            _leftToolbar = this.FindControl<StackPanel>("LeftToolBar");
            _topToolbarStackPanel = this.FindControl<StackPanel>("TopToolbarStackPanel");
            _bottomToolbarBorder = this.FindControl<Border>("BottomToolbarBorder");
            _bottomToolbarScrollViewer = this.FindControl<ScrollViewer>("BottomToolbarScrollViewer");
            _clearAllBtn = this.FindControl<Button>("ClearAllButton");
            _snapToggleBtn = this.FindControl<Button>("SnapToggleBtn");
            _emojiTool = this.FindControl<Button>("EmojiTool");
            _emojiGrid = this.FindControl<ItemsControl>("EmojiGrid");

            _colorPickerView = this.FindControl<ColorView>("ColorPickerView");
            _customColorBtn = this.FindControl<Button>("CustomColorBtn");
            _hexInput = this.FindControl<TextBox>("HexInput");
            _hexPreviewChip = this.FindControl<Border>("HexPreviewChip");
            _currentColorPreview = this.FindControl<Border>("CurrentColorPreview");
            _currentColorHex = this.FindControl<TextBlock>("CurrentColorHex");
            _colorPresetsPanel = this.FindControl<StackPanel>("ColorPresetsPanel");

            _thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            _thicknessChoicesPanel = this.FindControl<StackPanel>("ThicknessChoicesPanel");
            _thicknessLabel = this.FindControl<TextBlock>("ThicknessLabel");
            _thicknessFlyoutPanel = this.FindControl<WrapPanel>("ThicknessFlyoutPanel");
            _thicknessFlyoutSlider = this.FindControl<Slider>("ThicknessFlyoutSlider");
            if (_thicknessFlyoutSlider != null)
            {
                _thicknessFlyoutSlider.AddHandler(InputElement.PointerReleasedEvent, OnThicknessSliderPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
                _thicknessFlyoutSlider.PointerCaptureLost += OnThicknessSliderPointerCaptureLost;
            }
            _lineSizePopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("LineSizePopup");
            var lineSizeBtn = this.FindControl<Button>("LineSizeBtn");
            if (_lineSizePopup != null)
            {
                if (lineSizeBtn != null) _lineSizePopup.PlacementTarget = lineSizeBtn;
                _lineSizePopup.Closed += (s, e) => CommitSliderThickness();
                _lineSizePopup.AddHandler(InputElement.KeyDownEvent, OnPopupKeyDown, RoutingStrategies.Tunnel);
            }
            _thicknessFlyoutValueText = this.FindControl<TextBlock>("ThicknessFlyoutValueText");
            _fillToggleBtn = this.FindControl<Button>("FillToggleBtn");
            _fillToggleText = this.FindControl<TextBlock>("FillToggleText");
            _resetCounterBtn = this.FindControl<Button>("ResetCounterBtn");
            _counterAdjustPanel = this.FindControl<StackPanel>("CounterAdjustPanel");

            _ocrButton = this.FindControl<Button>("OcrButton");
            _ocrButtonText = this.FindControl<TextBlock>("OcrButtonText");
            _ocrContextToolbar = this.FindControl<Border>("OcrContextToolbar");
            _ocrContextCopyButton = this.FindControl<Button>("OcrContextCopyButton");
            _ocrContextOpenLinkButton = this.FindControl<Button>("OcrContextOpenLinkButton");
            _ocrContextEmailButton = this.FindControl<Button>("OcrContextEmailButton");

            _closePromptOverlay = this.FindControl<Grid>("ClosePromptOverlay");
            _closePromptMessage = this.FindControl<TextBlock>("ClosePromptMessage");
            _closePromptSaveButton = this.FindControl<Button>("ClosePromptSaveButton");
            _closePromptDiscardButton = this.FindControl<Button>("ClosePromptDiscardButton");
            _closePromptCancelButton = this.FindControl<Button>("ClosePromptCancelButton");
        }

        private void SetEditorCursor(Avalonia.Input.Cursor cursor)
        {
            if (_canvas != null) _canvas.Cursor = cursor;
            this.Cursor = ArrowCursor;
        }

        private void OnTitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    BeginMoveDrag(e);
                }
            }
        }

        private static readonly string[] DefaultEmojiPalette =
        {
            "😀","😂","😊","😍","👍","🔥","🎉","😎","🤣","❤️","🤔","😉","🙌","✨","💯","✅","❌","⚠️","❗","➡️","⬅️","⬆️","⬇️","⭐","📍","💡","🔒","👀","🎯","👎","😁","😘","😜","😏","😒","😔","😢","😭","😡","🤯","😳","😱","🤗","🙄","😐","😬","😴","🤐","🤮","😷","🤒","💀","🤖","💩"
        };

        private readonly System.Collections.ObjectModel.ObservableCollection<string> _activeEmojiList = new();
        private readonly List<Avalonia.Controls.Shapes.Rectangle> _multiSelectionIndicators = new();

        private void PopulateEmojiGrid()
        {
            if (_activeEmojiList.Count == 0)
            {
                foreach (var emoji in DefaultEmojiPalette) _activeEmojiList.Add(emoji);
            }
            if (_emojiGrid != null) _emojiGrid.ItemsSource = _activeEmojiList;
            this.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            KeyUp += OnWindowKeyUp;
            Activated += OnWindowActivated;
            Deactivated += OnWindowDeactivated;
            Title = "FreeSnip Editor";

            _selectionIndicator = new Avalonia.Controls.Shapes.Rectangle { 
                Classes = { "selection-indicator" },
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9999
            };
            _canvas.Children.Add(_selectionIndicator);

            _circleSelectionIndicator = new Avalonia.Controls.Shapes.Ellipse { 
                Classes = { "selection-indicator" },
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9999
            };
            _canvas.Children.Add(_circleSelectionIndicator);

            _lineSelectionIndicator = new Avalonia.Controls.Shapes.Polygon { 
                Classes = { "selection-indicator" },
                Fill = null,
                StrokeJoin = PenLineJoin.Round,
                Points = new Avalonia.Points(),
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9999
            };
            _canvas.Children.Add(_lineSelectionIndicator);

            _arrowSelectionIndicator = new Avalonia.Controls.Shapes.Polygon { 
                Classes = { "selection-indicator" },
                Fill = null,
                StrokeJoin = PenLineJoin.Round,
                Points = new Avalonia.Points(),
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9999
            };
            _canvas.Children.Add(_arrowSelectionIndicator);

            _hoverIndicator = new Avalonia.Controls.Shapes.Rectangle
            {
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9997
            };
            _canvas.Children.Add(_hoverIndicator);

            _circleHoverIndicator = new Avalonia.Controls.Shapes.Ellipse
            {
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9997
            };
            _canvas.Children.Add(_circleHoverIndicator);

            _lineHoverIndicator = new Avalonia.Controls.Shapes.Polygon
            {
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                Fill = null,
                StrokeJoin = PenLineJoin.Round,
                Points = new Avalonia.Points(),
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9997
            };
            _canvas.Children.Add(_lineHoverIndicator);

            _arrowHoverIndicator = new Avalonia.Controls.Shapes.Polygon
            {
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                Fill = null,
                StrokeJoin = PenLineJoin.Round,
                Points = new Avalonia.Points(),
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 9997
            };
            _canvas.Children.Add(_arrowHoverIndicator);

            _vectorInfoText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextAlignment = Avalonia.Media.TextAlignment.Center
            };
            _vectorInfoPopup = new Border
            {
                Background = new SolidColorBrush(AvaloniaColor.FromArgb(240, 26, 28, 32)),
                BorderBrush = new SolidColorBrush(AvaloniaColor.Parse("#00B4D8")),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4),
                BoxShadow = new BoxShadows(BoxShadow.Parse("0 4 12 0 #A0000000")),
                Child = _vectorInfoText,
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 10003
            };
            _canvas.Children.Add(_vectorInfoPopup);

            
            _toolGhostLayer = new Canvas { IsHitTestVisible = false, IsVisible = false, ZIndex = 10004 };
            _canvas.Children.Add(_toolGhostLayer);
            _snapGuideLayer = new Canvas { IsHitTestVisible = false, IsVisible = false, ZIndex = 9996 };
            _canvas.Children.Add(_snapGuideLayer);
            _canvas.PointerExited += OnCanvasPointerExited;

            for (int i = 0; i < 4; i++)
            {
                var handle = new Avalonia.Controls.Shapes.Rectangle {
                    Width = 14, Height = 14, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1, 
                    IsVisible = false, ZIndex = 10000, Tag = i
                };
                
                if (i == 0 || i == 2) handle.Cursor = TopLeftCursor;
                else handle.Cursor = TopRightCursor; 
                
                handle.PointerPressed += (s, e) => { 
                    _isResizing = true; 
                    _resizeUndoCaptured = false; 
                    int idx = (int)((AvaloniaControl)s).Tag;
                    _resizeHandleIndex = idx;
                    if (_selectedControl != null)
                    {
                        if (TryGetControlBounds(_selectedControl, out var initBounds))
                        {
                            _resizeUnsnappedLeft = initBounds.X;
                            _resizeUnsnappedTop = initBounds.Y;
                            _resizeUnsnappedWidth = initBounds.Width;
                            _resizeUnsnappedHeight = initBounds.Height;
                        }
                        else
                        {
                            _resizeUnsnappedLeft = Canvas.GetLeft(_selectedControl);
                            if (double.IsNaN(_resizeUnsnappedLeft)) _resizeUnsnappedLeft = _selectedControl.Bounds.X;
                            _resizeUnsnappedTop = Canvas.GetTop(_selectedControl);
                            if (double.IsNaN(_resizeUnsnappedTop)) _resizeUnsnappedTop = _selectedControl.Bounds.Y;
                            _resizeUnsnappedWidth = double.IsNaN(_selectedControl.Width) ? _selectedControl.Bounds.Width : _selectedControl.Width;
                            _resizeUnsnappedHeight = double.IsNaN(_selectedControl.Height) ? _selectedControl.Bounds.Height : _selectedControl.Height;
                        }
                    }
                    _dragLastPoint = e.GetPosition(_canvas);
                    bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                    UpdateMagnetButtonState(altPressed);
                    RefreshActiveDragAltHint(altPressed);
                    e.Pointer.Capture(_canvas);
                    e.Handled = true; 
                };
                _resizeHandles.Add(handle); _canvas.Children.Add(handle);
            }

            _startHandle.PointerPressed += (s, e) => { 
                _isDraggingStartPoint = true; 
                _dragUndoCaptured = false; 
                _dragLastPoint = e.GetPosition(_canvas); 
                bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                UpdateMagnetButtonState(altPressed);
                RefreshActiveDragAltHint(altPressed);
                e.Pointer.Capture(_canvas);
                e.Handled = true; 
            };
            _endHandle.PointerPressed += (s, e) => { 
                _isDraggingEndPoint = true; 
                _dragUndoCaptured = false; 
                _dragLastPoint = e.GetPosition(_canvas); 
                bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                UpdateMagnetButtonState(altPressed);
                RefreshActiveDragAltHint(altPressed);
                e.Pointer.Capture(_canvas);
                e.Handled = true; 
            };

            if (_pixelateStrengthHandle != null)
            {
                _pixelateStrengthHandle.PointerPressed += (s, e) => {
                    _isDraggingStrengthHandle = true;
                    _dragUndoCaptured = false;
                    _dragLastPoint = e.GetPosition(_canvas);
                    e.Pointer.Capture(_canvas);
                    e.Handled = true;
                };
            }
        }

        private void OnSnapToggleClick(object sender, RoutedEventArgs e)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            config.MagneticSnappingEnabled = !config.MagneticSnappingEnabled;
            IniConfig.Save();
            UpdateSnapToggleVisual();
            OverlayHelper.ShowLightToast(config.MagneticSnappingEnabled ? "MAGNETIC SNAPPING ON" : "MAGNETIC SNAPPING OFF", this);
        }

        private void UpdateSnapToggleVisual()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (config != null && _snapToggleBtn != null) 
            {
                if (config.MagneticSnappingEnabled) _snapToggleBtn.Classes.Add("selected"); 
                else _snapToggleBtn.Classes.Remove("selected");
            }
        }

        private void InitializeToolDefaults()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            var defaultBrush = new SolidColorBrush(AvaloniaColor.Parse("#007ACC"));
            var redBrush = Brushes.Red;

            foreach (EditorTool tool in Enum.GetValues<EditorTool>())
            {
                _toolBrushes[tool] = defaultBrush;
                _toolFontSizes[tool] = 18.0;
                _toolThicknesses[tool] = 3.0;
                _toolFillModes[tool] = false;
            }
            
            if (AvaloniaColor.TryParse(config.LastCounterColor ?? "#FF0000", out var counterColor))
                _toolBrushes[EditorTool.Counter] = new SolidColorBrush(counterColor);
            else
                _toolBrushes[EditorTool.Counter] = redBrush;
                
            if (config.LastCounterSize == 35.0) { config.LastCounterSize = 42.0; IniConfig.Save(); }
            _toolFontSizes[EditorTool.Counter] = config.LastCounterSize > 0 ? config.LastCounterSize : 42.0;

            _toolBrushes[EditorTool.Text] = new SolidColorBrush(AvaloniaColor.Parse("#0055AA"));
            _toolThicknesses[EditorTool.Text] = config.TextToolThickness;

            _toolBrushes[EditorTool.Highlight] = HighlightBrushBase;
            _toolThicknesses[EditorTool.Highlight] = 1.0;
            _toolThicknesses[EditorTool.Arrow] = 8.0;
            _toolThicknesses[EditorTool.Line] = 8.0;
            
            _toolFontSizes[EditorTool.Text] = Math.Max(20, config.LastTextSize);
            _toolFontSizes[EditorTool.Emoji] = Math.Max(60, config.LastEmojiSize);
            _toolFontSizes[EditorTool.Counter] = config.LastCounterSize > 0 ? config.LastCounterSize : 42.0;

            if (_toolBrushes.TryGetValue(EditorTool.Rectangle, out var b) && b is SolidColorBrush initialBrush)
            {
                UpdateCurrentColorDisplay(initialBrush.Color);
            }

            LoadToolPreferences(config);

            UpdateFillToggleVisual();
        }

        private void LoadToolPreferences(CoreConfiguration config)
        {
            if (!string.IsNullOrEmpty(config.LastToolColors))
            {
                var parts = config.LastToolColors.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && Enum.TryParse(kv[0], out EditorTool tool))
                    {
                        if (AvaloniaColor.TryParse(kv[1], out var color))
                        {
                            _toolBrushes[tool] = new SolidColorBrush(color);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(config.LastToolThicknesses))
            {
                var parts = config.LastToolThicknesses.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && Enum.TryParse(kv[0], out EditorTool tool))
                    {
                        if (double.TryParse(kv[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double thickness))
                        {
                            _toolThicknesses[tool] = thickness;
                        }
                    }
                }
            }
        }

        private void SaveToolPreferences()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            var colorsList = new System.Collections.Generic.List<string>();
            foreach (var kvp in _toolBrushes)
            {
                if (kvp.Value is SolidColorBrush scb)
                {
                    colorsList.Add($"{kvp.Key}:{scb.Color.ToString()}");
                }
            }
            config.LastToolColors = string.Join(",", colorsList);

            var thicknessList = new System.Collections.Generic.List<string>();
            foreach (var kvp in _toolThicknesses)
            {
                thicknessList.Add($"{kvp.Key}:{kvp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            config.LastToolThicknesses = string.Join(",", thicknessList);

            IniConfig.Save();
        }


        private void OnPixelateSliderChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Value" && _inlinePixelateSlider != null)
            {
                int val = NormalizePixelateStrength((int)Math.Round(_inlinePixelateSlider.Value));
                if (Math.Abs(_inlinePixelateSlider.Value - val) > 0.01)
                {
                    _inlinePixelateSlider.Value = val;
                    return;
                }

                _lastPixelateStrength = val;
                var config = IniConfig.GetIniSection<CoreConfiguration>();
                config.LastPixelateStrength = val;

                if (_pixelatePercentText != null)
                {
                    double pct = (double)(val - PixelateStrengthMin) / (PixelateStrengthMax - PixelateStrengthMin);
                    _pixelatePercentText.Text = $"{(int)Math.Round(pct * 100)}%";
                }
                
                if (IsPixelateControl(_selectedControl))
                {
                    if (_selectedControl.Tag is PixelateProperties props) props.Strength = val;
                    RefreshPixelateAnnotation(_selectedControl);
                    UpdateSelectionIndicator();
                }
            }
        }

        private void SyncPixelateSlider(int strength)
        {
            int val = NormalizePixelateStrength(strength);
            if (_inlinePixelateSlider != null && Math.Abs(_inlinePixelateSlider.Value - val) > 0.01)
            {
                _inlinePixelateSlider.Value = val;
            }

            if (_pixelatePercentText != null)
            {
                double pct = (double)(val - PixelateStrengthMin) / (PixelateStrengthMax - PixelateStrengthMin);
                _pixelatePercentText.Text = $"{(int)Math.Round(pct * 100)}%";
            }
        }

        private static int NormalizePixelateStrength(int value)
        {
            return Math.Clamp(value, PixelateStrengthMin, PixelateStrengthMax);
        }

        private bool MatchesHotkey(KeyEventArgs e, string configHotkey)
        {
            if (string.IsNullOrWhiteSpace(configHotkey) || configHotkey == "None") return false;
            var modifiers = new System.Collections.Generic.List<string>();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers.Add("Ctrl");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers.Add("Alt");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers.Add("Shift");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers.Add("Win");
            string eString = modifiers.Count > 0 ? string.Join(" + ", modifiers) + " + " + e.Key.ToString() : e.Key.ToString();
            return string.Equals(eString.Trim(), configHotkey.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatHotkeyForTooltip(string hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey) || hotkey == "None") return "not set";
            return string.Equals(hotkey, "Delete", StringComparison.OrdinalIgnoreCase) ? "Del" : hotkey;
        }

        private void UpdateContextToolbarHotkeyTooltips()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (_contextDuplicateButton != null)
            {
                ToolTip.SetTip(_contextDuplicateButton, $"Duplicate ({FormatHotkeyForTooltip(config.DuplicateObjectHotkey)})");
            }

            if (_contextDeleteButton != null)
            {
                ToolTip.SetTip(_contextDeleteButton, $"Delete ({FormatHotkeyForTooltip(config.DeleteObjectHotkey)})");
            }
        }

        private void OnWindowKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                _isSpaceDown = false;
                if (!_isPanning)
                {
                    if (_currentTool == EditorTool.None)
                        SetEditorCursor(HandCursor);
                    else
                        SetEditorCursor(CrossCursor);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt || !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                UpdateMagnetButtonState(false);
                RefreshActiveDragAltHint(false);
            }
        }

        public static Action RequestRegionCaptureAction { get; set; }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();

            if (e.Key == Key.PrintScreen || e.Key == Key.Snapshot)
            {
                RequestRegionCaptureAction?.Invoke();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                UpdateMagnetButtonState(true);
                RefreshActiveDragAltHint(true);
            }

            var focusManager = this.FocusManager;
            var focusedElement = focusManager?.GetFocusedElement();
            if (focusedElement is TextBox tb && !tb.IsReadOnly)
            {
                if (e.Key == Key.Escape)
                {
                    ResetToolsAndSelection();
                    _canvas?.Focus();
                    e.Handled = true;
                }
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (e.Key == Key.Z) { OnUndoClick(null, null); e.Handled = true; return; }
                if (e.Key == Key.Y) { OnRedoClick(null, null); e.Handled = true; return; }
                if (e.Key == Key.C) { HandleContextualCopy(allowCloseOnAction: false); e.Handled = true; return; }
                if (e.Key == Key.S) { OnDownloadClick(null, null); e.Handled = true; return; }
                if (e.Key == Key.V) { HandleContextualPaste(); e.Handled = true; return; }
            }

            if (e.Key == Key.Space && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                _isSpaceDown = true;
                if (!_isDrawing && !_isDraggingSelected && !_isResizing && !_isDraggingStartPoint && !_isDraggingEndPoint && !_isPanning)
                {
                    SetEditorCursor(HandCursor);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                var cropControl = (_selectedControl?.Tag?.ToString() == "CropObject") 
                    ? _selectedControl 
                    : _canvas.Children.FirstOrDefault(c => c.Tag?.ToString() == "CropObject");
                if (cropControl != null)
                {
                    _selectedControl = cropControl;
                    OnContextApplyClick(null, null);
                    e.Handled = true;
                    return;
                }
                if (_selectedControl != null && _selectedControl.Tag?.ToString() == "PasteObject")
                {
                    OnContextApplyClick(null, null);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Escape) { ResetToolsAndSelection(); e.Handled = true; return; }
            if (e.Key == Key.Tab && _selectedControl != null && _contextToolbar != null && _contextToolbar.IsVisible)
            {
                var focusTarget = (_contextApplyButton != null && _contextApplyButton.IsVisible) ? _contextApplyButton : _contextDuplicateButton;
                if (focusTarget != null && focusTarget.IsVisible)
                {
                    focusTarget.Focus();
                    e.Handled = true;
                    return;
                }
            }
            if (_selectedControl != null && MatchesHotkey(e, config.DeleteObjectHotkey)) { OnDeleteClick(null, null); e.Handled = true; return; }
            if (_selectedControl != null && MatchesHotkey(e, config.DuplicateObjectHotkey)) { OnDuplicateClick(null, null); e.Handled = true; return; }

            if (_selectedControl != null && (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right))
            {
                double moveBy = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 50.0 : 1.0;
                double dx = 0, dy = 0;
                if (e.Key == Key.Up) dy = -moveBy;
                if (e.Key == Key.Down) dy = moveBy;
                if (e.Key == Key.Left) dx = -moveBy;
                if (e.Key == Key.Right) dx = moveBy;
                NudgeSelectedControl(dx, dy);
                e.Handled = true;
                return;
            }

            if (MatchesHotkey(e, config.CounterHotkey)) { TriggerToolButton("CounterTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.ArrowHotkey)) { TriggerToolButton("ArrowTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.LineHotkey)) { TriggerToolButton("LineTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.FreehandHotkey)) { TriggerToolButton("FreeDrawTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.HighlightHotkey)) { TriggerToolButton("HighlightTool"); e.Handled = true; return; }

            if (MatchesHotkey(e, config.TextHotkey)) { TriggerToolButton("TextTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.RotateCcwHotkey)) { OnRotateCcwClick(null, null); e.Handled = true; return; }
            if (MatchesHotkey(e, config.RotateCwHotkey)) { OnRotateCwClick(null, null); e.Handled = true; return; }
            if (MatchesHotkey(e, config.ResizeHotkey)) { OnResizeClick(null, null); e.Handled = true; return; }
            if (MatchesHotkey(e, config.EmojiHotkey)) { TriggerToolButton("EmojiTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.PixelateHotkey1) || MatchesHotkey(e, config.PixelateHotkey2)) { TriggerToolButton("BlurTool"); e.Handled = true; return; }
            if (MatchesHotkey(e, config.CropHotkey)) 
            { 
                if (_selectedControl != null && _selectedControl.Tag as string == "CropObject")
                {
                    OnContextApplyClick(null, null);
                }
                else
                {
                    TriggerToolButton("CropTool"); 
                }
                e.Handled = true; 
                return; 
            }
        }

        private void UngroupMultiSelectGroup(AvaloniaControl group)
        {
            if (group is Canvas c && c.Tag as string == "MultiSelectGroup")
            {
                double gL = Canvas.GetLeft(c);
                if (double.IsNaN(gL)) gL = 0;
                double gT = Canvas.GetTop(c);
                if (double.IsNaN(gT)) gT = 0;

                var children = c.Children.ToList();
                c.Children.Clear();

                foreach (var child in children)
                {
                    double cL = Canvas.GetLeft(child);
                    if (double.IsNaN(cL)) cL = 0;
                    double cT = Canvas.GetTop(child);
                    if (double.IsNaN(cT)) cT = 0;
                    
                    if (IsVectorControl(child) && TryGetVectorAbsolutePoints(child, out var vStart, out var vEnd))
                    {
                        SetVectorAbsolutePoints(child, new AvaloniaPoint(vStart.X + gL, vStart.Y + gT), new AvaloniaPoint(vEnd.X + gL, vEnd.Y + gT));
                    }
                    else
                    {
                        Canvas.SetLeft(child, gL + cL);
                        Canvas.SetTop(child, gT + cT);
                    }
                    _canvas.Children.Add(child);
                }
                _canvas.Children.Remove(c);
            }
        }

        private void CleanupOrphanedMultiSelectGroups()
        {
            if (_canvas == null) return;
            var orphanedGroups = _canvas.Children
                .OfType<Canvas>()
                .Where(c => c.Tag as string == "MultiSelectGroup" && !ReferenceEquals(c, _selectedControl))
                .ToList();

            foreach (var group in orphanedGroups)
            {
                UngroupMultiSelectGroup(group);
            }
        }

        private void ResetToolsAndSelection()
        {
            HideCropModePopup();
            if (_lineSizePopup != null) _lineSizePopup.IsOpen = false;
            SetCurrentTool(EditorTool.None);
            if (_selectedControl is Canvas activeGroup && activeGroup.Tag as string == "MultiSelectGroup")
            {
                UngroupMultiSelectGroup(activeGroup);
            }
            CleanupOrphanedMultiSelectGroups();
            _selectedControl = null;
            RemovePreviewShape();
            UpdateSelectionIndicator();
            UpdateThicknessPanelVisibility();
            SetEditorCursor(HandCursor);
            if (_leftToolbar != null) { foreach (var child in _leftToolbar.Children) { if (child is Button b) b.Classes.Remove("selected"); } }
            _cropTool?.Classes.Remove("selected");
            HideToolGhost();
            HideSnapGuides();
            _isFillMode = false;
            UpdateFillToggleVisual();
            UpdateModeStatus();

            if (_isOcrInteractiveMode)
            {
                _isOcrInteractiveMode = false;
                if (_ocrButtonText != null) _ocrButtonText.Text = "OCR Text Extraction";
                if (_ocrButton != null) _ocrButton.Background = Brushes.Transparent;
                ToggleToolbarControls(false);
                ClearOcrVisuals();
                _interactiveOcrInfo = null;
            }
        }
        
        private void TriggerToolButton(string name)
        {
            var btn = this.FindControl<Button>(name);
            if (btn != null && btn.IsEnabled) SelectToolButton(btn, toggleIfActive: false);
        }

        private CancellationTokenSource _zoomHintCts;

        private async void ShowZoomHintDebounced()
        {
            var previous = System.Threading.Interlocked.Exchange(ref _zoomHintCts, null);
            if (previous != null)
            {
                try { previous.Cancel(); } catch (ObjectDisposedException) { }
                previous.Dispose();
            }

            var cts = new CancellationTokenSource();
            _zoomHintCts = cts;
            var ct = cts.Token;

            try
            {
                await Task.Delay(500, ct);
                if (ct.IsCancellationRequested || !IsVisible) return;
                OverlayHelper.ShowLightToast($"Zoom: {(int)(_zoomFactor * 100)}% (Middle-drag to pan)", this);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(ImageEditorWindow)).Error("ShowZoomHintDebounced failed", ex);
            }
            finally
            {
                if (System.Threading.Interlocked.CompareExchange(ref _zoomHintCts, null, cts) == cts)
                {
                    cts.Dispose();
                }
            }
        }

        private void CancelZoomHint()
        {
            var pending = System.Threading.Interlocked.Exchange(ref _zoomHintCts, null);
            if (pending == null) return;
            try { pending.Cancel(); } catch (ObjectDisposedException) { }
            pending.Dispose();
        }

        
        private static readonly EditorTool[] ScrollCycleTools =
        {
            EditorTool.Arrow, EditorTool.Line, EditorTool.Rectangle, EditorTool.Ellipse,
            EditorTool.FreeDraw, EditorTool.Highlight, EditorTool.Blur,
            EditorTool.Text, EditorTool.Counter, EditorTool.Emoji
        };

        private void OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                CycleToolInPlace(e.Delta.Y > 0);
                e.Handled = true;
                return;
            }

            if (_isPanning || _isDraggingSelected || _isResizing || _isDrawing) return;

            var scrollViewer = _editorScrollViewer;
            if (scrollViewer == null || _image == null) return;

            var pointerInScroller = e.GetPosition(scrollViewer);

            if (_zoomEngine.IsAnimating && _zoomFocalViewportPoint.HasValue)
            {
                var prevVp = _zoomFocalViewportPoint.Value;
                if (Math.Abs(prevVp.X - pointerInScroller.X) > 20 || Math.Abs(prevVp.Y - pointerInScroller.Y) > 20)
                {
                    SetFocalPoint(pointerInScroller);
                }
            }
            else
            {
                SetFocalPoint(pointerInScroller);
            }

            double delta = Math.Clamp(e.Delta.Y, -2.5, 2.5);
            double step = Math.Pow(ZoomStepMultiplier, delta);
            SmoothZoomTowards(_targetZoom * step);

            ShowZoomHintDebounced();
            e.Handled = true;
        }

        private void CycleToolInPlace(bool forward)
        {
            var current = _currentTool == EditorTool.None ? EditorTool.Arrow : _currentTool;
            int idx = Array.IndexOf(ScrollCycleTools, current);
            if (idx < 0) idx = 0;

            idx = forward
                ? (idx + 1) % ScrollCycleTools.Length
                : (idx - 1 + ScrollCycleTools.Length) % ScrollCycleTools.Length;

            var next = ScrollCycleTools[idx];
            var buttonName = GetToolButtonName(next);
            if (!string.IsNullOrEmpty(buttonName))
            {
                TriggerToolButton(buttonName);
                OverlayHelper.ShowLightToast($"⚡ {GetToolDisplayName(next).ToUpper()}", this);
            }
        }

        private void GetViewportPadding(double zoom, out double padX, out double padY, out double scaledW, out double scaledH)
        {
            var scrollViewer = _editorScrollViewer;
            double vpW = scrollViewer?.Viewport.Width ?? 0;
            double vpH = scrollViewer?.Viewport.Height ?? 0;
            double winW = Bounds.Width > 0 ? Bounds.Width : Width;
            double winH = Bounds.Height > 0 ? Bounds.Height : Height;
            EditorZoomEngine.CalculateViewportPadding(
                zoom,
                _image?.Width ?? 0,
                _image?.Height ?? 0,
                vpW,
                vpH,
                winW,
                winH,
                out padX,
                out padY,
                out scaledW,
                out scaledH);
        }

        private void ApplyZoom()
        {
            if (_imageControl != null && _canvas != null && _image != null)
            {
                _imageControl.Width = _image.Width;
                _imageControl.Height = _image.Height;
                _canvas.Width = _image.Width;
                _canvas.Height = _image.Height;

                var snipBorder = _snipBorder;
                if (snipBorder != null)
                {
                    snipBorder.Width = _image.Width;
                    snipBorder.Height = _image.Height;
                    var config = IniConfig.GetIniSection<CoreConfiguration>();
                    bool hasBorder = config?.AddFrameBorders ?? true;
                    if (hasBorder)
                    {
                        int t = config?.FrameBorderThickness > 0 ? config.FrameBorderThickness : 4;
                        snipBorder.BorderThickness = new Thickness(t);
                        if (AvaloniaColor.TryParse(config?.FrameBorderColor ?? "#434343", out var bc))
                        {
                            snipBorder.BorderBrush = new SolidColorBrush(bc);
                        }
                    }
                    else
                    {
                        snipBorder.BorderThickness = new Thickness(0);
                    }
                }

                var overlayCanvas = _overlayCanvas;
                if (overlayCanvas != null)
                {
                    overlayCanvas.Width = _image.Width;
                    overlayCanvas.Height = _image.Height;
                }

                var zoomContainer = _zoomContainer;
                if (zoomContainer != null)
                {
                    zoomContainer.Width = _image.Width;
                    zoomContainer.Height = _image.Height;
                    zoomContainer.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
                    zoomContainer.RenderTransform = new ScaleTransform(_zoomFactor, _zoomFactor);
                }

                GetViewportPadding(_zoomFactor, out double padX, out double padY, out double scaledW, out double scaledH);

                var scrollViewer = _editorScrollViewer;
                double vpW = scrollViewer?.Viewport.Width ?? 0;
                double vpH = scrollViewer?.Viewport.Height ?? 0;

                double totalW = Math.Max(vpW, scaledW + 2 * padX);
                double totalH = Math.Max(vpH, scaledH + 2 * padY);

                var scrollContentPanel = _scrollContentPanel;
                if (scrollContentPanel != null)
                {
                    scrollContentPanel.Width = totalW;
                    scrollContentPanel.Height = totalH;
                }

                var mainGrid = _mainGrid;
                if (mainGrid != null)
                {
                    mainGrid.Width = scaledW;
                    mainGrid.Height = scaledH;
                    mainGrid.Margin = new Thickness(padX, padY, 0, 0);
                }

                var zoomText = _zoomText;
                if (zoomText != null) zoomText.Content = $"{(int)Math.Round(_zoomFactor * 100)}%";
                
                _ghostIconScale.ScaleX = 1.0 / _zoomFactor;
                _ghostIconScale.ScaleY = 1.0 / _zoomFactor;
            }
        }

        private void SetFocalPoint(Avalonia.Point pointerInScroller)
        {
            var scrollViewer = _editorScrollViewer;
            if (scrollViewer == null || _image == null) return;

            GetViewportPadding(_zoomFactor, out double currentPadX, out double currentPadY, out _, out _);
            _zoomEngine.SetFocalPoint(
                pointerInScroller,
                scrollViewer.Offset.X,
                scrollViewer.Offset.Y,
                scrollViewer.Viewport.Width,
                scrollViewer.Viewport.Height,
                currentPadX,
                currentPadY,
                _image.Width,
                _image.Height);
        }

        private void SetFocalPointToCenter()
        {
            var scrollViewer = _editorScrollViewer;
            if (scrollViewer == null || _image == null) return;

            GetViewportPadding(_zoomFactor, out double currentPadX, out double currentPadY, out _, out _);
            _zoomEngine.SetFocalPointToCenter(
                scrollViewer.Offset.X,
                scrollViewer.Offset.Y,
                scrollViewer.Viewport.Width,
                scrollViewer.Viewport.Height,
                currentPadX,
                currentPadY,
                _image.Width,
                _image.Height);
        }

        private void SmoothZoomTowards(double requestedZoom)
        {
            _zoomEngine.SmoothZoomTowards(requestedZoom);
        }

        private void CenterImageInScrollViewer()
        {
            if (_image == null) return;
            var scrollViewer = _editorScrollViewer;
            if (scrollViewer == null) return;

            _zoomEngine.ClearFocalPoints();

            ApplyZoom();

            GetViewportPadding(_zoomFactor, out double padX, out double padY, out double scaledW, out double scaledH);
            double totalW = Math.Max(scrollViewer.Viewport.Width, scaledW + 2 * padX);
            double totalH = Math.Max(scrollViewer.Viewport.Height, scaledH + 2 * padY);

            scrollViewer.Offset = EditorZoomEngine.CalculateDesiredOffset(
                _zoomFactor,
                padX,
                padY,
                totalW,
                totalH,
                scrollViewer.Viewport.Width,
                scrollViewer.Viewport.Height,
                null,
                null);

            if (scrollViewer.Extent.Width >= totalW - 2 && scrollViewer.Extent.Height >= totalH - 2)
            {
                _initialCenterPending = false;
            }
        }

        private void ApplyZoomAndFocalOffset()
        {
            if (_image == null) return;
            var scrollViewer = _editorScrollViewer;
            if (scrollViewer == null) return;

            ApplyZoom();

            GetViewportPadding(_zoomFactor, out double padX, out double padY, out double scaledW, out double scaledH);

            double totalW = Math.Max(scrollViewer.Viewport.Width, scaledW + 2 * padX);
            double totalH = Math.Max(scrollViewer.Viewport.Height, scaledH + 2 * padY);

            scrollViewer.Offset = EditorZoomEngine.CalculateDesiredOffset(
                _zoomFactor,
                padX,
                padY,
                totalW,
                totalH,
                scrollViewer.Viewport.Width,
                scrollViewer.Viewport.Height,
                _zoomEngine.FocalViewportPoint,
                _zoomEngine.FocalUnscaledPoint);
        }

        private bool _initialCenterPending = true;

        private void OnScrollViewerLayoutUpdated(object sender, EventArgs e)
        {
            if (!_initialCenterPending || _image == null) return;
            var scroller = _editorScrollViewer;
            if (scroller == null || scroller.Viewport.Width <= 1 || scroller.Viewport.Height <= 1) return;

            GetViewportPadding(_zoomFactor, out double padX, out double padY, out double scaledW, out double scaledH);
            double totalW = Math.Max(scroller.Viewport.Width, scaledW + 2 * padX);
            double totalH = Math.Max(scroller.Viewport.Height, scaledH + 2 * padY);

            if (scroller.Extent.Width >= totalW - 2 && scroller.Extent.Height >= totalH - 2)
            {
                _initialCenterPending = false;
                scroller.Offset = EditorZoomEngine.CalculateDesiredOffset(
                    _zoomFactor,
                    padX,
                    padY,
                    totalW,
                    totalH,
                    scroller.Viewport.Width,
                    scroller.Viewport.Height,
                    null,
                    null);
            }
        }

        private void OnScrollViewerSizeChanged()
        {
            if (_image == null) return;
            if (_zoomEngine.IsAnimating) return;
            if (_initialCenterPending)
            {
                var scroller = _editorScrollViewer;
                if (scroller != null && scroller.Viewport.Width > 1 && scroller.Viewport.Height > 1)
                {
                    CenterImageInScrollViewer();
                    return;
                }
            }
            ApplyZoomAndFocalOffset();
        }

        private bool _forceClose = false;
        
        private readonly OcrOverlayController _ocrController = new OcrOverlayController();
        private bool _isOcrInteractiveMode
        {
            get => _ocrController.IsInteractiveMode;
            set => _ocrController.IsInteractiveMode = value;
        }
        private OcrInformation _interactiveOcrInfo
        {
            get => _ocrController.InteractiveOcrInfo;
            set => _ocrController.SetOcrInfo(value);
        }
        private List<OcrWord> _selectedOcrWords => _ocrController.SelectedOcrWords;
        private int _ocrSelectionStartIndex
        {
            get => _ocrController.SelectionStartIndex;
            set => _ocrController.SelectionStartIndex = value;
        }
        private int _ocrSelectionEndIndex
        {
            get => _ocrController.SelectionEndIndex;
            set => _ocrController.SelectionEndIndex = value;
        }
        private bool _isClosingPromptOpen = false;
        private void OnWindowClosing(object sender, WindowClosingEventArgs e)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (_forceClose || !_historyManager.CanUndo || !config.WarnBeforeClosingEditor) return;
            
            e.Cancel = true;
            if (!_isClosingPromptOpen)
            {
                _isClosingPromptOpen = true;
                SetClosePromptVisible(true);
            }
        }

        private void SetClosePromptVisible(bool visible)
        {
            var prompt = _closePromptOverlay;
            var content = _editorContentRoot;
            if (content != null) content.IsEnabled = !visible;
            if (prompt == null) return;

            prompt.IsVisible = visible;
            if (!visible) return;

            Dispatcher.UIThread.Post(() =>
            {
                var cancel = _closePromptCancelButton;
                if (cancel != null) cancel.Focus();
                else prompt.Focus();
            }, DispatcherPriority.Input);
        }

        private void OnClosePromptKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                OnPromptCancel(sender, null);
                return;
            }

            if (e.Key == Key.Enter)
            {
                var focused = FocusManager?.GetFocusedElement();
                if (focused is Button)
                {
                    return;
                }

                e.Handled = true;
                OnPromptSaveClose(sender, null);
            }
        }

        private async void OnPromptSaveClose(object sender, RoutedEventArgs e)
        {
            var save = _closePromptSaveButton;
            var discard = _closePromptDiscardButton;
            var cancel = _closePromptCancelButton;
            if (save != null) save.IsEnabled = false;
            if (discard != null) discard.IsEnabled = false;
            if (cancel != null) cancel.IsEnabled = false;

            bool saved;
            try
            {
                saved = await SaveToDownloadsAsync().ConfigureAwait(true);
            }
            finally
            {
                if (save != null) save.IsEnabled = true;
                if (discard != null) discard.IsEnabled = true;
                if (cancel != null) cancel.IsEnabled = true;
            }

            if (saved)
            {
                if (!IniConfig.GetIniSection<CoreConfiguration>().CloseEditorOnAction)
                {
                    _forceClose = true;
                    Close();
                }

                return;
            }

            _isClosingPromptOpen = false;
            var message = _closePromptMessage;
            if (message != null)
            {
                message.Text = "The image could not be saved, so nothing was closed. Try Save again, choose Discard to throw the changes away, or Cancel to keep editing.";
                message.Foreground = this.FindResource("FreeSnipWarningBrush") as IBrush ?? message.Foreground;
            }

            SetClosePromptVisible(true);
            _isClosingPromptOpen = true;
        }

        private void OnPromptDiscard(object sender, RoutedEventArgs e)
        {
            SetClosePromptVisible(false);
            _forceClose = true;
            Close();
        }

        private void OnPromptCancel(object sender, RoutedEventArgs e)
        {
            _isClosingPromptOpen = false;
            SetClosePromptVisible(false);
        }

        private void OnWindowOpened(object sender, EventArgs e)
        {
            UiClipboard.Register(this, text => Clipboard?.SetTextAsync(text) ?? Task.FromException(new InvalidOperationException("Clipboard unavailable.")));

            
            if (!_zoomViewportHooked)
            {
                _zoomViewportHooked = true;
                var zoomViewer = _editorScrollViewer;
                if (zoomViewer != null)
                {
                    zoomViewer.SizeChanged += (_, _) => OnScrollViewerSizeChanged();
                    zoomViewer.LayoutUpdated += OnScrollViewerLayoutUpdated;
                }
            }

            if (_autoFitPending)
            {
                Dispatcher.UIThread.Post(AutoFitWindowToContent, DispatcherPriority.Loaded);
            }
            else
            {
                Dispatcher.UIThread.Post(CenterImageInScrollViewer, DispatcherPriority.Loaded);
            }
        }
        private void OnWindowClosed(object sender, EventArgs e) 
        { 
            _zoomEngine.Dispose(); 
            for (int i = 0; i < _activeHaloTimers.Count; i++)
            {
                _activeHaloTimers[i]?.Stop();
            }
            _activeHaloTimers.Clear();
            _snapHaloLayer?.Children.Clear();
            CancelZoomHint(); 
            UiClipboard.Unregister(this); 
            var zoomViewer = _editorScrollViewer;
            if (zoomViewer != null)
            {
                zoomViewer.LayoutUpdated -= OnScrollViewerLayoutUpdated;
            }
            ReleaseImageResources(); 
            IniConfig.Flush();
        }

        private string _sourceTitle;
        public async Task SetImageAsync(ImageSharpImage image, RECT captureRect, string sourceTitle = null)
        {
            _sourceTitle = sourceTitle;
            if (image == null) throw new ArgumentNullException(nameof(image));
            ReleaseImageResources();
            SetImageUnderLock(image);
            _zoomFactor = 1.0;
            _targetZoom = 1.0;
            _initialCenterPending = true;
            await UpdateDisplayAsync().ConfigureAwait(true);
            PositionWindow(captureRect);
        }

        private async Task UpdateDisplayAsync()
        {
            if (_image == null) return;
            Bitmap rendered = await Task.Run(() => WithImage(img => img.ToAvaloniaBitmap())).ConfigureAwait(true);
            if (rendered == null) { return; }
            _displayBitmap?.Dispose();
            _displayBitmap = rendered;
            if (_imageControl != null) { _imageControl.Source = _displayBitmap; _imageControl.Width = _image.Width; _imageControl.Height = _image.Height; }
            if (_canvas != null) { _canvas.Width = _image.Width; _canvas.Height = _image.Height; }
            ApplyZoom();
            RefreshAllPixelateAnnotations();
            RefreshAllHighlightAnnotations();
            string dim = $"{_image.Width} x {_image.Height}";
            if (_titleDimensionText != null) _titleDimensionText.Text = dim;
            UpdateUndoRedoState();
        }

        private void UpdateUndoRedoState()
        {
            if (_undoBtn != null) _undoBtn.IsEnabled = _historyManager.CanUndo;
            if (_redoBtn != null) _redoBtn.IsEnabled = _historyManager.CanRedo;
            if (_undoBtn != null) ToolTip.SetTip(_undoBtn, _historyManager.CanUndo ? "Undo available (Ctrl+Z)" : "Undo (Ctrl+Z)");
            if (_redoBtn != null) ToolTip.SetTip(_redoBtn, _historyManager.CanRedo ? "Redo available (Ctrl+Y)" : "Redo (Ctrl+Y)");
        }

        private static string GetToolDisplayName(EditorTool tool) => tool switch
        {
            EditorTool.None => "Move",
            EditorTool.FreeDraw => "Free draw",
            EditorTool.Blur => "Pixelate",
            EditorTool.Rectangle => "Rectangle",
            EditorTool.Ellipse => "Circle",
            EditorTool.Crop => "Crop",
            EditorTool.Text => "Text",
            EditorTool.Counter => "Counter",
            EditorTool.Emoji => "Emoji",
            _ => tool.ToString()
        };

        private void UpdateModeStatus()
        {
            if (_modeText == null) return;
            SyncToolButtonSelection();

            if (_selectedControl != null)
            {
                var selectedTool = GetToolFromControl(_selectedControl);
                _modeText.Text = selectedTool == EditorTool.None
                    ? "Object selected"
                    : $"{GetToolDisplayName(selectedTool)} selected";
                return;
            }

            _modeText.Text = _currentTool == EditorTool.None
                ? "Move mode"
                : $"{GetToolDisplayName(_currentTool)} tool";
        }

        private static string GetToolButtonName(EditorTool tool) => tool switch
        {
            EditorTool.Arrow => "ArrowTool",
            EditorTool.Line => "LineTool",
            EditorTool.Rectangle => "RectTool",
            EditorTool.Ellipse => "EllipseTool",
            EditorTool.FreeDraw => "FreeDrawTool",
            EditorTool.Highlight => "HighlightTool",
            EditorTool.Blur => "BlurTool",
            EditorTool.Text => "TextTool",
            EditorTool.Counter => "CounterTool",
            EditorTool.Emoji => "EmojiTool",
            EditorTool.Crop => "CropTool",
            _ => null
        };

        private void SyncToolButtonSelection()
        {
            if (_leftToolbar != null)
            {
                foreach (var child in _leftToolbar.Children)
                {
                    if (child is Button button) button.Classes.Remove("selected");
                }
            }

            _cropTool?.Classes.Remove("selected");

            var activeTool = _currentTool != EditorTool.None
                ? _currentTool
                : _selectedControl != null ? GetToolFromControl(_selectedControl) : EditorTool.None;
            var buttonName = GetToolButtonName(activeTool);
            if (!string.IsNullOrEmpty(buttonName))
            {
                this.FindControl<Button>(buttonName)?.Classes.Add("selected");
            }
        }

        private bool IsEditorChrome(AvaloniaControl control)
        {
            return ReferenceEquals(control, _selectionIndicator)
                || ReferenceEquals(control, _circleSelectionIndicator)
                || ReferenceEquals(control, _lineSelectionIndicator)
                || ReferenceEquals(control, _arrowSelectionIndicator)
                || ReferenceEquals(control, _hoverIndicator)
                || ReferenceEquals(control, _circleHoverIndicator)
                || ReferenceEquals(control, _lineHoverIndicator)
                || ReferenceEquals(control, _arrowHoverIndicator)
                || ReferenceEquals(control, _vectorInfoPopup)
                || _resizeHandles.Contains(control)
                || ReferenceEquals(control, _previewControl)
                || ReferenceEquals(control, _contextToolbar)
                || ReferenceEquals(control, _startHandle)
                || ReferenceEquals(control, _endHandle)
                || ReferenceEquals(control, _toolGhostLayer)
                || ReferenceEquals(control, _snapGuideLayer)
                || ReferenceEquals(control, _snapDotsLayer)
                || ReferenceEquals(control, _snapHaloLayer)
                || ReferenceEquals(control, _rubberBandVisual)
                || _multiSelectionIndicators.Contains(control);
        }

        private readonly List<AvaloniaControl> _annotationScratch = new List<AvaloniaControl>(32);
        private readonly List<ShapeSnapTarget> _shapeSnapScratch = new List<ShapeSnapTarget>(64);
        private readonly List<AvaloniaPoint> _vectorSnapScratch = new List<AvaloniaPoint>(64);
        private bool _annotationScratchInUse;

        internal List<AvaloniaControl> GetUserAnnotations()
        {
            var result = new List<AvaloniaControl>(_canvas.Children.Count);
            CollectUserAnnotations(result);
            return result;
        }

        private void CollectUserAnnotations(List<AvaloniaControl> destination)
        {
            destination.Clear();
            var children = _canvas.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is AvaloniaControl control && !IsEditorChrome(control)) destination.Add(control);
            }
        }

        private List<AvaloniaControl> RentAnnotationBuffer(out bool pooled)
        {
            if (_annotationScratchInUse)
            {
                pooled = false;
                return new List<AvaloniaControl>(_canvas.Children.Count);
            }

            pooled = true;
            _annotationScratchInUse = true;
            return _annotationScratch;
        }

        private void ReturnAnnotationBuffer(bool pooled)
        {
            if (!pooled) return;
            _annotationScratch.Clear();
            _annotationScratchInUse = false;
        }

        private List<(AvaloniaControl Control, bool WasVisible)> SetEditorChromeVisible(bool visible)
        {
            var chrome = new List<AvaloniaControl>();
            if (_selectionIndicator != null) chrome.Add(_selectionIndicator);
            if (_circleSelectionIndicator != null) chrome.Add(_circleSelectionIndicator);
            if (_lineSelectionIndicator != null) chrome.Add(_lineSelectionIndicator);
            if (_arrowSelectionIndicator != null) chrome.Add(_arrowSelectionIndicator);
            if (_hoverIndicator != null) chrome.Add(_hoverIndicator);
            if (_circleHoverIndicator != null) chrome.Add(_circleHoverIndicator);
            if (_lineHoverIndicator != null) chrome.Add(_lineHoverIndicator);
            if (_arrowHoverIndicator != null) chrome.Add(_arrowHoverIndicator);
            if (_vectorInfoPopup != null) chrome.Add(_vectorInfoPopup);
            chrome.AddRange(_resizeHandles);
            if (_contextToolbar != null) chrome.Add(_contextToolbar);
            if (_startHandle != null) chrome.Add(_startHandle);
            if (_endHandle != null) chrome.Add(_endHandle);
            if (_toolGhostLayer != null) chrome.Add(_toolGhostLayer);
            if (_snapGuideLayer != null) chrome.Add(_snapGuideLayer);
            if (_snapDotsLayer != null) chrome.Add(_snapDotsLayer);
            if (_snapHaloLayer != null) chrome.Add(_snapHaloLayer);
            if (_rubberBandVisual != null) chrome.Add(_rubberBandVisual);
            chrome.AddRange(_multiSelectionIndicators);

            var state = new List<(AvaloniaControl Control, bool WasVisible)>();
            foreach (var control in chrome)
            {
                state.Add((control, control.IsVisible));
                control.IsVisible = visible && control.IsVisible;
            }
            return state;
        }

        private void RestoreVisibility(List<(AvaloniaControl Control, bool WasVisible)> state)
        {
            if (state == null) return;
            foreach (var item in state) item.Control.IsVisible = item.WasVisible;
        }

        private void RemoveUserAnnotations()
        {
            foreach (var child in GetUserAnnotations())
            {
                DisposeAnnotationResources(child);
                _canvas.Children.Remove(child);
            }
        }

        private static void DisposeAnnotationResources(AvaloniaControl control)
        {
            if (control == null) return;

            if (control is Avalonia.Controls.Image image)
            {
                if (image.Source is IDisposable source) source.Dispose();
                image.Source = null;
            }

            if (control is Avalonia.Controls.Shapes.Shape shape)
            {
                if (shape is Avalonia.Controls.Shapes.Rectangle rectangle && rectangle.Tag is PixelateProperties)
                {
                    rectangle.Resources["PixelateRequest"] = Interlocked.Increment(ref _pixelateCounter);
                }

                if (shape is Avalonia.Controls.Shapes.Rectangle highlightRect && highlightRect.Tag is HighlightProperties)
                {
                    highlightRect.Resources["HighlightRequest"] = Interlocked.Increment(ref _highlightCounter);
                }

                if (shape.Fill is ImageBrush fillBrush && fillBrush.Source is IDisposable fillSource) fillSource.Dispose();
                if (shape.Stroke is ImageBrush strokeBrush && strokeBrush.Source is IDisposable strokeSource) strokeSource.Dispose();
                if (shape.Fill is ImageBrush) shape.Fill = null;
                if (shape.Stroke is ImageBrush) shape.Stroke = null;
            }

            if (control is Border border && border.Child is AvaloniaControl child)
            {
                DisposeAnnotationResources(child);
            }

            if (control is Panel panel)
            {
                foreach (var panelChild in panel.Children.OfType<AvaloniaControl>().ToList())
                {
                    DisposeAnnotationResources(panelChild);
                }
            }
        }

        private void SaveUndoState(bool cloneImage = false)
        {
            _historyManager.PushUndo(CreateSnapshot(cloneImage));
            UpdateUndoRedoState();
        }

        private void ShowUndoAvailableHint()
        {
            if (_historyManager.CanUndo)
            {
                OverlayHelper.ShowLightToast("Press Ctrl+Z to undo", this);
            }
        }

        private EditorSnapshot CreateSnapshot(bool cloneImage)
        {
            var annotations = new List<AvaloniaControl>();
            foreach (var control in GetUserAnnotations())
            {
                if (control is Canvas group && group.Tag as string == "MultiSelectGroup")
                {
                    double gL = Canvas.GetLeft(group);
                    if (double.IsNaN(gL)) gL = 0;
                    double gT = Canvas.GetTop(group);
                    if (double.IsNaN(gT)) gT = 0;

                    foreach (var child in group.Children.OfType<AvaloniaControl>())
                    {
                        var clonedChild = CloneAnnotationControl(child);
                        if (clonedChild != null)
                        {
                            double cL = Canvas.GetLeft(child);
                            if (double.IsNaN(cL)) cL = 0;
                            double cT = Canvas.GetTop(child);
                            if (double.IsNaN(cT)) cT = 0;

                            Canvas.SetLeft(clonedChild, gL + cL);
                            Canvas.SetTop(clonedChild, gT + cT);
                            annotations.Add(clonedChild);
                        }
                    }
                }
                else
                {
                    var clone = CloneAnnotationControl(control);
                    if (clone != null) annotations.Add(clone);
                }
            }

            return new EditorSnapshot
            {
                Image = cloneImage ? WithImage(img => img.Clone(x => { })) : null,
                Annotations = annotations
            };
        }

        private async void RestoreSnapshot(EditorSnapshot snapshot)
        {
            await RestoreSnapshotAsync(snapshot).ConfigureAwait(true);
        }

        internal async Task RestoreSnapshotAsync(EditorSnapshot snapshot)
        {
            if (snapshot == null) return;
            try
            {
                if (_selectedControl is Canvas activeGroup && activeGroup.Tag as string == "MultiSelectGroup")
                {
                    UngroupMultiSelectGroup(activeGroup);
                }

                RemoveUserAnnotations();
                _selectedControl = null;
                _isDraggingSelected = false;
                _isResizing = false;
                _activePolyline = null;

                if (snapshot.Image != null)
                {
                    SetImageUnderLock(snapshot.Image.Clone(x => { }));
                }

                foreach (var annotation in snapshot.Annotations)
                {
                    var clone = CloneAnnotationControl(annotation);
                    if (clone == null) continue;

                    if (clone is Canvas g && g.Tag as string == "MultiSelectGroup")
                    {
                        UngroupMultiSelectGroup(g);
                    }
                    else
                    {
                        _canvas.Children.Add(clone);
                    }
                }

                await UpdateDisplayAsync().ConfigureAwait(true);
                RefreshAllPixelateAnnotations();
                RefreshAllHighlightAnnotations();
                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
                if (snapshot.Image != null) RefitWindowToImage();
            }
            catch (Exception ex)
            {
                Log.Error("RestoreSnapshot failed - undo aborted, current editor state preserved", ex);
                OverlayHelper.ShowLightToast("Undo failed - current view kept.", this);
            }
        }

        private AvaloniaControl CloneAnnotationControl(AvaloniaControl source)
        {
            AvaloniaControl clone = source switch
            {
                Avalonia.Controls.Shapes.Line line => new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = line.StartPoint,
                    EndPoint = line.EndPoint,
                    Stroke = line.Stroke,
                    StrokeThickness = line.StrokeThickness,
                    StrokeJoin = line.StrokeJoin,
                    StrokeLineCap = line.StrokeLineCap,
                    IsHitTestVisible = line.IsHitTestVisible
                },
                Avalonia.Controls.Shapes.Polyline polyline => new Avalonia.Controls.Shapes.Polyline
                {
                    Points = new List<AvaloniaPoint>(polyline.Points),
                    Stroke = polyline.Stroke,
                    StrokeThickness = polyline.StrokeThickness,
                    IsHitTestVisible = polyline.IsHitTestVisible
                },
                Avalonia.Controls.Shapes.Rectangle rectangle => new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = rectangle.Width,
                    Height = rectangle.Height,
                    Fill = CloneAnnotationBrush(rectangle.Fill),
                    Stroke = CloneAnnotationBrush(rectangle.Stroke),
                    StrokeThickness = rectangle.StrokeThickness,
                    Tag = rectangle.Tag,
                    IsHitTestVisible = rectangle.IsHitTestVisible
                },
                Avalonia.Controls.Shapes.Ellipse ellipse => new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = ellipse.Width,
                    Height = ellipse.Height,
                    Fill = CloneAnnotationBrush(ellipse.Fill),
                    Stroke = CloneAnnotationBrush(ellipse.Stroke),
                    StrokeThickness = ellipse.StrokeThickness,
                    Tag = ellipse.Tag,
                    IsHitTestVisible = ellipse.IsHitTestVisible
                },
                Avalonia.Controls.Shapes.Polygon polygon => new Avalonia.Controls.Shapes.Polygon
                {
                    Points = new List<AvaloniaPoint>(polygon.Points),
                    Fill = CloneAnnotationBrush(polygon.Fill),
                    Stroke = CloneAnnotationBrush(polygon.Stroke),
                    StrokeThickness = polygon.StrokeThickness,
                    IsHitTestVisible = polygon.IsHitTestVisible
                },
                Canvas canvas => CloneAnnotationCanvas(canvas),
                Border border => CloneAnnotationBorder(border),
                Avalonia.Controls.Image image => new Avalonia.Controls.Image
                {
                    Source = CloneImageSource(image.Source),
                    Width = image.Width,
                    Height = image.Height,
                    Stretch = image.Stretch,
                    IsHitTestVisible = image.IsHitTestVisible
                },
                TextBlock textBlock => new TextBlock
                {
                    Text = textBlock.Text,
                    Foreground = textBlock.Foreground,
                    FontSize = textBlock.FontSize,
                    FontWeight = textBlock.FontWeight,
                    FontFamily = textBlock.FontFamily,
                    Width = textBlock.Width,
                    Height = textBlock.Height,
                    HorizontalAlignment = textBlock.HorizontalAlignment,
                    VerticalAlignment = textBlock.VerticalAlignment,
                    IsHitTestVisible = textBlock.IsHitTestVisible
                },
                TextBox textBox => CloneTextBox(textBox),
                _ => null
            };

            if (clone == null) return null;
            CopyCommonState(source, clone);
            if (IsPixelateControl(clone) && clone is Avalonia.Controls.Shapes.Rectangle pixelation)
                pixelation.Fill = Brushes.Black;
            return clone;
        }

        private static IBrush CloneAnnotationBrush(IBrush brush)
        {
            return brush is ImageBrush ? null : brush;
        }

        private static Avalonia.Media.IImage CloneImageSource(Avalonia.Media.IImage source)
        {
            if (source is not Bitmap bitmap) return null;

            using var ms = new MemoryStream();
            bitmap.Save(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }

        private Canvas CloneAnnotationCanvas(Canvas source)
        {
            var clone = new Canvas
            {
                Width = source.Width,
                Height = source.Height,
                Background = source.Background,
                Tag = CloneControlTag(source.Tag),
                IsHitTestVisible = source.IsHitTestVisible
            };

            foreach (var child in source.Children.OfType<AvaloniaControl>())
            {
                var childClone = CloneAnnotationControl(child);
                if (childClone != null)
                {
                    Canvas.SetLeft(childClone, Canvas.GetLeft(child));
                    Canvas.SetTop(childClone, Canvas.GetTop(child));
                    clone.Children.Add(childClone);
                }
            }
            return clone;
        }

        private Border CloneAnnotationBorder(Border source)
        {
            var clone = new Border
            {
                Width = source.Width,
                Height = source.Height,
                Background = source.Background,
                BorderBrush = source.BorderBrush,
                BorderThickness = source.BorderThickness,
                CornerRadius = source.CornerRadius,
                Padding = source.Padding,
                IsHitTestVisible = source.IsHitTestVisible
            };

            if (source.Child is AvaloniaControl child)
            {
                clone.Child = CloneAnnotationControl(child);
                if (clone.Child is TextBox textBox)
                {
                    AttachTextBoxBehavior(clone, textBox, IniConfig.GetIniSection<CoreConfiguration>());
                }
            }

            return clone;
        }

        private TextBox CloneTextBox(TextBox source)
        {
            return new TextBox
            {
                Text = source.Text,
                Watermark = source.Watermark,
                Foreground = source.Foreground,
                CaretBrush = source.CaretBrush,
                Background = source.Background,
                BorderThickness = source.BorderThickness,
                FontSize = source.FontSize,
                FontWeight = source.FontWeight,
                Padding = source.Padding,
                AcceptsReturn = source.AcceptsReturn,
                MinWidth = source.MinWidth,
                Width = source.Width,
                Height = source.Height,
                TextWrapping = source.TextWrapping,
                Tag = CloneControlTag(source.Tag),
                IsHitTestVisible = source.IsHitTestVisible,
                FlowDirection = source.FlowDirection,
                TextAlignment = source.TextAlignment,
                VerticalContentAlignment = source.VerticalContentAlignment
            };
        }

        private void EnableTextBoxEditing(Border border, TextBox textBox)
        {
            if (textBox == null) return;
            textBox.IsReadOnly = false;
            textBox.CaretBrush = textBox.Foreground ?? Brushes.White;
            textBox.Cursor = IbeamCursor;
            textBox.Focus();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
        }

        [ThreadStatic]
        private static TextBlock s_measurementProbe;

        private static TextBlock GetMeasurementProbe()
        {
            return s_measurementProbe ??= new TextBlock();
        }

        internal void AttachTextBoxBehavior(Border border, TextBox textBox, CoreConfiguration config)
        {
            border.DoubleTapped += (s, e) =>
            {
                EnableTextBoxEditing(border, textBox);
                e.Handled = true;
            };
            textBox.DoubleTapped += (s, e) =>
            {
                EnableTextBoxEditing(border, textBox);
                e.Handled = true;
            };
            textBox.LostFocus += (s, e) =>
            {
                if (_isDraggingSelected && ReferenceEquals(_selectedControl, border)) return;
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    DisposeAnnotationResources(border);
                    _canvas?.Children.Remove(border);
                    if (ReferenceEquals(_selectedControl, border))
                    {
                        _selectedControl = null;
                        UpdateSelectionIndicator();
                    }
                }
                else {
                    SetTextBoxBoundsToBorder(border, textBox);
                    textBox.IsReadOnly = true;
                    textBox.CaretBrush = Brushes.Transparent;
                    textBox.Cursor = SizeAllCursor;
                    if (border.Width > 0 && border.Height > 0)
                    {
                        UpdatePersistedTextBoxDimensions(border.Width, border.Height);
                    }
                }
            };
            string lastText = textBox.Text ?? "";
            Action onTextChanged = () => {
                string text = textBox.Text ?? "";
                if (!string.IsNullOrEmpty(text)) {
                    char first = text.FirstOrDefault(c => char.IsLetter(c));
                    if (first != default && ((first >= 0x0590 && first <= 0x05FF) || (first >= 0x0600 && first <= 0x06FF))) {
                        textBox.FlowDirection = FlowDirection.RightToLeft;
                    } else {
                        textBox.FlowDirection = FlowDirection.LeftToRight;
                    }
                }
                textBox.TextAlignment = Avalonia.Media.TextAlignment.Center;

                double bW = !double.IsNaN(border.Width) && border.Width > 0 ? border.Width : border.Bounds.Width;
                double bH = !double.IsNaN(border.Height) && border.Height > 0 ? border.Height : border.Bounds.Height;
                if (bW > 0)
                {
                    double horizontalPadding = border.Padding.Left + border.Padding.Right +
                                               border.BorderThickness.Left + border.BorderThickness.Right +
                                               textBox.Padding.Left + textBox.Padding.Right + 8;
                    double verticalPadding = border.Padding.Top + border.Padding.Bottom +
                                             border.BorderThickness.Top + border.BorderThickness.Bottom +
                                             textBox.Padding.Top + textBox.Padding.Bottom + 8;
                    double availableWidth = Math.Max(20, bW - horizontalPadding);
                    double textHeight = MeasureTextHeight(textBox, textBox.FontSize, availableWidth);
                    double calculatedNewHeight = Math.Ceiling(textHeight + verticalPadding);
                    if (calculatedNewHeight > bH)
                    {
                        border.Height = calculatedNewHeight;
                        SetTextBoxBoundsToBorder(border, textBox);
                        if (ReferenceEquals(_selectedControl, border))
                        {
                            UpdateSelectionIndicator();
                        }
                    }
                }
            };
            textBox.TextChanged += (s, e) => {
                string current = textBox.Text ?? "";
                if (current != lastText)
                {
                    lastText = current;
                    onTextChanged();
                }
            };
            textBox.PropertyChanged += (s, e) => {
                if (e.Property == TextBox.TextProperty)
                {
                    string current = textBox.Text ?? "";
                    if (current != lastText)
                    {
                        lastText = current;
                        onTextChanged();
                    }
                }
            };
            border.AddHandler(InputElement.PointerPressedEvent, (object s, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                {
                    if (e.ClickCount >= 2)
                    {
                        EnableTextBoxEditing(border, textBox);
                        _isDraggingSelected = false;
                        UpdateSelectionIndicator();
                        e.Handled = true;
                        return;
                    }

                    var p = e.GetPosition(border);
                    double bW = border.Bounds.Width > 0 ? border.Bounds.Width : border.Width;
                    double bH = border.Bounds.Height > 0 ? border.Bounds.Height : border.Height;
                    double edgeThreshold = Math.Max(8, Math.Max(border.BorderThickness.Left, border.Padding.Left) + 4);
                    bool isEdge = p.X <= edgeThreshold || p.X >= bW - edgeThreshold ||
                                  p.Y <= edgeThreshold || p.Y >= bH - edgeThreshold;

                    if (textBox.IsReadOnly || isEdge)
                    {
                        var canvasPos = e.GetPosition(_canvas);
                        _selectedControl = border;
                        _isDraggingSelected = true;
                        _dragUndoCaptured = false;
                        _dragLastPoint = canvasPos;
                        _dragUnsnappedLeft = Canvas.GetLeft(border);
                        if (double.IsNaN(_dragUnsnappedLeft)) _dragUnsnappedLeft = border.Bounds.X;
                        _dragUnsnappedTop = Canvas.GetTop(border);
                        if (double.IsNaN(_dragUnsnappedTop)) _dragUnsnappedTop = border.Bounds.Y;

                        UpdateSelectionIndicator();
                        UpdateHoverIndicator(null);
                        HideToolGhost();
                        HideSnapHint();
                        e.Pointer.Capture(_canvas);
                        SetEditorCursor(HandCursor);
                        UpdateThicknessPanelVisibility();
                        e.Handled = true;
                    }
                }
            }, RoutingStrategies.Tunnel);
            border.PointerMoved += (s, e) =>
            {
                if (_isDraggingSelected || _isResizing || _isDrawing) return;
                var p = e.GetPosition(border);
                double bW = border.Bounds.Width > 0 ? border.Bounds.Width : border.Width;
                double bH = border.Bounds.Height > 0 ? border.Bounds.Height : border.Height;
                double edgeThreshold = Math.Max(8, Math.Max(border.BorderThickness.Left, border.Padding.Left) + 4);
                bool isEdge = p.X <= edgeThreshold || p.X >= bW - edgeThreshold ||
                              p.Y <= edgeThreshold || p.Y >= bH - edgeThreshold;
                border.Cursor = isEdge ? SizeAllCursor : (textBox.IsReadOnly ? HandCursor : IbeamCursor);
            };
            textBox.AddHandler(InputElement.PointerPressedEvent, (object s, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(textBox).Properties.IsRightButtonPressed) {
                    _canvas?.Focus();
                    OnWindowKeyDown(this, new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent });
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
            textBox.AddHandler(InputElement.KeyDownEvent, (object s, KeyEventArgs e) =>
            {
                if (e.Key == Key.Escape) { _canvas?.Focus(); OnWindowKeyDown(this, new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent }); e.Handled = true; }
                else if (e.Key == Key.Enter)
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    {
                        _canvas?.Focus();
                        OnWindowKeyDown(this, new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent });
                        e.Handled = true;
                    }
                    else
                    {
                        int caret = textBox.CaretIndex;
                        string currentText = textBox.Text ?? "";
                        textBox.Text = currentText.Insert(caret, Environment.NewLine);
                        textBox.CaretIndex = caret + Environment.NewLine.Length;
                        e.Handled = true;
                    }
                }
            }, RoutingStrategies.Tunnel);
        }

        internal static void SetTextBoxBoundsToBorder(Border border, TextBox textBox)
        {
            if (border == null || textBox == null) return;
            double bW = !double.IsNaN(border.Width) && border.Width > 0 ? border.Width : border.Bounds.Width;
            double bH = !double.IsNaN(border.Height) && border.Height > 0 ? border.Height : border.Bounds.Height;
            if (bW <= 0 || bH <= 0) return;

            double horizontalPadding = border.Padding.Left + border.Padding.Right +
                                       border.BorderThickness.Left + border.BorderThickness.Right + 4;
            double verticalPadding = border.Padding.Top + border.Padding.Bottom +
                                     border.BorderThickness.Top + border.BorderThickness.Bottom + 4;
            textBox.Width = Math.Max(20, bW - horizontalPadding);
            textBox.MaxHeight = Math.Max(20, bH - verticalPadding);
            textBox.Height = double.NaN;
            textBox.TextWrapping = TextWrapping.Wrap;
            textBox.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            textBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            textBox.TextAlignment = Avalonia.Media.TextAlignment.Center;
            textBox.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
            textBox.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        }

        internal static double MeasureTextHeight(TextBox source, double fontSize, double availableWidth)
        {
            string raw = source.Text ?? "";
            string text = raw.EndsWith("\n") || raw.EndsWith("\r") ? raw + " " : raw;
            if (string.IsNullOrEmpty(text)) text = " ";

            var probe = GetMeasurementProbe();
            probe.Text = text;
            probe.FontSize = fontSize;
            probe.FontWeight = source.FontWeight;
            probe.FontStyle = source.FontStyle;
            probe.FontFamily = source.FontFamily;
            probe.TextWrapping = TextWrapping.Wrap;
            probe.FlowDirection = source.FlowDirection;
            probe.MaxWidth = availableWidth;

            probe.InvalidateMeasure();
            probe.Measure(new AvaloniaSize(availableWidth, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }

        internal static void FitTextBoxToBorder(Border border, TextBox textBox)
        {
            if (border == null || textBox == null) return;
            double bW = !double.IsNaN(border.Width) && border.Width > 0 ? border.Width : border.Bounds.Width;
            double bH = !double.IsNaN(border.Height) && border.Height > 0 ? border.Height : border.Bounds.Height;
            if (bW <= 0 || bH <= 0) return;

            SetTextBoxBoundsToBorder(border, textBox);

            double horizontalPadding = border.Padding.Left + border.Padding.Right +
                                       border.BorderThickness.Left + border.BorderThickness.Right +
                                       textBox.Padding.Left + textBox.Padding.Right + 8;
            double verticalPadding = border.Padding.Top + border.Padding.Bottom +
                                     border.BorderThickness.Top + border.BorderThickness.Bottom +
                                     textBox.Padding.Top + textBox.Padding.Bottom + 8;

            double availableWidth = Math.Max(20, bW - horizontalPadding);
            double availableHeight = Math.Max(16, bH - verticalPadding);

            string text = textBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                double emptyFontSize = Math.Clamp(Math.Round(availableHeight * 0.65, 1), 10, 300);
                textBox.FontSize = emptyFontSize;
                if (textBox.Tag is TextAnnotationProperties props) props.PreferredFontSize = emptyFontSize;
                else textBox.Tag = new TextAnnotationProperties { PreferredFontSize = emptyFontSize };
                return;
            }

            double min = 8;
            double max = Math.Clamp(Math.Round(availableHeight * 0.80, 1), min, 300);
            double low = min;
            double high = max;

            for (int i = 0; i < 14; i++)
            {
                double mid = (low + high) / 2.0;
                if (DoesTextFit(textBox, mid, availableWidth, availableHeight))
                    low = mid;
                else
                    high = mid;
            }

            double chosenSize = Math.Round(low, 1);
            textBox.FontSize = chosenSize;
            if (textBox.Tag is TextAnnotationProperties tap) tap.PreferredFontSize = chosenSize;
            else textBox.Tag = new TextAnnotationProperties { PreferredFontSize = chosenSize };
        }

        internal static bool DoesTextFit(TextBox source, double fontSize, double availableWidth, double availableHeight)
        {
            string raw = source.Text ?? "";
            string text = raw.EndsWith("\n") || raw.EndsWith("\r") ? raw + " " : raw;
            if (string.IsNullOrEmpty(text)) text = " ";

            var probe = GetMeasurementProbe();
            probe.Text = text;
            probe.FontSize = fontSize;
            probe.FontWeight = source.FontWeight;
            probe.FontStyle = source.FontStyle;
            probe.FontFamily = source.FontFamily;
            probe.TextWrapping = TextWrapping.Wrap;
            probe.FlowDirection = source.FlowDirection;
            probe.MaxWidth = availableWidth;

            probe.InvalidateMeasure();
            probe.Measure(new AvaloniaSize(availableWidth, double.PositiveInfinity));
            return probe.DesiredSize.Width <= availableWidth + 1.0 && probe.DesiredSize.Height <= availableHeight + 1.0;
        }

        private void CopyCommonState(AvaloniaControl source, AvaloniaControl clone)
        {
            Canvas.SetLeft(clone, Canvas.GetLeft(source));
            Canvas.SetTop(clone, Canvas.GetTop(source));
            clone.Width = source.Width;
            clone.Height = source.Height;
            clone.Tag = CloneControlTag(source.Tag);
            clone.ZIndex = source.ZIndex;
            clone.RenderTransform = source.RenderTransform;
            clone.RenderTransformOrigin = source.RenderTransformOrigin;
            clone.Cursor = source.Cursor;
            foreach (var className in source.Classes)
            {
                if (string.IsNullOrEmpty(className) || className[0] == ':')
                {
                    continue;
                }

                clone.Classes.Add(className);
            }
        }

        private static object CloneControlTag(object tag)
        {
            if (tag is ArrowProperties arrow)
            {
                return new ArrowProperties { Start = arrow.Start, End = arrow.End };
            }

            if (tag is PixelateProperties pixelate)
            {
                return new PixelateProperties { Strength = pixelate.Strength };
            }

            if (tag is HighlightProperties highlight)
            {
                return new HighlightProperties { Color = highlight.Color };
            }

            if (tag is TextAnnotationProperties text)
            {
                return new TextAnnotationProperties { PreferredFontSize = text.PreferredFontSize };
            }

            return tag;
        }

        
        private readonly EditorWindowFitter _windowFitter = new();
        private bool _autoFitPending
        {
            get => _windowFitter.IsPending;
            set => _windowFitter.IsPending = value;
        }

        private void PositionWindow(RECT captureRect)
        {
            var screens = Screens.All.ToList();
            var targetScreen = Screens.Primary;
            long maxArea = -1;
            foreach (var screen in screens)
            {
                var intersect = captureRect.Intersect(RECT.FromXYWH(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height));
                if (!intersect.IsEmpty) { long area = (long)intersect.Width * intersect.Height; if (area > maxArea) { maxArea = area; targetScreen = screen; } }
            }

            double scaling = targetScreen.Scaling;
            double maxAllowedWidth = (targetScreen.WorkingArea.Width - 24) / scaling;
            double maxAllowedHeight = (targetScreen.WorkingArea.Height - 24) / scaling;

            var (targetWidth, targetHeight) = EditorWindowFitter.CalculateInitialSize(
                _document.Width, _document.Height, _zoomFactor, maxAllowedWidth, maxAllowedHeight, FreeSnipFrameThickness);

            Width = targetWidth; Height = targetHeight;

            Position = EditorWindowFitter.CalculateCenteredPosition(targetScreen.WorkingArea, Width, Height, scaling, _cascadeOffset++);

            _windowFitter.Configure(targetScreen.WorkingArea, scaling, maxAllowedWidth, maxAllowedHeight);
        }

        private void AutoFitWindowToContent()
        {
            var scroller = _editorScrollViewer;
            if (_image == null || scroller == null || scroller.Viewport.Width <= 1 || scroller.Viewport.Height <= 1)
            {
                if (++_windowFitter.Retries < EditorWindowFitter.MaxRetries) Dispatcher.UIThread.Post(AutoFitWindowToContent, DispatcherPriority.Loaded);
                return;
            }

            if (!_windowFitter.TryFit(Width, Height, scroller.Viewport.Width, scroller.Viewport.Height, _document.Width, _document.Height, _zoomFactor, out var newSize, out var newPos))
            {
                _initialCenterPending = true;
                CenterImageInScrollViewer();
                return;
            }

            Width = newSize.Width;
            Height = newSize.Height;
            Position = newPos;
            _initialCenterPending = true;
            Dispatcher.UIThread.Post(CenterImageInScrollViewer, DispatcherPriority.Render);
        }

        private void RefitWindowToImage()
        {
            var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            if (screen == null) return;
            double scaling = screen.Scaling;
            double maxW = (screen.WorkingArea.Width - 24) / scaling;
            double maxH = (screen.WorkingArea.Height - 24) / scaling;
            _windowFitter.Configure(screen.WorkingArea, scaling, maxW, maxH);
            Dispatcher.UIThread.Post(AutoFitWindowToContent, DispatcherPriority.Loaded);
        }

        private void ReleaseImageResources()
        {
            VerifyImageOwnerThread(nameof(ReleaseImageResources));
            _zoomEngine.StopAnimation();
            _zoomEngine.ClearFocalPoints();
            RemovePreviewShape();
            RemoveUserAnnotations();
            _document.Clear();
            _displayBitmap?.Dispose();
            _displayBitmap = null;
            _historyManager.Clear();
        }

        private T WithImage<T>(Func<ImageSharpImage, T> work) where T : class => _document.WithImage(work);

        private bool MutateImage(Action<ImageSharpImage> work) => _document.MutateImage(work);

        private void SetImageUnderLock(ImageSharpImage image)
        {
            VerifyImageOwnerThread(nameof(SetImageUnderLock));
            _document.SetImage(image);
        }

        private static void VerifyImageOwnerThread(string operation)
        {
            if (Dispatcher.UIThread.CheckAccess()) return;
            Log.Fatal("[FAIL] " + operation + " ran off the UI thread. The editor image may only be replaced or released from the UI thread.");
        }

        private bool HasImage => _document.HasImage;

        private bool TryGetImageSize(out int width, out int height) => _document.TryGetImageSize(out width, out height);
        private void InitializeComponent() { AvaloniaXamlLoader.Load(this); freesnip.foundation.core.UiLayoutDirection.Apply(this); }
        
        
        private void OnZoomInClick(object sender, RoutedEventArgs e) { SetFocalPointToCenter(); SmoothZoomTowards(_targetZoom * ZoomStepMultiplier); ShowZoomHintDebounced(); }
        private void OnZoomOutClick(object sender, RoutedEventArgs e) { SetFocalPointToCenter(); SmoothZoomTowards(_targetZoom / ZoomStepMultiplier); ShowZoomHintDebounced(); }
        private void OnZoomResetButtonClick(object sender, RoutedEventArgs e) { SetFocalPointToCenter(); SmoothZoomTowards(1.0); ShowZoomHintDebounced(); }
        private void OnCounterDoubleTapped(object sender, TappedEventArgs e) { _counterValue = 1; _ghostSettingsDirty = true; }
        private void OnCounterResetClick(object sender, RoutedEventArgs e) { _counterValue = 1; _ghostSettingsDirty = true; OverlayHelper.ShowLightToast("COUNTER RESET TO 1", this); }
        private void OnCounterUpClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl is Border border && border.Child is TextBlock tb && GetToolFromControl(_selectedControl) == EditorTool.Counter)
            {
                if (int.TryParse(tb.Text, out int val))
                {
                    val++;
                    tb.Text = val.ToString();
                    _counterValue = val + 1;
                    _ghostSettingsDirty = true;
                    OverlayHelper.ShowLightToast($"COUNTER: {val}", this);
                }
            }
            else
            {
                _counterValue++;
                _ghostSettingsDirty = true;
                OverlayHelper.ShowLightToast($"NEXT COUNTER: {_counterValue}", this);
            }
        }
        private void OnCounterDownClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl is Border border && border.Child is TextBlock tb && GetToolFromControl(_selectedControl) == EditorTool.Counter)
            {
                if (int.TryParse(tb.Text, out int val))
                {
                    val--;
                    tb.Text = val.ToString();
                    _counterValue = val + 1;
                    _ghostSettingsDirty = true;
                    OverlayHelper.ShowLightToast($"COUNTER: {val}", this);
                }
            }
            else
            {
                _counterValue = Math.Max(1, _counterValue - 1);
                _ghostSettingsDirty = true;
                OverlayHelper.ShowLightToast($"NEXT COUNTER: {_counterValue}", this);
            }
        }

        private void OnBottomToolbarPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            var scroller = _bottomToolbarScrollViewer;
            if (scroller == null) return;
            double delta = Math.Abs(e.Delta.X) > 0.01 ? e.Delta.X : e.Delta.Y;
            if (Math.Abs(delta) < 0.001) return;
            double step = 60.0;
            double newX = Math.Clamp(scroller.Offset.X - (delta * step), 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width));
            scroller.Offset = new Avalonia.Vector(newX, scroller.Offset.Y);
            e.Handled = true;
        }

        private void OnCanvasPointerPressed(object sender, PointerPressedEventArgs e)
        {
            HideCropModePopup();

            if (IsEditorOperationInProgress || _canvas == null) return;
            try
            {

            var pos = e.GetPosition(_canvas);
            
            if (_isOcrInteractiveMode)
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    _ocrSelectionStartIndex = FindClosestOcrWordIndex(pos);
                    _ocrSelectionEndIndex = _ocrSelectionStartIndex;
                    UpdateOcrSelectionVisuals(true);
                    e.Handled = true;
                }
                return;
            }

            bool isLeftPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
            bool isMiddlePressed = e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed;
            bool isSpacePan = _isSpaceDown && isLeftPressed;

            if (isMiddlePressed || isSpacePan)
            {
                var panScrollViewer = _editorScrollViewer;
                if (panScrollViewer != null)
                {
                    _isPanning = true;
                    _panStartPoint = e.GetPosition(this);
                    _panStartOffset = panScrollViewer.Offset;
                    e.Pointer.Capture(_canvas);
                    SetEditorCursor(PanHeldCursor);
                    HideToolGhost();
                    HideSnapGuides();
                    HideVectorInfo();
                }
                e.Handled = true;
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { OnWindowKeyDown(this, new KeyEventArgs { Key = Key.Escape }); e.Handled = true; return; }

            var clicked = FindSelectableControlAt(pos);
            if (_selectedControl != null && !ReferenceEquals(clicked, _selectedControl))
            {
                UngroupMultiSelectGroup(_selectedControl);
            }

            if (_currentTool != EditorTool.None && IsPastedImageControl(clicked))
            {
                clicked = null;
            }

            if (clicked != null)
            {
                if (!ReferenceEquals(clicked, _selectedControl))
                {
                    FinalizeSelectedPasteObject();
                }

                if (_currentTool != EditorTool.None)
                {
                    SetCurrentTool(EditorTool.None);
                    SyncToolButtonSelection();
                }

                _selectedControl = clicked; 
                if (clicked is Border b && b.Child is TextBox tb)
                {
                    if (e.ClickCount == 2)
                    {
                        EnableTextBoxEditing(b, tb);
                        _isDraggingSelected = false;
                        UpdateSelectionIndicator();
                        e.Handled = true;
                        return;
                    }

                    double bLeft = Canvas.GetLeft(b);
                    if (double.IsNaN(bLeft)) bLeft = b.Bounds.X;
                    double bTop = Canvas.GetTop(b);
                    if (double.IsNaN(bTop)) bTop = b.Bounds.Y;
                    double bWidth = b.Bounds.Width > 0 ? b.Bounds.Width : b.Width;
                    double bHeight = b.Bounds.Height > 0 ? b.Bounds.Height : b.Height;
                    double relX = pos.X - bLeft;
                    double relY = pos.Y - bTop;
                    double edgeThreshold = Math.Max(8, Math.Max(b.BorderThickness.Left, b.Padding.Left) + 4);
                    bool isBorderFrameGrab = relX <= edgeThreshold || relX >= bWidth - edgeThreshold ||
                                             relY <= edgeThreshold || relY >= bHeight - edgeThreshold ||
                                             ReferenceEquals(e.Source, b);

                    if (!tb.IsReadOnly && !isBorderFrameGrab)
                    {
                        _isDraggingSelected = false;
                        UpdateSelectionIndicator();
                        return;
                    }
                }

                _canvas?.Focus();
                e.Pointer.Capture(_canvas);
                _isDraggingSelected = true; 
                _dragUndoCaptured = false; 
                _dragLastPoint = pos; 
                _dragUnsnappedLeft = Canvas.GetLeft(_selectedControl);
                if (double.IsNaN(_dragUnsnappedLeft)) _dragUnsnappedLeft = _selectedControl.Bounds.X;
                _dragUnsnappedTop = Canvas.GetTop(_selectedControl);
                if (double.IsNaN(_dragUnsnappedTop)) _dragUnsnappedTop = _selectedControl.Bounds.Y;
                UpdateSelectionIndicator(); 
                UpdateHoverIndicator(null);
                HideToolGhost();
                HideSnapHint();
                bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                UpdateMagnetButtonState(altPressed);
                RefreshActiveDragAltHint(altPressed);
                
                IBrush existingBrush = null;
                if (_selectedControl is Avalonia.Controls.Shapes.Shape s) { 
                    _currentThickness = s.StrokeThickness; 
                    existingBrush = s.Stroke ?? s.Fill;
                }
                else if (_selectedControl is Canvas group && group.Tag is ArrowProperties)
                {
                    var arrowLine = group.Children.OfType<Avalonia.Controls.Shapes.Line>().FirstOrDefault();
                    var arrowHead = group.Children.OfType<Avalonia.Controls.Shapes.Polygon>().FirstOrDefault();
                    if (arrowLine != null)
                    {
                        _currentThickness = arrowLine.StrokeThickness;
                        existingBrush = arrowLine.Stroke ?? arrowHead?.Fill;
                    }
                }
                else if (_selectedControl is Border bShape) existingBrush = bShape.BorderBrush ?? bShape.Background;
                else if (_selectedControl is TextBlock tbBlock) existingBrush = tbBlock.Foreground;

                if (existingBrush is SolidColorBrush scb)
                {
                    UpdateCurrentColorDisplay(scb.Color);
                }

                SetEditorCursor(HandCursor);
                UpdateThicknessPanelVisibility();
                e.Handled = true;
                return;
            }

            if (_currentTool != EditorTool.None)
            {
                FinalizeSelectedPasteObject();
                _selectedControl = null;
                UpdateSelectionIndicator();
                UpdateHoverIndicator(null);
                HideVectorInfo();
                
                var toolEvent = CreateToolPointerEvent(e, pos);
                _canvas?.Focus();
                _activeToolHandler.OnPointerPressed(_toolContext, toolEvent);
                e.Pointer.Capture(_canvas);
                return;
            }
            
            FinalizeSelectedPasteObject();
            _selectedControl = null; 
            UpdateSelectionIndicator(); 
            UpdateHoverIndicator(null);
            _canvas?.Focus();

            if (GetUserAnnotations().Count == 0)
            {
                var panScrollViewer = _editorScrollViewer;
                if (panScrollViewer != null)
                {
                    _isPanning = true;
                    _panStartPoint = e.GetPosition(this);
                    _panStartOffset = panScrollViewer.Offset;
                    e.Pointer.Capture(_canvas);
                    SetEditorCursor(PanHeldCursor);
                    HideToolGhost();
                    HideSnapGuides();
                    HideVectorInfo();
                }
                e.Handled = true;
                return;
            }

            _isRubberBanding = true;
            _rubberBandStart = pos;
            _rubberBandVisual = new Avalonia.Controls.Shapes.Rectangle 
            { 
                Stroke = Brushes.LightBlue, 
                StrokeThickness = 1.5, 
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
                Fill = new SolidColorBrush(AvaloniaColor.FromArgb(40, 0, 120, 255)),
                IsHitTestVisible = false,
                ZIndex = 9999
            };
            Canvas.SetLeft(_rubberBandVisual, pos.X);
            Canvas.SetTop(_rubberBandVisual, pos.Y);
            _canvas.Children.Add(_rubberBandVisual);
            e.Pointer.Capture(_canvas);
            
            SetEditorCursor(CrossCursor);
            UpdateThicknessPanelVisibility();
            }
            catch (Exception ex)
            {
                Log.Error("OnCanvasPointerPressed failed", ex);
            }
        }

        private double PointToLineDistance(AvaloniaPoint p, AvaloniaPoint a, AvaloniaPoint b)
        {
            double l2 = Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);
            if (l2 == 0) return Distance(p, a);
            double t = ((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / l2;
            t = Math.Max(0, Math.Min(1, t));
            return Distance(p, new AvaloniaPoint(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
        }

        private static double Distance(AvaloniaPoint a, AvaloniaPoint b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double CanvasLeft(AvaloniaControl control)
        {
            double value = Canvas.GetLeft(control);
            return double.IsNaN(value) ? 0 : value;
        }

        private static double CanvasTop(AvaloniaControl control)
        {
            double value = Canvas.GetTop(control);
            return double.IsNaN(value) ? 0 : value;
        }

        private static bool IsArrowControl(AvaloniaControl control)
        {
            return control is Canvas canvas && canvas.Tag is ArrowProperties;
        }

        private static bool IsVectorControl(AvaloniaControl control)
        {
            return control is Avalonia.Controls.Shapes.Line || IsArrowControl(control);
        }

        private static bool IsPixelateControl(AvaloniaControl control)
        {
            return control is Avalonia.Controls.Shapes.Rectangle rectangle && rectangle.Tag is PixelateProperties;
        }

        private static bool IsHighlightControl(AvaloniaControl control)
        {
            return control is Avalonia.Controls.Shapes.Rectangle rectangle && rectangle.Tag is HighlightProperties;
        }

        private static bool IsPastedImageControl(AvaloniaControl control)
        {
            string tag = control?.Tag?.ToString();
            return control is Avalonia.Controls.Image && (tag == "PasteObject" || tag == "PastedImage");
        }

        private void FinalizeSelectedPasteObject()
        {
            if (_selectedControl?.Tag?.ToString() == "PasteObject")
            {
                _selectedControl.Tag = "PastedImage";
            }
        }

        private AvaloniaPoint ApplyMagneticSnap(AvaloniaControl control, double left, double top, bool altSnapBypass = false)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            
            if (altSnapBypass || !config.MagneticSnappingEnabled || !TryGetControlBounds(control, out var bounds))
            {
                HideSnapGuides();
                return new AvaloniaPoint(left, top);
            }

            if (IsVectorControl(control) && TryGetVectorAbsolutePoints(control, out var vStart, out var vEnd))
            {
                double curL = Canvas.GetLeft(control);
                if (double.IsNaN(curL)) curL = control.Bounds.X;
                double curT = Canvas.GetTop(control);
                if (double.IsNaN(curT)) curT = control.Bounds.Y;
                double dX = left - curL;
                double dY = top - curT;

                var pStart = new AvaloniaPoint(vStart.X + dX, vStart.Y + dY);
                var pEnd = new AvaloniaPoint(vEnd.X + dX, vEnd.Y + dY);
                var pMid = new AvaloniaPoint((pStart.X + pEnd.X) / 2.0, (pStart.Y + pEnd.Y) / 2.0);

                var vectorTargets = BuildVectorSnapTargets(control);
                var keypoints = new List<AvaloniaPoint> { pEnd, pStart, pMid };

                if (VectorSnappingEngine.TrySnapKeypointsToTargets(keypoints, vectorTargets, out var snapDx, out var snapDy, out _, out var snappedTarget))
                {
                    _activeSnapGuides.Clear();
                    if (_image != null)
                    {
                        _activeSnapGuides.Add(new SnapGuideInfo(false, snappedTarget.X, 0, _image.Height));
                        _activeSnapGuides.Add(new SnapGuideInfo(true, snappedTarget.Y, 0, _image.Width));
                    }
                    ShowSnapGuides();
                    return new AvaloniaPoint(left + snapDx, top + snapDy);
                }

                var shapeTargets = BuildShapeSnapTargets(control);
                bool snappedX = VectorSnappingEngine.TrySnapKeypointSingleAxis(false, keypoints, shapeTargets, out double axisDx, out var snapX);
                bool snappedY = VectorSnappingEngine.TrySnapKeypointSingleAxis(true, keypoints, shapeTargets, out double axisDy, out var snapY);

                _activeSnapGuides.Clear();
                if (_image != null)
                {
                    if (snappedX) _activeSnapGuides.Add(new SnapGuideInfo(false, snapX.Coordinate, 0, _image.Height));
                    if (snappedY) _activeSnapGuides.Add(new SnapGuideInfo(true, snapY.Coordinate, 0, _image.Width));
                }

                if (snappedX || snappedY)
                {
                    ShowSnapGuides();
                    return new AvaloniaPoint(left + (snappedX ? axisDx : 0), top + (snappedY ? axisDy : 0));
                }

                HideSnapGuides();
                return new AvaloniaPoint(left, top);
            }

            bool isRound = control is Avalonia.Controls.Shapes.Ellipse ||
                           GetToolFromControl(control) == EditorTool.Counter ||
                           GetToolFromControl(control) == EditorTool.Emoji;

            if (isRound)
            {
                var moving = new Rect(left, top, bounds.Width, bounds.Height);
                var circlePoints = new List<AvaloniaPoint>(9);
                VectorSnappingEngine.GetCircleKeypoints(moving, circlePoints);
                var vectorTargets = BuildVectorSnapTargets(control);

                if (VectorSnappingEngine.TrySnapKeypointsToTargets(circlePoints, vectorTargets, out var snapDx, out var snapDy, out _, out var snappedTarget))
                {
                    _activeSnapGuides.Clear();
                    if (_image != null)
                    {
                        _activeSnapGuides.Add(new SnapGuideInfo(false, snappedTarget.X, 0, _image.Height));
                        _activeSnapGuides.Add(new SnapGuideInfo(true, snappedTarget.Y, 0, _image.Width));
                    }
                    ShowSnapGuides();
                    return new AvaloniaPoint(left + snapDx, top + snapDy);
                }

                if (TrySnapRectToBoundsTargets(control, moving, out snapDx, out snapDy, _activeSnapGuides))
                {
                    ShowSnapGuides();
                    return new AvaloniaPoint(left + snapDx, top + snapDy);
                }

                HideSnapGuides();
                return new AvaloniaPoint(left, top);
            }

            var rectMoving = new Rect(left, top, bounds.Width, bounds.Height);
            if (TrySnapRectToBoundsTargets(control, rectMoving, out var rSnapDx, out var rSnapDy, _activeSnapGuides))
            {
                ShowSnapGuides();
                return new AvaloniaPoint(left + rSnapDx, top + rSnapDy);
            }

            HideSnapGuides();
            return new AvaloniaPoint(left, top);
        }

        private Rect ApplyResizeMagneticSnap(AvaloniaControl control, Rect raw, bool altSnapBypass)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (altSnapBypass || !config.MagneticSnappingEnabled || _image == null)
            {
                HideSnapGuides();
                return raw;
            }

            _activeSnapGuides.Clear();
            var targets = BuildShapeSnapTargets(control);
            bool dragLeftEdge = _resizeHandleIndex == 0 || _resizeHandleIndex == 3;
            bool dragTopEdge = _resizeHandleIndex == 0 || _resizeHandleIndex == 1;

            double l = raw.Left, t = raw.Top, r = raw.Right, b = raw.Bottom;
            if (TrySnapResizeEdge(dragLeftEdge ? l : r, targets, vertical: false, out double snapX))
            {
                if (dragLeftEdge) l = snapX; else r = snapX;
                _activeSnapGuides.Add(new SnapGuideInfo(false, snapX, 0, _image.Height));
            }
            if (TrySnapResizeEdge(dragTopEdge ? t : b, targets, vertical: true, out double snapY))
            {
                if (dragTopEdge) t = snapY; else b = snapY;
                _activeSnapGuides.Add(new SnapGuideInfo(true, snapY, 0, _image.Width));
            }

            if (_activeSnapGuides.Count > 0) ShowSnapGuides(); else HideSnapGuides();

            
            if (r - l < 10) { if (dragLeftEdge) l = r - 10; else r = l + 10; }
            if (b - t < 10) { if (dragTopEdge) t = b - 10; else b = t + 10; }
            return new Rect(l, t, r - l, b - t);
        }

        private static bool TrySnapResizeEdge(double coordinate, List<ShapeSnapTarget> targets, bool vertical, out double snapped)
            => VectorSnappingEngine.TrySnapResizeEdge(coordinate, targets, vertical, out snapped);

        private static bool IsStampTool(EditorTool tool) => tool is EditorTool.Counter or EditorTool.Emoji;

        private List<ShapeSnapTarget> BuildShapeSnapTargets(AvaloniaControl moving)
        {
            var targets = _shapeSnapScratch;
            targets.Clear();

            if (_image != null)
            {
                double w = _image.Width;
                double h = _image.Height;
                targets.Add(new ShapeSnapTarget(new Rect(0, 0, w, h)));
                targets.Add(new ShapeSnapTarget(new Rect(w / 2, 0, 0, h), isImageMiddle: true));
                targets.Add(new ShapeSnapTarget(new Rect(0, h / 2, w, 0), isImageMiddle: true));
            }

            var annotations = RentAnnotationBuffer(out bool pooled);
            try
            {
                CollectUserAnnotations(annotations);
                for (int i = 0; i < annotations.Count; i++)
                {
                    var control = annotations[i];
                    if (ReferenceEquals(control, moving) || !TryGetControlBounds(control, out var bounds)) continue;
                    targets.Add(new ShapeSnapTarget(bounds));
                }
            }
            finally
            {
                ReturnAnnotationBuffer(pooled);
            }

            return targets;
        }

        private bool TrySnapRectToBoundsTargets(AvaloniaControl moving, Rect movingBounds, out double snapDx, out double snapDy, List<SnapGuideInfo> guides)
        {
            snapDx = 0;
            snapDy = 0;
            guides?.Clear();
            if (_image == null) return false;

            var targets = BuildShapeSnapTargets(moving);
            bool snappedX = TrySnapAxis(vertical: false, movingBounds, targets, out snapDx, out var snapX);
            bool snappedY = TrySnapAxis(vertical: true, movingBounds, targets, out snapDy, out var snapY);

            if (guides == null) return snappedX || snappedY;

            
            if (snappedX) guides.Add(new SnapGuideInfo(false, snapX.Coordinate, 0, _image.Height));
            if (snappedY) guides.Add(new SnapGuideInfo(true, snapY.Coordinate, 0, _image.Width));

            
            if ((snappedX && snapX.IsMiddle) || (snappedY && snapY.IsMiddle))
            {
                AddGuideOnce(guides, new SnapGuideInfo(false, _image.Width / 2, 0, _image.Height));
                AddGuideOnce(guides, new SnapGuideInfo(true, _image.Height / 2, 0, _image.Width));
            }

            return snappedX || snappedY;
        }

        private static void AddGuideOnce(List<SnapGuideInfo> guides, SnapGuideInfo guide)
        {
            for (int i = 0; i < guides.Count; i++)
            {
                if (guides[i].Horizontal == guide.Horizontal && Math.Abs(guides[i].Coordinate - guide.Coordinate) < 0.5) return;
            }
            guides.Add(guide);
        }

        private static bool TrySnapAxis(bool vertical, Rect moving, List<ShapeSnapTarget> targets, out double delta, out AxisSnapResult snapped)
            => VectorSnappingEngine.TrySnapAxis(vertical, moving, targets, out delta, out snapped);

        private AvaloniaPoint ApplyStampSnap(EditorTool tool, AvaloniaPoint pos)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (!config.MagneticSnappingEnabled || !TryGetStampSnapRect(tool, pos, out var rect))
            {
                HideSnapGuides();
                return pos;
            }

            if (TrySnapRectToBoundsTargets(null, rect, out var dx, out var dy, _activeSnapGuides))
            {
                ShowSnapGuides();
                return new AvaloniaPoint(pos.X + dx, pos.Y + dy);
            }

            HideSnapGuides();
            return pos;
        }

        private bool TryGetStampSnapRect(EditorTool tool, AvaloniaPoint pos, out Rect rect)
        {
            rect = default;
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            switch (tool)
            {
                case EditorTool.Counter:
                {
                    double size = config.LastCounterSize > 0 ? config.LastCounterSize : 42;
                    rect = new Rect(pos.X - size / 2, pos.Y - size / 2, size, size);
                    return true;
                }
                case EditorTool.Emoji:
                {
                    
                    double size = Math.Max(20, config.LastEmojiSize);
                    rect = new Rect(pos.X - 16, pos.Y - 16, size, size);
                    return true;
                }
                case EditorTool.Text:
                    rect = new Rect(pos.X, pos.Y, 140, 36);
                    return true;
                default:
                    return false;
            }
        }

        private void ShowSnapGuides()
        {
            if (_snapGuideLayer == null) return;

            while (_snapGuideLines.Count < _activeSnapGuides.Count)
            {
                var line = new Avalonia.Controls.Shapes.Line
                {
                    Stroke = SnapGuideBrush,
                    StrokeLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                _snapGuideLines.Add(line);
                _snapGuideLayer.Children.Add(line);
            }
            while (_snapGuideLines.Count > _activeSnapGuides.Count)
            {
                var surplus = _snapGuideLines[_snapGuideLines.Count - 1];
                _snapGuideLines.RemoveAt(_snapGuideLines.Count - 1);
                _snapGuideLayer.Children.Remove(surplus);
            }

            double zoom = Math.Max(0.1, _zoomFactor);
            double thickness = 1.0 / zoom; 

            for (int i = 0; i < _activeSnapGuides.Count; i++)
            {
                var info = _activeSnapGuides[i];
                var line = _snapGuideLines[i];
                line.Stroke = SnapGuideBrush;
                line.StrokeThickness = thickness;
                line.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4.0 / zoom, 4.0 / zoom };
                line.Opacity = 0.55;
                if (info.Horizontal)
                {
                    line.StartPoint = new AvaloniaPoint(info.Start, info.Coordinate);
                    line.EndPoint = new AvaloniaPoint(info.End, info.Coordinate);
                }
                else
                {
                    line.StartPoint = new AvaloniaPoint(info.Coordinate, info.Start);
                    line.EndPoint = new AvaloniaPoint(info.Coordinate, info.End);
                }
            }

            _snapGuideLayer.IsVisible = _activeSnapGuides.Count > 0;
        }

        private void HideSnapGuides()
        {
            _activeSnapGuides.Clear();
            if (_snapGuideLayer != null) _snapGuideLayer.IsVisible = false;
        }

        private static double GetVectorThickness(AvaloniaControl control)
        {
            if (control is Avalonia.Controls.Shapes.Line line) return Math.Max(1, line.StrokeThickness);
            if (control is Canvas group)
            {
                var arrowLine = group.Children.OfType<Avalonia.Controls.Shapes.Line>().FirstOrDefault();
                if (arrowLine != null) return Math.Max(1, arrowLine.StrokeThickness);
            }

            return 1;
        }

        private static double GetArrowPadding(double thickness)
        {
            return 18 + thickness * 2.4;
        }

        private static double HalfControlSize(AvaloniaControl control)
        {
            if (control == null || double.IsNaN(control.Width) || control.Width <= 0) return 7;
            return control.Width / 2;
        }

        internal static bool TryGetVectorAbsolutePoints(AvaloniaControl control, out AvaloniaPoint start, out AvaloniaPoint end)
        {
            start = default;
            end = default;
            double left = CanvasLeft(control);
            double top = CanvasTop(control);

            if (control is Avalonia.Controls.Shapes.Line line)
            {
                start = new AvaloniaPoint(left + line.StartPoint.X, top + line.StartPoint.Y);
                end = new AvaloniaPoint(left + line.EndPoint.X, top + line.EndPoint.Y);
                return true;
            }

            if (control is Canvas group && group.Tag is ArrowProperties props)
            {
                start = props.Start;
                end = props.End;
                return true;
            }

            return false;
        }

        private void SetVectorAbsolutePoints(AvaloniaControl control, AvaloniaPoint start, AvaloniaPoint end)
        {
            if (control is Avalonia.Controls.Shapes.Line line)
            {
                double minX = Math.Min(start.X, end.X);
                double minY = Math.Min(start.Y, end.Y);
                Canvas.SetLeft(line, minX);
                Canvas.SetTop(line, minY);
                line.StartPoint = new AvaloniaPoint(start.X - minX, start.Y - minY);
                line.EndPoint = new AvaloniaPoint(end.X - minX, end.Y - minY);
            }
            else if (control is Canvas group && group.Tag is ArrowProperties)
            {
                UpdateArrowVisuals(group, start, end);
            }
        }

        private AvaloniaPoint ApplyVectorConstraints(AvaloniaPoint proposed, AvaloniaPoint anchor, KeyModifiers modifiers, bool allowTargetSnap = true)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            var targets = allowTargetSnap ? BuildVectorSnapTargets(_selectedControl, proposed) : null;
            return VectorSnappingEngine.ApplyVectorConstraints(proposed, anchor, modifiers, config.MagneticSnappingEnabled, allowTargetSnap, targets);
        }

        private AvaloniaPoint SnapToNearbyTarget(AvaloniaPoint proposed, AvaloniaPoint anchor)
        {
            AvaloniaPoint? bestTarget = null;
            double bestDistance = 8.0;

            var vectorTargets = BuildVectorSnapTargets(_selectedControl, proposed);
            for (int i = 0; i < vectorTargets.Count; i++)
            {
                var target = vectorTargets[i];
                double d = Distance(proposed, target);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestTarget = target;
                }
            }

            return bestTarget ?? proposed;
        }

        private List<AvaloniaPoint> BuildVectorSnapTargets(AvaloniaControl activeControl = null, AvaloniaPoint? activePoint = null)
        {
            var targets = _vectorSnapScratch;
            targets.Clear();
            _closestSnapRecipient = null;
            _closestSnapRecipientBounds = default;
            double closestDist = double.MaxValue;

            const double proximity = 110.0;
            Rect activeBounds = default;
            bool hasActiveBounds = false;
            if (activeControl != null && TryGetControlBounds(activeControl, out activeBounds))
            {
                hasActiveBounds = true;
            }
            else if (activePoint.HasValue)
            {
                activeBounds = new Rect(activePoint.Value.X - 1, activePoint.Value.Y - 1, 2, 2);
                hasActiveBounds = true;
            }

            if (!hasActiveBounds) return targets;

            double activeCenterX = activeBounds.X + activeBounds.Width / 2.0;
            double activeCenterY = activeBounds.Y + activeBounds.Height / 2.0;

            void AddTargetPoint(double x, double y)
            {
                for (int k = 0; k < targets.Count; k++)
                {
                    if (Math.Abs(targets[k].X - x) < 0.5 && Math.Abs(targets[k].Y - y) < 0.5) return;
                }
                targets.Add(new AvaloniaPoint(x, y));
            }

            var annotations = RentAnnotationBuffer(out bool pooled);
            try
            {
                CollectUserAnnotations(annotations);
                for (int i = 0; i < annotations.Count; i++)
                {
                    var annotation = annotations[i];
                    if (ReferenceEquals(annotation, _selectedControl)) continue;
                    if (activeControl != null && ReferenceEquals(annotation, activeControl)) continue;
                    if (!TryGetControlBounds(annotation, out var bounds)) continue;

                    double dx = Math.Max(0, Math.Max(activeBounds.X - bounds.Right, bounds.X - activeBounds.Right));
                    double dy = Math.Max(0, Math.Max(activeBounds.Y - bounds.Bottom, bounds.Y - activeBounds.Bottom));
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist > proximity) continue;

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        _closestSnapRecipient = annotation;
                        _closestSnapRecipientBounds = bounds;
                    }

                    if (TryGetVectorAbsolutePoints(annotation, out var vStart, out var vEnd))
                    {
                        AddTargetPoint(vStart.X, vStart.Y);
                        AddTargetPoint((vStart.X + vEnd.X) / 2.0, (vStart.Y + vEnd.Y) / 2.0);
                        AddTargetPoint(vEnd.X, vEnd.Y);
                        continue;
                    }

                    bool isRound = annotation is Avalonia.Controls.Shapes.Ellipse || GetToolFromControl(annotation) == EditorTool.Counter || GetToolFromControl(annotation) == EditorTool.Emoji;
                    VectorSnappingEngine.GeneratePerimeterPoints(bounds, isRound, activeCenterX, activeCenterY, AddTargetPoint);
                }
            }
            finally
            {
                ReturnAnnotationBuffer(pooled);
            }

            return targets;
        }

        private void ClearSnapTargets()
        {
            _snapDotTargets.Clear();
            _recipientSnapDotTargets.Clear();
            _heldSnapDotTargets.Clear();
            _closestSnapRecipient = null;
            _closestSnapRecipientBounds = default;
            _lastSnappedTargetPoint = null;
        }

        private void RefreshSnapTargetsList(AvaloniaControl activeControl = null, AvaloniaPoint? activePoint = null)
        {
            if (_snapDotsLayer == null)
            {
                _snapDotsLayer = new Canvas { IsHitTestVisible = false, ZIndex = 9998 };
                _canvas.Children.Add(_snapDotsLayer);
            }

            bool isManipulating = (_isDraggingSelected && _selectedControl != null) ||
                                  (_isResizing && _selectedControl != null) ||
                                  _isDraggingStartPoint ||
                                  _isDraggingEndPoint ||
                                  (_isDrawing && _currentTool != EditorTool.FreeDraw) ||
                                  (!_isDraggingSelected && !_isResizing && !_isDrawing && _currentTool != EditorTool.None && _currentTool != EditorTool.FreeDraw);

            if (!isManipulating || _image == null)
            {
                _snapDotsLayer.IsVisible = false;
                ClearSnapTargets();
                return;
            }

            ClearSnapTargets();
            var movingControl = activeControl ?? _selectedControl;
            _recipientSnapDotTargets.AddRange(BuildVectorSnapTargets(movingControl, activePoint));

            if (_isDraggingSelected && movingControl != null && _closestSnapRecipient != null &&
                TryGetControlBounds(movingControl, out var heldBounds))
            {
                if (TryGetVectorAbsolutePoints(movingControl, out var vStart, out var vEnd))
                {
                    _heldSnapDotTargets.Add(vStart);
                    _heldSnapDotTargets.Add(new AvaloniaPoint((vStart.X + vEnd.X) / 2.0, (vStart.Y + vEnd.Y) / 2.0));
                    _heldSnapDotTargets.Add(vEnd);
                }
                else
                {
                    double recCenterX = _closestSnapRecipientBounds.X + _closestSnapRecipientBounds.Width / 2.0;
                    double recCenterY = _closestSnapRecipientBounds.Y + _closestSnapRecipientBounds.Height / 2.0;
                    bool isRound = movingControl is Avalonia.Controls.Shapes.Ellipse ||
                                   GetToolFromControl(movingControl) == EditorTool.Counter ||
                                   GetToolFromControl(movingControl) == EditorTool.Emoji;
                    VectorSnappingEngine.GeneratePerimeterPoints(heldBounds, isRound, recCenterX, recCenterY, (hx, hy) =>
                    {
                        for (int k = 0; k < _heldSnapDotTargets.Count; k++)
                        {
                            if (Math.Abs(_heldSnapDotTargets[k].X - hx) < 0.5 && Math.Abs(_heldSnapDotTargets[k].Y - hy) < 0.5) return;
                        }
                        _heldSnapDotTargets.Add(new AvaloniaPoint(hx, hy));
                    });
                }
            }

            _snapDotTargets.AddRange(_recipientSnapDotTargets);
            _snapDotTargets.AddRange(_heldSnapDotTargets);

            if (_snapDotTargets.Count == 0)
            {
                _snapDotsLayer.IsVisible = false;
                _lastSnappedTargetPoint = null;
                return;
            }

            _snapDotsLayer.IsVisible = true;
            while (_snapDotsLayer.Children.Count < _snapDotTargets.Count)
            {
                var dot = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 5.5,
                    Height = 5.5,
                    IsHitTestVisible = false,
                    Fill = SnapDotDimBrush,
                    Stroke = SnapDotBorderBrush,
                    StrokeThickness = 0.5
                };
                _snapDotsLayer.Children.Add(dot);
            }
            while (_snapDotsLayer.Children.Count > _snapDotTargets.Count)
            {
                _snapDotsLayer.Children.RemoveAt(_snapDotsLayer.Children.Count - 1);
            }

            for (int i = 0; i < _snapDotTargets.Count; i++)
            {
                var dot = (Avalonia.Controls.Shapes.Ellipse)_snapDotsLayer.Children[i];
                Canvas.SetLeft(dot, _snapDotTargets[i].X - 2.75);
                Canvas.SetTop(dot, _snapDotTargets[i].Y - 2.75);
                dot.Fill = SnapDotDimBrush;
                dot.Stroke = SnapDotBorderBrush;
                dot.StrokeThickness = 0.5;
                dot.Width = 5.5;
                dot.Height = 5.5;
                dot.ZIndex = 0;
                dot.IsVisible = true;
            }
        }

        private void HighlightSnapDots(AvaloniaPoint? recipientSnapped, AvaloniaPoint? heldSnapped = null, AvaloniaPoint? cursorPoint = null)
        {
            if (_snapDotsLayer == null || !_snapDotsLayer.IsVisible) return;

            AvaloniaPoint? primarySnap = recipientSnapped ?? heldSnapped;
            if (primarySnap.HasValue)
            {
                if (!_lastSnappedTargetPoint.HasValue || Distance(_lastSnappedTargetPoint.Value, primarySnap.Value) > 3.0)
                {
                    _lastSnappedTargetPoint = primarySnap.Value;
                    TriggerSnapGlowEffect(primarySnap.Value);
                }
            }
            else
            {
                _lastSnappedTargetPoint = null;
            }

            for (int i = 0; i < _snapDotTargets.Count; i++)
            {
                var dot = (Avalonia.Controls.Shapes.Ellipse)_snapDotsLayer.Children[i];
                var target = _snapDotTargets[i];

                bool isRecipientSnapped = recipientSnapped.HasValue && Distance(target, recipientSnapped.Value) < 2.5;
                bool isHeldSnapped = heldSnapped.HasValue && Distance(target, heldSnapped.Value) < 2.5;
                bool isSnapped = isRecipientSnapped || isHeldSnapped;

                double dToCursor = cursorPoint.HasValue ? Distance(target, cursorPoint.Value) : 999.0;
                bool isNear = !isSnapped && dToCursor < 16.0;

                if (isSnapped)
                {
                    dot.IsVisible = true;
                    dot.Fill = new SolidColorBrush(AvaloniaColor.Parse("#00E5FF"));
                    dot.Stroke = Brushes.White;
                    dot.StrokeThickness = 1.5;
                    dot.Width = 9.0;
                    dot.Height = 9.0;
                    Canvas.SetLeft(dot, target.X - 4.5);
                    Canvas.SetTop(dot, target.Y - 4.5);
                    dot.ZIndex = 10;
                }
                else if (isNear)
                {
                    dot.IsVisible = true;
                    dot.Fill = new SolidColorBrush(AvaloniaColor.Parse("#80D8FF"));
                    dot.Stroke = Brushes.White;
                    dot.StrokeThickness = 1.0;
                    dot.Width = 7.0;
                    dot.Height = 7.0;
                    Canvas.SetLeft(dot, target.X - 3.5);
                    Canvas.SetTop(dot, target.Y - 3.5);
                    dot.ZIndex = 5;
                }
                else
                {
                    dot.IsVisible = true;
                    dot.Fill = SnapDotDimBrush;
                    dot.Stroke = SnapDotBorderBrush;
                    dot.StrokeThickness = 0.5;
                    dot.Width = 5.5;
                    dot.Height = 5.5;
                    Canvas.SetLeft(dot, target.X - 2.75);
                    Canvas.SetTop(dot, target.Y - 2.75);
                    dot.ZIndex = 0;
                }
            }
        }

        private void HighlightSnapDot(AvaloniaPoint? snappedPoint, AvaloniaPoint? cursorPoint = null)
            => HighlightSnapDots(snappedPoint, null, cursorPoint);

        private void TriggerSnapGlowEffect(AvaloniaPoint pt)
        {
            if (_canvas == null) return;
            if (_snapHaloLayer == null)
            {
                _snapHaloLayer = new Canvas { IsHitTestVisible = false, ZIndex = 9999 };
                _canvas.Children.Add(_snapHaloLayer);
            }

            var halo = new Avalonia.Controls.Shapes.Ellipse
            {
                IsHitTestVisible = false,
                Stroke = new SolidColorBrush(AvaloniaColor.FromArgb(220, 0, 229, 255)),
                StrokeThickness = 2.0,
                Fill = new SolidColorBrush(AvaloniaColor.FromArgb(35, 0, 229, 255)),
                Width = 8.0,
                Height = 8.0,
                Opacity = 0.95
            };
            Canvas.SetLeft(halo, pt.X - 4.0);
            Canvas.SetTop(halo, pt.Y - 4.0);
            _snapHaloLayer.Children.Add(halo);

            int frame = 0;
            const int totalFrames = 6;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _activeHaloTimers.Add(timer);
            EventHandler handler = null;
            handler = (s, ev) =>
            {
                frame++;
                if (frame >= totalFrames)
                {
                    timer.Tick -= handler;
                    timer.Stop();
                    _activeHaloTimers.Remove(timer);
                    _snapHaloLayer?.Children.Remove(halo);
                    return;
                }

                double progress = (double)frame / totalFrames;
                double easeOut = Math.Sin(progress * Math.PI / 2.0);
                double currentSize = 8.0 + (28.0 - 8.0) * easeOut;
                halo.Width = currentSize;
                halo.Height = currentSize;
                Canvas.SetLeft(halo, pt.X - currentSize / 2.0);
                Canvas.SetTop(halo, pt.Y - currentSize / 2.0);

                halo.Opacity = Math.Max(0.0, 0.95 * (1.0 - progress));
                halo.StrokeThickness = Math.Max(0.5, 2.0 * (1.0 - progress * 0.7));
            };
            timer.Tick += handler;
            timer.Start();
        }

        private (AvaloniaPoint? RecipientPoint, AvaloniaPoint? HeldPoint) FindSnappedTargetsForMovingControl(AvaloniaControl control, bool isSnapped)
        {
            if (!isSnapped || control == null || _snapDotTargets.Count == 0)
                return (null, null);

            if (_heldSnapDotTargets.Count > 0 && _recipientSnapDotTargets.Count > 0)
            {
                if (VectorSnappingEngine.TryFindMatchingSnapPair(_recipientSnapDotTargets, _heldSnapDotTargets, out var pair))
                {
                    return (pair.Recipient, pair.Held);
                }
            }

            if (TryGetControlBounds(control, out var bounds))
            {
                AvaloniaPoint? bestDot = null;
                double minDistance = 4.0;
                for (int i = 0; i < _recipientSnapDotTargets.Count; i++)
                {
                    var target = _recipientSnapDotTargets[i];
                    double d = DistanceToRectPerimeter(target, bounds);
                    if (d < minDistance)
                    {
                        minDistance = d;
                        bestDot = target;
                    }
                }
                return (bestDot, null);
            }

            return (null, null);
        }

        private AvaloniaPoint? FindSnappedTargetForMovingControl(AvaloniaControl control, bool isSnapped)
            => FindSnappedTargetsForMovingControl(control, isSnapped).RecipientPoint;

        private static double DistanceToRectPerimeter(AvaloniaPoint pt, Rect r)
        {
            double clampX = Math.Clamp(pt.X, r.Left, r.Right);
            double clampY = Math.Clamp(pt.Y, r.Top, r.Bottom);

            double dTop = Math.Sqrt((pt.X - clampX) * (pt.X - clampX) + (pt.Y - r.Top) * (pt.Y - r.Top));
            double dBottom = Math.Sqrt((pt.X - clampX) * (pt.X - clampX) + (pt.Y - r.Bottom) * (pt.Y - r.Bottom));
            double dLeft = Math.Sqrt((pt.X - r.Left) * (pt.X - r.Left) + (pt.Y - clampY) * (pt.Y - clampY));
            double dRight = Math.Sqrt((pt.X - r.Right) * (pt.X - r.Right) + (pt.Y - clampY) * (pt.Y - clampY));

            double dCenterH = Math.Sqrt((pt.X - clampX) * (pt.X - clampX) + (pt.Y - r.Center.Y) * (pt.Y - r.Center.Y));
            double dCenterV = Math.Sqrt((pt.X - r.Center.X) * (pt.X - r.Center.X) + (pt.Y - clampY) * (pt.Y - clampY));

            return Math.Min(Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight)), Math.Min(dCenterH, dCenterV));
        }

        private bool TryGetControlBounds(AvaloniaControl control, out Rect bounds)
        {
            bounds = default;
            if (control == null) return false;

            if (TryGetVectorAbsolutePoints(control, out var start, out var end))
            {
                double padding = Math.Max(4, GetVectorThickness(control) / 2);
                double minX = Math.Min(start.X, end.X) - padding;
                double minY = Math.Min(start.Y, end.Y) - padding;
                double maxX = Math.Max(start.X, end.X) + padding;
                double maxY = Math.Max(start.Y, end.Y) + padding;
                bounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                return true;
            }

            if (control is Avalonia.Controls.Shapes.Polyline pl && pl.Points != null && pl.Points.Count > 0)
            {
                double polyLeft = CanvasLeft(pl);
                double polyTop = CanvasTop(pl);
                double minX = pl.Points.Min(p => p.X);
                double minY = pl.Points.Min(p => p.Y);
                double maxX = pl.Points.Max(p => p.X);
                double maxY = pl.Points.Max(p => p.Y);
                bounds = new Rect(polyLeft + minX, polyTop + minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                return true;
            }

            double left = CanvasLeft(control);
            double top = CanvasTop(control);
            double width = double.IsNaN(control.Width) || control.Width <= 0 ? control.Bounds.Width : control.Width;
            double height = double.IsNaN(control.Height) || control.Height <= 0 ? control.Bounds.Height : control.Height;
            if (width <= 0 || height <= 0)
            {
                control.Measure(new AvaloniaSize(double.PositiveInfinity, double.PositiveInfinity));
                if (width <= 0) width = control.DesiredSize.Width;
                if (height <= 0) height = control.DesiredSize.Height;
            }

            if (width <= 0 || height <= 0) return false;

            bounds = new Rect(left, top, width, height);
            return true;
        }

        private bool IsPointerNearControl(AvaloniaPoint pos, AvaloniaControl control)
        {
            if (TryGetVectorAbsolutePoints(control, out var start, out var end))
            {
                double tolerance = Math.Max(VectorHitTolerance, GetVectorThickness(control) + 6);
                if (PointToLineDistance(pos, start, end) <= tolerance) return true;
                if (IsArrowControl(control))
                {
                    double headRadius = Math.Max(tolerance, 16.0);
                    if (Distance(pos, end) <= headRadius) return true;
                }
                return false;
            }

            if (control is Avalonia.Controls.Shapes.Polyline pl && pl.Points != null)
            {
                double left = CanvasLeft(pl);
                double top = CanvasTop(pl);
                for (int i = 0; i < pl.Points.Count - 1; i++)
                {
                    var a = new AvaloniaPoint(pl.Points[i].X + left, pl.Points[i].Y + top);
                    var b = new AvaloniaPoint(pl.Points[i + 1].X + left, pl.Points[i + 1].Y + top);
                    if (PointToLineDistance(pos, a, b) <= VectorHitTolerance) return true;
                }

                return false;
            }

            return TryGetControlBounds(control, out var bounds) && bounds.Inflate(10).Contains(pos);
        }

        private AvaloniaControl FindSelectableControlAt(AvaloniaPoint pos)
        {
            return _canvas.Children
                .OfType<AvaloniaControl>()
                .Where(c => !IsEditorChrome(c) && c.IsHitTestVisible && IsPointerNearControl(pos, c))
                .OrderBy(c => c.ZIndex)
                .LastOrDefault();
        }

        private static void ResetPolygonIndicator(Avalonia.Controls.Shapes.Polygon indicator)
        {
            if (indicator == null) return;
            indicator.IsVisible = false;
            indicator.Points = new Avalonia.Points();
        }

        private void UpdateHoverIndicator(AvaloniaControl hovered)
        {
            _hoveredControl = hovered;
            if (_hoverIndicator == null) return;
            if (hovered == null || ReferenceEquals(hovered, _selectedControl) || !TryGetControlBounds(hovered, out var bounds))
            {
                _hoverIndicator.IsVisible = false;
                if (_circleHoverIndicator != null) _circleHoverIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineHoverIndicator);
                ResetPolygonIndicator(_arrowHoverIndicator);
                return;
            }

            bool isVector = IsVectorControl(hovered);
            bool isRound = hovered is Avalonia.Controls.Shapes.Ellipse ||
                           GetToolFromControl(hovered) == EditorTool.Counter ||
                           GetToolFromControl(hovered) == EditorTool.Emoji;

            if (IsArrowControl(hovered) && TryGetVectorAbsolutePoints(hovered, out var aStart, out var aEnd))
            {
                _hoverIndicator.IsVisible = false;
                if (_circleHoverIndicator != null) _circleHoverIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineHoverIndicator);
                if (_arrowHoverIndicator != null)
                {
                    Canvas.SetLeft(_arrowHoverIndicator, 0);
                    Canvas.SetTop(_arrowHoverIndicator, 0);
                    _arrowHoverIndicator.Points = BuildArrowContourPoints(aStart, aEnd, GetVectorThickness(hovered), padding: 3.5);
                    _arrowHoverIndicator.IsVisible = true;
                }
            }
            else if (isVector && TryGetVectorAbsolutePoints(hovered, out var vStart, out var vEnd))
            {
                _hoverIndicator.IsVisible = false;
                if (_circleHoverIndicator != null) _circleHoverIndicator.IsVisible = false;
                ResetPolygonIndicator(_arrowHoverIndicator);
                if (_lineHoverIndicator != null)
                {
                    Canvas.SetLeft(_lineHoverIndicator, 0);
                    Canvas.SetTop(_lineHoverIndicator, 0);
                    _lineHoverIndicator.Points = BuildLineContourPoints(vStart, vEnd, GetVectorThickness(hovered), padding: 3.5);
                    _lineHoverIndicator.IsVisible = true;
                }
            }
            else if (isRound)
            {
                _hoverIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineHoverIndicator);
                ResetPolygonIndicator(_arrowHoverIndicator);
                if (_circleHoverIndicator != null)
                {
                    _circleHoverIndicator.Width = bounds.Width + 8;
                    _circleHoverIndicator.Height = bounds.Height + 8;
                    Canvas.SetLeft(_circleHoverIndicator, bounds.X - 4);
                    Canvas.SetTop(_circleHoverIndicator, bounds.Y - 4);
                    _circleHoverIndicator.IsVisible = true;
                }
            }
            else
            {
                if (_circleHoverIndicator != null) _circleHoverIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineHoverIndicator);
                ResetPolygonIndicator(_arrowHoverIndicator);
                _hoverIndicator.Width = bounds.Width + 8;
                _hoverIndicator.Height = bounds.Height + 8;
                Canvas.SetLeft(_hoverIndicator, bounds.X - 4);
                Canvas.SetTop(_hoverIndicator, bounds.Y - 4);
                _hoverIndicator.IsVisible = true;
            }
        }

        private void HideVectorInfo()
        {
            if (_vectorInfoPopup != null) _vectorInfoPopup.IsVisible = false;
        }

        private void HideSnapHint() => HideVectorInfo();

        private void UpdateMagnetButtonState(bool altPressed)
        {
            if (_snapToggleBtn == null) return;
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (config != null && config.MagneticSnappingEnabled)
            {
                if (altPressed) _snapToggleBtn.Classes.Remove("selected");
                else if (!_snapToggleBtn.Classes.Contains("selected")) _snapToggleBtn.Classes.Add("selected");
            }
        }

        private void RefreshActiveDragAltHint(bool altPressed)
        {
            if (_isDraggingStartPoint || _isDraggingEndPoint)
            {
                if (TryGetVectorAbsolutePoints(_selectedControl, out var start, out var end))
                {
                    UpdateVectorInfo(start, end, altPressed);
                }
            }
            else if (_isResizing && _selectedControl != null)
            {
                if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var vStart, out var vEnd))
                {
                    UpdateVectorInfo(vStart, vEnd, altPressed);
                }
                else
                {
                    ShowSnapHint(_dragLastPoint, altPressed);
                }
            }
            else if (_isDraggingSelected && _selectedControl != null)
            {
                if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var vStart, out var vEnd))
                {
                    UpdateVectorInfo(vStart, vEnd, altPressed);
                }
                else
                {
                    ShowSnapHint(_dragLastPoint, altPressed);
                }
            }
            else
            {
                HideSnapHint();
                HideVectorInfo();
            }
        }

        private void ShowSnapHint(AvaloniaPoint pos, bool altPressed)
        {
            if (_vectorInfoPopup == null || _vectorInfoText == null) return;
            double safeZoom = Math.Max(0.05, _zoomFactor);
            double invZoom = 1.0 / safeZoom;
            _vectorInfoPopup.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            _vectorInfoPopup.RenderTransform = new ScaleTransform(invZoom, invZoom);

            string hintText;
            double popupWidth;
            double popupHeight = 28.0;

            if (altPressed)
            {
                hintText = "⚡ Snapping OFF";
                _vectorInfoPopup.BorderBrush = new SolidColorBrush(AvaloniaColor.Parse("#FFC107"));
                popupWidth = 140.0;
            }
            else
            {
                hintText = "Hold [Alt] to disable snapping";
                _vectorInfoPopup.BorderBrush = new SolidColorBrush(AvaloniaColor.Parse("#00B4D8"));
                popupWidth = 210.0;
            }

            _vectorInfoText.Width = double.NaN;
            _vectorInfoText.Height = double.NaN;
            _vectorInfoText.Text = hintText;

            _vectorInfoPopup.Width = popupWidth;
            _vectorInfoPopup.Height = popupHeight;

            double visualW = popupWidth * invZoom;
            double visualH = popupHeight * invZoom;

            double left = pos.X + 16 * invZoom;
            double top = pos.Y - visualH - 8 * invZoom;
            if (top < 0)
            {
                top = pos.Y + 24 * invZoom;
            }

            if (_image != null)
            {
                left = Math.Clamp(left, 0, Math.Max(0, _image.Width - visualW));
                top = Math.Clamp(top, 0, Math.Max(0, _image.Height - visualH));
            }
            Canvas.SetLeft(_vectorInfoPopup, left);
            Canvas.SetTop(_vectorInfoPopup, top);
            _vectorInfoPopup.IsVisible = true;
        }

        private void UpdateVectorInfo(AvaloniaPoint start, AvaloniaPoint end, bool altPressed = false)
        {
            if (_vectorInfoPopup == null || _vectorInfoText == null) return;

            double safeZoom = Math.Max(0.05, _zoomFactor);
            double invZoom = 1.0 / safeZoom;
            _vectorInfoPopup.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            _vectorInfoPopup.RenderTransform = new ScaleTransform(invZoom, invZoom);

            double length = Distance(start, end);
            double angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180.0 / Math.PI;
            if (angle < 0) angle += 360;

            string infoText;
            if (altPressed)
            {
                infoText = $"{length:0} px  •  {angle:0}°\n⚡ Free Draw (Snapping OFF)";
                _vectorInfoPopup.BorderBrush = new SolidColorBrush(AvaloniaColor.Parse("#FFC107"));
            }
            else
            {
                infoText = $"{length:0} px  •  {angle:0}°\n🧲 Hold [Alt] to disable snap";
                _vectorInfoPopup.BorderBrush = new SolidColorBrush(AvaloniaColor.Parse("#00B4D8"));
            }

            _vectorInfoText.Width = double.NaN;
            _vectorInfoText.Height = double.NaN;
            _vectorInfoText.Text = infoText;

            double popupWidth = 205.0;
            double popupHeight = 44.0;

            _vectorInfoPopup.Width = popupWidth;
            _vectorInfoPopup.Height = popupHeight;

            double visualW = popupWidth * invZoom;
            double visualH = popupHeight * invZoom;

            double left = (start.X + end.X) / 2 + 12 * invZoom;
            double top = (start.Y + end.Y) / 2 - visualH - 8 * invZoom;

            if (top < 0)
            {
                top = (start.Y + end.Y) / 2 + 20 * invZoom;
            }

            if (_image != null)
            {
                left = Math.Clamp(left, 0, Math.Max(0, _image.Width - visualW));
                top = Math.Clamp(top, 0, Math.Max(0, _image.Height - visualH));
            }

            Canvas.SetLeft(_vectorInfoPopup, left);
            Canvas.SetTop(_vectorInfoPopup, top);
            _vectorInfoPopup.IsVisible = true;
        }

        private Avalonia.Controls.Shapes.Rectangle CreatePixelateAnnotation(AvaloniaPoint start, AvaloniaPoint end)
        {
            double left = Math.Min(start.X, end.X);
            double top = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);
            var rectangle = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Tag = new PixelateProperties { Strength = NormalizePixelateStrength(_lastPixelateStrength) }
            };
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            RefreshPixelateAnnotation(rectangle);
            return rectangle;
        }

        private void RefreshPixelateAnnotation(AvaloniaControl control)
        {
            if (!IsPixelateControl(control)) return;
            var task = RenderPixelateAnnotationAsync(control);
            control.Resources["PixelateTask"] = task;
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task RenderPixelateAnnotationAsync(AvaloniaControl control)
        {
            if (_image == null || control is not Avalonia.Controls.Shapes.Rectangle rectangle || rectangle.Tag is not PixelateProperties props) return;
            long requestId = System.Threading.Interlocked.Increment(ref _pixelateCounter);
            rectangle.Resources["PixelateRequest"] = requestId;
            if (rectangle.Fill is ImageBrush previousBrush && previousBrush.Source is IDisposable previousImage)
                previousImage.Dispose();
            rectangle.Fill = Brushes.Black;
            try
            {
                double left = CanvasLeft(rectangle);
                double top = CanvasTop(rectangle);
                double width = double.IsNaN(rectangle.Width) || rectangle.Width <= 0 ? rectangle.Bounds.Width : rectangle.Width;
                double height = double.IsNaN(rectangle.Height) || rectangle.Height <= 0 ? rectangle.Bounds.Height : rectangle.Height;
                var area = ClampImageRectangle(new Rect(left, top, width, height));
                if (area.Width < 1 || area.Height < 1) return;
                int strength = NormalizePixelateStrength(props.Strength);
                strength = Math.Clamp(strength, 1, Math.Max(1, Math.Min(area.Width, area.Height)));
                props.Strength = strength;
                var bitmap = await Task.Run(() => WithImage(img => {
                    try
                    {
                        using var patch = img.Clone(ctx => ctx.Crop(area).BoxBlur((int)Math.Max(1, strength / 2.0f)).Pixelate(strength).BoxBlur((int)Math.Max(1, strength / 4.0f)));
                        return patch.ToAvaloniaBitmap();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Pixelate bitmap rendering failed", ex);
                        return null;
                    }
                }));
                if (rectangle.Resources.TryGetValue("PixelateRequest", out var val) && val is long currentId && currentId != requestId) { bitmap?.Dispose(); return; }
                if (bitmap == null) throw new InvalidOperationException("Pixelation could not be rendered. Please retry before saving.");
                if (rectangle.Fill is ImageBrush oldBrush && oldBrush.Source is IDisposable disp) disp.Dispose();
                rectangle.Fill = new ImageBrush { Source = bitmap, Stretch = Stretch.Fill };
            }
            catch (Exception ex)
            {
                Log.Error("RefreshPixelateAnnotation failed", ex);
                throw;
            }
        }

        private void RefreshAllPixelateAnnotations()
        {
            foreach (var annotation in GetUserAnnotations())
            {
                if (IsPixelateControl(annotation)) RefreshPixelateAnnotation(annotation);
            }
        }

        private Avalonia.Controls.Shapes.Rectangle CreateHighlightAnnotation(AvaloniaPoint start, AvaloniaPoint end)
        {
            double left = Math.Min(start.X, end.X);
            double top = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);
            var rectangle = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Tag = new HighlightProperties { Color = HighlightMarkerColor() }
            };
            rectangle.Classes.Add("highlight");
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            RefreshHighlightAnnotation(rectangle);
            return rectangle;
        }

        private AvaloniaColor HighlightMarkerColor()
        {
            if (_toolBrushes.TryGetValue(EditorTool.Highlight, out var toolBrush) && toolBrush is SolidColorBrush solid)
            {
                return AvaloniaColor.FromArgb(255, solid.Color.R, solid.Color.G, solid.Color.B);
            }
            var fallback = ((SolidColorBrush)HighlightBrushBase).Color;
            return AvaloniaColor.FromArgb(255, fallback.R, fallback.G, fallback.B);
        }

        private void RefreshHighlightAnnotation(AvaloniaControl control)
        {
            if (control is not Avalonia.Controls.Shapes.Rectangle rectangle || rectangle.Tag is not HighlightProperties props) return;
            if (rectangle.Fill is ImageBrush oldBrush && oldBrush.Source is IDisposable oldImage)
                oldImage.Dispose();
            rectangle.Fill = new SolidColorBrush(AvaloniaColor.FromArgb(90, props.Color.R, props.Color.G, props.Color.B));
        }

        private void RefreshAllHighlightAnnotations()
        {
            foreach (var annotation in GetUserAnnotations())
            {
                if (IsHighlightControl(annotation)) RefreshHighlightAnnotation(annotation);
            }
        }

        private void BeginPreviewShape(AvaloniaPoint start, IBrush brush, EditorTool tool)
        {
            RemovePreviewShape();
            _rectDrawDirX = 0;
            _rectDrawDirY = 0;
            _previewControl = tool switch
            {
                EditorTool.Line => new Avalonia.Controls.Shapes.Line { Stroke = brush, StrokeThickness = _currentThickness, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                EditorTool.Arrow => CreateArrowPreview(brush),
                EditorTool.Highlight => new Avalonia.Controls.Shapes.Rectangle { Fill = brush, IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                EditorTool.Rectangle => new Avalonia.Controls.Shapes.Rectangle { Stroke = brush, StrokeThickness = _currentThickness, Fill = _toolFillModes.TryGetValue(EditorTool.Rectangle, out var rfm) && rfm ? brush : Brushes.Transparent, IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                EditorTool.Ellipse => new Avalonia.Controls.Shapes.Ellipse { Stroke = brush, StrokeThickness = _currentThickness, Fill = _toolFillModes.TryGetValue(EditorTool.Ellipse, out var efm) && efm ? brush : Brushes.Transparent, IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                EditorTool.Blur => new Avalonia.Controls.Shapes.Rectangle { Fill = new SolidColorBrush(AvaloniaColor.FromArgb(120, 100, 100, 100)), Stroke = Brushes.DeepSkyBlue, StrokeThickness = Math.Max(1, _currentThickness), IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                EditorTool.Crop => new Avalonia.Controls.Shapes.Rectangle { Stroke = brush, StrokeThickness = Math.Max(1, _currentThickness), Fill = Brushes.Transparent, IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                EditorTool.Text => new Avalonia.Controls.Shapes.Rectangle { Stroke = brush, StrokeThickness = 1.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 }, Fill = _toolFillModes.TryGetValue(EditorTool.Text, out var tfm) && tfm ? new SolidColorBrush(AvaloniaColor.FromArgb(50, 30, 30, 30)) : new SolidColorBrush(AvaloniaColor.FromArgb(20, 0, 0, 0)), IsHitTestVisible = false, ZIndex = _currentZIndex++ },
                _ => null
            };

            if (_previewControl != null)
            {
                _canvas.Children.Add(_previewControl);
                UpdatePreviewShape(start);
            }
        }

        private void BeginPreviewShape(AvaloniaPoint start, IBrush brush) => BeginPreviewShape(start, brush, _currentTool);

        private Canvas CreateArrowPreview(IBrush brush)
        {
            var group = new Canvas { IsHitTestVisible = false, ZIndex = _currentZIndex++ };
            group.Children.Add(new Avalonia.Controls.Shapes.Line { Stroke = brush, StrokeThickness = _currentThickness, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round });
            group.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = brush });
            return group;
        }

        private void UpdatePreviewShape(AvaloniaPoint end)
        {
            if (_previewControl == null) return;
            if (_previewControl is Canvas arrowGroup)
            {
                UpdateArrowPreview(arrowGroup, end);
                return;
            }

            end = ClampRectDrawEnd(end);
            double minX = Math.Min(_startPoint.X, end.X);
            double minY = Math.Min(_startPoint.Y, end.Y);
            double maxX = Math.Max(_startPoint.X, end.X);
            double maxY = Math.Max(_startPoint.Y, end.Y);
            double width = Math.Max(1, maxX - minX);
            double height = Math.Max(1, maxY - minY);

            Canvas.SetLeft(_previewControl, minX);
            Canvas.SetTop(_previewControl, minY);
            _previewControl.Width = width;
            _previewControl.Height = height;

            if (_previewControl is Avalonia.Controls.Shapes.Line line)
            {
                line.StartPoint = new AvaloniaPoint(_startPoint.X - minX, _startPoint.Y - minY);
                line.EndPoint = new AvaloniaPoint(end.X - minX, end.Y - minY);
            }
        }

        
        private CropMode _cropMode = CropMode.Regular;
        private bool _suppressCropModePopup;
        private Avalonia.Controls.Primitives.Popup _cropModePopup;
        private TextBlock _cropModeDescription;

        private static readonly System.Collections.Generic.Dictionary<CropMode, string> CropModeDescriptions = new System.Collections.Generic.Dictionary<CropMode, string>
        {
            [CropMode.Regular] = "Free crop. Drag any box you like - the picture keeps exactly what is inside it, in whatever shape you drew.",
            [CropMode.KeepRatio] = "Same shape as now. The box is locked to this picture's current shape, so the result looks like the original - just a smaller piece of it.",
            [CropMode.Square] = "Perfect square. Width and height come out equal - the shape profile pictures and app icons use.",
            [CropMode.Wide] = "Wide screen. The long, letterbox shape of a TV or a YouTube video.",
            [CropMode.Tall] = "Tall phone screen. The upright shape of a Story, a Reel or a TikTok.",
            [CropMode.VerticalStrip] = "Cut out a vertical slice. The selected middle column is deleted, and the left and right pieces weld together seamlessly.",
            [CropMode.HorizontalStrip] = "Cut out a horizontal slice. The selected middle band is deleted, and the top and bottom pieces weld together seamlessly."
        };

        private static readonly System.Collections.Generic.Dictionary<CropMode, string> CropModeLabels = new System.Collections.Generic.Dictionary<CropMode, string>
        {
            [CropMode.Regular] = "REGULAR",
            [CropMode.KeepRatio] = "KEEP RATIO",
            [CropMode.Square] = "1:1",
            [CropMode.Wide] = "16:9",
            [CropMode.Tall] = "9:16",
            [CropMode.VerticalStrip] = "CUT OUT VERTICAL",
            [CropMode.HorizontalStrip] = "CUT OUT HORIZONTAL"
        };

        private void InitializeCropModes()
        {
            if (_cropModePopup != null && _cropTool != null) _cropModePopup.PlacementTarget = _cropTool;
            if (_cropModePopup != null)
            {
                _cropModePopup.AddHandler(InputElement.KeyDownEvent, OnPopupKeyDown, RoutingStrategies.Tunnel);
            }

            AddHandler(InputElement.PointerPressedEvent, OnPointerPressedOutsideCropMenu, RoutingStrategies.Tunnel);

            try
            {
                var config = IniConfig.GetIniSection<CoreConfiguration>();
                if (config == null || !Enum.TryParse<CropMode>(config.LastCropMode, true, out _cropMode)) _cropMode = CropMode.Regular;
            }
            catch
            {
                _cropMode = CropMode.Regular;
            }

            UpdateCropModeVisuals();
        }

        private void UpdateCropModeVisuals()
        {
            var host = _cropModeButtons;
            if (host != null)
            {
                string active = _cropMode.ToString();
                foreach (var child in host.Children)
                {
                    if (child is not Button modeButton) continue;
                    modeButton.Classes.Remove("selected");
                    if (modeButton.Tag as string == active) modeButton.Classes.Add("selected");
                }
            }

            SetCropModeDescription(_cropMode);
        }

        private void SetCropModeDescription(CropMode mode)
        {
            if (_cropModeDescription == null) return;
            _cropModeDescription.Text = CropModeDescriptions.TryGetValue(mode, out string text) ? text : string.Empty;
        }

        private void OnPointerPressedOutsideCropMenu(object sender, PointerPressedEventArgs e)
        {
            if (_cropModePopup == null || !_cropModePopup.IsOpen) return;
            if (IsWithinVisual(e.Source, _cropModePopup.Child)) return;
            if (IsWithinVisual(e.Source, _cropTool)) return;
            HideCropModePopup();
        }

        private static bool IsWithinVisual(object source, Visual root)
        {
            if (root == null) return false;
            var visual = source as Visual;
            while (visual != null)
            {
                if (ReferenceEquals(visual, root)) return true;
                visual = visual.GetVisualParent();
            }

            return false;
        }

        private void ShowCropModePopup()
        {
            if (_cropModePopup == null) return;
            UpdateCropModeVisuals();

            var cropButton = _cropTool;
            if (cropButton != null && Bounds.Width > 0)
            {
                var pt = cropButton.TranslatePoint(new AvaloniaPoint(0, 0), this);
                if (pt.HasValue)
                {
                    double btnLeft = pt.Value.X;
                    const double popupWidth = 732.0;
                    double btnCenter = btnLeft + (cropButton.Bounds.Width / 2.0);
                    double idealPopupLeft = btnCenter - (popupWidth / 2.0);
                    double targetPopupLeft = Math.Clamp(idealPopupLeft, 10.0, Math.Max(10.0, Bounds.Width - popupWidth - 10.0));
                    _cropModePopup.HorizontalOffset = targetPopupLeft - btnLeft;
                }
            }

            _cropModePopup.IsOpen = true;
        }

        private void HideCropModePopup()
        {
            if (_cropModePopup != null) _cropModePopup.IsOpen = false;
        }

        private void OnPopupKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Source is Slider && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_cropModePopup != null) _cropModePopup.IsOpen = false;
                if (_lineSizePopup != null) _lineSizePopup.IsOpen = false;
                _canvas?.Focus();
                e.Handled = true;
                return;
            }

            if (_cropModePopup != null) _cropModePopup.IsOpen = false;
            if (_lineSizePopup != null) _lineSizePopup.IsOpen = false;
            _canvas?.Focus();
            OnWindowKeyDown(this, e);
        }

        private void OnCropModeHover(object sender, PointerEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && Enum.TryParse<CropMode>(tag, true, out var mode)) SetCropModeDescription(mode);
        }

        private void OnCropModeHoverEnd(object sender, PointerEventArgs e)
        {
            SetCropModeDescription(_cropMode);
        }

        private void OnCropModeClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag || !Enum.TryParse<CropMode>(tag, true, out var mode)) return;

            _cropMode = mode;

            try
            {
                var config = IniConfig.GetIniSection<CoreConfiguration>();
                if (config != null)
                {
                    config.LastCropMode = mode.ToString();
                    IniConfig.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not remember the crop shape.", ex);
            }

            UpdateCropModeVisuals();
            HideCropModePopup();

            if (_currentTool != EditorTool.Crop)
            {
                var cropButton = _cropTool;
                if (cropButton != null)
                {
                    _suppressCropModePopup = true;
                    try { OnToolClick(cropButton, new RoutedEventArgs()); }
                    finally { _suppressCropModePopup = false; }
                }
            }

            ReshapeExistingCropObject();
            OverlayHelper.ShowLightToast("CROP: " + (CropModeLabels.TryGetValue(mode, out string label) ? label : mode.ToString()), this);
            _canvas?.Focus();
        }

        /// <summary>Re-fits an already-drawn crop box when the user switches shape mid-crop.</summary>
        private void ReshapeExistingCropObject()
        {
            var cropObject = _selectedControl?.Tag as string == "CropObject"
                ? _selectedControl
                : _canvas?.Children.FirstOrDefault(c => c.Tag as string == "CropObject");
            if (cropObject == null) return;

            double canvasWidth = GetCropCanvasWidth();
            double canvasHeight = GetCropCanvasHeight();
            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            double left = Canvas.GetLeft(cropObject);
            if (double.IsNaN(left)) left = cropObject.Bounds.X;
            double top = Canvas.GetTop(cropObject);
            if (double.IsNaN(top)) top = cropObject.Bounds.Y;
            double width = double.IsNaN(cropObject.Width) ? cropObject.Bounds.Width : cropObject.Width;
            double height = double.IsNaN(cropObject.Height) ? cropObject.Bounds.Height : cropObject.Height;

            var fitted = ConstrainCropBox(left, top, width, height, canvasWidth, canvasHeight);
            Canvas.SetLeft(cropObject, fitted.X);
            Canvas.SetTop(cropObject, fitted.Y);
            cropObject.Width = fitted.Width;
            cropObject.Height = fitted.Height;
            _selectedControl = cropObject;
            UpdateSelectionIndicator();
        }

        private double GetCropCanvasWidth() => _image != null ? _image.Width : (_canvas?.Bounds.Width ?? 0);

        private double GetCropCanvasHeight() => _image != null ? _image.Height : (_canvas?.Bounds.Height ?? 0);

        /// <summary>Width divided by height for the shape-locked modes; 0 when the mode is free.</summary>
        private double GetCropAspectRatio()
        {
            switch (_cropMode)
            {
                case CropMode.Square: return 1.0;
                case CropMode.Wide: return 16.0 / 9.0;
                case CropMode.Tall: return 9.0 / 16.0;
                case CropMode.KeepRatio:
                    if (_image == null || _image.Height <= 0) return 0;
                    return (double)_image.Width / _image.Height;
                default: return 0;
            }
        }

        /// <summary>Pins the drag origin to an edge for the two strip modes.</summary>
        private AvaloniaPoint ApplyCropModeToStart(AvaloniaPoint start)
        {
            double canvasWidth = GetCropCanvasWidth();
            double canvasHeight = GetCropCanvasHeight();
            if (canvasWidth <= 0 || canvasHeight <= 0) return start;

            if (_cropMode == CropMode.VerticalStrip) return new AvaloniaPoint(Math.Clamp(start.X, 0, canvasWidth), 0);
            if (_cropMode == CropMode.HorizontalStrip) return new AvaloniaPoint(0, Math.Clamp(start.Y, 0, canvasHeight));
            return start;
        }

        /// <summary>Forces the live drag to obey the chosen crop shape.</summary>
        private AvaloniaPoint ApplyCropModeToEnd(AvaloniaPoint end)
        {
            double canvasWidth = GetCropCanvasWidth();
            double canvasHeight = GetCropCanvasHeight();
            if (canvasWidth <= 0 || canvasHeight <= 0) return end;

            if (_cropMode == CropMode.VerticalStrip) return new AvaloniaPoint(Math.Clamp(end.X, 0, canvasWidth), canvasHeight);
            if (_cropMode == CropMode.HorizontalStrip) return new AvaloniaPoint(canvasWidth, Math.Clamp(end.Y, 0, canvasHeight));

            double ratio = GetCropAspectRatio();
            if (ratio <= 0) return end;

            int directionX = end.X >= _startPoint.X ? 1 : -1;
            int directionY = end.Y >= _startPoint.Y ? 1 : -1;
            double width = Math.Abs(end.X - _startPoint.X);
            double height = Math.Abs(end.Y - _startPoint.Y);

            if (width / ratio >= height) height = width / ratio; else width = height * ratio;

            double maxWidth = directionX > 0 ? canvasWidth - _startPoint.X : _startPoint.X;
            double maxHeight = directionY > 0 ? canvasHeight - _startPoint.Y : _startPoint.Y;
            if (maxWidth <= 0 || maxHeight <= 0) return _startPoint;

            if (width > maxWidth) { width = maxWidth; height = width / ratio; }
            if (height > maxHeight) { height = maxHeight; width = height * ratio; }

            return new AvaloniaPoint(_startPoint.X + directionX * width, _startPoint.Y + directionY * height);
        }

        /// <summary>Keeps a drawn crop box obeying its shape while it is moved or resized.</summary>
        private Rect ConstrainCropBox(double left, double top, double width, double height, double canvasWidth, double canvasHeight)
        {
            if (_cropMode == CropMode.VerticalStrip)
            {
                width = Math.Clamp(width, 10, canvasWidth);
                left = Math.Clamp(left, 0, Math.Max(0, canvasWidth - width));
                return new Rect(left, 0, width, canvasHeight);
            }

            if (_cropMode == CropMode.HorizontalStrip)
            {
                height = Math.Clamp(height, 10, canvasHeight);
                top = Math.Clamp(top, 0, Math.Max(0, canvasHeight - height));
                return new Rect(0, top, canvasWidth, height);
            }

            double ratio = GetCropAspectRatio();
            if (ratio <= 0) return new Rect(left, top, width, height);

            double fittedHeight = width / ratio;
            if (top + fittedHeight > canvasHeight)
            {
                fittedHeight = Math.Max(10, canvasHeight - top);
                width = fittedHeight * ratio;
                if (left + width > canvasWidth)
                {
                    width = Math.Max(10, canvasWidth - left);
                    fittedHeight = width / ratio;
                }
            }

            return new Rect(left, top, Math.Max(10, width), Math.Max(10, fittedHeight));
        }

        private AvaloniaPoint ClampRectDrawEnd(AvaloniaPoint end)
        {
            if (_currentTool == EditorTool.Crop && _canvas != null)
            {
                end = new AvaloniaPoint(Math.Clamp(end.X, 0, _canvas.Bounds.Width), Math.Clamp(end.Y, 0, _canvas.Bounds.Height));
            }
            if (_currentTool != EditorTool.Rectangle && _currentTool != EditorTool.Ellipse && _currentTool != EditorTool.Crop) return end;

            const double lockEpsilon = 3.0;
            if (_rectDrawDirX == 0 && Math.Abs(end.X - _startPoint.X) > lockEpsilon)
                _rectDrawDirX = end.X > _startPoint.X ? 1 : -1;
            if (_rectDrawDirY == 0 && Math.Abs(end.Y - _startPoint.Y) > lockEpsilon)
                _rectDrawDirY = end.Y > _startPoint.Y ? 1 : -1;

            double x = _rectDrawDirX > 0 ? Math.Max(_startPoint.X, end.X)
                     : _rectDrawDirX < 0 ? Math.Min(_startPoint.X, end.X)
                     : end.X;
            double y = _rectDrawDirY > 0 ? Math.Max(_startPoint.Y, end.Y)
                     : _rectDrawDirY < 0 ? Math.Min(_startPoint.Y, end.Y)
                     : end.Y;

            var locked = new AvaloniaPoint(x, y);
            return _currentTool == EditorTool.Crop ? ApplyCropModeToEnd(locked) : locked;
        }

        private void UpdateArrowPreview(Canvas group, AvaloniaPoint end)
        {
            UpdateArrowVisuals(group, _startPoint, end);
        }

        private void UpdateArrowVisuals(Canvas group, AvaloniaPoint start, AvaloniaPoint end)
        {
            var line = group.Children.OfType<Avalonia.Controls.Shapes.Line>().FirstOrDefault();
            var head = group.Children.OfType<Avalonia.Controls.Shapes.Polygon>().FirstOrDefault();
            if (line == null || head == null) return;

            double thickness = Math.Max(1, line.StrokeThickness);
            double padding = GetArrowPadding(thickness);
            double minX = Math.Min(start.X, end.X) - padding;
            double minY = Math.Min(start.Y, end.Y) - padding;
            double maxX = Math.Max(start.X, end.X) + padding;
            double maxY = Math.Max(start.Y, end.Y) + padding;
            Canvas.SetLeft(group, minX);
            Canvas.SetTop(group, minY);
            group.Width = Math.Max(1, maxX - minX);
            group.Height = Math.Max(1, maxY - minY);

            if (group.Tag is ArrowProperties props)
            {
                props.Start = start;
                props.End = end;
            }

            double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            double vectorLength = Distance(start, end);
            double headLength = Math.Min(10 + thickness * 2.2, Math.Max(6, vectorLength * 0.45));
            double headWidth = Math.Min(7 + thickness * 1.35, Math.Max(4, headLength * 0.72));

            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            double pullBack = Math.Min(headLength * 0.4, vectorLength * 0.5);

            line.StartPoint = new AvaloniaPoint(start.X - minX, start.Y - minY);
            line.EndPoint = new AvaloniaPoint(end.X - pullBack * cos - minX, end.Y - pullBack * sin - minY);

            var tip = new AvaloniaPoint(end.X - minX, end.Y - minY);
            var p1 = new AvaloniaPoint(end.X - headLength * cos + headWidth * sin - minX, end.Y - headLength * sin - headWidth * cos - minY);
            var p2 = new AvaloniaPoint(end.X - (headLength * 0.7) * cos - minX, end.Y - (headLength * 0.7) * sin - minY);
            var p3 = new AvaloniaPoint(end.X - headLength * cos - headWidth * sin - minX, end.Y - headLength * sin + headWidth * cos - minY);

            head.Points = new List<AvaloniaPoint> { tip, p1, p2, p3 };
            head.Fill = line.Stroke;
        }

        public static List<AvaloniaPoint> BuildArrowContourPoints(AvaloniaPoint start, AvaloniaPoint end, double thickness, double padding = 2.5)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4) return new List<AvaloniaPoint>();

            double dirX = dx / len;
            double dirY = dy / len;
            double normX = -dirY;
            double normY = dirX;

            double halfShaft = (thickness / 2.0) + padding;
            double headLen = Math.Min(10 + thickness * 2.2, Math.Max(6, len * 0.45));
            double headWidth = Math.Min(7 + thickness * 1.35, Math.Max(4, headLen * 0.72));
            double notchDist = headLen * 0.7;

            var tip = new AvaloniaPoint(end.X + dirX * padding, end.Y + dirY * padding);
            var wing1 = new AvaloniaPoint(
                end.X - dirX * (headLen + padding) + normX * (headWidth + padding * 1.2),
                end.Y - dirY * (headLen + padding) + normY * (headWidth + padding * 1.2));
            var neck1 = new AvaloniaPoint(
                end.X - dirX * (notchDist + padding * 0.5) + normX * halfShaft,
                end.Y - dirY * (notchDist + padding * 0.5) + normY * halfShaft);
            var tail1 = new AvaloniaPoint(
                start.X - dirX * padding + normX * halfShaft,
                start.Y - dirY * padding + normY * halfShaft);
            var tail2 = new AvaloniaPoint(
                start.X - dirX * padding - normX * halfShaft,
                start.Y - dirY * padding - normY * halfShaft);
            var neck2 = new AvaloniaPoint(
                end.X - dirX * (notchDist + padding * 0.5) - normX * halfShaft,
                end.Y - dirY * (notchDist + padding * 0.5) - normY * halfShaft);
            var wing2 = new AvaloniaPoint(
                end.X - dirX * (headLen + padding) - normX * (headWidth + padding * 1.2),
                end.Y - dirY * (headLen + padding) - normY * (headWidth + padding * 1.2));

            return new List<AvaloniaPoint> { tip, wing1, neck1, tail1, tail2, neck2, wing2 };
        }

        public static List<AvaloniaPoint> BuildLineContourPoints(AvaloniaPoint start, AvaloniaPoint end, double thickness, double padding = 2.5)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4) return new List<AvaloniaPoint>();

            double radius = (thickness / 2.0) + padding;
            double theta = Math.Atan2(dy, dx);
            var points = new List<AvaloniaPoint>(14);

            for (int i = 0; i <= 6; i++)
            {
                double angle = theta + Math.PI / 2.0 - Math.PI * (i / 6.0);
                points.Add(new AvaloniaPoint(end.X + radius * Math.Cos(angle), end.Y + radius * Math.Sin(angle)));
            }

            for (int i = 0; i <= 6; i++)
            {
                double angle = theta - Math.PI / 2.0 - Math.PI * (i / 6.0);
                points.Add(new AvaloniaPoint(start.X + radius * Math.Cos(angle), start.Y + radius * Math.Sin(angle)));
            }

            return points;
        }

        private void RemovePreviewShape()
        {
            if (_previewControl != null)
            {
                DisposeAnnotationResources(_previewControl);
                _canvas.Children.Remove(_previewControl);
                _previewControl = null;
            }
        }

        internal class ArrowProperties { public AvaloniaPoint Start; public AvaloniaPoint End; }
        internal class PixelateProperties { public int Strength; }
        internal class HighlightProperties { public AvaloniaColor Color; }
        internal class TextAnnotationProperties { public double PreferredFontSize; }

        
        private void OnCanvasPointerExited(object sender, PointerEventArgs e)
        {
            HideToolGhost();
            HideSnapGuides();
            HideVectorInfo();
            this.Cursor = ArrowCursor;
            if (_canvas != null) _canvas.Cursor = _currentTool == EditorTool.None ? HandCursor : CrossCursor;
        }
        private void HideToolGhost()
        {
            if (_toolGhostLayer != null) _toolGhostLayer.IsVisible = false;
        }

        private CoreConfiguration _cachedCoreConfig;

        private void UpdateToolGhost(AvaloniaPoint pos)
        {
            if (_toolGhostLayer == null || _currentTool == EditorTool.None)
            {
                HideToolGhost();
                return;
            }

            if (_ghostSettingsDirty || _toolGhost == null) RebuildToolGhost();
            if (_toolGhost == null)
            {
                HideToolGhost();
                SetEditorCursor(CrossCursor);
                return;
            }

            if (_currentTool == EditorTool.Counter && _toolGhost is Border counterBorder && counterBorder.Child is TextBlock counterText)
            {
                counterText.Text = _counterValue.ToString();
            }

            AvaloniaPoint effective = IsStampTool(_currentTool) ? ApplyStampSnap(_currentTool, pos) : pos;

            switch (_currentTool)
            {
                case EditorTool.Emoji: 
                    Canvas.SetLeft(_toolGhost, effective.X - 16);
                    Canvas.SetTop(_toolGhost, effective.Y - 16);
                    break;
                case EditorTool.Counter: 
                {
                    var config = _cachedCoreConfig ??= IniConfig.GetIniSection<CoreConfiguration>();
                    double size = config.LastCounterSize > 0 ? config.LastCounterSize : 42;
                    Canvas.SetLeft(_toolGhost, effective.X - size / 2);
                    Canvas.SetTop(_toolGhost, effective.Y - size / 2);
                    break;
                }
                default: 
                    Canvas.SetLeft(_toolGhost, effective.X);
                    Canvas.SetTop(_toolGhost, effective.Y);
                    break;
            }

            _toolGhostLayer.IsVisible = true;
            SetEditorCursor(NoneCursor);
        }

        private void RebuildToolGhost()
        {
            _ghostSettingsDirty = false;
            if (_toolGhostLayer == null) return;

            _toolGhostLayer.Children.Clear();
            _toolGhost = null;

            var config = IniConfig.GetIniSection<CoreConfiguration>();
            var brush = _toolBrushes.TryGetValue(_currentTool, out var toolBrush) ? toolBrush : Brushes.White;

            switch (_currentTool)
            {
                case EditorTool.Emoji:
                    _toolGhost = new TextBlock
                    {
                        Text = _pendingEmoji,
                        FontSize = config.LastEmojiSize,
                        FontFamily = new Avalonia.Media.FontFamily("avares://freesnip.editor/Drawing/Emoji/#Twemoji Mozilla"),
                        Opacity = 0.65
                    };
                    break;

                case EditorTool.Counter:
                {
                    double size = config.LastCounterSize > 0 ? config.LastCounterSize : 42;
                    _toolGhost = new Border
                    {
                        Width = size,
                        Height = size,
                        Background = brush,
                        CornerRadius = new CornerRadius(size / 2),
                        Opacity = 0.65,
                        Child = new TextBlock
                        {
                            Text = _counterValue.ToString(),
                            Foreground = GetContrastColor(brush),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            FontSize = size * 0.6,
                            FontWeight = FontWeight.Bold,
                            IsHitTestVisible = false
                        }
                    };
                    break;
                }

                default:
                    _toolGhost = null;
                    break;
            }

            if (_toolGhost != null) _toolGhostLayer.Children.Add(_toolGhost);
        }

        private void OnCanvasPointerMoved(object sender, PointerEventArgs e)
        {
            if (IsEditorOperationInProgress || _canvas == null) return;
            try
            {

            if (_isPanning)
            {
                var sv = _editorScrollViewer;
                if (sv != null)
                {
                    var current = e.GetPosition(this);
                    double dx = current.X - _panStartPoint.X;
                    double dy = current.Y - _panStartPoint.Y;
                    var panel = _scrollContentPanel;
                    double maxOffsetX = panel != null ? Math.Max(0, panel.Width - sv.Viewport.Width) : double.MaxValue;
                    double maxOffsetY = panel != null ? Math.Max(0, panel.Height - sv.Viewport.Height) : double.MaxValue;

                    sv.Offset = new Avalonia.Vector(
                        Math.Clamp(_panStartOffset.X - dx, 0, maxOffsetX),
                        Math.Clamp(_panStartOffset.Y - dy, 0, maxOffsetY));
                }
                return;
            }

            var pos = e.GetPosition(_canvas);

            if (_isRubberBanding && _rubberBandVisual != null)
            {
                double minX = Math.Min(_rubberBandStart.X, pos.X);
                double minY = Math.Min(_rubberBandStart.Y, pos.Y);
                double maxX = Math.Max(_rubberBandStart.X, pos.X);
                double maxY = Math.Max(_rubberBandStart.Y, pos.Y);
                Canvas.SetLeft(_rubberBandVisual, minX);
                Canvas.SetTop(_rubberBandVisual, minY);
                _rubberBandVisual.Width = maxX - minX;
                _rubberBandVisual.Height = maxY - minY;
                return;
            }

            if (_isOcrInteractiveMode)
            {
                HideToolGhost();
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && _ocrSelectionStartIndex != -1)
                {
                    int currentIndex = FindClosestOcrWordIndex(pos);
                    if (currentIndex != -1)
                    {
                        _ocrSelectionEndIndex = currentIndex;
                        UpdateOcrSelectionVisuals();
                    }
                    e.Handled = true;
                }
                else
                {
                    SetEditorCursor(IbeamCursor);
                }
                return;
            }

            if (_contextToolbar != null && (_isDraggingSelected || _isResizing || _isDraggingStartPoint || _isDraggingEndPoint))
            {
                _contextToolbar.IsVisible = false;
            }

            if (!_isDraggingSelected && !_isResizing && !_isDraggingStartPoint && !_isDraggingEndPoint && !_isDrawing)
            {
                if (_resizeHandles != null && _resizeHandles.Count == 4 && _resizeHandles[0].IsVisible)
                {
                    int hoveredHandle = -1;
                    for (int i = 0; i < 4; i++)
                    {
                        var h = _resizeHandles[i];
                        double hl = Canvas.GetLeft(h);
                        double ht = Canvas.GetTop(h);
                        if (!double.IsNaN(hl) && !double.IsNaN(ht) &&
                            pos.X >= hl - 4 && pos.X <= hl + h.Width + 4 &&
                            pos.Y >= ht - 4 && pos.Y <= ht + h.Height + 4)
                        {
                            hoveredHandle = i;
                            break;
                        }
                    }

                    if (hoveredHandle >= 0)
                    {
                        HideToolGhost();
                        HideSnapGuides();
                        HideVectorInfo();
                        SetEditorCursor(hoveredHandle == 0 || hoveredHandle == 2 ? TopLeftCursor : TopRightCursor);
                        return;
                    }
                }

                if (e.Source is AvaloniaControl src && ((_resizeHandles != null && _resizeHandles.Contains(src)) || src == _startHandle || src == _endHandle)) return;
                var hovered = FindSelectableControlAt(pos);
                
                if (hovered != null)
                {
                    HideToolGhost();
                    HideSnapGuides();
                    HideVectorInfo();
                    if (_snapDotsLayer != null && _snapDotsLayer.IsVisible)
                    {
                        _snapDotsLayer.IsVisible = false;
                        ClearSnapTargets();
                    }
                    if (hovered is Border hb && hb.Child is TextBox htb)
                    {
                        double bLeft = Canvas.GetLeft(hb);
                        if (double.IsNaN(bLeft)) bLeft = hb.Bounds.X;
                        double bTop = Canvas.GetTop(hb);
                        if (double.IsNaN(bTop)) bTop = hb.Bounds.Y;
                        double bWidth = hb.Bounds.Width > 0 ? hb.Bounds.Width : hb.Width;
                        double bHeight = hb.Bounds.Height > 0 ? hb.Bounds.Height : hb.Height;
                        double relX = pos.X - bLeft;
                        double relY = pos.Y - bTop;
                        double edgeThreshold = Math.Max(8, Math.Max(hb.BorderThickness.Left, hb.Padding.Left) + 4);
                        bool isEdge = relX <= edgeThreshold || relX >= bWidth - edgeThreshold ||
                                      relY <= edgeThreshold || relY >= bHeight - edgeThreshold;
                        SetEditorCursor(isEdge ? SizeAllCursor : (htb.IsReadOnly ? HandCursor : IbeamCursor));
                    }
                    else
                    {
                        SetEditorCursor(HandCursor);
                    }
                    UpdateHoverIndicator(hovered);
                }
                else
                {
                    SetEditorCursor(_currentTool == EditorTool.None ? (GetUserAnnotations().Count == 0 ? HandCursor : CrossCursor) : CrossCursor);
                    UpdateHoverIndicator(null);
                    
                    if (_currentTool != EditorTool.None)
                    {
                        var hoverEvent = CreateToolPointerEvent(e, pos);
                        _activeToolHandler.OnPointerMoved(_toolContext, hoverEvent);
                    }
                    else
                    {
                        HideToolGhost();
                        HideSnapGuides();
                        HideVectorInfo();
                        if (_snapDotsLayer != null && _snapDotsLayer.IsVisible)
                        {
                            _snapDotsLayer.IsVisible = false;
                            ClearSnapTargets();
                        }
                    }
                }

                if (hovered != null)
                {
                    IBrush b = null;
                    if (hovered is Avalonia.Controls.Shapes.Shape s) b = s.Stroke ?? s.Fill;
                    else if (hovered is Canvas group && group.Tag is ArrowProperties)
                    {
                        var arrowLine = group.Children.OfType<Avalonia.Controls.Shapes.Line>().FirstOrDefault();
                        var arrowHead = group.Children.OfType<Avalonia.Controls.Shapes.Polygon>().FirstOrDefault();
                        b = arrowLine?.Stroke ?? arrowHead?.Fill;
                    }
                    else if (hovered is Border brd) b = brd.BorderBrush ?? brd.Background;
                    else if (hovered is TextBlock tb) b = tb.Foreground;

                    if (b is SolidColorBrush scb)
                    {
                        UpdateCurrentColorDisplay(scb.Color);
                    }
                }
            }
            if (_isDraggingStartPoint || _isDraggingEndPoint)
            {
                HideToolGhost();
                double dx = pos.X - _dragLastPoint.X; double dy = pos.Y - _dragLastPoint.Y;
                if ((Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) && !_dragUndoCaptured) { SaveUndoState(false); _dragUndoCaptured = true; }
                if (_selectedControl == null) return;

                bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                UpdateMagnetButtonState(altPressed);

                if (TryGetVectorAbsolutePoints(_selectedControl, out var start, out var end))
                {
                    if (_isDraggingStartPoint)
                    {
                        start = ApplyVectorConstraints(pos, end, e.KeyModifiers, allowTargetSnap: !altPressed);
                    }
                    else
                    {
                        end = ApplyVectorConstraints(pos, start, e.KeyModifiers, allowTargetSnap: !altPressed);
                    }

                    SetVectorAbsolutePoints(_selectedControl, start, end);
                    UpdateVectorInfo(start, end, altPressed);
                }

                _dragLastPoint = pos;
                UpdateSelectionIndicator();
                
                RefreshSnapTargetsList(_selectedControl, pos);
                AvaloniaPoint? snappedPoint = null;
                if (!altPressed)
                {
                    var t = SnapToNearbyTarget(pos, pos);
                    if (t != pos) snappedPoint = t;
                }
                HighlightSnapDot(snappedPoint, pos);
                return;
            }
            if (_isResizing && _selectedControl != null)
            {
                HideToolGhost();
                double dx = pos.X - _dragLastPoint.X;
                double dy = pos.Y - _dragLastPoint.Y;
                if ((Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) && !_resizeUndoCaptured)
                {
                    SaveUndoState(false);
                    _resizeUndoCaptured = true;
                }
                if (_selectedControl == null) return;
                bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                UpdateMagnetButtonState(altPressed);
                ResizeSelectedControl(dx, dy, altPressed);
                if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var vStart, out var vEnd))
                {
                    UpdateVectorInfo(vStart, vEnd, altPressed);
                }
                else
                {
                    ShowSnapHint(pos, altPressed);
                }
                if (IsPixelateControl(_selectedControl)) RefreshPixelateAnnotation(_selectedControl);
                if (IsHighlightControl(_selectedControl)) RefreshHighlightAnnotation(_selectedControl);
                _dragLastPoint = pos;
                UpdateSelectionIndicator();
                RefreshSnapTargetsList(_selectedControl, pos);
                var resizeSnapped = FindSnappedTargetForMovingControl(_selectedControl, _activeSnapGuides.Count > 0);
                HighlightSnapDot(resizeSnapped, pos);
                return;
            }
            if (_isDraggingSelected && _selectedControl != null)
            {
                HideToolGhost();
                double dx = pos.X - _dragLastPoint.X;
                double dy = pos.Y - _dragLastPoint.Y;
                if ((Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) && !_dragUndoCaptured)
                {
                    SaveUndoState(false);
                    _dragUndoCaptured = true;
                }
                if (_selectedControl == null) return;

                double curL = Canvas.GetLeft(_selectedControl);
                if (double.IsNaN(curL)) curL = _selectedControl.Bounds.X;
                double curT = Canvas.GetTop(_selectedControl);
                if (double.IsNaN(curT)) curT = _selectedControl.Bounds.Y;

                _dragUnsnappedLeft += dx;
                _dragUnsnappedTop += dy;

                bool altPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                UpdateMagnetButtonState(altPressed);

                var snapped = ApplyMagneticSnap(_selectedControl, _dragUnsnappedLeft, _dragUnsnappedTop, altPressed);

                if (_selectedControl.Tag as string == "CropObject")
                {
                    double cw = _image != null ? _image.Width : (_canvas?.Bounds.Width ?? 0);
                    double ch = _image != null ? _image.Height : (_canvas?.Bounds.Height ?? 0);
                    double w = double.IsNaN(_selectedControl.Width) ? _selectedControl.Bounds.Width : _selectedControl.Width;
                    double h = double.IsNaN(_selectedControl.Height) ? _selectedControl.Bounds.Height : _selectedControl.Height;
                    snapped = new AvaloniaPoint(Math.Clamp(snapped.X, 0, Math.Max(0, cw - w)), Math.Clamp(snapped.Y, 0, Math.Max(0, ch - h)));
                }

                double appliedDx = snapped.X - curL;
                double appliedDy = snapped.Y - curT;

                if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var vStart, out var vEnd))
                {
                    var newStart = new AvaloniaPoint(vStart.X + appliedDx, vStart.Y + appliedDy);
                    var newEnd = new AvaloniaPoint(vEnd.X + appliedDx, vEnd.Y + appliedDy);
                    SetVectorAbsolutePoints(_selectedControl, newStart, newEnd);
                    UpdateVectorInfo(newStart, newEnd, altPressed);
                }
                else
                {
                    Canvas.SetLeft(_selectedControl, snapped.X);
                    Canvas.SetTop(_selectedControl, snapped.Y);
                    ShowSnapHint(pos, altPressed);
                }

                if (IsPixelateControl(_selectedControl)) RefreshPixelateAnnotation(_selectedControl);
                if (IsHighlightControl(_selectedControl)) RefreshHighlightAnnotation(_selectedControl);
                _dragLastPoint = pos;
                UpdateSelectionIndicator();
                RefreshSnapTargetsList(_selectedControl, pos);
                bool isSnapActive = _activeSnapGuides.Count > 0 || snapped != new AvaloniaPoint(_dragUnsnappedLeft, _dragUnsnappedTop);
                var (dragRecipientSnapped, dragHeldSnapped) = FindSnappedTargetsForMovingControl(_selectedControl, isSnapActive);
                HighlightSnapDots(dragRecipientSnapped, dragHeldSnapped, pos);
                return;
            }
            if (!_isDrawing) 
            {
                if (_snapDotsLayer != null) _snapDotsLayer.IsVisible = false;
                ClearSnapTargets();
                return;
            }

            var toolEvent = CreateToolPointerEvent(e, pos);
            _activeToolHandler.OnPointerMoved(_toolContext, toolEvent);
            _dragLastPoint = pos;
            }
            catch (Exception ex)
            {
                Log.Error("OnCanvasPointerMoved failed", ex);
            }
        }

        private void NudgeSelectedControl(double dx, double dy)
        {
            if (_selectedControl == null) return;
            SaveUndoState(false);
            if (_selectedControl == null) return;
            double curL = Canvas.GetLeft(_selectedControl);
            if (double.IsNaN(curL)) curL = _selectedControl.Bounds.X;
            double curT = Canvas.GetTop(_selectedControl);
            if (double.IsNaN(curT)) curT = _selectedControl.Bounds.Y;
            double newL = curL + dx;
            double newT = curT + dy;
            if (_selectedControl.Tag as string == "CropObject")
            {
                double cw = _image != null ? _image.Width : (_canvas?.Bounds.Width ?? 0);
                double ch = _image != null ? _image.Height : (_canvas?.Bounds.Height ?? 0);
                double w = double.IsNaN(_selectedControl.Width) ? _selectedControl.Bounds.Width : _selectedControl.Width;
                double h = double.IsNaN(_selectedControl.Height) ? _selectedControl.Bounds.Height : _selectedControl.Height;
                newL = Math.Clamp(newL, 0, Math.Max(0, cw - w));
                newT = Math.Clamp(newT, 0, Math.Max(0, ch - h));

                var shapedNudge = ConstrainCropBox(newL, newT, w, h, cw, ch);
                newL = shapedNudge.X; newT = shapedNudge.Y;
                _selectedControl.Width = shapedNudge.Width;
                _selectedControl.Height = shapedNudge.Height;
            }
            if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var start, out var end))
            {
                var newStart = new AvaloniaPoint(start.X + dx, start.Y + dy);
                var newEnd = new AvaloniaPoint(end.X + dx, end.Y + dy);
                SetVectorAbsolutePoints(_selectedControl, newStart, newEnd);
            }
            else
            {
                Canvas.SetLeft(_selectedControl, newL);
                Canvas.SetTop(_selectedControl, newT);
            }
            if (IsPixelateControl(_selectedControl)) RefreshPixelateAnnotation(_selectedControl);
            if (IsHighlightControl(_selectedControl)) RefreshHighlightAnnotation(_selectedControl);
            UpdateSelectionIndicator();
        }

        private void ResizeSelectedControl(double dx, double dy, bool altSnapBypass = false)
        {
            double oldW, oldH, left, top;
            if (TryGetControlBounds(_selectedControl, out var curControlBounds))
            {
                oldW = curControlBounds.Width;
                oldH = curControlBounds.Height;
                left = curControlBounds.X;
                top = curControlBounds.Y;
            }
            else
            {
                oldW = double.IsNaN(_selectedControl.Width) ? _selectedControl.Bounds.Width : _selectedControl.Width;
                oldH = double.IsNaN(_selectedControl.Height) ? _selectedControl.Bounds.Height : _selectedControl.Height;
                left = Canvas.GetLeft(_selectedControl); if (double.IsNaN(left)) left = _selectedControl.Bounds.X;
                top = Canvas.GetTop(_selectedControl); if (double.IsNaN(top)) top = _selectedControl.Bounds.Y;
            }
            bool keepRatio = (_selectedControl is TextBlock) || (_selectedControl is Border b && b.Child is TextBlock) || (_selectedControl is Avalonia.Controls.Image);
            double newW = oldW; double newH = oldH;

            if (keepRatio)
            {
                double wDelta = 0, hDelta = 0;
                switch (_resizeHandleIndex)
                {
                    case 0: wDelta = -dx; hDelta = -dy; break;
                    case 1: wDelta = dx; hDelta = -dy; break;
                    case 2: wDelta = dx; hDelta = dy; break;
                    case 3: wDelta = -dx; hDelta = dy; break;
                }

                double scale = ((oldW + wDelta) * oldW + (oldH + hDelta) * oldH) / (oldW * oldW + oldH * oldH);
                scale = Math.Max(10 / oldW, Math.Max(10 / oldH, scale));
                newW = oldW * scale; newH = oldH * scale;
                switch (_resizeHandleIndex) { case 0: Canvas.SetLeft(_selectedControl, left + (oldW - newW)); Canvas.SetTop(_selectedControl, top + (oldH - newH)); break; case 1: Canvas.SetTop(_selectedControl, top + (oldH - newH)); break; case 3: Canvas.SetLeft(_selectedControl, left + (oldW - newW)); break; }
            }
            else
            {
                switch (_resizeHandleIndex)
                {
                    case 0:
                        if (_resizeUnsnappedWidth - dx >= 10) { _resizeUnsnappedWidth -= dx; _resizeUnsnappedLeft += dx; }
                        if (_resizeUnsnappedHeight - dy >= 10) { _resizeUnsnappedHeight -= dy; _resizeUnsnappedTop += dy; }
                        break;
                    case 1:
                        if (_resizeUnsnappedWidth + dx >= 10) _resizeUnsnappedWidth += dx;
                        if (_resizeUnsnappedHeight - dy >= 10) { _resizeUnsnappedHeight -= dy; _resizeUnsnappedTop += dy; }
                        break;
                    case 2:
                        if (_resizeUnsnappedWidth + dx >= 10) _resizeUnsnappedWidth += dx;
                        if (_resizeUnsnappedHeight + dy >= 10) _resizeUnsnappedHeight += dy;
                        break;
                    case 3:
                        if (_resizeUnsnappedWidth - dx >= 10) { _resizeUnsnappedWidth -= dx; _resizeUnsnappedLeft += dx; }
                        if (_resizeUnsnappedHeight + dy >= 10) _resizeUnsnappedHeight += dy;
                        break;
                }

                var raw = new Rect(_resizeUnsnappedLeft, _resizeUnsnappedTop, _resizeUnsnappedWidth, _resizeUnsnappedHeight);
                var snapped = ApplyResizeMagneticSnap(_selectedControl, raw, altSnapBypass);
                newW = snapped.Width; newH = snapped.Height;
                Canvas.SetLeft(_selectedControl, snapped.Left);
                Canvas.SetTop(_selectedControl, snapped.Top);
                if (_selectedControl is Border borderCtrl && borderCtrl.Child is TextBlock txt && GetToolFromControl(_selectedControl) == EditorTool.Counter)
                {
                    double avgSize = (newW + newH) / 2.0;
                    borderCtrl.CornerRadius = new CornerRadius(avgSize / 2);
                    txt.FontSize = avgSize * 0.6;
                }
            }

            if (_selectedControl.Tag as string == "CropObject")
            {
                double cw = _image != null ? _image.Width : (_canvas?.Bounds.Width ?? 0);
                double ch = _image != null ? _image.Height : (_canvas?.Bounds.Height ?? 0);
                double cl = Canvas.GetLeft(_selectedControl);
                double ct = Canvas.GetTop(_selectedControl);
                if (cl < 0) { newW += cl; cl = 0; }
                if (ct < 0) { newH += ct; ct = 0; }
                if (cl + newW > cw) { newW = cw - cl; }
                if (ct + newH > ch) { newH = ch - ct; }
                cl = Math.Clamp(cl, 0, Math.Max(0, cw - 10));
                ct = Math.Clamp(ct, 0, Math.Max(0, ch - 10));
                newW = Math.Clamp(newW, 10, cw - cl);
                newH = Math.Clamp(newH, 10, ch - ct);

                var shaped = ConstrainCropBox(cl, ct, newW, newH, cw, ch);
                cl = shaped.X; ct = shaped.Y; newW = shaped.Width; newH = shaped.Height;

                Canvas.SetLeft(_selectedControl, cl);
                Canvas.SetTop(_selectedControl, ct);
            }
            double scaleX = newW / oldW;
            double scaleY = newH / oldH;
            _selectedControl.Width = newW; _selectedControl.Height = newH;
            
            if (_selectedControl is Avalonia.Controls.Shapes.Line lineCtrl)
            {
                lineCtrl.StartPoint = new AvaloniaPoint(lineCtrl.StartPoint.X * scaleX, lineCtrl.StartPoint.Y * scaleY);
                lineCtrl.EndPoint = new AvaloniaPoint(lineCtrl.EndPoint.X * scaleX, lineCtrl.EndPoint.Y * scaleY);
            }
            else if (_selectedControl is Avalonia.Controls.Shapes.Polyline polyCtrl && polyCtrl.Points != null && polyCtrl.Points.Count > 0)
            {
                double pMinX = polyCtrl.Points.Min(p => p.X);
                double pMinY = polyCtrl.Points.Min(p => p.Y);
                double pMaxX = polyCtrl.Points.Max(p => p.X);
                double pMaxY = polyCtrl.Points.Max(p => p.Y);
                double curW = Math.Max(1.0, pMaxX - pMinX);
                double curH = Math.Max(1.0, pMaxY - pMinY);

                double sx = newW / curW;
                double sy = newH / curH;

                var newPoints = new System.Collections.Generic.List<AvaloniaPoint>(polyCtrl.Points.Count);
                foreach (var pt in polyCtrl.Points)
                {
                    newPoints.Add(new AvaloniaPoint((pt.X - pMinX) * sx, (pt.Y - pMinY) * sy));
                }
                polyCtrl.Points = newPoints;
                polyCtrl.Width = newW;
                polyCtrl.Height = newH;
                polyCtrl.InvalidateMeasure();
                polyCtrl.InvalidateArrange();
                polyCtrl.InvalidateVisual();
            }
            else if (_selectedControl is Canvas group)
            {
                if (group.Tag is ArrowProperties props)
                {
                    double oldLX = Math.Min(props.Start.X, props.End.X);
                    double oldLY = Math.Min(props.Start.Y, props.End.Y);
                    
                    props.Start = new AvaloniaPoint(props.Start.X + (left - (oldLX - 20 - _currentThickness * 2)), props.Start.Y + (top - (oldLY - 20 - _currentThickness * 2)));
                    props.End = new AvaloniaPoint(props.End.X + (left - (oldLX - 20 - _currentThickness * 2)), props.End.Y + (top - (oldLY - 20 - _currentThickness * 2)));

                    UpdateArrowVisuals(group, props.Start, props.End);
                }
                group.InvalidateVisual();
            }
            else if (_selectedControl is TextBlock t) { t.FontSize *= scaleX; UpdatePersistedSize(EditorTool.Emoji, t.FontSize); }
            else if (_selectedControl is Border borderCtrl)
            {
                if (borderCtrl.Child is TextBox tb)
                {
                    SetTextBoxBoundsToBorder(borderCtrl, tb);
                    FitTextBoxToBorder(borderCtrl, tb);
                    UpdatePersistedSize(EditorTool.Text, tb.FontSize);
                    UpdatePersistedTextBoxDimensions(newW, newH);
                }
                else if (borderCtrl.Child is TextBlock txt)
                {
                    txt.FontSize *= scaleX;
                    borderCtrl.CornerRadius = new CornerRadius(newW / 2);
                    UpdatePersistedSize(EditorTool.Counter, newW);
                }
            }
        }

        private void UpdatePersistedTextBoxDimensions(double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (config != null)
            {
                config.LastTextBoxWidth = width;
                config.LastTextBoxHeight = height;
                _ghostSettingsDirty = true;
            }
        }

        private void UpdatePersistedSize(EditorTool tool, double size) { var config = IniConfig.GetIniSection<CoreConfiguration>(); if (tool == EditorTool.Text) config.LastTextSize = size; else if (tool == EditorTool.Emoji) config.LastEmojiSize = size; else if (tool == EditorTool.Counter) config.LastCounterSize = size; _toolFontSizes[tool] = size; _ghostSettingsDirty = true; }

        private void OnCanvasPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (IsEditorOperationInProgress || _canvas == null) return;
            try
            {

            if (_isPanning)
            {
                _isPanning = false;
                e.Pointer.Capture(null);
                
                SetEditorCursor(_currentTool == EditorTool.None ? HandCursor : CrossCursor);
                return;
            }

            var pos = e.GetPosition(_canvas);
            
            if (_isRubberBanding)
            {
                _isRubberBanding = false;
                e.Pointer.Capture(null);
                
                if (_rubberBandVisual != null)
                {
                    double rL = Canvas.GetLeft(_rubberBandVisual);
                    double rT = Canvas.GetTop(_rubberBandVisual);
                    double rW = _rubberBandVisual.Width;
                    double rH = _rubberBandVisual.Height;
                    
                    _canvas.Children.Remove(_rubberBandVisual);
                    _rubberBandVisual = null;
                    
                    var rect = new Rect(rL, rT, rW, rH);
                    var selectedItems = new List<AvaloniaControl>();
                    
                    foreach (var child in _canvas.Children)
                    {
                        if (IsEditorChrome(child) || child.IsHitTestVisible == false) continue;
                        if (TryGetControlBounds(child, out var bounds) && rect.Intersects(bounds))
                        {
                            selectedItems.Add(child);
                        }
                    }
                    
                    if (selectedItems.Count == 1)
                    {
                        _selectedControl = selectedItems[0];
                        UpdateSelectionIndicator();
                    }
                    else if (selectedItems.Count > 1)
                    {
                        double minX = selectedItems.Min(c => { TryGetControlBounds(c, out var b); return b.X; });
                        double minY = selectedItems.Min(c => { TryGetControlBounds(c, out var b); return b.Y; });
                        double maxX = selectedItems.Max(c => { TryGetControlBounds(c, out var b); return b.Right; });
                        double maxY = selectedItems.Max(c => { TryGetControlBounds(c, out var b); return b.Bottom; });
                        
                        var group = new Canvas 
                        { 
                            Tag = "MultiSelectGroup", 
                            Width = maxX - minX, 
                            Height = maxY - minY,
                            IsHitTestVisible = true,
                            Background = Brushes.Transparent
                        };
                        Canvas.SetLeft(group, minX);
                        Canvas.SetTop(group, minY);
                        
                        foreach (var item in selectedItems)
                        {
                            _canvas.Children.Remove(item);
                            if (IsVectorControl(item) && TryGetVectorAbsolutePoints(item, out var vStart, out var vEnd))
                            {
                                SetVectorAbsolutePoints(item, new AvaloniaPoint(vStart.X - minX, vStart.Y - minY), new AvaloniaPoint(vEnd.X - minX, vEnd.Y - minY));
                            }
                            else
                            {
                                double iL = Canvas.GetLeft(item);
                                if (double.IsNaN(iL)) iL = item.Bounds.X;
                                double iT = Canvas.GetTop(item);
                                if (double.IsNaN(iT)) iT = item.Bounds.Y;
                                
                                Canvas.SetLeft(item, iL - minX);
                                Canvas.SetTop(item, iT - minY);
                            }
                            group.Children.Add(item);
                        }
                        
                        _canvas.Children.Add(group);
                        _selectedControl = group;
                        UpdateSelectionIndicator();
                    }
                }
                SetEditorCursor(HandCursor);
                return;
            }

            if (_isOcrInteractiveMode)
            {
                if (_ocrSelectionStartIndex != -1)
                {
                    _ocrController.CollectSelectedWords();
                    UpdateOcrSelectionVisuals(true);
                    
                    var ocrToolbar = _ocrContextToolbar;
                    double maxW = _document.HasImage ? _document.Width : (_canvas?.Bounds.Width ?? 800);
                    double maxH = _document.HasImage ? _document.Height : (_canvas?.Bounds.Height ?? 600);
                    _ocrController.ShowContextToolbar(
                        ocrToolbar,
                        maxW,
                        maxH,
                        SampleUnderlyingLuminance,
                        ApplyOcrToolbarContrastTheme,
                        UpdateOcrContextToolbarButtons);
                    
                    _ocrSelectionStartIndex = -1;
                    _ocrSelectionEndIndex = -1;
                }
                e.Handled = true;
                return;
            }

            bool wasTransformingSelection = _selectedControl != null && (_isDraggingSelected || _isResizing || _isDraggingStartPoint || _isDraggingEndPoint);

            _isResizing = false;
            _resizeUndoCaptured = false;
            _isDraggingStartPoint = false;
            _isDraggingEndPoint = false;
            UpdateMagnetButtonState(false);
            e.Pointer.Capture(null);
            HideSnapGuides();
            HideVectorInfo();
            HideSnapHint();
            if (_snapDotsLayer != null) _snapDotsLayer.IsVisible = false;
            ClearSnapTargets();

            if (wasTransformingSelection)
            {
                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
            }

            if (_isDraggingSelected)
            {
                _isDraggingSelected = false;
                _dragUndoCaptured = false;
                UpdateSelectionIndicator();
                _canvas?.Focus();
                return;
            }

            if (!_isDrawing)
            {
                _canvas?.Focus();
                return;
            }
            var toolEvent = CreateToolPointerEvent(e, pos);
            _activeToolHandler.OnPointerReleased(_toolContext, toolEvent);
            _canvas?.Focus();
            }
            catch (Exception ex)
            {
                Log.Error("OnCanvasPointerReleased failed", ex);
            }
        }

        private void UpdateSelectionIndicator()
        {
            if (_selectedControl == null || _image == null) { 
                if (_selectionIndicator != null) _selectionIndicator.IsVisible = false; 
                if (_circleSelectionIndicator != null) _circleSelectionIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineSelectionIndicator);
                ResetPolygonIndicator(_arrowSelectionIndicator);
                foreach (var ind in _multiSelectionIndicators) ind.IsVisible = false;
                if (_resizeHandles != null) foreach (var handleItem in _resizeHandles) handleItem.IsVisible = false; 
                if (_deleteBtn != null) _deleteBtn.IsEnabled = false; 
                if (_contextToolbar != null) _contextToolbar.IsVisible = false;
                if (_startHandle != null) _startHandle.IsVisible = false;
                if (_endHandle != null) _endHandle.IsVisible = false;
                if (_hoverIndicator != null) _hoverIndicator.IsVisible = false;
                if (_circleHoverIndicator != null) _circleHoverIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineHoverIndicator);
                ResetPolygonIndicator(_arrowHoverIndicator);
                HideVectorInfo();
                if (_pixelateStrengthHandle != null) _pixelateStrengthHandle.IsVisible = false;
                UpdateModeStatus();
                return; 
            }
            
            if (_deleteBtn != null) _deleteBtn.IsEnabled = true;

            bool isCropOrPaste = _selectedControl?.Tag?.ToString() == "CropObject" || _selectedControl?.Tag?.ToString() == "PasteObject";
            if (_contextApplyButton != null) _contextApplyButton.IsVisible = isCropOrPaste;
            if (_contextDuplicateButton != null) _contextDuplicateButton.IsVisible = !isCropOrPaste;
            if (_contextApplySeparator != null) _contextApplySeparator.IsVisible = isCropOrPaste;

            if (!TryGetControlBounds(_selectedControl, out var selectedBounds))
            {
                if (_selectionIndicator != null) _selectionIndicator.IsVisible = false;
                if (_circleSelectionIndicator != null) _circleSelectionIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineSelectionIndicator);
                ResetPolygonIndicator(_arrowSelectionIndicator);
                foreach (var ind in _multiSelectionIndicators) ind.IsVisible = false;
                return;
            }

            bool isVector = IsVectorControl(_selectedControl);
            double absX = selectedBounds.X;
            double absY = selectedBounds.Y;
            double w = selectedBounds.Width;
            double h = selectedBounds.Height;

            if (_selectedControl is Canvas group && group.Tag as string == "MultiSelectGroup")
            {
                if (_selectionIndicator != null) _selectionIndicator.IsVisible = false;
                if (_circleSelectionIndicator != null) _circleSelectionIndicator.IsVisible = false;
                ResetPolygonIndicator(_lineSelectionIndicator);
                ResetPolygonIndicator(_arrowSelectionIndicator);
                int childCount = group.Children.Count;
                while (_multiSelectionIndicators.Count < childCount)
                {
                    var ind = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Classes = { "selection-indicator" },
                        IsHitTestVisible = false,
                        ZIndex = 9999
                    };
                    _multiSelectionIndicators.Add(ind);
                    _canvas.Children.Add(ind);
                }

                double groupLeft = Canvas.GetLeft(group);
                if (double.IsNaN(groupLeft)) groupLeft = group.Bounds.X;
                double groupTop = Canvas.GetTop(group);
                if (double.IsNaN(groupTop)) groupTop = group.Bounds.Y;

                for (int i = 0; i < _multiSelectionIndicators.Count; i++)
                {
                    if (i < childCount)
                    {
                        var child = group.Children[i];
                        if (TryGetControlBounds(child, out var childBounds))
                        {
                            var ind = _multiSelectionIndicators[i];
                            ind.Width = childBounds.Width + 4;
                            ind.Height = childBounds.Height + 4;
                            Canvas.SetLeft(ind, groupLeft + childBounds.X - 2);
                            Canvas.SetTop(ind, groupTop + childBounds.Y - 2);
                            ind.IsVisible = true;
                        }
                        else
                        {
                            _multiSelectionIndicators[i].IsVisible = false;
                        }
                    }
                    else
                    {
                        _multiSelectionIndicators[i].IsVisible = false;
                    }
                }
            }
            else
            {
                foreach (var ind in _multiSelectionIndicators) ind.IsVisible = false;
                bool isRound = _selectedControl is Avalonia.Controls.Shapes.Ellipse ||
                               GetToolFromControl(_selectedControl) == EditorTool.Counter ||
                               GetToolFromControl(_selectedControl) == EditorTool.Emoji;

                if (IsArrowControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var arrowStart, out var arrowEnd))
                {
                    if (_selectionIndicator != null) _selectionIndicator.IsVisible = false;
                    if (_circleSelectionIndicator != null) _circleSelectionIndicator.IsVisible = false;
                    ResetPolygonIndicator(_lineSelectionIndicator);
                    if (_arrowSelectionIndicator != null)
                    {
                        Canvas.SetLeft(_arrowSelectionIndicator, 0);
                        Canvas.SetTop(_arrowSelectionIndicator, 0);
                        _arrowSelectionIndicator.Points = BuildArrowContourPoints(arrowStart, arrowEnd, GetVectorThickness(_selectedControl), padding: 2.5);
                        _arrowSelectionIndicator.InvalidateMeasure();
                        _arrowSelectionIndicator.InvalidateArrange();
                        _arrowSelectionIndicator.InvalidateVisual();
                        _arrowSelectionIndicator.IsVisible = true;
                    }
                }
                else if (isVector && TryGetVectorAbsolutePoints(_selectedControl, out var vStartPt, out var vEndPt))
                {
                    if (_selectionIndicator != null) _selectionIndicator.IsVisible = false;
                    if (_circleSelectionIndicator != null) _circleSelectionIndicator.IsVisible = false;
                    ResetPolygonIndicator(_arrowSelectionIndicator);
                    if (_lineSelectionIndicator != null)
                    {
                        Canvas.SetLeft(_lineSelectionIndicator, 0);
                        Canvas.SetTop(_lineSelectionIndicator, 0);
                        _lineSelectionIndicator.Points = BuildLineContourPoints(vStartPt, vEndPt, GetVectorThickness(_selectedControl), padding: 2.5);
                        _lineSelectionIndicator.InvalidateMeasure();
                        _lineSelectionIndicator.InvalidateArrange();
                        _lineSelectionIndicator.InvalidateVisual();
                        _lineSelectionIndicator.IsVisible = true;
                    }
                }
                else if (isRound)
                {
                    if (_selectionIndicator != null) _selectionIndicator.IsVisible = false;
                    ResetPolygonIndicator(_lineSelectionIndicator);
                    ResetPolygonIndicator(_arrowSelectionIndicator);
                    if (_circleSelectionIndicator != null)
                    {
                        _circleSelectionIndicator.Width = w + 4;
                        _circleSelectionIndicator.Height = h + 4;
                        Canvas.SetLeft(_circleSelectionIndicator, absX - 2);
                        Canvas.SetTop(_circleSelectionIndicator, absY - 2);
                        _circleSelectionIndicator.IsVisible = true;
                    }
                }
                else
                {
                    if (_circleSelectionIndicator != null) _circleSelectionIndicator.IsVisible = false;
                    ResetPolygonIndicator(_lineSelectionIndicator);
                    ResetPolygonIndicator(_arrowSelectionIndicator);
                    if (_selectionIndicator != null)
                    {
                        _selectionIndicator.Width = w + 4;
                        _selectionIndicator.Height = h + 4;
                        Canvas.SetLeft(_selectionIndicator, absX - 2);
                        Canvas.SetTop(_selectionIndicator, absY - 2);
                        _selectionIndicator.IsVisible = true;
                    }
                }
            }
            
            bool isMultiGroup = _selectedControl is Canvas groupControl && groupControl.Tag as string == "MultiSelectGroup";
            if (isVector || isMultiGroup)
            {
                foreach (var handle in _resizeHandles) handle.IsVisible = false;
            }
            else
            {
                _resizeHandles[0].IsVisible = true; Canvas.SetLeft(_resizeHandles[0], absX - 7); Canvas.SetTop(_resizeHandles[0], absY - 7);
                _resizeHandles[1].IsVisible = true; Canvas.SetLeft(_resizeHandles[1], absX + w - 7); Canvas.SetTop(_resizeHandles[1], absY - 7);
                _resizeHandles[2].IsVisible = true; Canvas.SetLeft(_resizeHandles[2], absX + w - 7); Canvas.SetTop(_resizeHandles[2], absY + h - 7);
                _resizeHandles[3].IsVisible = true; Canvas.SetLeft(_resizeHandles[3], absX - 7); Canvas.SetTop(_resizeHandles[3], absY + h - 7);
            }

            if (isVector)
            {
                _startHandle.IsVisible = true;
                _endHandle.IsVisible = true;
                
                if (!TryGetVectorAbsolutePoints(_selectedControl, out var startP, out var endP))
                {
                    _startHandle.IsVisible = false;
                    _endHandle.IsVisible = false;
                    goto PostHandles;
                }
                
                double startOffset = HalfControlSize(_startHandle);
                double endOffset = HalfControlSize(_endHandle);
                Canvas.SetLeft(_startHandle, startP.X - startOffset); Canvas.SetTop(_startHandle, startP.Y - startOffset);
                Canvas.SetLeft(_endHandle, endP.X - endOffset); Canvas.SetTop(_endHandle, endP.Y - endOffset);
            }
            else
            {
                _startHandle.IsVisible = false; 
                _endHandle.IsVisible = false;
            }

        PostHandles:
            if (IsPixelateControl(_selectedControl))
            {
                if (_selectedControl.Tag is PixelateProperties props)
                {
                    SyncPixelateSlider(props.Strength);
                }
            }
            else
            {
                if (_pixelateStrengthHandle != null) _pixelateStrengthHandle.IsVisible = false;
            }

            if (_contextToolbar != null)
            {
                bool transformingSelection = _isDraggingSelected || _isResizing || _isDraggingStartPoint || _isDraggingEndPoint || _isDraggingStrengthHandle;
                _contextToolbar.IsVisible = !transformingSelection;
                if (!transformingSelection) PositionContextToolbar(absX, absY, w, h);
            }

            UpdateModeStatus();
        }

        private void PositionContextToolbar(double absX, double absY, double width, double height)
        {
            if (_contextToolbar == null || _canvas == null) return;

            double safeZoom = Math.Max(0.05, _zoomFactor);
            double invZoom = 1.0 / safeZoom;
            _contextToolbar.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            _contextToolbar.RenderTransform = new ScaleTransform(invZoom, invZoom);

            const double baseGap = 18.0;
            double gap = baseGap / safeZoom;
            double canvasW = _canvas.Bounds.Width > 0 ? _canvas.Bounds.Width : _canvas.Width;
            double canvasH = _canvas.Bounds.Height > 0 ? _canvas.Bounds.Height : _canvas.Height;
            if (double.IsNaN(canvasW) || canvasW <= 0) canvasW = _image?.Width ?? 0;
            if (double.IsNaN(canvasH) || canvasH <= 0) canvasH = _image?.Height ?? 0;

            _contextToolbar.Measure(new AvaloniaSize(double.PositiveInfinity, double.PositiveInfinity));
            double unscaledBarW = Math.Max(89, _contextToolbar.DesiredSize.Width);
            double unscaledBarH = Math.Max(40, _contextToolbar.DesiredSize.Height);
            double barW = unscaledBarW / safeZoom;
            double barH = unscaledBarH / safeZoom;

            var sideSpace = new[]
            {
                ("Right", canvasW - (absX + width)),
                ("Left", absX),
                ("Bottom", canvasH - (absY + height)),
                ("Top", absY)
            };

            string side = sideSpace
                .Where(item =>
                    ((item.Item1 == "Right" || item.Item1 == "Left") && item.Item2 >= barW + gap) ||
                    ((item.Item1 == "Bottom" || item.Item1 == "Top") && item.Item2 >= barH + gap))
                .OrderByDescending(item => item.Item2)
                .Select(item => item.Item1)
                .FirstOrDefault();

            if (side == null)
            {
                side = sideSpace.OrderByDescending(item => item.Item2).First().Item1;
            }

            double toolbarL;
            double toolbarT;
            switch (side)
            {
                case "Left":
                    toolbarL = absX - barW - gap;
                    toolbarT = absY + (height - barH) / 2;
                    break;
                case "Bottom":
                    toolbarL = absX + (width - barW) / 2;
                    toolbarT = absY + height + gap;
                    break;
                case "Top":
                    toolbarL = absX + (width - barW) / 2;
                    toolbarT = absY - barH - gap;
                    break;
                default:
                    toolbarL = absX + width + gap;
                    toolbarT = absY + (height - barH) / 2;
                    break;
            }

            double maxLeft = canvasW - barW;
            double clampedL = maxLeft > 0 ? Math.Clamp(toolbarL, 0, maxLeft) : (canvasW - barW) / 2;
            double clampedT = Math.Clamp(toolbarT, 0, Math.Max(0, canvasH - barH));
            Canvas.SetLeft(_contextToolbar, clampedL);
            Canvas.SetTop(_contextToolbar, clampedT);

            double lum = SampleUnderlyingLuminance(clampedL, clampedT, barW, barH);
            ApplyContextToolbarContrastTheme(_contextToolbar, lum);
        }

        private void ApplyContextToolbarContrastTheme(Border toolbar, double lum)
        {
            if (toolbar == null) return;

            ContextToolbarTheme.ApplyTheme(
                toolbar,
                lum,
                _contextDuplicateButton,
                _contextDeleteButton,
                _contextApplyButton,
                _contextDuplicateIcon,
                _contextDeleteIcon,
                _contextApplyIcon,
                _contextApplySeparator,
                _contextDuplicateSeparator);
        }

        private void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl == null) return;
            SaveUndoState(false);
            if (_selectedControl == null) return;
            
            if (!TryGetControlBounds(_selectedControl, out var origBounds)) return;

            var clone = CloneAnnotationControl(_selectedControl);
            if (clone != null)
            {
                clone.Width = origBounds.Width;
                clone.Height = origBounds.Height;

                double canvasW = _canvas.Bounds.Width;
                double canvasH = _canvas.Bounds.Height;
                
                bool isLeft = (origBounds.Center.X < canvasW / 2);
                bool isTop = (origBounds.Center.Y < canvasH / 2);
                
                double dirX = isLeft ? 1 : -1;
                double dirY = isTop ? 1 : -1;
                double offset = 85;
                double targetL = Canvas.GetLeft(_selectedControl);
                double targetT = Canvas.GetTop(_selectedControl);

                if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var start, out var end))
                {
                    var offsetVec = new AvaloniaPoint(dirX * offset, dirY * offset);
                    SetVectorAbsolutePoints(clone, start + offsetVec, end + offsetVec);
                }
                else
                {
                    targetL += dirX * offset;
                    targetT += dirY * offset;
                    Canvas.SetLeft(clone, targetL);
                    Canvas.SetTop(clone, targetT);
                }

                if (clone is Border border && border.Child is TextBlock textBlock && GetToolFromControl(clone) == EditorTool.Counter)
                {
                    textBlock.Text = (_counterValue++).ToString();
                }

                if (IsPixelateControl(clone)) RefreshPixelateAnnotation(clone);
                if (IsHighlightControl(clone)) RefreshHighlightAnnotation(clone);
                _canvas.Children.Add(clone);
                _selectedControl = clone;
                
                SetCurrentTool(EditorTool.None);
                SyncToolButtonSelection();
                clone.Cursor = HandCursor;
                
                UpdateSelectionIndicator();
                SetEditorCursor(HandCursor);
                UpdateHoverIndicator(clone);
                
                OverlayHelper.ShowLightToast("Duplicated", this);
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl == null) return;
            try
            {
                var target = _selectedControl;
                _selectedControl = null;
                _hoveredControl = null;
                UpdateHoverIndicator(null);
                _canvas?.Focus();

                SaveUndoState(false);

                if (target is Canvas group && group.Tag as string == "MultiSelectGroup")
                {
                    foreach (var child in group.Children.OfType<AvaloniaControl>().ToList())
                    {
                        DisposeAnnotationResources(child);
                    }
                }
                else
                {
                    DisposeAnnotationResources(target);
                }

                _canvas?.Children.Remove(target);
                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
                OverlayHelper.ShowLightToast("Deleted", this);
                Log.Info("Object deleted via toolbar button.");
            }
            catch (Exception ex)
            {
                Log.Error("OnDeleteClick failed.", ex);
            }
        }

        private bool IsEditorOperationInProgress => System.Threading.Volatile.Read(ref _isBusy) != 0;

        private bool TryBeginEditorOperation()
        {
            return System.Threading.Interlocked.CompareExchange(ref _isBusy, 1, 0) == 0;
        }

        private void EndEditorOperation()
        {
            System.Threading.Interlocked.Exchange(ref _isBusy, 0);
        }

        
        private static int GetFinalActionCloseDelayMs()
        {
            return Math.Clamp(NotificationOverlayWindow.GetOverlayDurationMs(), 250, 1000);
        }

        private async void OnResizeClick(object sender, RoutedEventArgs e)
        {
            if (_image == null) return;
            int currentWidth;
            int currentHeight;
            lock (_imageLock)
            {
                if (_image == null) return;
                currentWidth = _image.Width;
                currentHeight = _image.Height;
            }

            var resizeWin = new ResizeWindow(currentWidth, currentHeight);
            await resizeWin.ShowDialog(this);
            if (resizeWin.IsConfirmed)
            {
                Log.Info($"Resizing image to {resizeWin.ResultWidth}x{resizeWin.ResultHeight}");
                await ApplyResize(resizeWin.ResultWidth, resizeWin.ResultHeight).ConfigureAwait(true);
            }
        }

        internal async Task ApplyResize(int resultWidth, int resultHeight)
        {
            if (_image == null || resultWidth < 5 || resultHeight < 5 || !TryBeginEditorOperation()) return;
            try
            {
                int oldWidth;
                int oldHeight;
                lock (_imageLock)
                {
                    if (_image == null) return;
                    oldWidth = _image.Width;
                    oldHeight = _image.Height;
                }

                if (oldWidth <= 0 || oldHeight <= 0 || (oldWidth == resultWidth && oldHeight == resultHeight))
                {
                    return;
                }

                if (_selectedControl is Canvas activeGroup && activeGroup.Tag as string == "MultiSelectGroup")
                {
                    UngroupMultiSelectGroup(activeGroup);
                }

                SaveUndoState(true);

                double scaleX = (double)resultWidth / oldWidth;
                double scaleY = (double)resultHeight / oldHeight;

                ScaleAnnotationsForResize(scaleX, scaleY);

                if (!await Task.Run(() => MutateImage(img => img.Mutate(x => x.Resize(resultWidth, resultHeight)))).ConfigureAwait(true)) return;

                if (_canvas != null)
                {
                    _canvas.Width = resultWidth;
                    _canvas.Height = resultHeight;
                }

                await UpdateDisplayAsync().ConfigureAwait(true);
                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
                ShowUndoAvailableHint();
                OverlayHelper.ShowLightToast("IMAGE RESIZED", this);
                RefitWindowToImage();
            }
            catch (Exception ex)
            {
                Log.Error("Resize failed", ex);
                OverlayHelper.ShowLightToast("RESIZE FAILED", this);
            }
            finally
            {
                EndEditorOperation();
            }
        }

        private void ScaleAnnotationsForResize(double scaleX, double scaleY)
        {
            double avgScale = (scaleX + scaleY) / 2.0;
            var userAnnotations = GetUserAnnotations();

            foreach (var child in userAnnotations)
            {
                if (IsVectorControl(child) && TryGetVectorAbsolutePoints(child, out var vStart, out var vEnd))
                {
                    var newStart = new AvaloniaPoint(vStart.X * scaleX, vStart.Y * scaleY);
                    var newEnd = new AvaloniaPoint(vEnd.X * scaleX, vEnd.Y * scaleY);

                    if (child is Avalonia.Controls.Shapes.Line line)
                    {
                        line.StrokeThickness = Math.Max(1.0, line.StrokeThickness * avgScale);
                    }
                    else if (child is Canvas arrowCanvas)
                    {
                        var arrowLine = arrowCanvas.Children.OfType<Avalonia.Controls.Shapes.Line>().FirstOrDefault();
                        if (arrowLine != null)
                        {
                            arrowLine.StrokeThickness = Math.Max(1.0, arrowLine.StrokeThickness * avgScale);
                        }
                    }

                    SetVectorAbsolutePoints(child, newStart, newEnd);
                }
                else if (child is Avalonia.Controls.Shapes.Polyline poly)
                {
                    double polyLeft = CanvasLeft(poly) * scaleX;
                    double polyTop = CanvasTop(poly) * scaleY;
                    Canvas.SetLeft(poly, polyLeft);
                    Canvas.SetTop(poly, polyTop);
                    poly.StrokeThickness = Math.Max(1.0, poly.StrokeThickness * avgScale);
                    if (poly.Points != null && poly.Points.Count > 0)
                    {
                        var newPts = new List<AvaloniaPoint>(poly.Points.Count);
                        foreach (var pt in poly.Points)
                        {
                            newPts.Add(new AvaloniaPoint(pt.X * scaleX, pt.Y * scaleY));
                        }
                        poly.Points = new Avalonia.Collections.AvaloniaList<AvaloniaPoint>(newPts);
                        poly.InvalidateVisual();
                    }
                }
                else if (child is Border border)
                {
                    Canvas.SetLeft(border, CanvasLeft(border) * scaleX);
                    Canvas.SetTop(border, CanvasTop(border) * scaleY);
                    if (!double.IsNaN(border.Width) && border.Width > 0)
                        border.Width = Math.Max(10, border.Width * scaleX);
                    if (!double.IsNaN(border.Height) && border.Height > 0)
                        border.Height = Math.Max(10, border.Height * scaleY);

                    if (border.Child is TextBox tb)
                    {
                        tb.FontSize = Math.Max(8, tb.FontSize * avgScale);
                        if (tb.Tag is TextAnnotationProperties tap)
                        {
                            tap.PreferredFontSize = Math.Max(8, tap.PreferredFontSize * avgScale);
                        }
                        SetTextBoxBoundsToBorder(border, tb);
                        FitTextBoxToBorder(border, tb);
                    }
                    else if (border.Child is TextBlock counterTxt)
                    {
                        double avgSize = (border.Width + border.Height) / 2.0;
                        border.CornerRadius = new CornerRadius(avgSize / 2.0);
                        counterTxt.FontSize = Math.Max(8, avgSize * 0.6);
                    }
                }
                else if (child is TextBlock textBlock)
                {
                    Canvas.SetLeft(textBlock, CanvasLeft(textBlock) * scaleX);
                    Canvas.SetTop(textBlock, CanvasTop(textBlock) * scaleY);
                    if (!double.IsNaN(textBlock.Width) && textBlock.Width > 0)
                        textBlock.Width = Math.Max(10, textBlock.Width * scaleX);
                    if (!double.IsNaN(textBlock.Height) && textBlock.Height > 0)
                        textBlock.Height = Math.Max(10, textBlock.Height * scaleY);
                    textBlock.FontSize = Math.Max(8, textBlock.FontSize * avgScale);
                }
                else if (child is Avalonia.Controls.Shapes.Shape shape)
                {
                    Canvas.SetLeft(shape, CanvasLeft(shape) * scaleX);
                    Canvas.SetTop(shape, CanvasTop(shape) * scaleY);
                    double curW = double.IsNaN(shape.Width) || shape.Width <= 0 ? shape.Bounds.Width : shape.Width;
                    double curH = double.IsNaN(shape.Height) || shape.Height <= 0 ? shape.Bounds.Height : shape.Height;
                    if (curW > 0) shape.Width = Math.Max(5, curW * scaleX);
                    if (curH > 0) shape.Height = Math.Max(5, curH * scaleY);
                    shape.StrokeThickness = Math.Max(1.0, shape.StrokeThickness * avgScale);
                }
                else
                {
                    Canvas.SetLeft(child, CanvasLeft(child) * scaleX);
                    Canvas.SetTop(child, CanvasTop(child) * scaleY);
                    if (!double.IsNaN(child.Width) && child.Width > 0)
                        child.Width = Math.Max(5, child.Width * scaleX);
                    if (!double.IsNaN(child.Height) && child.Height > 0)
                        child.Height = Math.Max(5, child.Height * scaleY);
                }
            }
        }

        private async Task<DownloadTarget> GetEffectiveDownloadPathAsync()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            return await EditorExportService.ResolveDownloadTargetAsync(
                config.UserDownloadPath,
                folderPicker: async () =>
                {
                    if (this.StorageProvider.CanPickFolder)
                    {
                        var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = "FreeSnip: Select Default Save Folder",
                            AllowMultiple = false
                        }).ConfigureAwait(true);
                        return folders?.FirstOrDefault()?.TryGetLocalPath();
                    }
                    return null;
                },
                onUserPathConfigured: path =>
                {
                    config.UserDownloadPath = path;
                    IniConfig.Save();
                }
            ).ConfigureAwait(true);
        }

        private async void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            await SaveToDownloadsAsync().ConfigureAwait(true);
        }

        private async Task<bool> SaveToDownloadsAsync()
        {
            if (_image == null || !TryBeginEditorOperation()) return false;
            bool saved = false;
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info("[STEP:START] SaveToDownloadsAsync - Beginning image export and double-clipboard synchronization.");
            try
            {
                var flattenSw = System.Diagnostics.Stopwatch.StartNew();
                using var tempImage = await GetFlattenedImageAsync().ConfigureAwait(true);
                flattenSw.Stop();
                if (tempImage == null)
                {
                    Log.Error($"[STEP:FAIL] SaveToDownloadsAsync ({flattenSw.ElapsedMilliseconds}ms) - Failed to acquire flattened image.");
                    return false;
                }
                Log.Info($"[STEP:SUCCESS] SaveToDownloadsAsync - Image flattened ({tempImage.Width}x{tempImage.Height}) in {flattenSw.ElapsedMilliseconds}ms.");

                var config = IniConfig.GetIniSection<CoreConfiguration>();
                string fileName = EditorExportService.GenerateDownloadFileName(_sourceTitle, config.OutputFileAllowPng);

                DownloadTarget target = await GetEffectiveDownloadPathAsync().ConfigureAwait(true);
                bool savedToDownloads = target.IsDownloadsFolder;
                await Task.Run(() => Directory.CreateDirectory(target.Path)).ConfigureAwait(true);
                string downloadedFilePath = Path.Combine(target.Path, fileName);

                var saveSw = System.Diagnostics.Stopwatch.StartNew();
                await EditorExportService.SaveImageAsync(tempImage, downloadedFilePath, config.OutputFileAllowPng, config.OutputFileJpegQuality).ConfigureAwait(true);
                saveSw.Stop();
                saved = true;

                long fileSizeBytes = 0;
                try { fileSizeBytes = new FileInfo(downloadedFilePath).Length; } catch { }
                Log.Info($"[STEP:SUCCESS] SaveToDownloadsAsync - Saved image file '{downloadedFilePath}' ({fileSizeBytes} bytes) in {saveSw.ElapsedMilliseconds}ms.");

                await EditorExportService.SaveToHistoryBackupAsync(fileName, tempImage, config.KeepBackup, config.OutputFileAllowPng, config.OutputFileJpegQuality).ConfigureAwait(true);

                // --- DOUBLE CLIPBOARD SEQUENCE (Issue 2) ---
                // 1. Stage CF_DIB of image into clipboard first
                Log.Info($"[STEP:START] SaveToDownloadsAsync.Clipboard - Staging CF_DIB image into clipboard for '{downloadedFilePath}'.");
                var dibSw = System.Diagnostics.Stopwatch.StartNew();
                await UiClipboard.SetImageAsync(tempImage, markFreeSnipEditorImage: true).ConfigureAwait(true);
                dibSw.Stop();
                Log.Info($"[STEP:SUCCESS] SaveToDownloadsAsync.Clipboard ({dibSw.ElapsedMilliseconds}ms) - CF_DIB image staged onto clipboard.");

                // 2. Probe CF_DIB format available on clipboard to prevent race conditions
                bool dibProbed = UiClipboard.ProbeClipboardFormat(UiClipboard.CF_DIB_FORMAT, 150);
                Log.Info($"[STEP:{(dibProbed ? "SUCCESS" : "FAIL")}] SaveToDownloadsAsync.Clipboard - Probed CF_DIB format available: {dibProbed}.");

                // 3. Immediately insert plain text ONLY wrapped with two " marks from its side of the full path
                string quotedPath = $"\"{downloadedFilePath}\"";
                Log.Info($"[STEP:START] SaveToDownloadsAsync.Clipboard - Setting plain text only quoted path: {quotedPath}");
                var textSw = System.Diagnostics.Stopwatch.StartNew();
                bool textSetSuccess = await UiClipboard.SetPlainTextOnlyAsync(quotedPath).ConfigureAwait(true);
                textSw.Stop();
                Log.Info($"[STEP:{(textSetSuccess ? "SUCCESS" : "FAIL")}] SaveToDownloadsAsync.Clipboard ({textSw.ElapsedMilliseconds}ms) - Plain text quoted path set (Success={textSetSuccess}).");

                bool textProbed = UiClipboard.ProbeClipboardFormat(UiClipboard.CF_UNICODETEXT_FORMAT, 150);
                Log.Info($"[STEP:{(textProbed ? "SUCCESS" : "FAIL")}] SaveToDownloadsAsync.Clipboard - Probed CF_UNICODETEXT format available: {textProbed}.");

                string overlayMessage = savedToDownloads
                    ? "IMAGE SAVED TO DOWNLOADS"
                    : "IMAGE SAVED TO FREESNIP TEMP FOLDER";

                Dispatcher.UIThread.Post(() => {
                    OverlayHelper.ShowNotification(overlayMessage, this);
                    if (!savedToDownloads)
                    {
                        OverlayHelper.ShowLightToast("Saved to FreeSnip Temp Folder (Downloads unavailable)", this);
                    }
                });

                totalSw.Stop();
                Log.Info($"[STEP:SUCCESS] SaveToDownloadsAsync - Finished entire export and double-clipboard sequence in {totalSw.ElapsedMilliseconds}ms.");

                if (config.CloseEditorOnAction)
                {
                    await Task.Delay(GetFinalActionCloseDelayMs()).ConfigureAwait(true);

                    Dispatcher.UIThread.Post(() => {
                        _forceClose = true;
                        Close();
                    });
                }
                else
                {
                    Dispatcher.UIThread.Post(() => {
                        this.Activate();
                        this.Focus();
                        _canvas?.Focus();
                    });
                }
            }
            catch (Exception ex)
            {
                saved = false;
                totalSw.Stop();
                Log.Fatal($"[STEP:FAIL] SaveToDownloadsAsync ({totalSw.ElapsedMilliseconds}ms) - [DOWNLOAD_CRITICAL_ERROR]", ex);
                Dispatcher.UIThread.Post(() => {
                    OverlayHelper.ShowNotification("Save Failed", this);
                });
            }
            finally
            {
                EndEditorOperation();
            }

            return saved;
        }

        private sealed class AnnotationClipboardModel
        {
            public string ShapeType { get; set; } = string.Empty;
            public double Width { get; set; }
            public double Height { get; set; }
            public double SourceLeft { get; set; }
            public double SourceTop { get; set; }
            public string StrokeColorHex { get; set; }
            public double StrokeThickness { get; set; }
            public bool HasFill { get; set; }
            public string FillColorHex { get; set; }
            public string TextContent { get; set; }
            public double FontSize { get; set; }
            public string Emoji { get; set; }
            public int? CounterValue { get; set; }
            public bool IsVector { get; set; }
            public AvaloniaPoint VectorStart { get; set; }
            public AvaloniaPoint VectorEnd { get; set; }
            public AvaloniaControl Prototype { get; set; }
        }

        private static AnnotationClipboardModel _annotationClipboard;
        private static int _clipboardPasteIteration = 1;
        private static uint _annotationClipboardSequence = 0;

        private AnnotationClipboardModel CreateAnnotationClipboardModel(AvaloniaControl control)
        {
            if (control == null) return null;

            TryGetControlBounds(control, out var bounds);
            double left = Canvas.GetLeft(control);
            if (double.IsNaN(left)) left = bounds.X;
            double top = Canvas.GetTop(control);
            if (double.IsNaN(top)) top = bounds.Y;
            double width = bounds.Width > 0 ? bounds.Width : control.Width;
            double height = bounds.Height > 0 ? bounds.Height : control.Height;

            AvaloniaPoint vStart = default;
            AvaloniaPoint vEnd = default;
            bool isVector = IsVectorControl(control) && TryGetVectorAbsolutePoints(control, out vStart, out vEnd);

            string strokeColorHex = null;
            double strokeThickness = 0;
            bool hasFill = false;
            string fillColorHex = null;
            string textContent = null;
            double fontSize = 0;
            string emoji = null;
            int? counterValue = null;

            if (control is Avalonia.Controls.Shapes.Shape shape)
            {
                strokeThickness = shape.StrokeThickness;
                if (shape.Stroke is ISolidColorBrush sb) strokeColorHex = sb.Color.ToString();
                hasFill = shape.Fill != null && shape.Fill != Brushes.Transparent;
                if (shape.Fill is ISolidColorBrush fb) fillColorHex = fb.Color.ToString();
            }
            else if (control is Border border)
            {
                strokeThickness = border.BorderThickness.Left;
                if (border.BorderBrush is ISolidColorBrush bb) strokeColorHex = bb.Color.ToString();
                hasFill = border.Background != null && border.Background != Brushes.Transparent;
                if (border.Background is ISolidColorBrush bbg) fillColorHex = bbg.Color.ToString();

                if (border.Child is TextBox tb)
                {
                    textContent = tb.Text;
                    fontSize = tb.FontSize;
                }
                else if (border.Child is TextBlock tblk)
                {
                    textContent = tblk.Text;
                    fontSize = tblk.FontSize;
                    if (GetToolFromControl(control) == EditorTool.Counter && int.TryParse(tblk.Text, out int cVal))
                    {
                        counterValue = cVal;
                    }
                }
            }
            else if (control is Canvas arrowCanvas && arrowCanvas.Tag is ArrowProperties)
            {
                strokeThickness = GetVectorThickness(control);
                var line = arrowCanvas.Children.OfType<Avalonia.Controls.Shapes.Line>().FirstOrDefault();
                if (line?.Stroke is ISolidColorBrush ab) strokeColorHex = ab.Color.ToString();
            }

            if (control is TextBlock directTb)
            {
                textContent = directTb.Text;
                fontSize = directTb.FontSize;
                if (GetToolFromControl(control) == EditorTool.Emoji)
                {
                    emoji = directTb.Text;
                }
            }

            string shapeType = control switch
            {
                _ when IsArrowControl(control) => "Arrow",
                _ when IsPixelateControl(control) => "Pixelate",
                _ when IsHighlightControl(control) => "Highlight",
                _ when GetToolFromControl(control) == EditorTool.Counter => "Counter",
                _ when GetToolFromControl(control) == EditorTool.Text || (control is Border b && b.Child is TextBox) => "Text",
                _ when GetToolFromControl(control) == EditorTool.Emoji => "Emoji",
                Avalonia.Controls.Shapes.Line => "Line",
                Avalonia.Controls.Shapes.Rectangle => "Rectangle",
                Avalonia.Controls.Shapes.Ellipse => "Ellipse",
                Avalonia.Controls.Shapes.Polyline => "Polyline",
                Avalonia.Controls.Shapes.Polygon => "Polygon",
                Avalonia.Controls.Image => "Image",
                _ => control.GetType().Name
            };

            var prototype = CloneAnnotationControl(control);
            if (prototype == null) return null;

            return new AnnotationClipboardModel
            {
                ShapeType = shapeType,
                Width = width,
                Height = height,
                SourceLeft = left,
                SourceTop = top,
                StrokeColorHex = strokeColorHex,
                StrokeThickness = strokeThickness,
                HasFill = hasFill,
                FillColorHex = fillColorHex,
                TextContent = textContent,
                FontSize = fontSize,
                Emoji = emoji,
                CounterValue = counterValue,
                IsVector = isVector,
                VectorStart = isVector ? vStart : default,
                VectorEnd = isVector ? vEnd : default,
                Prototype = prototype
            };
        }

        private async void HandleContextualCopy(bool allowCloseOnAction = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_isOcrInteractiveMode)
            {
                if (_selectedOcrWords != null && _selectedOcrWords.Count > 0)
                {
                    string textToCopy = OcrTextLayout.BuildVisualSelectionText(_selectedOcrWords);
                    await UiClipboard.SetTextAsync(textToCopy).ConfigureAwait(true);
                    sw.Stop();
                    Log.Info($"[STEP:SUCCESS] ContextualCopy.Ocr ({sw.ElapsedMilliseconds}ms) - Copied {textToCopy.Length} chars of recognized OCR text.");
                    OverlayHelper.ShowNotification("TEXT COPIED", this);
                }
                else
                {
                    sw.Stop();
                    Log.Info($"[STEP:INFO] ContextualCopy.Ocr ({sw.ElapsedMilliseconds}ms) - No OCR words selected.");
                    OverlayHelper.ShowLightToast("NO TEXT SELECTED", this);
                }
                return;
            }

            if (_selectedControl != null)
            {
                var model = CreateAnnotationClipboardModel(_selectedControl);
                if (model != null)
                {
                    _annotationClipboard = model;
                    _clipboardPasteIteration = 1;
                    _annotationClipboardSequence = UiClipboard.GetCurrentSequenceNumber();
                    UiClipboard.SetInMemoryAnnotationMarker(true);

                    sw.Stop();
                    Log.Info($"[STEP:SUCCESS] ContextualCopy ({sw.ElapsedMilliseconds}ms) - Copied {model.ShapeType} (Bounds: {model.SourceLeft:F0},{model.SourceTop:F0},{model.Width:F0}x{model.Height:F0}, Seq: {_annotationClipboardSequence}) to internal clipboard.");
                    return;
                }
                else
                {
                    sw.Stop();
                    Log.Warn($"[STEP:FAIL] ContextualCopy ({sw.ElapsedMilliseconds}ms) - Could not construct clipboard model for selected control.");
                }
            }

            // Nothing selected: Ctrl+C does nothing. Do not implicitly copy flattened image.
            sw.Stop();
            Log.Info($"[STEP:INFO] ContextualCopy ({sw.ElapsedMilliseconds}ms) - No object selected; clipboard unchanged.");
        }

        private async Task CopyFlattenedImageAsync(bool allowCloseOnAction)
        {
            if (_image == null || !TryBeginEditorOperation()) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info("[STEP:START] CopyFlattenedImageAsync - Flattening image for clipboard transfer.");
            try
            {
                using var tempImage = await GetFlattenedImageAsync().ConfigureAwait(true);
                if (tempImage == null)
                {
                    Log.Error($"[STEP:FAIL] CopyFlattenedImageAsync ({sw.ElapsedMilliseconds}ms) - Failed to get flattened image.");
                    return;
                }

                var config = IniConfig.GetIniSection<CoreConfiguration>();
                string backupFileName = EditorExportService.GenerateClipboardBackupFileName(config.OutputFileAllowPng);
                await EditorExportService.SaveToHistoryBackupAsync(backupFileName, tempImage, config.KeepBackup, config.OutputFileAllowPng, config.OutputFileJpegQuality).ConfigureAwait(true);

                await UiClipboard.SetImageAsync(tempImage, true).ConfigureAwait(true);

                Dispatcher.UIThread.Post(() => {
                    OverlayHelper.ShowNotification("IMAGE SAVED TO CLIPBOARD", this);
                });

                sw.Stop();
                Log.Info($"[STEP:SUCCESS] CopyFlattenedImageAsync ({sw.ElapsedMilliseconds}ms) - Image copied to clipboard.");

                if (allowCloseOnAction && config.CloseEditorOnAction)
                {
                    await Task.Delay(GetFinalActionCloseDelayMs()).ConfigureAwait(true);

                    Dispatcher.UIThread.Post(() => {
                        _forceClose = true;
                        Close();
                    });
                }
                else
                {
                    Dispatcher.UIThread.Post(() => {
                        this.Activate();
                        this.Focus();
                        _canvas?.Focus();
                    });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log.Fatal($"[STEP:FAIL] CopyFlattenedImageAsync ({sw.ElapsedMilliseconds}ms) - [COPY_CRITICAL_ERROR]", ex);
                Dispatcher.UIThread.Post(() => {
                    OverlayHelper.ShowNotification("Copy Failed", this);
                });
            }
            finally
            {
                EndEditorOperation();
            }
        }

        private async void OnCopyClick(object sender, RoutedEventArgs e)
        {
            await CopyFlattenedImageAsync(allowCloseOnAction: true).ConfigureAwait(true);
        }

        private void RotateSelectedControl(double angleDelta)
        {
            if (_selectedControl == null) return;
            SaveUndoState(false);

            if (IsVectorControl(_selectedControl) && TryGetVectorAbsolutePoints(_selectedControl, out var start, out var end))
            {
                double cx = (start.X + end.X) / 2.0;
                double cy = (start.Y + end.Y) / 2.0;
                double rad = angleDelta * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);

                double sDx = start.X - cx;
                double sDy = start.Y - cy;
                var newStart = new AvaloniaPoint(cx + sDx * cos - sDy * sin, cy + sDx * sin + sDy * cos);

                double eDx = end.X - cx;
                double eDy = end.Y - cy;
                var newEnd = new AvaloniaPoint(cx + eDx * cos - eDy * sin, cy + eDx * sin + eDy * cos);

                SetVectorAbsolutePoints(_selectedControl, newStart, newEnd);
            }
            else
            {
                var transform = _selectedControl.RenderTransform as RotateTransform;
                if (transform == null)
                {
                    transform = new RotateTransform();
                    _selectedControl.RenderTransform = transform;
                    _selectedControl.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                }
                transform.Angle = (transform.Angle + angleDelta) % 360;
                if (transform.Angle < 0) transform.Angle += 360;
            }

            UpdateSelectionIndicator();
            ShowUndoAvailableHint();
            OverlayHelper.ShowLightToast($"ROTATED {Math.Abs(angleDelta)}°", this);
        }

        private async void OnRotateCwClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl != null)
            {
                RotateSelectedControl(90);
                return;
            }
            await RotateImageAsync(RotateMode.Rotate90);
        }

        private async void OnRotateCcwClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl != null)
            {
                RotateSelectedControl(-90);
                return;
            }
            await RotateImageAsync(RotateMode.Rotate270);
        }

        private async void OnOcrClick(object sender, RoutedEventArgs e)
        {
            if (_image == null || !TryBeginEditorOperation()) return;
            var ocrButton = _ocrButton;
            var ocrButtonText = _ocrButtonText;
            var config = IniConfig.GetIniSection<CoreConfiguration>();

            if (_isOcrInteractiveMode)
            {
                _isOcrInteractiveMode = false;
                if (ocrButtonText != null) ocrButtonText.Text = "OCR Text Extraction";
                if (ocrButton != null) ocrButton.Background = Brushes.Transparent;
                SetEditorCursor(ArrowCursor);
                ToggleToolbarControls(false);
                ClearOcrVisuals();
                _interactiveOcrInfo = null;
                EndEditorOperation();
                return;
            }

            try
            {
                if (ocrButton != null) ocrButton.IsEnabled = false;
                if (ocrButtonText != null) ocrButtonText.Text = "OCR Working...";

                Log.Info("STAGED OCR INITIATED");
                using var tempImage = await GetFlattenedImageForOcrAsync().ConfigureAwait(true);
                if (tempImage == null) return;
                
                var providers = SimpleServiceProvider.Current.GetAllInstances<IOcrProvider>();
                var ocrProvider = OcrProviderSelector.Select(providers, config.OcrEngine);
                if (ocrProvider == null)
                {
                    Dispatcher.UIThread.Post(() => OverlayHelper.ShowNotification("OCR UNAVAILABLE", this));
                    return;
                }

                var ocrInfo = await ocrProvider.DoOcrAsync(tempImage).ConfigureAwait(true);
                if (ocrInfo == null)
                {
                    Dispatcher.UIThread.Post(() => OverlayHelper.ShowNotification("OCR UNAVAILABLE", this));
                    return;
                }

                if (config.LeavePictureAsIsDuringOcr)
                {
                    Dispatcher.UIThread.Post(() => {
                        _interactiveOcrInfo = ocrInfo;
                        if (_interactiveOcrInfo.Words == null || _interactiveOcrInfo.Words.Count == 0)
                        {
                            OverlayHelper.ShowNotification("No Selectable Text Found", this);
                            _isOcrInteractiveMode = false;
                        }
                        else
                        {
                            _isOcrInteractiveMode = true;
                            if (ocrButtonText != null) ocrButtonText.Text = "Exit OCR Mode";
                            if (ocrButton != null) ocrButton.Background = new SolidColorBrush(AvaloniaColor.Parse("#B23A3A"));
                            SetEditorCursor(IbeamCursor);
                            ToggleToolbarControls(true);
                            PaintOcrWords();
                        }
                    });
                }
                else
                {
                    string text = ocrInfo?.Text ?? "";
                    
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        OverlayHelper.ShowNotification("No Text Found", this);
                        return;
                    }
                    await UiClipboard.SetTextAsync(text).ConfigureAwait(true);

                    if (config.KeepBackup)
                    {
                        string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
                        await Task.Run(() => Directory.CreateDirectory(tempDir)).ConfigureAwait(true);
                        string fileName = $"OCR_{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}.txt";
                        string fullPath = Path.Combine(tempDir, fileName);
                        
                        await File.WriteAllTextAsync(fullPath, text).ConfigureAwait(true);
                        _ = Task.Run(() => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{fullPath}\"") { UseShellExecute = true }));
                        
                        Dispatcher.UIThread.Post(() => OverlayHelper.ShowNotification("TEXT COPIED & SAVED", this));
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() => OverlayHelper.ShowNotification("TEXT COPIED", this));
                    }
                    
                    if (config.CloseEditorOnAction)
                    {
                        await Task.Delay(GetFinalActionCloseDelayMs()).ConfigureAwait(true);
                        Dispatcher.UIThread.Post(() => {
                            _forceClose = true;
                            Close();
                        });
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() => {
                            this.Activate();
                            this.Focus();
                            _canvas?.Focus();
                        });
                    }
                }
            }
            catch (Exception ex) 
            { 
                Log.Error("OCR failed", ex); 
                Dispatcher.UIThread.Post(() => OverlayHelper.ShowNotification("OCR FAILED", this)); 
            }
            finally
            {
                Dispatcher.UIThread.Post(() => {
                    if (!_isOcrInteractiveMode)
                    {
                        if (ocrButtonText != null) ocrButtonText.Text = "OCR Text Extraction";
                        if (ocrButton != null) ocrButton.Background = Brushes.Transparent;
                        ToggleToolbarControls(false);
                    }
                    if (ocrButton != null) ocrButton.IsEnabled = true;
                    EndEditorOperation();
                });
            }
        }
        
        private void ToggleToolbarControls(bool isOcrMode)
        {
            var bottomToolbar = _bottomToolbarBorder;
            var leftToolbar = _leftToolbar;
            var topToolbarStackPanel = _topToolbarStackPanel;
            var clearAllBtn = _clearAllBtn;

            if (bottomToolbar != null) 
            {
                bottomToolbar.IsEnabled = !isOcrMode;
                bottomToolbar.Opacity = isOcrMode ? 0.0 : 1.0;
                bottomToolbar.IsVisible = !isOcrMode;
            }
            
            if (leftToolbar != null) 
            {
                leftToolbar.IsEnabled = !isOcrMode;
                leftToolbar.Opacity = isOcrMode ? 0.0 : 1.0;
                leftToolbar.IsVisible = !isOcrMode;
            }

            if (topToolbarStackPanel != null)
            {
                foreach (var child in topToolbarStackPanel.Children)
                {
                    if (child is Avalonia.Controls.Control ctrl && ctrl.Name != "OcrButton")
                    {
                        ctrl.IsEnabled = !isOcrMode;
                        ctrl.Opacity = isOcrMode ? 0.0 : 1.0;
                    }
                }
            }

            if (clearAllBtn != null)
            {
                clearAllBtn.IsEnabled = !isOcrMode;
                clearAllBtn.Opacity = isOcrMode ? 0.0 : 1.0;
                clearAllBtn.IsVisible = !isOcrMode;
            }
            
            if (_contextToolbar != null && isOcrMode)
            {
                _contextToolbar.IsVisible = false;
            }
        }

        private double SampleUnderlyingLuminance(double x, double y, double width, double height)
        {
            lock (_imageLock)
            {
                return OcrOverlayController.SampleUnderlyingLuminance(_image, x, y, width, height);
            }
        }

        private void ApplyOcrToolbarContrastTheme(Border ocrToolbar, double lum)
        {
            var buttons = new[]
            {
                _ocrContextCopyButton,
                _ocrContextOpenLinkButton,
                _ocrContextEmailButton
            };
            OcrOverlayController.ApplyContrastTheme(ocrToolbar, lum, buttons);
        }
        
        private int FindClosestOcrWordIndex(AvaloniaPoint pos)
        {
            return _ocrController.FindClosestWordIndex(pos);
        }

        private void UpdateOcrSelectionVisuals(bool refreshAll = false)
        {
            _ocrController.UpdateSelectionVisuals(refreshAll);
        }

        private void PaintOcrWords()
        {
            _ocrController.PaintWords(_canvas);
        }

        private void ClearOcrVisuals()
        {
            _ocrController.ClearVisuals(_canvas);
            _ocrController.HideContextToolbar(_ocrContextToolbar);
        }

        private void UpdateOcrContextToolbarButtons(string text)
        {
            var (hasLink, hasEmail) = OcrOverlayController.DetectActions(text);
            if (_ocrContextOpenLinkButton != null) _ocrContextOpenLinkButton.IsVisible = hasLink;
            if (_ocrContextEmailButton != null) _ocrContextEmailButton.IsVisible = hasEmail;
        }

        private void OnOcrContextOpenLinkClick(object sender, RoutedEventArgs e)
        {
            if (_selectedOcrWords.Count == 0) return;
            string text = OcrTextLayout.BuildVisualSelectionText(_selectedOcrWords).Trim();
            if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
            _ = Task.Run(() => Process.Start(new ProcessStartInfo(text) { UseShellExecute = true }));
            ClearOcrContext();
        }

        private void OnOcrContextEmailClick(object sender, RoutedEventArgs e)
        {
            if (_selectedOcrWords.Count == 0) return;
            string text = OcrTextLayout.BuildVisualSelectionText(_selectedOcrWords).Trim();
            _ = Task.Run(() => Process.Start(new ProcessStartInfo("mailto:" + text) { UseShellExecute = true }));
            ClearOcrContext();
        }

        private void ClearOcrContext()
        {
            _ocrController.ClearSelection();
            _ocrController.HideContextToolbar(_ocrContextToolbar);
        }

        private async void OnOcrContextCopyClick(object sender, RoutedEventArgs e)
        {
            if (_selectedOcrWords.Count == 0) return;
            
            string textToCopy = OcrTextLayout.BuildVisualSelectionText(_selectedOcrWords);
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null) await topLevel.Clipboard.SetTextAsync(textToCopy);
            
            OverlayHelper.ShowNotification("TEXT COPIED", this);
            
            ClearOcrContext();
        }

        private async void OnUndoClick(object sender, RoutedEventArgs e)
        {
            await PerformUndoAsync().ConfigureAwait(true);
        }

        internal async Task<bool> PerformUndoAsync()
        {
            var previous = _historyManager.PeekUndo();
            if (previous == null) return false;
            bool cloneImage = previous.Image != null;
            if (_historyManager.TryUndo(CreateSnapshot(cloneImage), out var toRestore))
            {
                await RestoreSnapshotAsync(toRestore!).ConfigureAwait(true);
                UpdateUndoRedoState();
                return true;
            }
            return false;
        }

        private async void OnRedoClick(object sender, RoutedEventArgs e)
        {
            await PerformRedoAsync().ConfigureAwait(true);
        }

        internal async Task<bool> PerformRedoAsync()
        {
            var next = _historyManager.PeekRedo();
            if (next == null) return false;
            bool cloneImage = next.Image != null;
            if (_historyManager.TryRedo(CreateSnapshot(cloneImage), out var toRestore))
            {
                await RestoreSnapshotAsync(toRestore!).ConfigureAwait(true);
                UpdateUndoRedoState();
                return true;
            }
            return false;
        }

        private void OnToolClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                bool toggleIfActive = _selectedControl == null;
                SelectToolButton(btn, toggleIfActive);
            }
        }

        private void SelectToolButton(Button btn, bool toggleIfActive)
        {
            if (btn.Tag is not string toolStr || !Enum.TryParse<EditorTool>(toolStr, out var tool)) return;

            if (_selectedControl != null)
            {
                if (IsPastedImageControl(_selectedControl)) FinalizeSelectedPasteObject();
                if (_selectedControl is Border b && b.Child is TextBox tb)
                {
                    SetTextBoxBoundsToBorder(b, tb);
                    tb.IsReadOnly = true;
                    tb.CaretBrush = Brushes.Transparent;
                    tb.Cursor = SizeAllCursor;
                }
                if (_selectedControl.Tag as string == "CropObject")
                {
                    DisposeAnnotationResources(_selectedControl);
                    _canvas?.Children.Remove(_selectedControl);
                }
                _selectedControl = null;
                UpdateSelectionIndicator();
                UpdateHoverIndicator(null);
            }

            if (tool != EditorTool.Crop && _canvas != null)
            {
                var orphanCrop = _canvas.Children.FirstOrDefault(c => c.Tag as string == "CropObject");
                if (orphanCrop != null)
                {
                    DisposeAnnotationResources(orphanCrop);
                    _canvas.Children.Remove(orphanCrop);
                }
            }

            if (_currentTool == tool && toggleIfActive)
            {
                SetCurrentTool(EditorTool.None);
                SetEditorCursor(HandCursor);
                HideToolGhost();
                HideSnapGuides();
                btn.Classes.Remove("selected");
            }
            else
            {
                SetCurrentTool(tool);
                SetEditorCursor(CrossCursor);

                if (_leftToolbar != null)
                {
                    foreach (var child in _leftToolbar.Children)
                    {
                        if (child is Button b) b.Classes.Remove("selected");
                    }
                }

                _cropTool?.Classes.Remove("selected");
                btn.Classes.Add("selected");
            }

            _currentThickness = _toolThicknesses.TryGetValue(_currentTool, out var thickness) ? thickness : 3.0;
            _isFillMode = _toolFillModes.TryGetValue(_currentTool, out var fillMode) && fillMode;

            UpdateHoverIndicator(null);
            HideVectorInfo();
            UpdateFillToggleVisual();
            UpdateThicknessPanelVisibility();
            UpdateModeStatus();

            if (_toolBrushes.TryGetValue(_currentTool, out var brush) && brush is SolidColorBrush scb)
            {
                UpdateCurrentColorDisplay(scb.Color);
                UpdatePresetSelectionVisuals(scb.Color);
            }

            if (_currentTool == EditorTool.Crop && !_suppressCropModePopup) ShowCropModePopup();
            else HideCropModePopup();

            _ghostSettingsDirty = true; 
            _canvas?.Focus();
        }
        private void OnColorSelectClick(object sender, RoutedEventArgs e) { if (sender is Button btn && btn.Tag is string colorHex) { var color = AvaloniaColor.Parse(colorHex); ApplyColor(color); UpdateCurrentColorDisplay(color); } }

        private void OnColorConfirmClick(object sender, RoutedEventArgs e)
        {
            var picker = _colorPickerView;
            if (picker != null)
            {
                var color = picker.Color;
                UpdateCurrentColorDisplay(color);
                ApplyColor(color);
                PushColorToHistory(color);
            }
            _customColorBtn?.Flyout?.Hide();
        }

        private void OnColorCancelClick(object sender, RoutedEventArgs e)
        {
            _customColorBtn?.Flyout?.Hide();
        }

        private bool _syncingHexInput;

        
        private void InitializeCustomColorFlyout()
        {
            var picker = _colorPickerView;
            var hexInput = _hexInput;
            if (picker == null || hexInput == null) return;

            hexInput.Text = ToRgbHex(picker.Color);
            SyncHexPreview(picker.Color);

            picker.ColorChanged += (s, e) =>
            {
                _syncingHexInput = true;
                try
                {
                    hexInput.Text = ToRgbHex(e.NewColor);
                    SyncHexPreview(e.NewColor);
                }
                finally { _syncingHexInput = false; }
            };

            hexInput.TextChanged += (s, e) =>
            {
                if (_syncingHexInput) return;
                if (TryParseHexColor(hexInput.Text, out var parsed))
                {
                    picker.Color = parsed; 
                }
            };
        }

        private void SyncHexPreview(AvaloniaColor color)
        {
            var chip = _hexPreviewChip;
            if (chip != null) chip.Background = new SolidColorBrush(color);
        }

        private static bool TryParseHexColor(string text, out AvaloniaColor color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim().TrimStart('#');
            if (trimmed.Length != 6 && trimmed.Length != 8) return false;
            foreach (char c in trimmed)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }
            try { color = AvaloniaColor.Parse("#" + trimmed); return true; }
            catch { return false; }
        }

        private async void OnCopyHexClick(object sender, RoutedEventArgs e)
        {
            var picker = _colorPickerView;
            string hex = picker != null ? ToRgbHex(picker.Color) : "#000000";
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null) await topLevel.Clipboard.SetTextAsync(hex).ConfigureAwait(true);
                OverlayHelper.ShowLightToast($"COPIED {hex}", this);
            }
            catch (Exception ex)
            {
                OverlayHelper.ShowLightToast($"COPY FAILED: {ex.Message}", this);
            }
        }

        private async void OnCopyCurrentColorClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            string hex = ToRgbHex(_lastSelectedColor);
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null) await topLevel.Clipboard.SetTextAsync(hex).ConfigureAwait(true);
                OverlayHelper.ShowLightToast($"COPIED {hex}", this);
            }
            catch (Exception ex)
            {
                OverlayHelper.ShowLightToast($"COPY FAILED: {ex.Message}", this);
            }
        }

        private void OnHexInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var picker = _colorPickerView;
            if (picker != null)
            {
                UpdateCurrentColorDisplay(picker.Color);
                ApplyColor(picker.Color);
                PushColorToHistory(picker.Color);
            }
            _customColorBtn?.Flyout?.Hide();
            e.Handled = true;
        }

        private async void OnSamplerClick(object sender, RoutedEventArgs e)
        {
            _customColorBtn?.Flyout?.Hide();
            OverlayHelper.ShowLightToast("COLOR SAMPLER ACTIVE (Esc cancels)", this);
            var color = await ScreenColorSampler.PickColorAsync();
            if (color.HasValue)
            {
                var picker = _colorPickerView;
                if (picker != null) picker.Color = color.Value;
                UpdateCurrentColorDisplay(color.Value);
                _customColorBtn?.Flyout?.ShowAt(_customColorBtn);
            }
        }

        private static string ToRgbHex(AvaloniaColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private void UpdateCurrentColorDisplay(AvaloniaColor color)
        {
            var brush = new SolidColorBrush(color);
            var preview = _currentColorPreview;
            if (preview != null) preview.Background = brush;
            var hexText = _currentColorHex;
            if (hexText != null) hexText.Text = ToRgbHex(color);
            var button = _customColorBtn;
            if (button != null) ToolTip.SetTip(button, $"Current color {ToRgbHex(color)}");
        }

        private void PushColorToHistory(AvaloniaColor color)
        {
            string hex = color.ToString().ToUpperInvariant();
            if (hex.Length == 9 && hex.StartsWith("#FF")) hex = "#" + hex.Substring(3);
            
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            var colors = new List<string>((config.CustomColors ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            
            if (colors.Count > 0 && colors.Last() == hex) return;
            if (colors.Contains(hex)) colors.Remove(hex);
            
            colors.Add(hex);
            while (colors.Count > 6) colors.RemoveAt(0);
            
            config.CustomColors = string.Join(",", colors);
            IniConfig.Save();
            
            RefreshColorPresetsPanel();
        }

        private void RefreshColorPresetsPanel()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            var colors = new List<string>((config.CustomColors ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            var panel = _colorPresetsPanel;
            if (panel == null) return;
            
            var buttons = panel.Children.OfType<Button>().Where(b => b.Classes.Contains("color-preset")).ToList();
            for (int i = 0; i < buttons.Count; i++)
            {
                if (i < colors.Count)
                {
                    if (AvaloniaColor.TryParse(colors[i], out var parsedColor))
                    {
                        buttons[i].Background = new SolidColorBrush(parsedColor);
                        buttons[i].Tag = colors[i];
                        buttons[i].IsVisible = true;
                    }
                }
                else
                {
                    buttons[i].IsVisible = false;
                }
            }
        }

        private void ApplyColor(AvaloniaColor color)
        {
            _lastSelectedColor = color;
            _ghostSettingsDirty = true; 
            var brush = new SolidColorBrush(color);
            if (_selectedControl != null)
            {
                SaveUndoState(false);
                UpdateControlColor(_selectedControl, color);

                var inferredTool = GetToolFromControl(_selectedControl);
                if (inferredTool != EditorTool.None)
                {
                    if (inferredTool == EditorTool.Highlight) {
                        _toolBrushes[EditorTool.Highlight] = new SolidColorBrush(AvaloniaColor.FromArgb(80, color.R, color.G, color.B));
                    } else {
                        _toolBrushes[inferredTool] = brush;
                    }
                    if (inferredTool == EditorTool.Counter) { var config = IniConfig.GetIniSection<CoreConfiguration>(); config.LastCounterColor = ToRgbHex(color); IniConfig.Save(); }
                }
                OverlayHelper.ShowLightToast("COLOR UPDATED", this);
                ShowUndoAvailableHint();
            }
            else if (_currentTool != EditorTool.None)
            {
                if (_currentTool == EditorTool.Highlight) {
                    _toolBrushes[EditorTool.Highlight] = new SolidColorBrush(AvaloniaColor.FromArgb(80, color.R, color.G, color.B));
                } else {
                    _toolBrushes[_currentTool] = brush;
                }
                if (_currentTool == EditorTool.Counter) { var config = IniConfig.GetIniSection<CoreConfiguration>(); config.LastCounterColor = ToRgbHex(color); IniConfig.Save(); }
            }
            else if (_currentTool == EditorTool.None)
            {
                var config = IniConfig.GetIniSection<CoreConfiguration>(); 
                config.LastCounterColor = ToRgbHex(color); 
                IniConfig.Save();

                foreach (EditorTool tool in Enum.GetValues<EditorTool>())
                {
                    if (tool == EditorTool.None) continue;
                    if (tool == EditorTool.Highlight)
                        _toolBrushes[tool] = new SolidColorBrush(AvaloniaColor.FromArgb(80, color.R, color.G, color.B));
                    else
                        _toolBrushes[tool] = brush;
                }
            }
            UpdatePresetSelectionVisuals(color);
            SaveToolPreferences();
        }

        private EditorTool GetToolFromControl(AvaloniaControl control)
        {
            if (control is Avalonia.Controls.Shapes.Line) return EditorTool.Line;
            if (control is Avalonia.Controls.Shapes.Rectangle r) {
                if (IsHighlightControl(r)) return EditorTool.Highlight;
                if (r.Classes.Contains("highlight")) return EditorTool.Highlight;
                if (IsPixelateControl(r)) return EditorTool.Blur;
                return EditorTool.Rectangle;
            }
            if (control is Avalonia.Controls.Shapes.Ellipse) return EditorTool.Ellipse;
            if (control is Avalonia.Controls.Shapes.Polyline) return EditorTool.FreeDraw;
            if (control is Border b) {
                if (b.Child is TextBox) return EditorTool.Text;
                if (b.Child is TextBlock) return EditorTool.Counter;
            }
            if (control is TextBlock) return EditorTool.Emoji;
            if (IsArrowControl(control)) return EditorTool.Arrow;
            return EditorTool.None;
        }

        private void UpdateControlColor(AvaloniaControl control, AvaloniaColor color)
        {
            var brush = new SolidColorBrush(color); 
            var isHighlight = control.Classes.Contains("highlight"); 
            var targetBrush = isHighlight ? new SolidColorBrush(AvaloniaColor.FromArgb(80, color.R, color.G, color.B)) : brush;
            if (IsPixelateControl(control)) return;
            if (control is Avalonia.Controls.Shapes.Shape s) { 
                if (s is Avalonia.Controls.Shapes.Rectangle r && r.Tag is HighlightProperties highlightProps)
                {
                    highlightProps.Color = AvaloniaColor.FromArgb(255, color.R, color.G, color.B);
                    RefreshHighlightAnnotation(r);
                }
                else
                {
                    if (s.Fill != null) s.Fill = targetBrush;
                    if (s.Stroke != null || s.Fill == null) s.Stroke = brush;
                }
            }
            else if (control is Border b) { 
                if (b.Child is TextBlock) b.Background = brush; 
                else b.BorderBrush = brush; 
                if (b.Child is TextBox tb) { tb.Foreground = brush; tb.CaretBrush = brush; }
            }
            else if (control is TextBlock t) t.Foreground = brush;
            else if (control is Canvas c && c.Tag is ArrowProperties)
            {
                foreach (var child in c.Children)
                {
                    if (child is Avalonia.Controls.Shapes.Line line) line.Stroke = brush;
                    else if (child is Avalonia.Controls.Shapes.Polygon polygon) polygon.Fill = brush;
                }

                if (TryGetVectorAbsolutePoints(c, out var start, out var end)) UpdateArrowVisuals(c, start, end);
            }
            else if (control is Canvas group && group.Tag as string == "MultiSelectGroup")
            {
                foreach (var child in group.Children.OfType<AvaloniaControl>())
                {
                    UpdateControlColor(child, color);
                }
            }
        }

        private void UpdatePresetSelectionVisuals(AvaloniaColor color) { var panel = _colorPresetsPanel; if (panel != null) { string hex = color.ToString().ToUpperInvariant(); if (hex.Length == 9 && hex.StartsWith("#FF")) hex = "#" + hex.Substring(3); foreach (var child in panel.Children.OfType<Button>().Where(b => b.Classes.Contains("color-preset"))) { if (child.Tag?.ToString() == hex) child.Classes.Add("selected"); else child.Classes.Remove("selected"); } } }

        private void OnEmojiSelectClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string emoji) return;

            if (_activeEmojiList.Contains(emoji))
            {
                _activeEmojiList.Remove(emoji);
            }
            _activeEmojiList.Insert(0, emoji);

            SetCurrentTool(EditorTool.Emoji);
            _pendingEmoji = emoji;
            _ghostSettingsDirty = true; 
            SetEditorCursor(HandCursor);

            var emojiBtn = _emojiTool;
            if (emojiBtn != null) emojiBtn.Content = emoji;

            if (_leftToolbar != null)
            {
                foreach (var child in _leftToolbar.Children)
                {
                    if (child is Button b) b.Classes.Remove("selected");
                }
            }

            _cropTool?.Classes.Remove("selected");
            emojiBtn?.Classes.Add("selected");
            UpdateThicknessPanelVisibility();
            UpdateModeStatus();

            emojiBtn?.Flyout?.Hide();
        }

        private void SyncThicknessUI(double val) 
        { 
            int intVal = (int)Math.Round(val);
            string tag = intVal.ToString(); 

            var choicesPanel = _thicknessChoicesPanel; 
            if (choicesPanel != null) 
            { 
                foreach (var b in choicesPanel.Children.OfType<Button>().Where(x => x.Classes.Contains("thickness"))) 
                { 
                    if (b.Tag?.ToString() == tag) {
                        b.Classes.Add("selected");
                    } else {
                        b.Classes.Remove("selected");
                    }
                } 
            } 

            var flyoutPanel = _thicknessFlyoutPanel;
            if (flyoutPanel != null)
            {
                foreach (var b in flyoutPanel.Children.OfType<Button>().Where(x => x.Classes.Contains("flyout-thickness")))
                {
                    if (b.Tag?.ToString() == tag) {
                        b.Classes.Add("selected");
                    } else {
                        b.Classes.Remove("selected");
                    }
                }
            }

            if (_thicknessFlyoutSlider != null && Math.Abs(_thicknessFlyoutSlider.Value - val) > 0.4)
            {
                _syncingThicknessSlider = true;
                try { _thicknessFlyoutSlider.Value = val; }
                finally { _syncingThicknessSlider = false; }
            }

            if (_thicknessFlyoutValueText != null)
            {
                _thicknessFlyoutValueText.Text = $"{intVal} px";
            }
        }

        private static bool IsThicknessRelevantForTool(EditorTool tool) => tool is EditorTool.Arrow
            or EditorTool.Line
            or EditorTool.FreeDraw
            or EditorTool.Rectangle
            or EditorTool.Ellipse;

        private static bool IsThicknessRelevantForControl(AvaloniaControl control)
        {
            if (IsPixelateControl(control)) return false;
            if (IsHighlightControl(control)) return false;
            if (control is Avalonia.Controls.Shapes.Shape) return true;
            if (IsArrowControl(control)) return true;
            return control is Border border && border.Child is TextBox;
        }

        private string GetThicknessLabelText()
        {
            var tool = _selectedControl != null ? GetToolFromControl(_selectedControl) : _currentTool;
            return tool switch
            {
                EditorTool.Rectangle or EditorTool.Ellipse => "Border size",
                EditorTool.FreeDraw => "Pen size",
                EditorTool.Text => "Frame size",
                _ => "Line size"
            };
        }

        private void UpdateThicknessPanelVisibility()
        {
            var panel = _thicknessPanel;
            var choicesPanel = _thicknessChoicesPanel;
            var fillBtn = _fillToggleBtn;
            var resetCounterBtn = _resetCounterBtn;
            var counterAdjustPanel = _counterAdjustPanel;
            var tool = _selectedControl != null ? GetToolFromControl(_selectedControl) : _currentTool;

            bool thicknessRelevant = _selectedControl != null
                ? IsThicknessRelevantForControl(_selectedControl)
                : IsThicknessRelevantForTool(_currentTool);
            bool pixelateRelevant = tool == EditorTool.Blur || IsPixelateControl(_selectedControl);
            bool relevant = thicknessRelevant || pixelateRelevant;

            if (panel != null) panel.IsVisible = relevant;
            if (choicesPanel != null) choicesPanel.IsVisible = thicknessRelevant && !pixelateRelevant;
            if (_pixelateToolbarSection != null) _pixelateToolbarSection.IsVisible = pixelateRelevant;

            if (pixelateRelevant)
            {
                int strength = _selectedControl?.Tag is PixelateProperties props ? props.Strength : _lastPixelateStrength;
                SyncPixelateSlider(strength);
            }

            if (fillBtn != null)
            {
                bool fillRelevant = tool == EditorTool.Rectangle || tool == EditorTool.Ellipse || tool == EditorTool.Text;
                fillBtn.IsVisible = fillRelevant;
            }

            if (resetCounterBtn != null)
            {
                resetCounterBtn.IsVisible = tool == EditorTool.Counter;
            }
            if (counterAdjustPanel != null)
            {
                counterAdjustPanel.IsVisible = tool == EditorTool.Counter;
            }

            if (!relevant) return;
            if (!thicknessRelevant || pixelateRelevant) return;

            var label = _thicknessLabel;
            if (label != null) label.Text = GetThicknessLabelText();
            SyncThicknessUI(_currentThickness);
        }
        private void OnThicknessSliderPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            CommitSliderThickness();
        }

        private void OnThicknessSliderPointerCaptureLost(object sender, PointerCaptureLostEventArgs e)
        {
            CommitSliderThickness();
        }

        private void CommitSliderThickness()
        {
            if (_sliderUndoPending)
            {
                _sliderUndoPending = false;
                ShowUndoAvailableHint();
            }
            IniConfig.Save();
            SaveToolPreferences();
        }

        private void ApplyThicknessValue(double val, bool fromInteractiveSlider = false) { 
            _currentThickness = val; 
            SyncThicknessUI(val); 
            
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            bool saveConfig = false;

            if (_selectedControl != null) { 
                if (!fromInteractiveSlider)
                {
                    SaveUndoState(false);
                }
                else if (!_sliderUndoPending)
                {
                    SaveUndoState(false);
                    _sliderUndoPending = true;
                }

                UpdateControlThickness(_selectedControl, val); 
                var inferred = GetToolFromControl(_selectedControl); 
                if (inferred != EditorTool.None) {
                    _toolThicknesses[inferred] = val; 
                    if (inferred == EditorTool.Text) { config.TextToolThickness = val; saveConfig = true; }
                }
                if (!fromInteractiveSlider)
                {
                    OverlayHelper.ShowLightToast("THICKNESS UPDATED", this); 
                    ShowUndoAvailableHint(); 
                }
            } else if (_currentTool != EditorTool.None) { 
                _toolThicknesses[_currentTool] = val; 
                if (_currentTool == EditorTool.Text) { config.TextToolThickness = val; saveConfig = true; }
            } else if (_currentTool == EditorTool.None) { 
                foreach (EditorTool tool in Enum.GetValues<EditorTool>()) { 
                    if (tool != EditorTool.None) _toolThicknesses[tool] = val; 
                } 
                config.TextToolThickness = val;
                saveConfig = true;
            } 
            
            if (saveConfig && !fromInteractiveSlider) IniConfig.Save();
            if (!fromInteractiveSlider) SaveToolPreferences();
            _ghostSettingsDirty = true; 
            Log.Info($"Thickness set to: {val}");
        }

        private void OnThicknessClick(object sender, RoutedEventArgs e) { 
            if (sender is Button btn && btn.Tag is string tStr && double.TryParse(tStr, out var val)) { 
                ApplyThicknessValue(val);
            } 
        }

        private void OnLineSizeBtnClick(object sender, RoutedEventArgs e)
        {
            if (_lineSizePopup != null)
            {
                _lineSizePopup.IsOpen = !_lineSizePopup.IsOpen;
            }
        }

        private void OnThicknessSliderChanged(object sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingThicknessSlider) return;
            ApplyThicknessValue(Math.Round(e.NewValue), fromInteractiveSlider: true);
        }

        private void OnFillToggleClick(object sender, RoutedEventArgs e)
        {
            _isFillMode = !_isFillMode;
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (_selectedControl != null) SaveUndoState(false);

            if (_currentTool == EditorTool.Text || (_selectedControl is Border b && b.Child is TextBox)) {
                config.UseTextFrame = _isFillMode;
                if (_selectedControl is Border textBorder && textBorder.Child is TextBox tb) {
                    double oldT = textBorder.BorderThickness.Left;
                    double newT = config.UseTextFrame ? config.TextToolThickness : 0;
                    double delta = newT - oldT;
                    if (Math.Abs(delta) > 0.001)
                    {
                        double curL = Canvas.GetLeft(textBorder);
                        if (double.IsNaN(curL)) curL = textBorder.Bounds.X;
                        double curT = Canvas.GetTop(textBorder);
                        if (double.IsNaN(curT)) curT = textBorder.Bounds.Y;

                        Canvas.SetLeft(textBorder, curL - delta);
                        Canvas.SetTop(textBorder, curT - delta);
                        textBorder.Width = Math.Max(20, textBorder.Width + (2 * delta));
                        textBorder.Height = Math.Max(20, textBorder.Height + (2 * delta));
                        textBorder.BorderThickness = new Thickness(newT);
                        SetTextBoxBoundsToBorder(textBorder, tb);
                        UpdateSelectionIndicator();
                    }
                    textBorder.Background = _isFillMode ? new SolidColorBrush(AvaloniaColor.FromArgb(100, 30, 30, 30)) : new SolidColorBrush(AvaloniaColor.FromArgb(30, 0, 0, 0));
                }
            }

            UpdateFillToggleVisual();
            _ghostSettingsDirty = true; 
            OverlayHelper.ShowLightToast(_isFillMode ? "FILL ON" : "FILL OFF", this);

            if (_selectedControl != null) {
                UpdateControlFill(_selectedControl, _isFillMode);
                var inferred = GetToolFromControl(_selectedControl);
                if (inferred != EditorTool.None) _toolFillModes[inferred] = _isFillMode;
                ShowUndoAvailableHint();
            } else if (_currentTool != EditorTool.None) {
                _toolFillModes[_currentTool] = _isFillMode;
            } else if (_currentTool == EditorTool.None) {
                foreach (EditorTool tool in Enum.GetValues<EditorTool>()) {
                    if (tool != EditorTool.None) _toolFillModes[tool] = _isFillMode;
                }
            }
        }

        private void UpdateFillToggleVisual()
        {
            var fillBtn = _fillToggleBtn;
            var fillText = _fillToggleText;
            if (_isFillMode) fillBtn?.Classes.Add("selected"); else fillBtn?.Classes.Remove("selected");
            if (fillText != null) fillText.Text = _isFillMode ? "Fill: On" : "Fill: Off";
        }

        private void UpdateControlThickness(AvaloniaControl control, double val)
        {
            if (control is Avalonia.Controls.Shapes.Shape s)
            {
                s.StrokeThickness = val;
            }
            else if (control is Canvas c)
            {
                foreach (var child in c.Children.OfType<Avalonia.Controls.Shapes.Shape>())
                {
                    if (child is Avalonia.Controls.Shapes.Line || child.Stroke != null) child.StrokeThickness = val;
                }

                if (IsArrowControl(c) && TryGetVectorAbsolutePoints(c, out var start, out var end)) UpdateArrowVisuals(c, start, end);
            }
            else if (control is Border b && b.Child is TextBox tb)
            {
                var config = IniConfig.GetIniSection<CoreConfiguration>();
                double oldT = b.BorderThickness.Left;
                double newT = config.UseTextFrame ? val : 0;
                double delta = newT - oldT;
                if (Math.Abs(delta) > 0.001)
                {
                    double curL = Canvas.GetLeft(b);
                    if (double.IsNaN(curL)) curL = b.Bounds.X;
                    double curT = Canvas.GetTop(b);
                    if (double.IsNaN(curT)) curT = b.Bounds.Y;

                    Canvas.SetLeft(b, curL - delta);
                    Canvas.SetTop(b, curT - delta);
                    b.Width = Math.Max(20, b.Width + (2 * delta));
                    b.Height = Math.Max(20, b.Height + (2 * delta));
                    b.BorderThickness = new Thickness(newT);
                    SetTextBoxBoundsToBorder(b, tb);
                    UpdateSelectionIndicator();
                }
            }
        }
        private void UpdateControlFill(AvaloniaControl control, bool fill) { 
            if (IsPixelateControl(control)) return;
            if (control is Avalonia.Controls.Shapes.Shape s && (s is Avalonia.Controls.Shapes.Rectangle || s is Avalonia.Controls.Shapes.Ellipse)) { 
                if (fill) {
                    s.Fill = s.Stroke ?? _toolBrushes[_currentTool] ?? Brushes.Blue;
                } else {
                    if (s.Stroke == null) s.Stroke = s.Fill ?? _toolBrushes[_currentTool] ?? Brushes.Blue;
                    s.Fill = null; 
                }
                s.InvalidateVisual();
            } 
        }

        private IBrush GetContrastColor(IBrush backgroundBrush)
        {
            if (backgroundBrush is SolidColorBrush solidColorBrush)
            {
                var color = solidColorBrush.Color;
                double r = color.R / 255.0;
                double g = color.G / 255.0;
                double b = color.B / 255.0;
                r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
                g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
                b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);
                double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                return luminance > 0.179 ? Brushes.Black : Brushes.White;
            }
            return Brushes.White;
        }

        private void PlaceCounter(AvaloniaPoint pos, IBrush brush) { 
            var config = IniConfig.GetIniSection<CoreConfiguration>(); 
            int val = _counterValue++; 
            _ghostSettingsDirty = true;
            IBrush textBrush = GetContrastColor(brush);
            var border = new Border { Width = config.LastCounterSize, Height = config.LastCounterSize, Background = brush, CornerRadius = new CornerRadius(config.LastCounterSize/2), Child = new TextBlock { Text = val.ToString(), Foreground = textBrush, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = config.LastCounterSize * 0.6, FontWeight = FontWeight.Bold, IsHitTestVisible = false } }; 
            Canvas.SetLeft(border, pos.X - (border.Width/2)); Canvas.SetTop(border, pos.Y - (border.Height/2)); AddAnnotation(border); PlayStampIn(border); OverlayHelper.ShowLightToast("COUNTER PLACED", this); 
        }
        private void PlaceEmoji(AvaloniaPoint pos, string emoji) { var config = IniConfig.GetIniSection<CoreConfiguration>(); var text = new TextBlock { Text = emoji, FontSize = config.LastEmojiSize, FontFamily = new Avalonia.Media.FontFamily("avares://freesnip.editor/Drawing/Emoji/#Twemoji Mozilla") }; Canvas.SetLeft(text, pos.X - 16); Canvas.SetTop(text, pos.Y - 16); AddAnnotation(text); PlayStampIn(text); OverlayHelper.ShowLightToast("EMOJI PLACED", this); }

        private void PlaceSpreadText(AvaloniaPoint start, AvaloniaPoint end, IBrush brush)
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            double rawW = Math.Abs(end.X - start.X);
            double rawH = Math.Abs(end.Y - start.Y);
            bool isSingleClick = rawW < 5 && rawH < 5;

            double width;
            double height;
            double fontSize;

            bool useFrame = config.UseTextFrame;
            double t = useFrame ? _currentThickness : 0;

            if (isSingleClick)
            {
                width = config != null && config.LastTextBoxWidth > 0 ? config.LastTextBoxWidth : 160;
                height = config != null && config.LastTextBoxHeight > 0 ? config.LastTextBoxHeight : 50;
                fontSize = config != null && config.LastTextSize > 0 ? config.LastTextSize : 20;
            }
            else
            {
                width = Math.Max(40, rawW);
                height = Math.Max(24, rawH);
                double interiorHeight = Math.Max(12, height - (2 * t) - 8);
                fontSize = Math.Clamp(Math.Round(interiorHeight * 0.65, 1), 10, 300);

                if (config != null)
                {
                    config.LastTextSize = fontSize;
                    config.LastTextBoxWidth = width;
                    config.LastTextBoxHeight = height;
                }
                UpdatePersistedSize(EditorTool.Text, fontSize);
                UpdatePersistedTextBoxDimensions(width, height);
            }

            double originX = isSingleClick ? start.X : Math.Min(start.X, end.X);
            double originY = isSingleClick ? start.Y : Math.Min(start.Y, end.Y);

            var textBox = new TextBox
            {
                MinWidth = 30,
                Watermark = "Type here...",
                Foreground = brush,
                CaretBrush = brush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(4),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Tag = new TextAnnotationProperties { PreferredFontSize = fontSize },
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var border = new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(t),
                Background = _toolFillModes[EditorTool.Text] ? new SolidColorBrush(AvaloniaColor.FromArgb(100, 30, 30, 30)) : new SolidColorBrush(AvaloniaColor.FromArgb(30, 0, 0, 0)),
                Padding = new Thickness(2),
                Child = textBox,
                Classes = { "annotation-text-border" }
            };

            border.Width = width + (2 * t);
            border.Height = height + (2 * t);
            Canvas.SetLeft(border, originX - t);
            Canvas.SetTop(border, originY - t);

            SetTextBoxBoundsToBorder(border, textBox);
            if (!isSingleClick)
            {
                FitTextBoxToBorder(border, textBox);
            }
            AttachTextBoxBehavior(border, textBox, config);
            AddAnnotation(border);
            PlayStampIn(border);
            Dispatcher.UIThread.Post(() => textBox.Focus());
        }

        private void CommitShape(AvaloniaPoint start, AvaloniaPoint end, EditorTool tool)
        {
            if (tool == EditorTool.Text)
            {
                var textBrush = _toolBrushes[tool];
                PlaceSpreadText(start, end, textBrush);
                return;
            }

            AvaloniaControl shape = null; var brush = _toolBrushes[tool]; double w = Math.Abs(end.X - start.X); double h = Math.Abs(end.Y - start.Y); if (w < 2 && h < 2) return;
            switch (tool)
            {
                case EditorTool.Line: shape = new Avalonia.Controls.Shapes.Line { StartPoint = new AvaloniaPoint(start.X - Math.Min(start.X, end.X), start.Y - Math.Min(start.Y, end.Y)), EndPoint = new AvaloniaPoint(end.X - Math.Min(start.X, end.X), end.Y - Math.Min(start.Y, end.Y)), Stroke = brush, StrokeThickness = _currentThickness, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round }; Canvas.SetLeft(shape, Math.Min(start.X, end.X)); Canvas.SetTop(shape, Math.Min(start.Y, end.Y)); break;
                case EditorTool.Arrow: DrawArrow(start, end, brush); return;
                case EditorTool.Highlight: shape = CreateHighlightAnnotation(start, end); break;
                case EditorTool.Rectangle: shape = new Avalonia.Controls.Shapes.Rectangle { Stroke = brush, StrokeThickness = _currentThickness, Width = w, Height = h, Fill = _toolFillModes.TryGetValue(EditorTool.Rectangle, out var rfm) && rfm ? brush : null }; Canvas.SetLeft(shape, Math.Min(start.X, end.X)); Canvas.SetTop(shape, Math.Min(start.Y, end.Y)); break;
                case EditorTool.Ellipse: shape = new Avalonia.Controls.Shapes.Ellipse { Stroke = brush, StrokeThickness = _currentThickness, Width = w, Height = h, Fill = _toolFillModes.TryGetValue(EditorTool.Ellipse, out var efm) && efm ? brush : null }; Canvas.SetLeft(shape, Math.Min(start.X, end.X)); Canvas.SetTop(shape, Math.Min(start.Y, end.Y)); break;
                case EditorTool.Blur: shape = CreatePixelateAnnotation(start, end); break;
                case EditorTool.Crop:
                    if (w < 10 || h < 10) return;
                    SaveUndoState(false);
                    var cropRect = new Avalonia.Controls.Shapes.Rectangle { Width = w, Height = h, Stroke = Brushes.Yellow, StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 2 }, Fill = new SolidColorBrush(AvaloniaColor.FromArgb(50, 0, 0, 0)), Tag = "CropObject", Cursor = SizeAllCursor };
                    double cropL = Math.Min(start.X, end.X);
                    double cropT = Math.Min(start.Y, end.Y);
                    if (_image != null)
                    {
                        cropL = Math.Clamp(cropL, 0, Math.Max(0, _image.Width - w));
                        cropT = Math.Clamp(cropT, 0, Math.Max(0, _image.Height - h));
                    }
                    Canvas.SetLeft(cropRect, cropL);
                    Canvas.SetTop(cropRect, cropT);
                    _canvas.Children.Add(cropRect);
                    SetCurrentTool(EditorTool.None); SetEditorCursor(HandCursor); SyncToolButtonSelection();
                    _selectedControl = cropRect;
                    UpdateSelectionIndicator();
                    _canvas.Focus();
                    return;
            }
            if (shape != null) AddAnnotation(shape);
        }

        private void CommitShape(AvaloniaPoint start, AvaloniaPoint end) => CommitShape(start, end, _currentTool);

        private void OnContextApplyClick(object sender, RoutedEventArgs e)
        {
            if (_selectedControl == null) return;
            var tag = _selectedControl.Tag?.ToString();
            
            if (tag == "CropObject")
            {
                double left = Canvas.GetLeft(_selectedControl);
                if (double.IsNaN(left)) left = _selectedControl.Bounds.X;
                double top = Canvas.GetTop(_selectedControl);
                if (double.IsNaN(top)) top = _selectedControl.Bounds.Y;
                double w = double.IsNaN(_selectedControl.Width) ? _selectedControl.Bounds.Width : _selectedControl.Width;
                double h = double.IsNaN(_selectedControl.Height) ? _selectedControl.Bounds.Height : _selectedControl.Height;

                DisposeAnnotationResources(_selectedControl);
                _canvas.Children.Remove(_selectedControl);
                _selectedControl = null;
                UpdateSelectionIndicator();

                if (_cropMode == CropMode.VerticalStrip || _cropMode == CropMode.HorizontalStrip)
                {
                    _ = ApplyCutOutSlice(new Rect(left, top, w, h), _cropMode == CropMode.VerticalStrip);
                }
                else
                {
                    _ = ApplyCropRect(new Rect(left, top, w, h));
                }
            }
            else if (tag == "PasteObject")
            {
                _selectedControl.Tag = "PastedImage";
                UpdateSelectionIndicator();
            }
        }

        private void OnPasteClick(object sender, RoutedEventArgs e)
        {
            HandleContextualPaste();
        }

        private async void HandleContextualPaste()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info("[STEP:START] HandleContextualPaste - Checking clipboard context.");
            if (HasAnnotationInClipboard())
            {
                PasteAnnotationFromClipboard();
                sw.Stop();
                Log.Info($"[STEP:SUCCESS] HandleContextualPaste ({sw.ElapsedMilliseconds}ms) - Annotation pasted from memory.");
                return;
            }

            await PasteImageFromClipboardAsync().ConfigureAwait(true);
            sw.Stop();
            Log.Info($"[STEP:SUCCESS] HandleContextualPaste ({sw.ElapsedMilliseconds}ms) - External clipboard paste evaluated.");
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _annotationClipboardSequence = UiClipboard.GetCurrentSequenceNumber();
            }
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _annotationClipboardSequence = UiClipboard.GetCurrentSequenceNumber();
            }
        }

        private bool HasAnnotationInClipboard()
        {
            return _annotationClipboard?.Prototype != null;
        }

        private void PasteAnnotationFromClipboard()
        {
            if (_annotationClipboard?.Prototype == null) return;

            var duplicate = CloneAnnotationControl(_annotationClipboard.Prototype);
            if (duplicate == null) return;

            double canvasW = _canvas?.Bounds.Width > 0 ? _canvas.Bounds.Width : (_image?.Width ?? 800);
            double canvasH = _canvas?.Bounds.Height > 0 ? _canvas.Bounds.Height : (_image?.Height ?? 600);

            double offsetDeltaX = 20.0 * _clipboardPasteIteration;
            double offsetDeltaY = 20.0 * _clipboardPasteIteration;
            _clipboardPasteIteration++;

            if (_annotationClipboard.IsVector)
            {
                var vStart = _annotationClipboard.VectorStart;
                var vEnd = _annotationClipboard.VectorEnd;
                double pStartX = vStart.X + offsetDeltaX;
                double pStartY = vStart.Y + offsetDeltaY;
                double pEndX = vEnd.X + offsetDeltaX;
                double pEndY = vEnd.Y + offsetDeltaY;

                double minX = Math.Min(pStartX, pEndX);
                double maxX = Math.Max(pStartX, pEndX);
                double minY = Math.Min(pStartY, pEndY);
                double maxY = Math.Max(pStartY, pEndY);

                double shiftX = 0;
                if (maxX > canvasW) shiftX = canvasW - maxX;
                if (minX + shiftX < 0) shiftX = -minX;

                double shiftY = 0;
                if (maxY > canvasH) shiftY = canvasH - maxY;
                if (minY + shiftY < 0) shiftY = -minY;

                var clampedStart = new AvaloniaPoint(pStartX + shiftX, pStartY + shiftY);
                var clampedEnd = new AvaloniaPoint(pEndX + shiftX, pEndY + shiftY);

                SetVectorAbsolutePoints(duplicate, clampedStart, clampedEnd);
            }
            else
            {
                double origW = _annotationClipboard.Width;
                double origH = _annotationClipboard.Height;
                if (origW <= 0) origW = duplicate.Width;
                if (origH <= 0) origH = duplicate.Height;
                if (origW <= 0) origW = 50;
                if (origH <= 0) origH = 50;

                double proposedL = _annotationClipboard.SourceLeft + offsetDeltaX;
                double proposedT = _annotationClipboard.SourceTop + offsetDeltaY;

                double maxL = Math.Max(0, canvasW - origW);
                double maxT = Math.Max(0, canvasH - origH);
                double clampedL = Math.Clamp(proposedL, 0, maxL);
                double clampedT = Math.Clamp(proposedT, 0, maxT);

                duplicate.Width = origW;
                duplicate.Height = origH;
                Canvas.SetLeft(duplicate, clampedL);
                Canvas.SetTop(duplicate, clampedT);
            }

            if (duplicate is Border border && border.Child is TextBlock textBlock && GetToolFromControl(duplicate) == EditorTool.Counter)
            {
                if (_annotationClipboard.CounterValue.HasValue)
                {
                    textBlock.Text = _annotationClipboard.CounterValue.Value.ToString();
                }
            }

            if (duplicate is Border b && b.Child is TextBox tb)
            {
                SetTextBoxBoundsToBorder(b, tb);
                tb.IsReadOnly = true;
                tb.CaretBrush = Brushes.Transparent;
                tb.Cursor = SizeAllCursor;
            }

            if (IsPixelateControl(duplicate)) RefreshPixelateAnnotation(duplicate);
            if (IsHighlightControl(duplicate)) RefreshHighlightAnnotation(duplicate);

            SaveUndoState(false);
            _canvas.Children.Add(duplicate);
            _selectedControl = duplicate;

            SetCurrentTool(EditorTool.None);
            SyncToolButtonSelection();
            duplicate.Cursor = HandCursor;

            UpdateSelectionIndicator();
            SetEditorCursor(HandCursor);
            UpdateHoverIndicator(duplicate);
            UpdateThicknessPanelVisibility();

            Log.Info($"[STEP:SUCCESS] PasteAnnotationFromClipboard - Duplicated {_annotationClipboard.ShapeType} (Iteration: {_clipboardPasteIteration - 1}).");
            _canvas?.Focus();
        }

        private async Task PasteImageFromClipboardAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info("[STEP:START] PasteImageFromClipboardAsync - Fetching image from clipboard.");
            try
            {
                bool isFreeSnipEditorImage = await UiClipboard.HasFreeSnipEditorImageAsync().ConfigureAwait(true);
                using var clipboardImage = await UiClipboard.GetImageAsync().ConfigureAwait(true);
                if (clipboardImage == null)
                {
                    Log.Info($"[STEP:INFO] PasteImageFromClipboardAsync ({sw.ElapsedMilliseconds}ms) - No image available on clipboard.");
                    return;
                }

                using var trimmedImage = isFreeSnipEditorImage ? TryTrimFreeSnipFrame(clipboardImage) : null;
                var image = trimmedImage ?? clipboardImage;
                var avaloniaBitmap = freesnip.editor.helpers.ImageSharpAvaloniaHelper.ToAvaloniaBitmap(image);
                double canvasW = _canvas?.Bounds.Width > 0 ? _canvas.Bounds.Width : (_image?.Width ?? 800);
                double canvasH = _canvas?.Bounds.Height > 0 ? _canvas.Bounds.Height : (_image?.Height ?? 600);

                double w = image.Width;
                double h = image.Height;
                if (w > canvasW || h > canvasH)
                {
                    double ratio = Math.Min((canvasW * 0.85) / w, (canvasH * 0.85) / h);
                    w *= ratio;
                    h *= ratio;
                }

                var imageControl = new Avalonia.Controls.Image
                {
                    Source = avaloniaBitmap,
                    Width = w,
                    Height = h,
                    Stretch = Avalonia.Media.Stretch.Uniform,
                    ZIndex = _currentZIndex++,
                    Tag = "PasteObject"
                };

                Canvas.SetLeft(imageControl, Math.Max(0, (canvasW - w) / 2));
                Canvas.SetTop(imageControl, Math.Max(0, (canvasH - h) / 2));

                SaveUndoState(false);
                _canvas.Children.Add(imageControl);
                if (_currentTool == EditorTool.None)
                {
                    _selectedControl = imageControl;
                }
                else
                {
                    imageControl.Tag = "PastedImage";
                    _selectedControl = null;
                }

                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
            }
            catch (Exception ex)
            {
                freesnip.foundation.core.LogHelper.GetLogger(typeof(ImageEditorWindow)).Error("Failed to paste image.", ex);
            }
        }

        private static ImageSharpImage TryTrimFreeSnipFrame(ImageSharpImage image)
        {
            if (!HasFreeSnipFrame(image)) return null;
            return image.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(FreeSnipFrameThickness, FreeSnipFrameThickness, image.Width - FreeSnipFrameThickness * 2, image.Height - FreeSnipFrameThickness * 2)));
        }

        private static bool HasFreeSnipFrame(ImageSharpImage image)
        {
            if (image == null || image.Width <= FreeSnipFrameThickness * 2 || image.Height <= FreeSnipFrameThickness * 2)
            {
                return false;
            }

            using var pixels = image.CloneAs<Rgba32>();
            int total = 0;
            int matched = 0;
            int stepX = Math.Max(1, image.Width / 64);
            int stepY = Math.Max(1, image.Height / 64);

            for (int y = 0; y < FreeSnipFrameThickness; y++)
            {
                SampleHorizontalFrameRow(pixels, y, stepX, ref total, ref matched);
                SampleHorizontalFrameRow(pixels, image.Height - 1 - y, stepX, ref total, ref matched);
            }

            for (int x = 0; x < FreeSnipFrameThickness; x++)
            {
                SampleVerticalFrameColumn(pixels, x, stepY, ref total, ref matched);
                SampleVerticalFrameColumn(pixels, image.Width - 1 - x, stepY, ref total, ref matched);
            }

            return total > 0 && matched >= total * 0.85;
        }

        private static void SampleHorizontalFrameRow(SixLabors.ImageSharp.Image<Rgba32> image, int y, int step, ref int total, ref int matched)
        {
            for (int x = 0; x < image.Width; x += step)
            {
                total++;
                if (IsFreeSnipFramePixel(image[x, y])) matched++;
            }
        }

        private static void SampleVerticalFrameColumn(SixLabors.ImageSharp.Image<Rgba32> image, int x, int step, ref int total, ref int matched)
        {
            for (int y = 0; y < image.Height; y += step)
            {
                total++;
                if (IsFreeSnipFramePixel(image[x, y])) matched++;
            }
        }

        private static bool IsFreeSnipFramePixel(Rgba32 pixel)
        {
            return pixel.R <= 30 && pixel.G <= 55 && pixel.B >= 55 && pixel.B <= 160;
        }

        private static double TransformCutCoordinate(double val, double cutStart, double cutSize)
        {
            if (val <= cutStart) return val;
            if (val >= cutStart + cutSize) return val - cutSize;
            return cutStart;
        }

        private void TransformAnnotationsForCutout(bool isVertical, double cutCoord, double cutSize)
        {
            var annotations = GetUserAnnotations();

            if (isVertical)
            {
                double cutX = cutCoord;
                double cutW = cutSize;
                double cutRight = cutX + cutW;

                foreach (var child in annotations)
                {
                    if (IsVectorControl(child) && TryGetVectorAbsolutePoints(child, out var start, out var end))
                    {
                        double vMinX = Math.Min(start.X, end.X);
                        double vMaxX = Math.Max(start.X, end.X);

                        if (vMinX >= cutX && vMaxX <= cutRight)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                            continue;
                        }

                        if (vMaxX <= cutX)
                        {
                            continue;
                        }

                        if (vMinX >= cutRight)
                        {
                            SetVectorAbsolutePoints(child, new AvaloniaPoint(start.X - cutW, start.Y), new AvaloniaPoint(end.X - cutW, end.Y));
                            continue;
                        }

                        var newStart = new AvaloniaPoint(TransformCutCoordinate(start.X, cutX, cutW), start.Y);
                        var newEnd = new AvaloniaPoint(TransformCutCoordinate(end.X, cutX, cutW), end.Y);
                        if (Math.Abs(newStart.X - newEnd.X) < 1 && Math.Abs(newStart.Y - newEnd.Y) < 1)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                        }
                        else
                        {
                            SetVectorAbsolutePoints(child, newStart, newEnd);
                        }
                        continue;
                    }

                    if (child is Avalonia.Controls.Shapes.Polyline poly)
                    {
                        double polyLeft = CanvasLeft(poly);
                        double polyTop = CanvasTop(poly);
                        if (poly.Points == null || poly.Points.Count == 0) continue;

                        double minAbsX = polyLeft + poly.Points.Min(p => p.X);
                        double maxAbsX = polyLeft + poly.Points.Max(p => p.X);

                        if (minAbsX >= cutX && maxAbsX <= cutRight)
                        {
                            _canvas.Children.Remove(poly);
                            if (_selectedControl == poly) _selectedControl = null;
                            continue;
                        }

                        if (maxAbsX <= cutX)
                        {
                            continue;
                        }

                        if (minAbsX >= cutRight)
                        {
                            Canvas.SetLeft(poly, polyLeft - cutW);
                            continue;
                        }

                        var newPts = new List<AvaloniaPoint>(poly.Points.Count);
                        double newPolyLeft = TransformCutCoordinate(polyLeft, cutX, cutW);
                        foreach (var pt in poly.Points)
                        {
                            double ptAbsX = polyLeft + pt.X;
                            double newPtAbsX = TransformCutCoordinate(ptAbsX, cutX, cutW);
                            newPts.Add(new AvaloniaPoint(newPtAbsX - newPolyLeft, pt.Y));
                        }
                        poly.Points = new Avalonia.Collections.AvaloniaList<AvaloniaPoint>(newPts);
                        Canvas.SetLeft(poly, newPolyLeft);
                        poly.InvalidateVisual();
                        continue;
                    }

                    if (TryGetControlBounds(child, out var bounds))
                    {
                        double left = bounds.X;
                        double width = bounds.Width;
                        double right = bounds.Right;

                        if (left >= cutX && right <= cutRight)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                            continue;
                        }

                        if (right <= cutX)
                        {
                            continue;
                        }

                        if (left >= cutRight)
                        {
                            Canvas.SetLeft(child, left - cutW);
                            continue;
                        }

                        double newLeft = TransformCutCoordinate(left, cutX, cutW);
                        double newRight = TransformCutCoordinate(right, cutX, cutW);
                        double newWidth = newRight - newLeft;

                        if (left < cutX && right > cutRight)
                        {
                            newWidth = Math.Max(10, bounds.Width - cutW);
                        }

                        if (newWidth < 5)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                            continue;
                        }

                        child.Width = newWidth;
                        Canvas.SetLeft(child, newLeft);

                        if (child is Border border)
                        {
                            if (border.Child is TextBox tb)
                            {
                                SetTextBoxBoundsToBorder(border, tb);
                                FitTextBoxToBorder(border, tb);
                            }
                            else if (border.Child is TextBlock counterTxt)
                            {
                                double avgSize = (newWidth + child.Height) / 2.0;
                                border.CornerRadius = new CornerRadius(avgSize / 2);
                                counterTxt.FontSize = avgSize * 0.6;
                            }
                        }
                        else if (child is TextBlock emojiBlock)
                        {
                            if (newWidth < emojiBlock.FontSize)
                            {
                                emojiBlock.FontSize = Math.Max(8, newWidth);
                            }
                        }
                    }
                }
            }
            else
            {
                double cutY = cutCoord;
                double cutH = cutSize;
                double cutBottom = cutY + cutH;

                foreach (var child in annotations)
                {
                    if (IsVectorControl(child) && TryGetVectorAbsolutePoints(child, out var start, out var end))
                    {
                        double vMinY = Math.Min(start.Y, end.Y);
                        double vMaxY = Math.Max(start.Y, end.Y);

                        if (vMinY >= cutY && vMaxY <= cutBottom)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                            continue;
                        }

                        if (vMaxY <= cutY)
                        {
                            continue;
                        }

                        if (vMinY >= cutBottom)
                        {
                            SetVectorAbsolutePoints(child, new AvaloniaPoint(start.X, start.Y - cutH), new AvaloniaPoint(end.X, end.Y - cutH));
                            continue;
                        }

                        var newStart = new AvaloniaPoint(start.X, TransformCutCoordinate(start.Y, cutY, cutH));
                        var newEnd = new AvaloniaPoint(end.X, TransformCutCoordinate(end.Y, cutY, cutH));
                        if (Math.Abs(newStart.X - newEnd.X) < 1 && Math.Abs(newStart.Y - newEnd.Y) < 1)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                        }
                        else
                        {
                            SetVectorAbsolutePoints(child, newStart, newEnd);
                        }
                        continue;
                    }

                    if (child is Avalonia.Controls.Shapes.Polyline poly)
                    {
                        double polyLeft = CanvasLeft(poly);
                        double polyTop = CanvasTop(poly);
                        if (poly.Points == null || poly.Points.Count == 0) continue;

                        double minAbsY = polyTop + poly.Points.Min(p => p.Y);
                        double maxAbsY = polyTop + poly.Points.Max(p => p.Y);

                        if (minAbsY >= cutY && maxAbsY <= cutBottom)
                        {
                            _canvas.Children.Remove(poly);
                            if (_selectedControl == poly) _selectedControl = null;
                            continue;
                        }

                        if (maxAbsY <= cutY)
                        {
                            continue;
                        }

                        if (minAbsY >= cutBottom)
                        {
                            Canvas.SetTop(poly, polyTop - cutH);
                            continue;
                        }

                        var newPts = new List<AvaloniaPoint>(poly.Points.Count);
                        double newPolyTop = TransformCutCoordinate(polyTop, cutY, cutH);
                        foreach (var pt in poly.Points)
                        {
                            double ptAbsY = polyTop + pt.Y;
                            double newPtAbsY = TransformCutCoordinate(ptAbsY, cutY, cutH);
                            newPts.Add(new AvaloniaPoint(pt.X, newPtAbsY - newPolyTop));
                        }
                        poly.Points = new Avalonia.Collections.AvaloniaList<AvaloniaPoint>(newPts);
                        Canvas.SetTop(poly, newPolyTop);
                        poly.InvalidateVisual();
                        continue;
                    }

                    if (TryGetControlBounds(child, out var bounds))
                    {
                        double top = bounds.Y;
                        double height = bounds.Height;
                        double bottom = bounds.Bottom;

                        if (top >= cutY && bottom <= cutBottom)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                            continue;
                        }

                        if (bottom <= cutY)
                        {
                            continue;
                        }

                        if (top >= cutBottom)
                        {
                            Canvas.SetTop(child, top - cutH);
                            continue;
                        }

                        double newTop = TransformCutCoordinate(top, cutY, cutH);
                        double newBottom = TransformCutCoordinate(bottom, cutY, cutH);
                        double newHeight = newBottom - newTop;

                        if (top < cutY && bottom > cutBottom)
                        {
                            newHeight = Math.Max(10, bounds.Height - cutH);
                        }

                        if (newHeight < 5)
                        {
                            _canvas.Children.Remove(child);
                            if (_selectedControl == child) _selectedControl = null;
                            continue;
                        }

                        child.Height = newHeight;
                        Canvas.SetTop(child, newTop);

                        if (child is Border border)
                        {
                            if (border.Child is TextBox tb)
                            {
                                SetTextBoxBoundsToBorder(border, tb);
                                FitTextBoxToBorder(border, tb);
                            }
                            else if (border.Child is TextBlock counterTxt)
                            {
                                double avgSize = (child.Width + newHeight) / 2.0;
                                border.CornerRadius = new CornerRadius(avgSize / 2);
                                counterTxt.FontSize = avgSize * 0.6;
                            }
                        }
                        else if (child is TextBlock emojiBlock)
                        {
                            if (newHeight < emojiBlock.FontSize)
                            {
                                emojiBlock.FontSize = Math.Max(8, newHeight);
                            }
                        }
                    }
                }
            }
        }

        internal async Task ApplyCropRect(Rect rect)
        {
            if (_image == null || rect.Width < 5 || rect.Height < 5 || !TryBeginEditorOperation()) return;
            try
            {
                var cropRect = ClampImageRectangle(rect);
                if (cropRect.Width < 5 || cropRect.Height < 5) return;

                if (_selectedControl is Canvas activeGroup && activeGroup.Tag as string == "MultiSelectGroup")
                {
                    UngroupMultiSelectGroup(activeGroup);
                }

                SaveUndoState(true);

                Rect cropBox = new Rect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
                var userAnnotations = GetUserAnnotations();

                foreach (var child in userAnnotations)
                {
                    if (TryGetControlBounds(child, out var oldBounds) && !cropBox.Intersects(oldBounds))
                    {
                        _canvas.Children.Remove(child);
                        if (_selectedControl == child) _selectedControl = null;
                        continue;
                    }

                    if (IsVectorControl(child) && TryGetVectorAbsolutePoints(child, out var vStart, out var vEnd))
                    {
                        var newStart = new AvaloniaPoint(vStart.X - cropRect.X, vStart.Y - cropRect.Y);
                        var newEnd = new AvaloniaPoint(vEnd.X - cropRect.X, vEnd.Y - cropRect.Y);
                        SetVectorAbsolutePoints(child, newStart, newEnd);
                    }
                    else if (child is Avalonia.Controls.Shapes.Polyline poly)
                    {
                        Canvas.SetLeft(poly, CanvasLeft(poly) - cropRect.X);
                        Canvas.SetTop(poly, CanvasTop(poly) - cropRect.Y);
                    }
                    else
                    {
                        Canvas.SetLeft(child, CanvasLeft(child) - cropRect.X);
                        Canvas.SetTop(child, CanvasTop(child) - cropRect.Y);
                    }
                }

                if (_selectedControl != null && !_canvas.Children.Contains(_selectedControl))
                {
                    _selectedControl = null;
                }

                if (!await Task.Run(() => MutateImage(img => img.Mutate(x => x.Crop(cropRect)))).ConfigureAwait(true)) return;

                if (_canvas != null)
                {
                    _canvas.Width = cropRect.Width;
                    _canvas.Height = cropRect.Height;
                }

                await UpdateDisplayAsync().ConfigureAwait(true);
                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
                ShowUndoAvailableHint();
            }
            catch (Exception ex)
            {
                Log.Error("Crop failed", ex);
                OverlayHelper.ShowLightToast("CROP FAILED", this);
            }
            finally
            {
                EndEditorOperation();
            }
        }

        internal async Task ApplyCutOutSlice(Rect rect, bool isVertical)
        {
            if (_image == null || !TryBeginEditorOperation()) return;
            try
            {
                int origW;
                int origH;
                lock (_imageLock)
                {
                    if (_image == null) return;
                    origW = _image.Width;
                    origH = _image.Height;
                }

                int cutCoord = 0;
                int cutSize = 0;
                int newW = origW;
                int newH = origH;
                int leftW = 0, rightX = 0, rightW = 0;
                int topH = 0, bottomY = 0, bottomH = 0;

                if (isVertical)
                {
                    int cutX = (int)Math.Round(rect.X);
                    int cutW = (int)Math.Round(rect.Width);
                    cutX = Math.Clamp(cutX, 0, origW);
                    cutW = Math.Clamp(cutW, 0, origW - cutX);
                    if (cutW <= 0 || origW - cutW < 5) return;

                    cutCoord = cutX;
                    cutSize = cutW;
                    leftW = cutX;
                    rightX = cutX + cutW;
                    rightW = origW - rightX;
                    newW = leftW + rightW;
                    newH = origH;
                }
                else
                {
                    int cutY = (int)Math.Round(rect.Y);
                    int cutH = (int)Math.Round(rect.Height);
                    cutY = Math.Clamp(cutY, 0, origH);
                    cutH = Math.Clamp(cutH, 0, origH - cutY);
                    if (cutH <= 0 || origH - cutH < 5) return;

                    cutCoord = cutY;
                    cutSize = cutH;
                    topH = cutY;
                    bottomY = cutY + cutH;
                    bottomH = origH - bottomY;
                    newW = origW;
                    newH = topH + bottomH;
                }

                if (_selectedControl is Canvas activeGroup && activeGroup.Tag as string == "MultiSelectGroup")
                {
                    UngroupMultiSelectGroup(activeGroup);
                }

                SaveUndoState(true);

                ImageSharpImage newImage = await Task.Run(() =>
                {
                    lock (_imageLock)
                    {
                        if (_image == null) return null;
                        var result = new SixLabors.ImageSharp.Image<Rgba32>(newW, newH);
                        if (isVertical)
                        {
                            if (leftW > 0)
                            {
                                using var leftSlice = _image.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(0, 0, leftW, origH)));
                                result.Mutate(ctx => ctx.DrawImage(leftSlice, new SixLabors.ImageSharp.Point(0, 0), 1f));
                            }
                            if (rightW > 0)
                            {
                                using var rightSlice = _image.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(rightX, 0, rightW, origH)));
                                result.Mutate(ctx => ctx.DrawImage(rightSlice, new SixLabors.ImageSharp.Point(leftW, 0), 1f));
                            }
                        }
                        else
                        {
                            if (topH > 0)
                            {
                                using var topSlice = _image.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(0, 0, origW, topH)));
                                result.Mutate(ctx => ctx.DrawImage(topSlice, new SixLabors.ImageSharp.Point(0, 0), 1f));
                            }
                            if (bottomH > 0)
                            {
                                using var bottomSlice = _image.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(0, bottomY, origW, bottomH)));
                                result.Mutate(ctx => ctx.DrawImage(bottomSlice, new SixLabors.ImageSharp.Point(0, topH), 1f));
                            }
                        }
                        return (ImageSharpImage)result;
                    }
                }).ConfigureAwait(true);

                if (newImage == null) return;

                TransformAnnotationsForCutout(isVertical, cutCoord, cutSize);

                if (_selectedControl != null && !_canvas.Children.Contains(_selectedControl))
                {
                    _selectedControl = null;
                }

                SetImageUnderLock(newImage);
                if (_canvas != null)
                {
                    _canvas.Width = newW;
                    _canvas.Height = newH;
                }

                await UpdateDisplayAsync().ConfigureAwait(true);
                UpdateSelectionIndicator();
                UpdateThicknessPanelVisibility();
                ShowUndoAvailableHint();
                OverlayHelper.ShowLightToast(isVertical ? "CUT OUT VERTICAL APPLIED" : "CUT OUT HORIZONTAL APPLIED", this);
            }
            catch (Exception ex)
            {
                Log.Error("Cut out failed", ex);
                OverlayHelper.ShowLightToast("CUT OUT FAILED", this);
            }
            finally
            {
                EndEditorOperation();
            }
        }

        private async Task RotateImageAsync(RotateMode mode)
        {
            if (_image == null || !TryBeginEditorOperation()) return;
            try
            {
                SaveUndoState(true);
                int oldW = _image.Width;
                int oldH = _image.Height;
                if (!await Task.Run(() => MutateImage(img => img.Mutate(x => x.Rotate(mode)))).ConfigureAwait(true)) return;
                
                double rotAngle = 0;
                if (mode == RotateMode.Rotate90) rotAngle = 90;
                else if (mode == RotateMode.Rotate180) rotAngle = 180;
                else if (mode == RotateMode.Rotate270) rotAngle = 270;

                foreach (var child in GetUserAnnotations())
                {
                    if (!TryGetControlBounds(child, out var oldBounds)) continue;
                    
                    double l = oldBounds.X;
                    double t = oldBounds.Y;
                    double w = oldBounds.Width;
                    double h = oldBounds.Height;
                    
                    double newL, newT, newW, newH;
                    if (mode == RotateMode.Rotate90)
                    {
                        newL = oldH - t - h;
                        newT = l;
                        newW = h;
                        newH = w;
                    }
                    else if (mode == RotateMode.Rotate270)
                    {
                        newL = t;
                        newT = oldW - l - w;
                        newW = h;
                        newH = w;
                    }
                    else
                    {
                        newL = oldW - l - w;
                        newT = oldH - t - h;
                        newW = w;
                        newH = h;
                    }

                    if (child is Avalonia.Controls.Shapes.Line line)
                    {
                        var s = line.StartPoint; var e = line.EndPoint;
                        if (mode == RotateMode.Rotate90) { line.StartPoint = new AvaloniaPoint(newW - s.Y, s.X); line.EndPoint = new AvaloniaPoint(newW - e.Y, e.X); }
                        else if (mode == RotateMode.Rotate270) { line.StartPoint = new AvaloniaPoint(s.Y, newH - s.X); line.EndPoint = new AvaloniaPoint(e.Y, newH - e.X); }
                        else if (mode == RotateMode.Rotate180) { line.StartPoint = new AvaloniaPoint(newW - s.X, newH - s.Y); line.EndPoint = new AvaloniaPoint(newW - e.X, newH - e.Y); }
                        Canvas.SetLeft(line, newL); Canvas.SetTop(line, newT);
                    }
                    else if (child is Avalonia.Controls.Shapes.Polyline poly)
                    {
                        if (poly.Points == null || poly.Points.Count == 0) continue;
                        var pts = new List<AvaloniaPoint>();
                        foreach (var p in poly.Points)
                        {
                            if (mode == RotateMode.Rotate90) pts.Add(new AvaloniaPoint(newW - p.Y, p.X));
                            else if (mode == RotateMode.Rotate270) pts.Add(new AvaloniaPoint(p.Y, newH - p.X));
                            else if (mode == RotateMode.Rotate180) pts.Add(new AvaloniaPoint(newW - p.X, newH - p.Y));
                        }
                        poly.Points = new Avalonia.Collections.AvaloniaList<AvaloniaPoint>(pts);
                        Canvas.SetLeft(poly, newL); Canvas.SetTop(poly, newT);
                    }
                    else if (child is Canvas group && group.Tag is ArrowProperties props)
                    {
                        if (mode == RotateMode.Rotate90)
                        {
                            props.Start = new AvaloniaPoint(oldH - props.Start.Y, props.Start.X);
                            props.End = new AvaloniaPoint(oldH - props.End.Y, props.End.X);
                        }
                        else if (mode == RotateMode.Rotate270)
                        {
                            props.Start = new AvaloniaPoint(props.Start.Y, oldW - props.Start.X);
                            props.End = new AvaloniaPoint(props.End.Y, oldW - props.End.X);
                        }
                        else if (mode == RotateMode.Rotate180)
                        {
                            props.Start = new AvaloniaPoint(oldW - props.Start.X, oldH - props.Start.Y);
                            props.End = new AvaloniaPoint(oldW - props.End.X, oldH - props.End.Y);
                        }
                        UpdateArrowVisuals(group, props.Start, props.End);
                    }
                    else 
                    {
                        Canvas.SetLeft(child, newL);
                        Canvas.SetTop(child, newT);
                        child.Width = newW;
                        child.Height = newH;

                        var tool = GetToolFromControl(child);
                        if (tool == EditorTool.Text || tool == EditorTool.Counter || tool == EditorTool.Emoji)
                        {
                            var transform = child.RenderTransform as RotateTransform;
                            if (transform == null)
                            {
                                transform = new RotateTransform();
                                child.RenderTransform = transform;
                                child.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                            }
                            transform.Angle = (transform.Angle + rotAngle) % 360;
                        }
                    }
                }
                UpdateSelectionIndicator();
                await UpdateDisplayAsync().ConfigureAwait(true);
                ShowUndoAvailableHint();
            }
            finally
            {
                EndEditorOperation();
            }
        }

        private void AddAnnotation(AvaloniaControl control)
        {
            control.ZIndex = _currentZIndex++;
            SaveUndoState(false);
            _canvas.Children.Add(control);
            _selectedControl = control;
            UpdateSelectionIndicator();
            UpdateThicknessPanelVisibility();
            UpdateUndoRedoState();
            ShowUndoAvailableHint();
            _canvas?.Focus();
        }

        
        private static void PlayStampIn(AvaloniaControl control)
        {
            if (control == null) return;
            control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            control.Classes.Add("ink-stamp");
            
            DispatcherTimer.RunOnce(() =>
            {
                control.Classes.Remove("ink-stamp");
                control.RenderTransform = null;
            }, TimeSpan.FromMilliseconds(320));
        }

        private void DrawArrow(AvaloniaPoint start, AvaloniaPoint end, IBrush brush)
        {
            double minX = Math.Min(start.X, end.X);
            double minY = Math.Min(start.Y, end.Y);
            double maxX = Math.Max(start.X, end.X);
            double maxY = Math.Max(start.Y, end.Y);
            
            double padding = 20 + _currentThickness * 2;
            minX -= padding; minY -= padding; maxX += padding; maxY += padding;

            var group = new Canvas { 
                Width = maxX - minX, 
                Height = maxY - minY, 
                Tag = new ArrowProperties { Start = start, End = end } 
            };
            Canvas.SetLeft(group, minX);
            Canvas.SetTop(group, minY);

            group.Children.Add(new Avalonia.Controls.Shapes.Line { Stroke = brush, StrokeThickness = _currentThickness, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round });
            group.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = brush });
            
            UpdateArrowVisuals(group, start, end);
            AddAnnotation(group);
        }

        private async Task<ImageSharpImage> GetFlattenedImageAsync()
        {
            if (!HasImage) return null;
            FlattenedSurface surface = await RenderFlattenedSurfaceAsync().ConfigureAwait(true);
            if (surface == null)
            {
                throw new InvalidOperationException("The edited picture could not be rendered. Nothing was exported.");
            }
            bool addFrameBorders = IniConfig.GetIniSection<CoreConfiguration>().AddFrameBorders;
            return await Task.Run(() => LoadFlattenedImage(surface, addFrameBorders)).ConfigureAwait(true);
        }

        private async Task<ImageSharpImage> GetFlattenedImageForOcrAsync()
        {
            if (!HasImage) return null;
            FlattenedSurface surface = await RenderFlattenedSurfaceAsync().ConfigureAwait(true);
            if (surface == null)
            {
                throw new InvalidOperationException("The edited picture could not be rendered. OCR was cancelled.");
            }
            return await Task.Run(() => LoadFlattenedImage(surface, addFrameBorders: false)).ConfigureAwait(true);
        }

        private async Task<FlattenedSurface> RenderFlattenedSurfaceAsync()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                await WaitForPixelationAsync().ConfigureAwait(true);
                return RenderFlattenedSurface();
            }

            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await WaitForPixelationAsync().ConfigureAwait(true);
                return RenderFlattenedSurface();
            });
        }

        private async Task WaitForPixelationAsync()
        {
            while (true)
            {
                var pixelations = GetPixelationsForExport().ToArray();
                foreach (var control in pixelations)
                    if (!control.Resources.ContainsKey("PixelateTask")) RefreshPixelateAnnotation(control);
                var tasks = pixelations
                    .Select(c => c.Resources.TryGetValue("PixelateTask", out var task) ? task as Task : null)
                    .Where(t => t != null).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(true);
                var current = GetPixelationsForExport()
                    .Select(c => c.Resources.TryGetValue("PixelateTask", out var task) ? task as Task : null)
                    .Where(t => t != null).ToArray();
                if (tasks.SequenceEqual(current)) return;
            }
        }

        private IEnumerable<AvaloniaControl> GetPixelationsForExport()
        {
            static IEnumerable<AvaloniaControl> Walk(AvaloniaControl control)
            {
                if (IsPixelateControl(control)) yield return control;
                if (control is Canvas group)
                    foreach (var child in group.Children)
                        foreach (var item in Walk(child)) yield return item;
            }
            return GetUserAnnotations().SelectMany(Walk);
        }

        private sealed class FlattenedSurface
        {
            public byte[] Pixels;
            public int Width;
            public int Height;
            public bool IsRgbaOrder;
            public bool IsPremultiplied;
        }

        private FlattenedSurface RenderFlattenedSurface()
        {
            if (!TryGetImageSize(out int imageWidth, out int imageHeight)) return null;
            var chromeState = SetEditorChromeVisible(false);
            double oldZoom = _zoomFactor;
            var oldScrollOffset = _editorScrollViewer?.Offset ?? default;
            ITransform oldZoomTransform = _zoomContainer?.RenderTransform;
            bool oldOverlayVisible = _overlayCanvas?.IsVisible ?? true;
            IBrush oldSnipBackground = _snipBorder?.Background;
            Thickness oldSnipThickness = _snipBorder?.BorderThickness ?? default;
            IBrush oldSnipBrush = _snipBorder?.BorderBrush;

            try
            {
                int w = imageWidth;
                int h = imageHeight;

                if (_overlayCanvas != null)
                {
                    _overlayCanvas.IsVisible = false;
                }

                if (_snipBorder != null)
                {
                    _snipBorder.Background = null;
                    _snipBorder.BorderThickness = new Thickness(0);
                    _snipBorder.Width = w;
                    _snipBorder.Height = h;
                }

                if (_imageControl != null)
                {
                    _imageControl.Width = w;
                    _imageControl.Height = h;
                }

                if (_canvas != null)
                {
                    _canvas.RenderTransform = null;
                    _canvas.Width = w;
                    _canvas.Height = h;
                }

                Visual renderVisual;
                if (_zoomContainer != null)
                {
                    _zoomFactor = 1.0;
                    _zoomContainer.RenderTransform = null;
                    _zoomContainer.Width = w;
                    _zoomContainer.Height = h;
                    _zoomContainer.Measure(new AvaloniaSize(w, h));
                    _zoomContainer.Arrange(new Rect(0, 0, w, h));
                    renderVisual = _zoomContainer;
                }
                else
                {
                    _canvas?.Measure(new AvaloniaSize(w, h));
                    _canvas?.Arrange(new Rect(0, 0, w, h));
                    renderVisual = _canvas;
                }

                if (renderVisual == null) return null;

                using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                rtb.Render(renderVisual);

                int stride = checked(w * 4);
                byte[] pixels = new byte[checked(stride * h)];
                GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    rtb.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), pixels.Length, stride);
                }
                finally
                {
                    pin.Free();
                }

                Avalonia.Platform.PixelFormat? format = rtb.Format;
                return new FlattenedSurface
                {
                    Pixels = pixels,
                    Width = w,
                    Height = h,
                    IsRgbaOrder = format.HasValue && format.Value == Avalonia.Platform.PixelFormat.Rgba8888,
                    IsPremultiplied = rtb.AlphaFormat.GetValueOrDefault(Avalonia.Platform.AlphaFormat.Premul) == Avalonia.Platform.AlphaFormat.Premul
                };
            }
            catch (Exception ex)
            {
                Log.Error("Flattening failed", ex);
                return null;
            }
            finally
            {
                if (_snipBorder != null)
                {
                    _snipBorder.Background = oldSnipBackground;
                    _snipBorder.BorderThickness = oldSnipThickness;
                    _snipBorder.BorderBrush = oldSnipBrush;
                }

                if (_overlayCanvas != null)
                {
                    _overlayCanvas.IsVisible = oldOverlayVisible;
                }

                if (_zoomContainer != null)
                {
                    _zoomContainer.RenderTransform = oldZoomTransform;
                }

                _zoomFactor = oldZoom;
                ApplyZoom();
                if (_editorScrollViewer != null)
                {
                    _editorScrollViewer.Offset = oldScrollOffset;
                }
                RestoreVisibility(chromeState);
            }
        }

        private static ImageSharpImage LoadFlattenedImage(FlattenedSurface surface, bool addFrameBorders)
        {
            if (surface?.Pixels == null) return null;
            NormalizeFlattenedPixels(surface);
            var sharpImg = ImageSharpImage.LoadPixelData<Bgra32>(surface.Pixels, surface.Width, surface.Height);
            if (addFrameBorders)
            {
                var config = IniConfig.GetIniSection<CoreConfiguration>();
                int t = config?.FrameBorderThickness > 0 ? config.FrameBorderThickness : 4;
                EditorExportService.ApplyFrameBorder(sharpImg, t, config?.FrameBorderColor);
            }
            return sharpImg;
        }

        private static void NormalizeFlattenedPixels(FlattenedSurface surface)
        {
            byte[] pixels = surface.Pixels;
            bool swap = surface.IsRgbaOrder;
            bool unpremultiply = surface.IsPremultiplied;
            if (!swap && !unpremultiply) return;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                if (swap)
                {
                    byte tmp = b;
                    b = r;
                    r = tmp;
                }

                if (unpremultiply && a != 0 && a != 255)
                {
                    b = (byte)Math.Min(255, b * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    r = (byte)Math.Min(255, r * 255 / a);
                }

                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        private SixLabors.ImageSharp.Rectangle ClampImageRectangle(Rect rect)
        {
            return ClampImageRectangle((int)Math.Floor(rect.X), (int)Math.Floor(rect.Y), (int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height));
        }

        private SixLabors.ImageSharp.Rectangle ClampImageRectangle(int x, int y, int width, int height)
        {
            if (_image == null) return new SixLabors.ImageSharp.Rectangle(0, 0, 0, 0);
            int left = Math.Clamp(x, 0, _image.Width);
            int top = Math.Clamp(y, 0, _image.Height);
            int right = Math.Clamp(x + width, 0, _image.Width);
            int bottom = Math.Clamp(y + height, 0, _image.Height);
            return new SixLabors.ImageSharp.Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
        
        private async Task FlattenAnnotationsAsync()
        {
            if (GetUserAnnotations().Count == 0) return;
            var flattened = await GetFlattenedImageAsync().ConfigureAwait(true); 
            if (flattened == null) return;
            SetImageUnderLock(flattened);
            RemoveUserAnnotations(); 
            _selectedControl = null; 
            UpdateSelectionIndicator(); 
            UpdateThicknessPanelVisibility(); 
            await UpdateDisplayAsync().ConfigureAwait(true);
        }

        private async void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            if (GetUserAnnotations().Count == 0) return;
            int count = GetUserAnnotations().Count;
            bool confirmed = await ConfirmDialog.ShowAsync(
                this,
                "Clear All",
                count == 1
                    ? "This permanently removes the 1 object you added to this image. It cannot be recovered after the editor closes."
                    : $"This permanently removes all {count} objects you added to this image. They cannot be recovered after the editor closes.",
                "Clear All",
                "Keep Them",
                true).ConfigureAwait(true);
            if (confirmed) { SaveUndoState(false); RemoveUserAnnotations(); _selectedControl = null; UpdateSelectionIndicator(); ShowUndoAvailableHint(); }
        }

        private void SetCurrentTool(EditorTool newTool)
        {
            if (_currentTool == newTool) return;
            if (_toolHandlers.TryGetValue(_currentTool, out var oldHandler))
            {
                oldHandler.OnDeactivated(_toolContext);
            }
            _currentTool = newTool;
            if (_toolHandlers.TryGetValue(newTool, out var newHandler))
            {
                _activeToolHandler = newHandler;
                newHandler.OnActivated(_toolContext);
            }
            else if (_toolHandlers.TryGetValue(EditorTool.None, out var noneHandler))
            {
                _activeToolHandler = noneHandler;
            }
        }

        private ToolPointerEvent CreateToolPointerEvent(PointerEventArgs e, AvaloniaPoint canvasPos)
        {
            var p = e.GetCurrentPoint(this);
            return new ToolPointerEvent(
                canvasPosition: canvasPos,
                windowPosition: e.GetPosition(this),
                modifiers: e.KeyModifiers,
                isLeftButtonPressed: p.Properties.IsLeftButtonPressed,
                isRightButtonPressed: p.Properties.IsRightButtonPressed,
                isMiddleButtonPressed: p.Properties.IsMiddleButtonPressed,
                clickCount: (e as PointerPressedEventArgs)?.ClickCount ?? 0,
                isCaptured: ReferenceEquals(e.Pointer.Captured, _canvas)
            );
        }

        #region IToolContextBridge Implementation
        Canvas IToolContextBridge.Canvas => _canvas;
        AvaloniaSize IToolContextBridge.CurrentImageSize => _image != null ? new AvaloniaSize(_image.Width, _image.Height) : (_canvas != null ? new AvaloniaSize(_canvas.Bounds.Width, _canvas.Bounds.Height) : new AvaloniaSize(0, 0));
        double IToolContextBridge.ZoomFactor => _zoomFactor;
        EditorHistoryManager IToolContextBridge.History => _historyManager;
        IBrush IToolContextBridge.CurrentBrush => _toolBrushes.TryGetValue(_currentTool, out var b) ? b : Brushes.Red;
        double IToolContextBridge.CurrentThickness => _currentThickness;
        string IToolContextBridge.PendingEmoji => _pendingEmoji;
        int IToolContextBridge.CounterValue { get => _counterValue; set => _counterValue = value; }
        bool IToolContextBridge.IsFillMode => _isFillMode;
        bool IToolContextBridge.GetToolFillMode(EditorTool tool) => _toolFillModes.TryGetValue(tool, out var fm) && fm;
        bool IToolContextBridge.IsDrawing { get => _isDrawing; set => _isDrawing = value; }
        AvaloniaPoint IToolContextBridge.StartPoint { get => _startPoint; set => _startPoint = value; }

        void IToolContextBridge.CaptureUndoCheckpoint(bool cloneImage) => SaveUndoState(cloneImage);
        void IToolContextBridge.SetEditorCursor(Avalonia.Input.Cursor cursor) => SetEditorCursor(cursor);
        void IToolContextBridge.ShowToolGhost(AvaloniaPoint pos) => UpdateToolGhost(pos);
        void IToolContextBridge.HideToolGhost() => HideToolGhost();
        void IToolContextBridge.ShowSnapGuides() => ShowSnapGuides();
        void IToolContextBridge.HideSnapGuides() => HideSnapGuides();
        void IToolContextBridge.ShowSnapHint(AvaloniaPoint pos, bool altPressed) => ShowSnapHint(pos, altPressed);
        void IToolContextBridge.HideSnapHint() => HideSnapHint();
        void IToolContextBridge.ShowToast(string message) => OverlayHelper.ShowLightToast(message, this);
        void IToolContextBridge.CapturePointer(bool capture) { }
        void IToolContextBridge.UpdateMagnetButtonState(bool altPressed) => UpdateMagnetButtonState(altPressed);

        AvaloniaPoint IToolContextBridge.SnapToNearbyTarget(AvaloniaPoint target, AvaloniaPoint fallback) => SnapToNearbyTarget(target, fallback);
        AvaloniaPoint IToolContextBridge.ClampRectDrawEnd(AvaloniaPoint end) => ClampRectDrawEnd(end);
        AvaloniaPoint IToolContextBridge.ApplyVectorConstraints(AvaloniaPoint current, AvaloniaPoint anchor, KeyModifiers modifiers, bool allowTargetSnap)
            => ApplyVectorConstraints(current, anchor, modifiers, allowTargetSnap);
        void IToolContextBridge.TriggerSnapGlowEffect(AvaloniaPoint point) => TriggerSnapGlowEffect(point);
        void IToolContextBridge.RefreshSnapTargetsList(AvaloniaControl exclude, AvaloniaPoint currentPos) => RefreshSnapTargetsList(exclude, currentPos);
        void IToolContextBridge.HighlightSnapDot(AvaloniaPoint? snappedPoint, AvaloniaPoint currentPos) => HighlightSnapDot(snappedPoint, currentPos);
        void IToolContextBridge.UpdateVectorInfo(AvaloniaPoint start, AvaloniaPoint end, bool altPressed) => UpdateVectorInfo(start, end, altPressed);
        void IToolContextBridge.HideVectorInfo() => HideVectorInfo();

        void IToolContextBridge.BeginPreviewShape(AvaloniaPoint start, IBrush brush, EditorTool tool) => BeginPreviewShape(start, brush, tool);
        void IToolContextBridge.UpdatePreviewShape(AvaloniaPoint end) => UpdatePreviewShape(end);
        void IToolContextBridge.RemovePreviewShape() => RemovePreviewShape();
        void IToolContextBridge.CommitShape(AvaloniaPoint start, AvaloniaPoint end, EditorTool tool) => CommitShape(start, end, tool);
        void IToolContextBridge.AddAnnotation(AvaloniaControl control) => AddAnnotation(control);
        void IToolContextBridge.FinalizeSelectedPasteObject() => FinalizeSelectedPasteObject();
        void IToolContextBridge.ClearSelection()
        {
            if (_selectedControl is Canvas activeGroup && activeGroup.Tag as string == "MultiSelectGroup")
            {
                UngroupMultiSelectGroup(activeGroup);
            }
            CleanupOrphanedMultiSelectGroups();
            _selectedControl = null;
            UpdateSelectionIndicator();
            UpdateHoverIndicator(null);
        }

        void IToolContextBridge.PlaceCounter(AvaloniaPoint pos, IBrush brush) => PlaceCounter(pos, brush);
        void IToolContextBridge.PlaceEmoji(AvaloniaPoint pos, string emoji) => PlaceEmoji(pos, emoji);
        void IToolContextBridge.BeginFreeDraw(AvaloniaPoint start, IBrush brush)
        {
            _activePolyline = new Avalonia.Controls.Shapes.Polyline
            {
                Stroke = brush,
                StrokeThickness = _currentThickness,
                IsHitTestVisible = true,
                ZIndex = _currentZIndex++
            };
            Canvas.SetLeft(_activePolyline, start.X);
            Canvas.SetTop(_activePolyline, start.Y);
            _activePolyline.Points.Add(new AvaloniaPoint(0, 0));
            AddAnnotation(_activePolyline);
        }
        void IToolContextBridge.AddFreeDrawPoint(AvaloniaPoint pos)
        {
            if (_snapDotsLayer != null) _snapDotsLayer.IsVisible = false;
            ClearSnapTargets();
            _activePolyline?.Points.Add(new AvaloniaPoint(pos.X - _startPoint.X, pos.Y - _startPoint.Y));
        }
        void IToolContextBridge.EndFreeDraw()
        {
            if (_activePolyline != null && _activePolyline.Points != null && _activePolyline.Points.Count > 1)
            {
                double pMinX = _activePolyline.Points.Min(p => p.X);
                double pMinY = _activePolyline.Points.Min(p => p.Y);
                double pMaxX = _activePolyline.Points.Max(p => p.X);
                double pMaxY = _activePolyline.Points.Max(p => p.Y);
                double curW = Math.Max(1.0, pMaxX - pMinX);
                double curH = Math.Max(1.0, pMaxY - pMinY);

                if (pMinX != 0 || pMinY != 0)
                {
                    double curL = Canvas.GetLeft(_activePolyline);
                    double curT = Canvas.GetTop(_activePolyline);
                    Canvas.SetLeft(_activePolyline, curL + pMinX);
                    Canvas.SetTop(_activePolyline, curT + pMinY);

                    var normalized = new System.Collections.Generic.List<AvaloniaPoint>(_activePolyline.Points.Count);
                    foreach (var pt in _activePolyline.Points)
                    {
                        normalized.Add(new AvaloniaPoint(pt.X - pMinX, pt.Y - pMinY));
                    }
                    _activePolyline.Points = normalized;
                }
                _activePolyline.Width = curW;
                _activePolyline.Height = curH;
            }
            _activePolyline = null;
        }
        AvaloniaPoint IToolContextBridge.ApplyCropModeToStart(AvaloniaPoint pt) => ApplyCropModeToStart(pt);
        AvaloniaPoint IToolContextBridge.ApplyCropModeToEnd(AvaloniaPoint pt) => ApplyCropModeToEnd(pt);
        void IToolContextBridge.CommitCrop(AvaloniaPoint start, AvaloniaPoint end) => CommitShape(start, end, EditorTool.Crop);
        #endregion
    }
}
