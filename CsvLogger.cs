using System;
using System.IO;

namespace BuildSmarterContentPackage
{
    enum Severity
    {
        Message,
        Benign,
        Tolerable,
        Degraded,
        Severe
    }

    class CsvLogger : IDisposable
    {
        StreamWriter m_writer;
        int m_errorCount;
        int m_messageCount;
        TextWriter m_trace;

        public CsvLogger(string filename)
        {
            m_writer = new StreamWriter(filename, false, System.Text.Encoding.UTF8);
            m_writer.WriteLine("Severity,ItemId,Message,Detail");
        }

        public void Log(Severity severity, string itemId, string message, string detail = null)
        {
            if (itemId == null) itemId = string.Empty;
            if (message == null) message = string.Empty;
            if (detail == null) detail = string.Empty;
            string msg = String.Join(",", severity.ToString(), CsvEncode(itemId), CsvEncode(message), CsvEncode(detail));
            m_writer.WriteLine(msg);
            if (m_trace != null)
            {
                m_trace.WriteLine(msg);
            }
            ++m_messageCount;
            ++m_errorCount;
        }

        public int MessageCount
        {
            get { return m_messageCount; }
        }

        public int ErrorCount
        {
            get { return m_errorCount; }
        }

        public TextWriter Trace
        {
            get { return m_trace; }
            set { m_trace = value; }
        }

        private static readonly char[] cCsvEscapeChars = { ',', '"', '\'', '\r', '\n' };

        private static string CsvEncode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            if (text.IndexOfAny(cCsvEscapeChars) < 0) return text;
            return string.Concat("\"", text.Replace("\"", "\"\""), "\"");
        }

        #region IDisposable Support
        public void Dispose()
        {
            if (m_writer != null)
            {
                m_writer.Dispose();
            }
            m_writer = null;
        }
        #endregion

    }
}
