using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Diagnostics;

namespace BuildSmarterContentPackage
{
    class IdReader : IEnumerator<ItemId>, IDisposable
    {
        CsvReader m_csvReader;
        int m_defaultBankKey;
        int m_idColumn = -1;
        int m_bankKeyColumn = -1;
        int m_minColumns = -1;
        ItemId m_current = null;

        public IdReader(string filename, int defaultBankKey)
        {
            m_csvReader = new CsvReader(filename);
            m_defaultBankKey = defaultBankKey;
        }

        public ItemId Current
        {
            get { return m_current; }
        }

        object IEnumerator.Current
        {
            get { return m_current; }
        }

        public bool MoveNext()
        {
            if (m_idColumn == -1)
            {
                return FirstMoveNext();
            }

            for (; ; )
            {
                string[] row = m_csvReader.Read();
                if (row == null) return false;

                if (row.Length < m_minColumns)
                {
                    Program.ProgressLog.Log(Severity.Degraded, string.Empty, "Too few columns in item ID input file row.", $"minColumns={m_minColumns} line='{String.Join(",", row)}'");
                    continue;
                }

                int bankKey = m_defaultBankKey;
                if (m_bankKeyColumn >= 0 && !string.IsNullOrEmpty(row[m_bankKeyColumn]))
                {
                    if (!int.TryParse(row[m_bankKeyColumn], out bankKey))
                    {
                        Program.ProgressLog.Log(Severity.Degraded, row[m_idColumn], "Invalid bankKey value in item ID input file row.", $"bankKey='{row[m_bankKeyColumn]}'");
                        continue;
                    }
                }

                if (!ItemId.TryParse(row[m_idColumn], bankKey, out m_current))
                {
                    Program.ProgressLog.Log(Severity.Degraded, row[m_idColumn], "Invalid item ID in item ID input file row.", $"itemId='{row[m_idColumn]}'");
                    continue;
                }

                return true;
            }
        }

        private bool FirstMoveNext()
        {
            string[] row = m_csvReader.Read();
            if (row == null)
            {
                throw new ArgumentException("Empty item ID input file.");
            }

            // First line is either column headings or an item ID. Determine by looking for expected headings.
            for (int i=0; i<row.Length; ++i)
            {
                if (row[i].Equals("ItemId", StringComparison.OrdinalIgnoreCase))
                {
                    m_idColumn = i;
                }
                else if (row[i].Equals("BankKey", StringComparison.OrdinalIgnoreCase))
                {
                    m_bankKeyColumn = i;
                }
            }

            if (m_idColumn >= 0)
            {
                m_minColumns = Math.Max(m_idColumn + 1, m_bankKeyColumn + 1);
                return MoveNext();
            }

            if (row.Length != 0 && ItemId.TryParse(row[0], m_defaultBankKey, out m_current))
            {
                m_idColumn = 0;
                m_minColumns = 1;
                return true;
            }

            throw new ArgumentException("Item ID input file in unexpected format.");
        }

        public void Reset()
        {
            throw new NotSupportedException("Cannot reset IdReader.");
        }

        public void Dispose()
        {
            if (m_csvReader != null)
            {
                m_csvReader.Dispose();
            }
            m_csvReader = null;
        }
    }

    enum ItemClass
    {
        Item,
        Stim
    }

    class ItemId
    {
        public ItemId(ItemClass itemClass, int bankKey, int id)
        {
            Class = itemClass;
            BankKey = bankKey;
            Id = id;
        }

        public ItemClass Class;
        public int BankKey;
        public int Id;

        public static bool TryParse(string str, out ItemId value)
        {
            value = null;
            string[] parts = str.Split('-');
            if (parts.Length != 3) return false;

            ItemClass cls;
            switch (parts[0].ToLowerInvariant())
            {
                case "item":
                    cls = ItemClass.Item;
                    break;

                case "stim":
                    cls = ItemClass.Stim;
                    break;

                default:
                    return false;
            }

            int bankKey;
            if (!int.TryParse(parts[1], out bankKey))
            {
                return false;
            }

            int id;
            if (!int.TryParse(parts[2], out id))
            {
                return false;
            }

            value = new ItemId(cls, bankKey, id);
            return true;
        }

        public static bool TryParse(string str, int defaultBankKey, out ItemId value)
        {
            int id;
            if (int.TryParse(str, out id))
            {
                value = new ItemId(ItemClass.Item, defaultBankKey, id);
                return true;
            }

            return TryParse(str, out value);
        }

        /// <summary>
        /// Convert to string form
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            //return $"{Class.ToString().ToLowerInvariant()}-{BankKey.ToString()}-{Id.ToString()}";
            return $"{Id.ToString()}";
        }

        /// <summary>
        /// Convert to string form with "Item" or "Stim" capitalized.
        /// </summary>
        /// <returns></returns>
        public string ToStringCap()
        {
            //return $"{Class.ToString()}-{BankKey.ToString()}-{Id.ToString()}";
            return $"{Id.ToString()}";
        }

        public override int GetHashCode()
        {
            //return Class.GetHashCode() ^ BankKey.GetHashCode() ^ Id.GetHashCode();
            return Id.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            var b = obj as ItemId;
            if (b == null) return false;
            //return Class == b.Class && BankKey == b.BankKey && Id == b.Id;
            return Id == b.Id;
        }
    }

    class CsvReader : IDisposable
    {
        TextReader m_reader;

        public CsvReader(string filename)
        {
            m_reader = new StreamReader(filename, true);
        }

        public CsvReader(TextReader reader, bool autoCloseReader = true)
        {
            m_reader = reader;
        }

        /// <summary>
        /// Read one line from a CSV file
        /// </summary>
        /// <returns>An array of strings parsed from the line or null if at end-of-file.</returns>
        public string[] Read()
        {
            List<string> line = new List<string>();
            StringBuilder builder = new StringBuilder();

            if (m_reader.Peek() < 0) return null;

            for (; ; )
            {
                int c = m_reader.Read();
                char ch = (c >= 0) ? (char)c : '\n'; // Treat EOF like newline.

                // Reduce CRLF to LF
                if (ch == '\r')
                {
                    if (m_reader.Peek() == '\n') continue;
                    ch = '\n';
                }

                if (ch == '\n')
                {
                    line.Add(builder.ToString());
                    break;
                }
                else if (ch == ',')
                {
                    line.Add(builder.ToString());
                    builder.Clear();
                }
                else if (ch == '"')
                {
                    for (; ; )
                    {
                        c = m_reader.Read();
                        if (c < 0) break;
                        ch = (char)c;

                        if (ch == '"')
                        {
                            if (m_reader.Peek() == (int)'"')
                            {
                                // Double quote means embedded quote
                                m_reader.Read(); // read the second quote
                            }
                            else
                            {
                                break;
                            }
                        }
                        builder.Append(ch);
                    }
                } // if quote
                else
                {
                    builder.Append(ch);
                }
            } // forever loop

            return line.ToArray();
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (m_reader != null)
            {
                m_reader.Dispose();
                m_reader = null;
#if DEBUG
                if (!disposing)
                {
                    Debug.Fail("Failed to dispose CsvReader.");
                }
#endif
            }
        }

        ~CsvReader()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }

}
