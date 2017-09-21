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

        public string ItemBankUrl { get; set; }

        public string ItemBankAccessToken { get; set; }

        public string ItemBankNamespace { get; set; }

        public bool IncludeTutorials { get; set; }

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
            using (m_zipArchive = ZipFile.Open(packageFilename, ZipArchiveMode.Create))
            {
                using (m_gitLab = new GitLab(ItemBankUrl, ItemBankAccessToken))
                {
                    while (m_itemQueue.Count > 0)
                    {
                        PackageItem(m_itemQueue.Dequeue());
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
