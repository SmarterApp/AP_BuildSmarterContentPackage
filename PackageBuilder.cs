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
        ManifestBuilder manifestBuilder = new ManifestBuilder();

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
                Console.WriteLine($"Writing package manifest.");
                manifestBuilder.BuildContent();
                AddManifest(manifestBuilder.Content);
            }

            m_elapsed = unchecked((uint)Environment.TickCount - (uint)startTicks);
        }

        void PackageItem(ItemId itemId)
        {
            Console.WriteLine(itemId.ToString());
            MemoryStream itemXmlStream = null;
            Item parentItem = new Item();
            
            try
            {
                string projectId = m_gitLab.ProjectIdFromName(ItemBankNamespace, itemId.ToString());

                string directoryPath = string.Concat((itemId.Class == ItemClass.Item) ? "Items" : "Stimuli", "/", itemId.ToStringCap(), "/");

                string itemXmlName = itemId.ToString() + ".xml";

                // There is a need to not include files that are not referenced in the XML content. This is seperate from files that are referenced in the IMRT
                // item attachments table. Place the XML content into a variable for inspection further down.
                XElement contentXml = null;
                KeyValuePair<string, string> contentXmlFile = m_gitLab.ListRepositoryTree(projectId)
                                                                      .First(w => w.Key == itemId.Class.ToString().ToLower() + "-" + itemId.BankKey + "-" + itemId.Id + ".xml");
                var contentStr = m_gitLab.ReadBlob(projectId, contentXmlFile.Value);
                MemoryStream contentXmlStream = new MemoryStream();
                contentStr.CopyTo(contentXmlStream);
                contentXmlStream.Position = 0;
                contentXml = XElement.Load(contentXmlStream);

                // check if there is a RendererSpec element with a filename attribute value. This will be the GAX file, which will need to be inspected for any referenced image files
                XElement rendererSpecXml = null;
                if (itemId.Class == ItemClass.Item) { 
                    XElement contentXmlItemElement = contentXml.Element("item");
                    XElement rendererSpecElement = contentXmlItemElement.Element("RendererSpec");
                    if (rendererSpecElement != null)
                    {
                        var rendererSpecFileName = rendererSpecElement.Attribute("filename").Value.ToString().Substring(2); // the filename may include 2 leading forward slashes
                        Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "RendererSpec filename: " + rendererSpecFileName, "");

                        KeyValuePair<string, string> rendererSpecFile = m_gitLab.ListRepositoryTree(projectId)
                                                                                .First(w => w.Key == rendererSpecFileName);
                        var rendererSpecStr = m_gitLab.ReadBlob(projectId, rendererSpecFile.Value);
                        MemoryStream rendererSpecXmlStream = new MemoryStream();
                        rendererSpecStr.CopyTo(rendererSpecXmlStream);
                        rendererSpecXmlStream.Position = 0;                    
                        rendererSpecXml = XElement.Load(rendererSpecXmlStream);
                    }
                }

                // prepare the parent Item object for the manifest
                parentItem.Identifier = itemId.Class.ToString().ToLower() + "-" + itemId.BankKey + "-" + itemId.Id;
                parentItem.Type = itemId.Class == ItemClass.Item ? Item.ResourceType.Item : Item.ResourceType.Stim;
                parentItem.Folder = itemId.Class == ItemClass.Item ? "Items/" + itemId.Class.ToString() + "-" + itemId.BankKey + "-" + itemId.Id + "/" 
                                                                   : "Stimuli/" + itemId.Class.ToString().ToLower() + "-" + itemId.BankKey + "-" + itemId.Id + "/";
                parentItem.Href = parentItem.Folder + itemId.Class.ToString().ToLower() + "-" + itemId.BankKey + "-" + itemId.Id + ".xml";
                parentItem.IsADependency = false;
                
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

                        bool validEntry = false; // this is the flag used to determine if a file should be added to the content package
                        
                        //test here for Items, Stims, and Tutorials -- not WITs
                        if (itemId.TypeOfItem == ItemType.Item ||
                            itemId.TypeOfItem == ItemType.Tut) { 
                            imrtDb.Connect(ConfigurationManager.ConnectionStrings["imrt_connectionString"].ToString());
                            imrtDb.GetItemAttachments(itemId.Id);
                            imrtDb.Disconnect();

                            if (entry.Key.Substring(entry.Key.Length - 3) != "xml" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "qrx" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "eax" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "gax")
                            {
                                Console.WriteLine($"      Checking if {entry.Key} is a valid attachment file");
                                if (imrtDb.itemAttachments.Where(a => a.FileName == entry.Key).Any())
                                {
                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is a valid attachment file");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid attachment file.", "");
                                }
                                else if (contentXml.ToString().Contains(entry.Key) ||
                                         contentXml.ToString().Contains(entry.Key.Substring(1, entry.Key.Length - 4)))
                                {
                                    // check the content. there may be imbedded references to files not in the attachments table. 
                                    // this includes the case of alternate audio files, independent of the file extension
                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is a valid file referenced in the stem content");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file referenced in the stem content.", "");
                                }
                                else if (rendererSpecXml != null) {
                                    if (rendererSpecXml.ToString().Contains(entry.Key))
                                    {
                                        // check the GAX content. there may be imbedded referensed to files not in the item content file.
                                        validEntry = true;
                                        Console.WriteLine($"      {entry.Key} is a valid file referenced in the GAX content");
                                        Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file referenced in the GAX content.", "");
                                    }                                
                                }
                                else
                                {
                                    validEntry = false;
                                    Console.WriteLine($"      {entry.Key} is NOT a valid attachment file, or a file referenced in the stem or GAX content.");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Will not add the following object: " + entry.Key + 
                                                                                                 ". The file is NOT a valid attachment file, or a file referenced in the stem or GAX content", "");
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
                                if (contentXml.ToString().Contains(entry.Key.Substring(1, entry.Key.Length - 4))) // check the audio file, independent of the audio file extension.
                                {
                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is a valid WIT audio or image file");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid WIT audio or image file.", "");
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
                                        inStr.CopyTo(itemXmlStream); // this saves the stream to a temporary memory stream for later use 
                                        itemXmlStream.Position = 0;
                                        itemXmlStream.CopyTo(outStr); // this saves the stream to the zip file
                                    }

                                    // Else, just copy directly as long as it is a valid attachment file
                                    else
                                    {
                                        inStr.CopyTo(outStr); // this saves the stream to the zip file
                                    }
                                }

                                //place into the manifest. this will need to be an item added to the manifestBuilder.Items (or stim added to the manifestBuilder.Stims)
                                //need to determine a couple of things: 
                                //  1. determine the object. if not metadata, then a dependency, which belongs as an asset type or stim
                                //  2. need to determine the stim, wit, and tutorial dependencies, if any. investigate the contentXml variable
                                //  3. handle metadata on it's own because the file is always called metadata
                                if (entry.Key != "metadata.xml" &&
                                    !entry.Key.Equals(itemXmlName, StringComparison.OrdinalIgnoreCase))
                                {
                                    Item assetItem = new Item();
                                    assetItem.Identifier = entry.Key.Replace('.', '_'); // replace the "dot" file extension as an "underscore" file extension
                                    assetItem.Type = Item.ResourceType.AllOtherAssets;
                                    assetItem.Folder = parentItem.Folder;
                                    assetItem.Href = parentItem.Folder + entry.Key;
                                    assetItem.IsADependency = true;

                                    parentItem.DependentAssets.Add(assetItem);
                                }
                                else if (entry.Key == "metadata.xml")
                                {
                                    Item metadataItem = new Item();
                                    metadataItem.Identifier = parentItem.Identifier + "_metadata";
                                    metadataItem.Type = Item.ResourceType.Metadata;
                                    metadataItem.Folder = parentItem.Folder;
                                    metadataItem.Href = parentItem.Folder + "metadata.xml";
                                    metadataItem.IsADependency = true;

                                    parentItem.DependentMetadata = metadataItem;
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

                                    // add the wit item for the manifest
                                    Item witItem = new Item();
                                    witItem.Identifier = ItemClass.Item.ToString().ToLower() + "-" + resource.Attribute("bankkey").Value + "-" + resource.Attribute("id").Value;
                                    witItem.Type = Item.ResourceType.Item;
                                    witItem.Folder = "Items/" + ItemClass.Item.ToString() + "-" + resource.Attribute("bankkey").Value + "-" + resource.Attribute("id").Value;
                                    witItem.Href = parentItem.Folder + witItem.Identifier + ".xml";
                                    witItem.IsADependency = true;

                                    parentItem.DependentWit = witItem;
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

                                    // add the stim item for the manifest
                                    Item stimItem = new Item();
                                    stimItem.Identifier = ItemClass.Stim.ToString().ToLower() + "-" + bankKey + "-" + attrib.Element("val").Value;
                                    stimItem.Type = Item.ResourceType.Stim;
                                    stimItem.Folder = "Stimuli/" + ItemClass.Stim.ToString() + "-" + bankKey + "-" + attrib.Element("val").Value;
                                    stimItem.Href = stimItem.Folder + stimItem.Identifier + ".xml";
                                    stimItem.IsADependency = true;

                                    parentItem.DependentStim = stimItem;
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

                                    // add the tutorial item for the manifest
                                    Item tutItem = new Item();
                                    tutItem.Identifier = ItemClass.Item.ToString().ToLower() + "-" + tutorial.Attribute("bankkey").Value + "-" + tutorial.Attribute("id").Value;
                                    tutItem.Type = Item.ResourceType.Item;
                                    tutItem.Folder = "Items/" + ItemClass.Item.ToString() + "-" + tutorial.Attribute("bankkey").Value + "-" + tutorial.Attribute("id").Value;
                                    tutItem.Href = parentItem.Folder + tutItem.Identifier + ".xml";
                                    tutItem.IsADependency = true;

                                    parentItem.DependentTut = tutItem;
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

                                    // add the wit item for the manifest
                                    Item witItem = new Item();
                                    witItem.Identifier = ItemClass.Item.ToString().ToLower() + "-" + resource.Attribute("bankkey").Value + "-" + resource.Attribute("id").Value;
                                    witItem.Type = Item.ResourceType.Item;
                                    witItem.Folder = "Items/" + ItemClass.Item.ToString() + "-" + resource.Attribute("bankkey").Value + "-" + resource.Attribute("id").Value;
                                    witItem.Href = parentItem.Folder + witItem.Identifier + ".xml";
                                    witItem.IsADependency = true;

                                    parentItem.DependentWit = witItem;
                                }
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        throw new ApplicationException("Expected content missing from item xml.", err);
                    }
                }

                // add the parent item to the main manifest builder list of items or stims
                if (parentItem.Type == Item.ResourceType.Item)
                {
                    manifestBuilder.Items.Add(parentItem);
                }
                else if (parentItem.Type == Item.ResourceType.Stim)
                {
                    manifestBuilder.Stims.Add(parentItem);
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
        //const string c_emptyManifest = "<manifest xmlns=\"http://www.imsglobal.org/xsd/apip/apipv1p0/imscp_v1p1\"></manifest>";

        void AddManifest(string manifestBody)
        {
            var zipEntry = m_zipArchive.CreateEntry(c_manifestName);
            using (var outStr = zipEntry.Open())
            {
                using (var writer = new StreamWriter(outStr))
                {
                    writer.Write(manifestBody);
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
