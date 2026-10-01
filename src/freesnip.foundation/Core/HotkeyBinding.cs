using System;
using Avalonia.Input;

namespace freesnip.foundation.core
{
    public readonly record struct HotkeyBinding
    {
        public Key Key { get; init; }
        public KeyModifiers Modifiers { get; init; }
        public string RawString { get; init; }

        public bool IsValid => Key != Key.None;
        public bool IsEmpty => Key == Key.None;

        public static readonly HotkeyBinding Empty = new HotkeyBinding(Key.None, KeyModifiers.None, string.Empty);

        public HotkeyBinding(Key key, KeyModifiers modifiers = KeyModifiers.None, string rawString = null)
        {
            Key = key;
            Modifiers = modifiers;
            RawString = rawString ?? Format(key, modifiers);
        }

        public static HotkeyBinding Parse(string hotkeyString)
        {
            if (string.IsNullOrWhiteSpace(hotkeyString) || string.Equals(hotkeyString, "None", StringComparison.OrdinalIgnoreCase))
            {
                return Empty;
            }

            KeyModifiers modifiers = KeyModifiers.None;
            if (hotkeyString.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase) >= 0) modifiers |= KeyModifiers.Control;
            if (hotkeyString.IndexOf("Alt", StringComparison.OrdinalIgnoreCase) >= 0) modifiers |= KeyModifiers.Alt;
            if (hotkeyString.IndexOf("Shift", StringComparison.OrdinalIgnoreCase) >= 0) modifiers |= KeyModifiers.Shift;
            if (hotkeyString.IndexOf("Win", StringComparison.OrdinalIgnoreCase) >= 0 || hotkeyString.IndexOf("Meta", StringComparison.OrdinalIgnoreCase) >= 0) modifiers |= KeyModifiers.Meta;

            var parts = hotkeyString.Split('+');
            string keyPart = parts[parts.Length - 1].Trim();
            if (!Enum.TryParse<Key>(keyPart, true, out var key))
            {
                return Empty;
            }

            return new HotkeyBinding(key, modifiers, hotkeyString.Trim());
        }

        public bool Matches(Key key, KeyModifiers modifiers)
        {
            if (!IsValid) return false;
            return Key == key && Modifiers == modifiers;
        }

        private static string Format(Key key, KeyModifiers modifiers)
        {
            if (key == Key.None) return "None";
            var parts = new System.Collections.Generic.List<string>();
            if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join(" + ", parts);
        }

        public override string ToString() => RawString;
    }
}