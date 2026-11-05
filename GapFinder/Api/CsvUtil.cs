using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GapFinder.Api
{
    /// <summary>RFC 4180 CSV writer with CSV injection protection.</summary>
    public static class CsvUtil
    {
        public static char DelimiterFromName(string name)
        {
            return string.Equals(name, "comma", System.StringComparison.OrdinalIgnoreCase) ? ',' : ';';
        }

        public static string Escape(string value, char delimiter)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // CSV injection protection (OWASP): cells starting with =, +, -, @, tab or CR get a leading ' so
            // they are not evaluated as formulas.
            if (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@'
                || value[0] == '\t' || value[0] == '\r')
            {
                value = "'" + value;
            }

            var needsQuoting = value.IndexOf(delimiter) >= 0
                || value.IndexOf('"') >= 0
                || value.IndexOf('\n') >= 0
                || value.IndexOf('\r') >= 0;

            if (needsQuoting)
            {
                value = "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        public static string BuildRow(char delimiter, params string[] values)
        {
            return string.Join(delimiter.ToString(), values.Select(v => Escape(v, delimiter)));
        }

        /// <summary>CSV file with UTF-8 BOM (otherwise Excel does not detect non-ASCII characters).</summary>
        public static byte[] BuildCsvFile(char delimiter, string[] header, IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append(BuildRow(delimiter, header)).Append("\r\n");
            foreach (var row in rows)
            {
                sb.Append(BuildRow(delimiter, row)).Append("\r\n");
            }

            var preamble = Encoding.UTF8.GetPreamble();
            var bodyBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var result = new byte[preamble.Length + bodyBytes.Length];
            preamble.CopyTo(result, 0);
            bodyBytes.CopyTo(result, preamble.Length);
            return result;
        }
    }
}
