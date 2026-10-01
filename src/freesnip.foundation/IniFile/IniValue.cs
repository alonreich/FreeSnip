using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using log4net;
using freesnip.foundation.core.AvaloniaShims;
using SixLabors.ImageSharp;

namespace freesnip.foundation.IniFile
{
    public class IniValue
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(IniValue));
        private readonly PropertyInfo _propertyInfo;
        private readonly FieldInfo _fieldInfo;
        private readonly IniSection _containingIniSection;
        private readonly IniPropertyAttribute _attributes;

        public IniValue(IniSection containingIniSection, PropertyInfo propertyInfo, IniPropertyAttribute iniPropertyAttribute)
        {
            _containingIniSection = containingIniSection;
            _propertyInfo = propertyInfo;
            _attributes = iniPropertyAttribute;
            IsFixed = iniPropertyAttribute.FixedValue;
            IsExpert = iniPropertyAttribute.Expert;
        }

        public IniValue(IniSection containingIniSection, FieldInfo fieldInfo, IniPropertyAttribute iniPropertyAttribute)
        {
            _containingIniSection = containingIniSection;
            _fieldInfo = fieldInfo;
            _attributes = iniPropertyAttribute;
            IsFixed = iniPropertyAttribute.FixedValue;
            IsExpert = iniPropertyAttribute.Expert;
        }

        public bool IsFixed { get; set; }
        public bool IsExpert { get; set; }
        public bool IsVisible => !IsExpert;

        public object Value
        {
            get
            {
                if (_propertyInfo != null) return _propertyInfo.GetValue(_containingIniSection, null);
                return _fieldInfo?.GetValue(_containingIniSection);
            }
            set
            {
                try
                {
                    if (_propertyInfo != null)
                    {
                        if (_propertyInfo.CanWrite) _propertyInfo.SetValue(_containingIniSection, value, null);
                        else Log.WarnFormat("INI: Property {0} in section {1} is read-only.", _propertyInfo.Name, _containingIniSection.GetType().Name);
                    }
                    else if (_fieldInfo != null)
                    {
                        _fieldInfo.SetValue(_containingIniSection, value);
                    }
                }
                catch (Exception ex) { Log.Error(string.Format("INI: Failed to set {0} in {1}", _propertyInfo?.Name ?? _fieldInfo?.Name, _containingIniSection.GetType().Name), ex); }
            }
        }

        public Type ValueType
        {
            get
            {
                var valueType = _propertyInfo?.PropertyType ?? _fieldInfo.FieldType;
                if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>)) return Nullable.GetUnderlyingType(valueType);
                return valueType;
            }
        }

        public void Write(TextWriter writer, bool onlyProperties)
        {
            object myValue = Value;
            Type valueType = ValueType;
            if (myValue == null)
            {
                if (_attributes.ExcludeIfNull) return;
                myValue = _attributes.DefaultValue ?? _containingIniSection.GetDefault(_attributes.Name);
                if (myValue != null) valueType = myValue.GetType();
            }
            if (myValue == null && _attributes.ExcludeIfNull) return;
            if (!onlyProperties) writer.WriteLine("; {0}", _attributes.Description);
            if (myValue == null) { writer.WriteLine("{0}=", _attributes.Name); return; }

            if (myValue is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    writer.WriteLine("{0}.{1}={2}", _attributes.Name, entry.Key?.ToString() ?? "null", ConvertValueToString(entry.Value?.GetType() ?? typeof(object), entry.Value, _attributes.Separator));
                }
            }
            else
            {
                writer.WriteLine("{0}={1}", _attributes.Name, ConvertValueToString(valueType, myValue, _attributes.Separator));
            }
        }

        public void SetValueFromProperties(IDictionary<string, string> properties)
        {
            string val = properties.ContainsKey(_attributes.Name) ? _containingIniSection.PreCheckValue(_attributes.Name, properties[_attributes.Name]) : null;
            UseValueOrDefault(val);
        }

        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "IniValue handled via rooted IniSection subclasses.")]
        public void UseValueOrDefault(string propertyValue)
        {
            Type valueType = ValueType;
            string propertyName = _attributes.Name;
            bool defaultUsed = false;
            if (string.IsNullOrEmpty(propertyValue))
            {
                if (!string.IsNullOrEmpty(_attributes.DefaultValue)) { propertyValue = _attributes.DefaultValue; defaultUsed = true; }
                else
                {
                    object def = _containingIniSection.GetDefault(propertyName);
                    if (def != null) { Value = def; return; }
                    if (_attributes.ExcludeIfNull) { Value = null; return; }
                }
            }

            if (typeof(IDictionary).IsAssignableFrom(valueType))
            {
                Type[] args = valueType.GetGenericArguments();
                Type t1 = args.Length > 0 ? args[0] : typeof(object);
                Type t2 = args.Length > 1 ? args[1] : typeof(object);
                IDictionary dict = (IDictionary)CreateInstanceForIni(valueType);
                bool added = false;
                IDictionary<string, string> props = IniConfig.PropertiesForSection(_containingIniSection);
                foreach (string key in props.Keys)
                {
                    if (key != null && key.StartsWith(propertyName + "."))
                    {
                        object k = null, v = null;
                        try { k = ConvertStringToValueType(t1, key.Substring(propertyName.Length + 1), _attributes.Separator); } catch { }
                        try { v = ConvertStringToValueType(t2, props[key], _attributes.Separator); } catch { }
                        if (k != null) { dict[k] = v; added = true; }
                    }
                }
                if (added) { Value = dict; return; }
                object d = _containingIniSection.GetDefault(propertyName);
                if (d != null) Value = d;
            }
            else if (!string.IsNullOrEmpty(propertyValue))
            {
                object newValue;
                try { newValue = ConvertStringToValueType(valueType, propertyValue, _attributes.Separator); }
                catch (Exception ex1)
                {
                    newValue = null;
                    if (!defaultUsed)
                    {
                        try { newValue = ConvertStringToValueType(valueType, _attributes.DefaultValue, _attributes.Separator); _containingIniSection.IsDirty = true; }
                        catch { }
                    }
                    if (newValue == null) Log.Warn("INI: Conversion failed for " + propertyName, ex1);
                }
                Value = newValue;
            }
            else
            {
                try { Value = CreateInstanceForIni(ValueType); } catch { }
            }
        }

        private static object CreateInstanceForIni([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type t)
        {
            if (t == typeof(string)) return string.Empty;
            return Activator.CreateInstance(t);
        }

        [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Converter targets declared on rooted subclasses.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Reflection over List/Dictionary is safe for rooted types.")]
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "RequiresUnreferencedCode handled via rooted metadata.")]
        [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "Type.GetType for legacy syntax is non-critical.")]
        private static object ConvertStringToValueType(Type valueType, string valueString, string separator)
        {
            if (valueString == null) return null;
            if (valueType == typeof(string)) return valueString;
            if (string.IsNullOrEmpty(valueString)) return null;

            if (valueType == typeof(bool) && bool.TryParse(valueString, out bool b)) return b;
            if (valueType == typeof(int) && int.TryParse(valueString, out int i)) return i;
            if (valueType == typeof(double) && double.TryParse(valueString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
            if (valueType == typeof(float) && float.TryParse(valueString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float f)) return f;
            if (valueType == typeof(long) && long.TryParse(valueString, out long l)) return l;
            if (valueType == typeof(DateTime) && DateTime.TryParse(valueString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt)) return dt;

            if (valueType == typeof(SIZE))
            {
                string[] parts = valueString.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)) return new SIZE { cx = w, cy = h };
            }
            if (valueType == typeof(freesnip.foundation.core.AvaloniaShims.Rectangle))
            {
                string[] parts = valueString.Split(',');
                if (parts.Length == 4 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y) && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h)) return new freesnip.foundation.core.AvaloniaShims.Rectangle(x, y, w, h);
            }
            if (valueType == typeof(POINT))
            {
                string[] parts = valueString.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)) return new POINT(x, y);
            }
            if (valueType == typeof(RECT))
            {
                string[] parts = valueString.Split(',');
                if (parts.Length == 4 && int.TryParse(parts[0], out int x1) && int.TryParse(parts[1], out int y1) && int.TryParse(parts[2], out int x2) && int.TryParse(parts[3], out int y2)) return new RECT(x1, y1, x2, y2);
            }
            if (valueType == typeof(Color))
            {
                if (Color.TryParseHex(valueString, out Color color)) return color;
                return Color.Black;
            }
            if (valueType.IsEnum)
            {
                if (Enum.TryParse(valueType, valueString, true, out object enumResult)) return enumResult;
                string search = valueString.Replace("_", string.Empty).ToLowerInvariant();
                foreach (var v in Enum.GetValuesAsUnderlyingType(valueType)) 
                {
                    string s = v.ToString() ?? "";
                    if (s.Replace("_", string.Empty).ToLowerInvariant().Equals(search)) return Enum.ToObject(valueType, v);
                }
            }
            if (typeof(IList).IsAssignableFrom(valueType))
            {
                IList list = (IList)CreateInstanceForIni(valueType);
                string[] vals = valueString.Split(new[] { separator }, StringSplitOptions.None);
                Type itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0] : typeof(object);
                foreach (string v in vals) if (!string.IsNullOrEmpty(v)) { try { object n = ConvertStringToValueType(itemType, v, separator); if (n != null) list.Add(n); } catch { } }
                return list;
            }
            if (valueType == typeof(object) && valueString.Contains(':'))
            {
                string[] parts = valueString.Split(':');
                return ConvertStringToValueType(Type.GetType(parts[0], true), parts[1], separator);
            }
            
            try
            {
                return TypeDescriptor.GetConverter(valueType).ConvertFromInvariantString(valueString);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Native AOT Parsing Failed for Type '{valueType.Name}' with value '{valueString}'.", ex);
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Converter targets declared on rooted subclasses.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Reflection over List/Dictionary is safe for rooted types.")]
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "RequiresUnreferencedCode handled via rooted metadata.")]
        private static string ConvertValueToString(Type valueType, object valueObject, string separator)
        {
            if (valueObject == null) return string.Empty;
            if (valueType == typeof(string)) return (string)valueObject;
            if (valueType == typeof(bool)) return ((bool)valueObject).ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
            if (valueType == typeof(int)) return ((int)valueObject).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (valueType == typeof(double)) return ((double)valueObject).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (valueType == typeof(float)) return ((float)valueObject).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (valueType == typeof(long)) return ((long)valueObject).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (valueType == typeof(DateTime)) return ((DateTime)valueObject).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            if (valueType == typeof(SIZE) && valueObject is SIZE s) return $"{s.cx},{s.cy}";
            if (valueType == typeof(freesnip.foundation.core.AvaloniaShims.Rectangle) && valueObject is freesnip.foundation.core.AvaloniaShims.Rectangle r) return $"{r.X},{r.Y},{r.Width},{r.Height}";
            if (valueType == typeof(POINT) && valueObject is POINT pt) return $"{pt.X},{pt.Y}";
            if (valueType == typeof(RECT) && valueObject is RECT rect) return $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
            if (valueType == typeof(Color) && valueObject is Color color) return color.ToHex();

            if (valueObject is IList list)
            {
                StringBuilder sb = new StringBuilder();
                Type itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0] : typeof(object);
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(separator);
                    sb.Append(ConvertValueToString(itemType, list[i], separator));
                }
                return sb.ToString();
            }
            if (valueType.IsEnum) return valueObject.ToString();
            if (valueType == typeof(object))
            {
                Type ot = valueObject.GetType();
                string ov = ConvertValueToString(ot, valueObject, separator);
                string an = ot.Assembly.FullName;
                if (an.StartsWith("Green")) an = an.Substring(0, an.IndexOf(','));
                return $"{ot.FullName},{an}:{ov}";
            }
            
            try
            {
                return TypeDescriptor.GetConverter(valueType).ConvertToInvariantString(valueObject);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Native AOT Formatting Failed for Type '{valueType.Name}'.", ex);
            }
        }
    }
}
