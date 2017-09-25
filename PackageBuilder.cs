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

        public string ItemBankUrl { get; set; }

        public string ItemBankAccessToken { get; set; }

        public string ItemBankNamespace { get; set; }

        public bool IncludeTutorials { get; set; }

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
            m_itemCount = m_itemQueue.Count;
            m_stimCount = 0;
            m_witCount = 0;
            m_tutorialCount = 0;

            using (m_zipArchive = ZipFile.Open(packageFilename, ZipArchiveMode.Create))
            {
                using (m_gitLab = new GitLab(ItemBankUrl, ItemBankAccessToken))
                {
                    while (m_itemQueue.Count > 0)
                    {
                        PackageItem(m_itemQueue.Dequeue());
                        Console.WriteLine($"Completed {m_itemQueue.CountDequeued} of {m_itemQueue.CountDistinct} items.");
                    }
                }

                // Add manifest
            }
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

                // If an item xml stream was found, parse and include any dependencies
                if (itemXmlStream != null)
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
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on WordList", $"witid='{witId}'");
                                    if (AddId(witId))
                                    {
                                        ++m_witCount;
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
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on stimulus", $"stimId='{stimId}'");
                                    if (AddId(stimId))
                                    {
                                        ++m_stimCount;
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
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on tutorial", $"tutorialId='{tutId}'");
                                    if (AddId(tutId))
                                    {
                                        ++m_tutorialCount;
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
            }
            catch (HttpNotFoundException)
            {
                Program.ProgressLog.Log(Severity.Severe, itemId.ToString(), "Item not found in item bank.");
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

    }
}
