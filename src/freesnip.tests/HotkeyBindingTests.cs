using Avalonia.Input;
using freesnip.foundation.core;
using Xunit;

namespace freesnip.tests
{
    public class HotkeyBindingTests
    {
        [Fact]
        public void Parse_SingleKey_ParsesCorrectly()
        {
            var binding = HotkeyBinding.Parse("PrintScreen");
            Assert.True(binding.IsValid);
            Assert.False(binding.IsEmpty);
            Assert.Equal(Key.PrintScreen, binding.Key);
            Assert.Equal(KeyModifiers.None, binding.Modifiers);
        }

        [Fact]
        public void Parse_ModifiersAndKey_ParsesCorrectly()
        {
            var binding = HotkeyBinding.Parse("Ctrl + Alt + PrintScreen");
            Assert.True(binding.IsValid);
            Assert.Equal(Key.PrintScreen, binding.Key);
            Assert.True(binding.Modifiers.HasFlag(KeyModifiers.Control));
            Assert.True(binding.Modifiers.HasFlag(KeyModifiers.Alt));
            Assert.False(binding.Modifiers.HasFlag(KeyModifiers.Shift));
        }

        [Fact]
        public void Parse_NoneOrEmpty_ReturnsEmptyBinding()
        {
            var b1 = HotkeyBinding.Parse(null);
            var b2 = HotkeyBinding.Parse("");
            var b3 = HotkeyBinding.Parse("None");

            Assert.True(b1.IsEmpty);
            Assert.False(b1.IsValid);
            Assert.True(b2.IsEmpty);
            Assert.True(b3.IsEmpty);
        }

        [Fact]
        public void Matches_VerifiesKeyAndModifiers()
        {
            var binding = HotkeyBinding.Parse("Ctrl + S");
            Assert.True(binding.Matches(Key.S, KeyModifiers.Control));
            Assert.False(binding.Matches(Key.S, KeyModifiers.None));
            Assert.False(binding.Matches(Key.A, KeyModifiers.Control));
        }

        [Fact]
        public void CoreConfiguration_ExposesStronglyTypedBindings()
        {
            var config = new CoreConfiguration
            {
                RegionHotkey = "PrintScreen",
                WindowHotkey = "Alt + PrintScreen",
                FullscreenHotkey = "Ctrl + PrintScreen"
            };

            Assert.Equal(Key.PrintScreen, config.GetRegionBinding().Key);
            Assert.Equal(KeyModifiers.None, config.GetRegionBinding().Modifiers);

            Assert.Equal(Key.PrintScreen, config.GetWindowBinding().Key);
            Assert.Equal(KeyModifiers.Alt, config.GetWindowBinding().Modifiers);

            Assert.Equal(Key.PrintScreen, config.GetFullscreenBinding().Key);
            Assert.Equal(KeyModifiers.Control, config.GetFullscreenBinding().Modifiers);
        }
    }
}

