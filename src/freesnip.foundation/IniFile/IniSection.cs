using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using freesnip.foundation.core;
using log4net;

namespace freesnip.foundation.IniFile
{
    [Serializable]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public abstract class IniSection
    {
        protected static ILog LOG = freesnip.foundation.core.LogHelper.GetLogger(typeof(IniSection));
        [NonSerialized] private readonly IDictionary<string, IniValue> values = new Dictionary<string, IniValue>();
        [NonSerialized] private IniSectionAttribute iniSectionAttribute;

        public IniSectionAttribute IniSectionAttribute => iniSectionAttribute ??= GetIniSectionAttribute(GetType());
        public IDictionary<string, IniValue> Values => values;
        public bool IsDirty { get; set; }

        public static IniSectionAttribute GetIniSectionAttribute(Type iniSectionType)
        {
            if (Attribute.GetCustomAttribute(iniSectionType, typeof(IniSectionAttribute)) is IniSectionAttribute attr) return attr;
            throw new InvalidOperationException("IniSection subclasses must be decorated with IniSectionAttribute.");
        }

        public virtual void Fill(IDictionary<string, string> properties)
        {
            Type type = GetType();
            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Attribute.IsDefined(prop, typeof(IniPropertyAttribute)))
                {
                    var attr = (IniPropertyAttribute)prop.GetCustomAttributes(typeof(IniPropertyAttribute), false)[0];
                    if (!values.ContainsKey(attr.Name)) values[attr.Name] = new IniValue(this, prop, attr);
                }
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Attribute.IsDefined(field, typeof(IniPropertyAttribute)))
                {
                    var attr = (IniPropertyAttribute)field.GetCustomAttributes(typeof(IniPropertyAttribute), false)[0];
                    if (!values.ContainsKey(attr.Name)) values[attr.Name] = new IniValue(this, field, attr);
                }
            }
            foreach (IniValue val in values.Values) val.SetValueFromProperties(properties);
            AfterLoad();
        }

        public virtual void Write(TextWriter writer, bool onlyProperties)
        {
            if (!onlyProperties) writer.WriteLine("[{0}]", IniSectionAttribute.Name);
            foreach (IniValue val in values.Values) val.Write(writer, onlyProperties);
        }

        public virtual void AfterLoad() { }
        public virtual object GetDefault(string propertyName) => null;
        public virtual string PreCheckValue(string propertyName, string propertyValue) => propertyValue;
    }
}
