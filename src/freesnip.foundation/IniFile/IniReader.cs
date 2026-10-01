using freesnip.native;
using freesnip.native.foundation;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace freesnip.foundation.IniFile
{

    public static class IniReader
    {
        private const char SectionStartToken = '[';
        private const char SectionEndToken = ']';
        private const char CommentToken = ';';

        private static readonly char[] Assignment =
        {
            '='
        };

        public static IDictionary<string, IDictionary<string, string>> Read(string path, Encoding encoding)
        {
            var ini = new Dictionary<string, IDictionary<string, string>>();
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024))
            {
                using var streamReader = new StreamReader(fileStream, encoding);
                IDictionary<string, string> nameValues = new Dictionary<string, string>();
                while (!streamReader.EndOfStream)
                {
                    string line = streamReader.ReadLine();
                    if (line == null)
                    {
                        continue;
                    }

                    string cleanLine = line.Trim();
                    if (cleanLine.Length == 0 || cleanLine[0] == CommentToken)
                    {
                        continue;
                    }

                    if (cleanLine[0] == SectionStartToken)
                    {
                        var sectionEndIndex = line.IndexOf(SectionEndToken, 1);
                        if (sectionEndIndex < 0)
                        {
                            continue;
                        }

                        string section = line.Substring(1, sectionEndIndex - 1).Trim();
                        if (!ini.TryGetValue(section, out nameValues))
                        {
                            nameValues = new Dictionary<string, string>();
                            ini.Add(section, nameValues);
                        }
                    }
                    else
                    {
                        string[] keyvalueSplitter = line.Split(Assignment, 2);
                        string name = keyvalueSplitter[0];
                        string inivalue = keyvalueSplitter.Length > 1 ? keyvalueSplitter[1] : null;
                        if (nameValues.ContainsKey(name))
                        {
                            nameValues[name] = inivalue;
                        }
                        else
                        {
                            nameValues.Add(name, inivalue);
                        }
                    }
                }
            }

            return ini;
        }
    }
}
