using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace freesnip.editor.forms
{
    public partial class ResizeWindow : Window
    {
        private TextBox _widthInput;
        private TextBox _heightInput;
        private CheckBox _ratioCheckbox;
        private double _ratio;
        private bool _isUpdating;

        public int ResultWidth { get; private set; }
        public int ResultHeight { get; private set; }
        public bool IsConfirmed { get; private set; }

        private Slider _sizeSlider;
        private TextBlock _percentText;
        private TextBlock _scaleHintText;
        private TextBlock _currentSizeText;
        private int _originalWidth;
        private int _originalHeight;

        /// <summary>Smallest and largest scale offered: five times smaller through five times bigger.</summary>
        public const double MinScalePercent = 20.0;
        public const double MaxScalePercent = 500.0;

        /// <summary>
        /// The slider track is logarithmic: -100 is 20%, 0 is 100%, +100 is 500%. A linear
        /// 20-500 track would squeeze every shrink value into the bottom sixth of the track.
        /// </summary>
        private const double ScaleFactor = 5.0;
        private const double SliderSnapWindow = 4.0;

        /// <summary>Refuse a resize that would allocate an unreasonable bitmap.</summary>
        private const long MaxResultPixels = 80_000_000L;

        public ResizeWindow()
        {
            InitializeComponent();
        }

        public ResizeWindow(int currentWidth, int currentHeight)
        {
            InitializeComponent();
            _widthInput = this.FindControl<TextBox>("WidthInput");
            _heightInput = this.FindControl<TextBox>("HeightInput");
            _ratioCheckbox = this.FindControl<CheckBox>("RatioCheckbox");
            _sizeSlider = this.FindControl<Slider>("SizeSlider");
            _percentText = this.FindControl<TextBlock>("PercentText");
            _scaleHintText = this.FindControl<TextBlock>("ScaleHintText");
            _currentSizeText = this.FindControl<TextBlock>("CurrentSizeText");

            _originalWidth = currentWidth;
            _originalHeight = currentHeight;
            _ratio = (double)currentWidth / currentHeight;
            _widthInput.Text = currentWidth.ToString();
            _heightInput.Text = currentHeight.ToString();
            if (_currentSizeText != null) _currentSizeText.Text = $"Current: {currentWidth} x {currentHeight}";

            _widthInput.PropertyChanged += OnWidthChanged;
            _heightInput.PropertyChanged += OnHeightChanged;
            _ratioCheckbox.IsCheckedChanged += OnRatioCheckedChanged;
            _sizeSlider.PropertyChanged += OnSliderChanged;
            _sizeSlider.Value = 0;
            UpdatePercentText(100);
            
            KeyDown += OnWindowKeyDown;
            Opened += (_, __) =>
            {
                if (Owner != null) Topmost = Owner.Topmost;
                _widthInput?.Focus();
            };
        }

        private static double SliderPositionToPercent(double position)
        {
            return 100.0 * Math.Pow(ScaleFactor, Math.Clamp(position, -100.0, 100.0) / 100.0);
        }

        private static double PercentToSliderPosition(double percent)
        {
            percent = Math.Clamp(percent, MinScalePercent, MaxScalePercent);
            return Math.Clamp(100.0 * Math.Log(percent / 100.0) / Math.Log(ScaleFactor), -100.0, 100.0);
        }

        private void OnSliderChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Value" && !_isUpdating && _ratioCheckbox.IsChecked == true)
            {
                double position = _sizeSlider.Value;

                if (Math.Abs(position) < SliderSnapWindow) position = 0;

                if (Math.Abs(_sizeSlider.Value - position) > 0.01)
                {
                    _sizeSlider.Value = position;
                    return;
                }

                ApplyScalePercent(SliderPositionToPercent(position), false);
            }
        }

        private void OnPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            double percent;
            if (button.Tag?.ToString() == "Fit")
            {
                percent = GetFitPercent();
            }
            else if (!double.TryParse(button.Tag?.ToString(), out percent))
            {
                return;
            }

            ApplyScalePercent(percent, true);
        }

        private double GetFitPercent()
        {
            var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            if (screen == null || _originalWidth <= 0 || _originalHeight <= 0) return 100;
            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            double maxWidth = screen.WorkingArea.Width / scaling * 0.9;
            double maxHeight = screen.WorkingArea.Height / scaling * 0.9;
            return Math.Min(maxWidth / _originalWidth, maxHeight / _originalHeight) * 100.0;
        }

        private void ApplyScalePercent(double percent, bool moveSlider)
        {
            percent = Math.Clamp(percent, MinScalePercent, MaxScalePercent);
            double scale = percent / 100.0;

            _isUpdating = true;
            _widthInput.Text = Math.Max(1, (int)Math.Round(_originalWidth * scale)).ToString();
            _heightInput.Text = Math.Max(1, (int)Math.Round(_originalHeight * scale)).ToString();
            if (moveSlider && _sizeSlider != null) _sizeSlider.Value = PercentToSliderPosition(percent);
            UpdatePercentText(percent);
            _isUpdating = false;
        }

        private void UpdatePercentText(double percent)
        {
            if (_percentText != null) _percentText.Text = $"{(int)Math.Round(percent)}%";
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            freesnip.foundation.core.UiLayoutDirection.Apply(this);
        }

        private void OnRatioCheckedChanged(object sender, RoutedEventArgs e)
        {
            bool isRatioLocked = _ratioCheckbox.IsChecked == true;
            if (_sizeSlider != null)
            {
                _sizeSlider.IsEnabled = isRatioLocked;
                _sizeSlider.Opacity = isRatioLocked ? 1.0 : 0.4;
            }

            if (_scaleHintText != null)
            {
                _scaleHintText.Opacity = isRatioLocked ? 1.0 : 0.4;
            }

            if (isRatioLocked)
            {
                if (int.TryParse(_widthInput.Text, out int w))
                {
                    _isUpdating = true;
                    _heightInput.Text = Math.Max(1, (int)Math.Round(w / _ratio)).ToString();
                    double percent = _originalWidth == 0 ? 100 : w * 100.0 / _originalWidth;
                    if (_sizeSlider != null) _sizeSlider.Value = PercentToSliderPosition(percent);
                    UpdatePercentText(percent);
                    _isUpdating = false;
                }
            }
            else
            {
                if (_percentText != null) _percentText.Text = "Custom";
            }
        }

        private void OnWidthChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Text" && !_isUpdating)
            {
                if (_ratioCheckbox.IsChecked == true)
                {
                    if (int.TryParse(_widthInput.Text, out int w))
                    {
                        _isUpdating = true;
                        _heightInput.Text = Math.Max(1, (int)Math.Round(w / _ratio)).ToString();
                        double percent = _originalWidth == 0 ? 100 : w * 100.0 / _originalWidth;
                        if (_sizeSlider != null) _sizeSlider.Value = PercentToSliderPosition(percent);
                        UpdatePercentText(percent);
                        _isUpdating = false;
                    }
                }
                else
                {
                    if (_percentText != null) _percentText.Text = "Custom";
                }
            }
        }

        private void OnHeightChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Text" && !_isUpdating)
            {
                if (_ratioCheckbox.IsChecked == true)
                {
                    if (int.TryParse(_heightInput.Text, out int h))
                    {
                        _isUpdating = true;
                        _widthInput.Text = Math.Max(1, (int)Math.Round(h * _ratio)).ToString();
                        double percent = _originalHeight == 0 ? 100 : h * 100.0 / _originalHeight;
                        if (_sizeSlider != null) _sizeSlider.Value = PercentToSliderPosition(percent);
                        UpdatePercentText(percent);
                        _isUpdating = false;
                    }
                }
                else
                {
                    if (_percentText != null) _percentText.Text = "Custom";
                }
            }
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnOkClick(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                OnCancelClick(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private async void OnOkClick(object sender, RoutedEventArgs e)
        {
            bool validW = int.TryParse(_widthInput.Text, out int w);
            bool validH = int.TryParse(_heightInput.Text, out int h);

            if (!validW || !validH)
            {
                await freesnip.editor.helpers.ConfirmDialog.ShowAlertAsync(
                    this,
                    "Invalid Dimensions",
                    "Please enter valid whole numbers for width and height.",
                    "OK",
                    true).ConfigureAwait(true);
                if (!validW) _widthInput?.Focus();
                else _heightInput?.Focus();
                return;
            }

            if (w < 1 || h < 1)
            {
                await freesnip.editor.helpers.ConfirmDialog.ShowAlertAsync(
                    this,
                    "Size Too Small",
                    "Width and height both have to be at least 1 pixel. Pick a larger size and try again.",
                    "OK",
                    true).ConfigureAwait(true);
                return;
            }

            if ((long)w * h > MaxResultPixels)
            {
                await freesnip.editor.helpers.ConfirmDialog.ShowAlertAsync(
                    this,
                    "Picture Would Be Too Big",
                    $"{w} x {h} is about {(long)w * h / 1_000_000L} megapixels. SnapVox stops at {MaxResultPixels / 1_000_000L} megapixels because anything larger can run the computer out of memory. Choose a smaller percentage and try again.",
                    "OK",
                    true).ConfigureAwait(true);
                return;
            }

            ResultWidth = w;
            ResultHeight = h;
            IsConfirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            Close();
        }
    }
}
