#nullable enable
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using freesnip.editor.Services;
using Xunit;

namespace freesnip.tests
{
    public class ContextToolbarThemeTests
    {
        [Theory]
        [InlineData(0.0, ContextToolbarTheme.ThemeMode.DarkBackground)]
        [InlineData(0.2, ContextToolbarTheme.ThemeMode.DarkBackground)]
        [InlineData(0.37, ContextToolbarTheme.ThemeMode.DarkBackground)]
        [InlineData(0.38, ContextToolbarTheme.ThemeMode.MidtoneBackground)]
        [InlineData(0.50, ContextToolbarTheme.ThemeMode.MidtoneBackground)]
        [InlineData(0.65, ContextToolbarTheme.ThemeMode.MidtoneBackground)]
        [InlineData(0.66, ContextToolbarTheme.ThemeMode.BrightBackground)]
        [InlineData(0.95, ContextToolbarTheme.ThemeMode.BrightBackground)]
        [InlineData(1.0, ContextToolbarTheme.ThemeMode.BrightBackground)]
        public void GetThemeMode_ThresholdsCorrect(double lum, ContextToolbarTheme.ThemeMode expected)
        {
            var mode = ContextToolbarTheme.GetThemeMode(lum);
            Assert.Equal(expected, mode);
        }

        [Fact]
        public void Brushes_AllInitialized()
        {
            Assert.NotNull(ContextToolbarTheme.DarkBgToolbarBg);
            Assert.NotNull(ContextToolbarTheme.DarkBgToolbarBorder);
            Assert.NotNull(ContextToolbarTheme.DarkBgSep);
            Assert.NotNull(ContextToolbarTheme.DarkBgDupBtnBg);
            Assert.NotNull(ContextToolbarTheme.DarkBgDupBtnBorder);
            Assert.NotNull(ContextToolbarTheme.DarkBgDupIcon);
            Assert.NotNull(ContextToolbarTheme.DarkBgDelBtnBg);
            Assert.NotNull(ContextToolbarTheme.DarkBgDelBtnBorder);
            Assert.NotNull(ContextToolbarTheme.DarkBgDelIcon);
            Assert.NotNull(ContextToolbarTheme.DarkBgAppBtnBg);
            Assert.NotNull(ContextToolbarTheme.DarkBgAppBtnBorder);
            Assert.NotNull(ContextToolbarTheme.DarkBgAppIcon);

            Assert.NotNull(ContextToolbarTheme.BrightBgToolbarBg);
            Assert.NotNull(ContextToolbarTheme.MidBgToolbarBg);
        }

        [Fact]
        public void ApplyTheme_DarkBackground_AppliesCorrectBrushes()
        {
            var toolbar = new Border();
            var dupBtn = new Button();
            var delBtn = new Button();
            var appBtn = new Button();
            var dupIcon = new Avalonia.Controls.Shapes.Path();
            var delIcon = new Avalonia.Controls.Shapes.Path();
            var appIcon = new Avalonia.Controls.Shapes.Path();
            var sep1 = new Rectangle();
            var sep2 = new Rectangle();

            ContextToolbarTheme.ApplyTheme(
                toolbar,
                0.20,
                dupBtn,
                delBtn,
                appBtn,
                dupIcon,
                delIcon,
                appIcon,
                sep1,
                sep2);

            Assert.Same(ContextToolbarTheme.DarkBgToolbarBg, toolbar.Background);
            Assert.Same(ContextToolbarTheme.DarkBgToolbarBorder, toolbar.BorderBrush);
            Assert.Same(ContextToolbarTheme.DarkBgDupBtnBg, dupBtn.Background);
            Assert.Same(ContextToolbarTheme.DarkBgDelBtnBg, delBtn.Background);
            Assert.Same(ContextToolbarTheme.DarkBgAppBtnBg, appBtn.Background);
            Assert.Same(ContextToolbarTheme.DarkBgDupIcon, dupIcon.Fill);
            Assert.Same(ContextToolbarTheme.DarkBgDelIcon, delIcon.Fill);
            Assert.Same(ContextToolbarTheme.DarkBgAppIcon, appIcon.Fill);
            Assert.Same(ContextToolbarTheme.DarkBgSep, sep1.Fill);
            Assert.Same(ContextToolbarTheme.DarkBgSep, sep2.Fill);
        }

        [Fact]
        public void ApplyTheme_BrightBackground_AppliesCorrectBrushes()
        {
            var toolbar = new Border();
            ContextToolbarTheme.ApplyTheme(toolbar, 0.80, null, null, null, null, null, null, null, null);

            Assert.Same(ContextToolbarTheme.BrightBgToolbarBg, toolbar.Background);
            Assert.Same(ContextToolbarTheme.BrightBgToolbarBorder, toolbar.BorderBrush);
        }

        [Fact]
        public void ApplyTheme_MidtoneBackground_AppliesCorrectBrushes()
        {
            var toolbar = new Border();
            ContextToolbarTheme.ApplyTheme(toolbar, 0.50, null, null, null, null, null, null, null, null);

            Assert.Same(ContextToolbarTheme.MidBgToolbarBg, toolbar.Background);
            Assert.Same(ContextToolbarTheme.MidBgToolbarBorder, toolbar.BorderBrush);
        }
    }
}

