using System;
using System.Configuration;
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
        PostGresDb imrtDb = new PostGresDb();

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

                // check if the item is a WIT. If so, put the full WIT item xml into the witXml variable
                XElement witXml = null;
                if (itemId.TypeOfItem == ItemType.Wit)
                {
                    KeyValuePair<string, string> witXmlFile = m_gitLab.ListRepositoryTree(projectId)
                                                                        .First(w => w.Key == itemId.Class.ToString().ToLower() + "-" + itemId.BankKey + "-" + itemId.Id + ".xml");
                    var inStr = m_gitLab.ReadBlob(projectId, witXmlFile.Value);
                    MemoryStream witXmlStream = new MemoryStream();
                    inStr.CopyTo(witXmlStream);
                    witXmlStream.Position = 0;
                    witXml = XElement.Load(witXmlStream);
                }

                foreach (var entry in m_gitLab.ListRepositoryTree(projectId))
                {
                    // ignore any sub folders (like glossary, general-attachments), glossary folder files, general-attachment folder files, item.json, import.zip, and the old <itemID>.xml files                   
                    if (entry.Key != "glossary" &&
                        !entry.Key.Contains("glossary/") &&
                        entry.Key != "general-attachments" &&
                        !entry.Key.Contains("general-attachments/") &&
                        entry.Key != "item.json" &&
                        entry.Key != "import.zip" &&
                        entry.Key != itemId.Id + ".xml")
                    {
                        Console.WriteLine($"   {entry.Key}");

                        //test here for Items (not WITs, stims, or tutorials)
                        bool validEntry; // this is the flag used to determine if a file should be added to the content package
                        if (itemId.TypeOfItem == ItemType.Item) { 
                            imrtDb.Connect(ConfigurationManager.ConnectionStrings["imrt_connectionString"].ToString());
                            imrtDb.GetItemAttachments(itemId.Id);
                            imrtDb.Disconnect();

                            if (entry.Key.Substring(entry.Key.Length - 3) != "xml" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "qrx" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "eax" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "svg" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "png")
                            {
                                Console.WriteLine($"      Checking if {entry.Key} is a valid attachement file");
                                if (imrtDb.itemAttachments.Where(a => a.FileName == entry.Key).Any())
                                {
                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is a valid attachement file");
                                }
                                else
                                {
                                    validEntry = false;
                                    Console.WriteLine($"      {entry.Key} is NOT a valid attachement file");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Will not add the following object: " + entry.Key, "");
                                }
                            }
                            else
                            {
                                validEntry = true;
                            }
                        }
                        else if (itemId.TypeOfItem == ItemType.Wit) // special case for handling WIT items where only the referenced audio files will be set as valid
                        {
                            // for each WIT audio file, check to see if it is referenced in the WIT XML by doing a simple string contains check. 
                            if (entry.Key.Substring(entry.Key.Length - 3) != "xml") // only check the non XML files
                            {
                                Console.WriteLine($"      Checking if {entry.Key} is a valid WIT audio or image file");
                                if (witXml.ToString().Contains(entry.Key.Substring(1, entry.Key.Length - 4)))
                                {
                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is a valid WIT audio or image file");
                                }
                                else
                                {
                                    validEntry = false;
                                    Console.WriteLine($"      Checking if {entry.Key} is NOT a valid WIT audio or image file");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Will not add the following object: " + entry.Key, "");
                                }
                            }
                            else
                            {
                                validEntry = true;
                            }
                        }
                        else
                        {
                            validEntry = true;
                        }

                        if (validEntry) { 
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

                                    // Else, just copy directly as long as it is a valid attachment file
                                    else
                                    {
                                        inStr.CopyTo(outStr);
                                    }
                                }                       
                            }
                        }
                    }
                    else
                    {
                        Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Will not add the following object: " + entry.Key, "");
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
                                        int.Parse(resource.Attribute("id").Value),
                                        ItemType.Wit);
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
                                        int.Parse(tutorial.Attribute("id").Value),
                                        ItemType.Tut);
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

                            // Find any wordlist references
                            XElement resourceList = itemEle.Element("resourceslist");
                            if (resourceList != null)
                            {
                                IEnumerable<XElement> witResources =
                                from resource in resourceList.Elements("resource")
                                where resource.Attribute("type").Value.Equals("wordList", StringComparison.OrdinalIgnoreCase)
                                select resource;

                                foreach (var resource in witResources)
                                {
                                    var witId = new ItemId(ItemClass.Item,
                                        int.Parse(resource.Attribute("bankkey").Value),
                                        int.Parse(resource.Attribute("id").Value),
                                        ItemType.Wit);
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on WordList", witId.ToString());
                                    if (AddId(witId))
                                    {
                                        ++witsAdded;
                                    }
                                }
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
