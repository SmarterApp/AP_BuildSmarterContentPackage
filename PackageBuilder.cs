using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace BuildSmarterContentPackage
{
    class PackageBuilder
    {
        DistinctQueue<ItemId> m_itemQueue = new DistinctQueue<ItemId>();
        GitLab m_gitLab;
        ZipArchive m_zipArchive;

        // Progress counters
        int m_itemCount;
        int m_stimCount;
        int m_witCount;
        int m_tutorialCount;
        uint m_elapsed;

        public string ItemBankUrl { get; set; }

        public string ItemBankAccessToken { get; set; }

        public string ItemBankNamespace { get; set; }

        public bool IncludeTutorials { get; set; }

        /// <summary>
        /// Elapsed time in milliseconds
        /// </summary>
        public uint Elapsed { get { return m_elapsed; } }
        public int ItemCount { get { return m_itemCount; } }
        public int StimCount { get { return m_stimCount; } }
        public int WitCount { get { return m_witCount; } }
        public int TutorialCount { get { return m_tutorialCount; } }

        public bool AddId(ItemId id)
        {
            return m_itemQueue.Enqueue(id);
        }

        public int AddIds(IEnumerator<ItemId> ids)
        {
            return m_itemQueue.Load(ids);
        }

        public void ProducePackage(string packageFilename)
        {
            m_itemCount = 0;
            m_stimCount = 0;
            m_witCount = 0;
            m_tutorialCount = 0;
            int startTicks = Environment.TickCount;

            using (m_zipArchive = ZipFile.Open(packageFilename, ZipArchiveMode.Create))
            {
                m_gitLab = new GitLab(ItemBankUrl, ItemBankAccessToken);
                while (m_itemQueue.Count > 0)
                {
                    PackageItem(m_itemQueue.Dequeue());
                    Console.WriteLine($"Completed: {m_itemQueue.CountDequeued} of {m_itemQueue.CountDistinct} items. Elapsed: {TickFormatter.AsElapsed(unchecked((uint)Environment.TickCount - (uint)startTicks))}");
                }

                // Add manifest
                AddManifest();
            }

            m_elapsed = unchecked((uint)Environment.TickCount - (uint)startTicks);
        }

        void PackageItem(ItemId itemId)
        {
            Console.WriteLine(itemId.ToString());
            MemoryStream itemXmlStream = null;
            try
            {
                string projectId = m_gitLab.ProjectIdFromName(ItemBankNamespace, itemId.ToString());

                string directoryPath = string.Concat((itemId.Class == ItemClass.Item) ? "Items" : "Stimuli", "/", itemId.ToStringCap(), "/");

                string itemXmlName = itemId.ToString() + ".xml";
                foreach (var entry in m_gitLab.ListRepositoryTree(projectId))
                {
                    Console.WriteLine($"   {entry.Key}");
                    using (var inStr = m_gitLab.ReadBlob(projectId, entry.Value))
                    {
                        var zipEntry = m_zipArchive.CreateEntry(directoryPath + entry.Key);
                        using (var outStr = zipEntry.Open())
                        {
                            // If this is the item file, save a copy in a memory stream.
                            if (entry.Key.Equals(itemXmlName, StringComparison.OrdinalIgnoreCase))
                            {
                                itemXmlStream = new MemoryStream();
                                inStr.CopyTo(itemXmlStream);
                                itemXmlStream.Position = 0;
                                itemXmlStream.CopyTo(outStr);
                            }

                            // Else, just copy directly
                            else
                            {
                                inStr.CopyTo(outStr);
                            }
                        }
                    }
                }

                int witsAdded = 0;
                int stimsAdded = 0;
                int tutorialsAdded = 0;

                // If an item xml stream was found, parse and include any dependencies
                if (itemXmlStream == null)
                {
                    Program.ProgressLog.Log(Severity.Severe, itemId.ToString(), "Item has no content file.", itemXmlName);
                    Console.WriteLine("   No item content file found.");
                }
                else
                {
                    try
                    {
                        itemXmlStream.Position = 0;
                        XElement xml = XElement.Load(itemXmlStream);
                        XElement itemEle = xml.Element("item");
                        if (itemEle != null)
                        {
                            // Look up the bankKey
                            int bankKey = int.Parse(itemEle.Attribute("bankkey").Value);

                            // Get the item type
                            var attEle = itemEle.Attribute("format");
                            if (attEle == null)
                            {
                                attEle = itemEle.Attribute("type");
                            }
                            string type = (attEle != null) ? attEle.Value : string.Empty;
                            if (type.Equals("tut", StringComparison.OrdinalIgnoreCase))
                            {
                                ++m_tutorialCount;
                            }
                            else if (type.Equals("wordList", StringComparison.OrdinalIgnoreCase))
                            {
                                ++m_witCount;
                            }
                            else
                            {
                                ++m_itemCount;
                            }

                            // Find any wordlist references
                            XElement resourceList = itemEle.Element("resourceslist");
                            if (resourceList != null)
                            {
                                IEnumerable<XElement> witResources =
                                from resource in resourceList.Elements("resource")
                                where resource.Attribute("type").Value.Equals("wordList", StringComparison.OrdinalIgnoreCase)
                                select resource;

                                foreach(var resource in witResources)
                                {
                                    var witId = new ItemId(ItemClass.Item,
                                        int.Parse(resource.Attribute("bankkey").Value),
                                        int.Parse(resource.Attribute("id").Value));
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on WordList", witId.ToString());
                                    if (AddId(witId))
                                    {
                                        ++witsAdded;
                                    }
                                }
                            }

                            // Find any stimulus references
                            XElement attribList = itemEle.Element("attriblist");
                            if (attribList != null)
                            {
                                IEnumerable<XElement> stimAttributes =
                                from attrib in attribList.Elements("attrib")
                                where attrib.Attribute("attid").Value.Equals("stm_pass_id", StringComparison.OrdinalIgnoreCase)
                                select attrib;

                                foreach (var attrib in stimAttributes)
                                {
                                    var stimId = new ItemId(ItemClass.Stim,
                                        bankKey,
                                        int.Parse(attrib.Element("val").Value));
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on stimulus", stimId.ToString());
                                    if (AddId(stimId))
                                    {
                                        ++stimsAdded;
                                    }
                                }
                            }

                            // Find any tutorial references
                            if (IncludeTutorials)
                            {
                                XElement tutorial = itemEle.Element("tutorial");
                                if (tutorial != null)
                                {
                                    var tutId = new ItemId(ItemClass.Item,
                                        int.Parse(tutorial.Attribute("bankkey").Value),
                                        int.Parse(tutorial.Attribute("id").Value));
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on tutorial", tutId.ToString());
                                    if (AddId(tutId))
                                    {
                                        ++tutorialsAdded;
                                    }

                                }
                            }


                        }
                        else
                        {
                            itemEle = xml.Element("passage");
                            if (itemEle != null)
                            {
                                ++m_stimCount;
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        throw new ApplicationException("Expected content missing from item xml.", err);
                    }
                }

                var sb = new System.Text.StringBuilder("  ");
                if (witsAdded > 0) sb.Append($" +{witsAdded} WIT");
                if (stimsAdded > 0) sb.Append($" +{stimsAdded} Stimulus");
                if (tutorialsAdded > 0) sb.Append($" +{tutorialsAdded} Tutorial");
                if (sb.Length > 2) Console.WriteLine(sb);
            }
            catch (HttpNotFoundException)
            {
                Program.ProgressLog.Log(Severity.Severe, itemId.ToString(), "Item not found in item bank.");
                Console.WriteLine("   Item not found!");
            }
            catch (Exception err)
            {
                Program.ProgressLog.Log(Severity.Severe, itemId.ToString(), "Exception", err.Message);
            }
            finally
            {
                if (itemXmlStream != null)
                {
                    itemXmlStream.Dispose();
                    itemXmlStream = null;
                }
            }
        }

        const string c_manifestName = "imsmanifest.xml";
        const string c_emptyManifest = "<manifest xmlns=\"http://www.imsglobal.org/xsd/apip/apipv1p0/imscp_v1p1\"></manifest>";

        void AddManifest()
        {
            var zipEntry = m_zipArchive.CreateEntry(c_manifestName);
            using (var outStr = zipEntry.Open())
            {
                using (var writer = new StreamWriter(outStr))
                {
                    writer.Write(c_emptyManifest);
                }
            }
        }

    }

    static class TickFormatter
    {
        /// <summary>
        /// Format millisecond ticks (from Environment.TickCount) into hours, minutes, seconds, tenths.
        /// </summary>
        public static string AsElapsed(uint ticks)
        {
            var hours = ticks / (60 * 60 * 1000);
            var minutes = (ticks / (60 * 1000)) % 60;
            var seconds = (ticks / 1000) % 60;
            var tenths = (ticks / 100) % 10;
            var sb = new System.Text.StringBuilder();
            bool leading = false;
            if (hours > 0)
            {
                sb.Append(hours.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(":");
                leading = true;
            }
            if (minutes > 0 || leading)
            {
                sb.Append(minutes.ToString(leading ? "D2" : "D", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(":");
                leading = true;
            }
            sb.Append(seconds.ToString(leading ? "D2" : "D", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(".");
            sb.Append(tenths.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
