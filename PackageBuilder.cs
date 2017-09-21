using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Compression;

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
            try
            {
                string projectId = m_gitLab.ProjectIdFromName(ItemBankNamespace, itemId.ToString());

                string directoryPath = string.Concat((itemId.Class == ItemClass.Item) ? "Items" : "Stimuli", "/", itemId.ToStringCap(), "/");

                foreach (var entry in m_gitLab.ListRepositoryTree(projectId))
                {
                    Console.WriteLine($"   {entry.Key}");
                    using (var inStr = m_gitLab.ReadBlob(projectId, entry.Value))
                    {
                        var zipEntry = m_zipArchive.CreateEntry(directoryPath + entry.Key);
                        using (var outStr = zipEntry.Open())
                        {
                            inStr.CopyTo(outStr);
                        }
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
        }

    }
}
