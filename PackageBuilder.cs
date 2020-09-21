using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text;

namespace BuildSmarterContentPackage
{
    public class PackageBuilder
    {
        DistinctQueue<ItemId> m_itemQueue = new DistinctQueue<ItemId>();
        GitLab m_gitLab;
        ZipArchive m_zipArchive;
        PostGresDb imrtDb = new PostGresDb();
        ManifestBuilder manifestBuilder = new ManifestBuilder();
        bool recodeOgg = false;

        // Attachment file name pattern
        string fileNamePatternCC = @"passage_\d+_v[0-9]+(\.[0-9]+)?_\d+_[a-z]+[0-9]?\.vtt";
        string fileNamePatternASL = @"(item|stim|passage)_\d+_ASL_[a-z]+[0-9]?\.(mp4|webm)";
        string fileNamePatternBraille = @"(item|passage)_\d+_enu_(exn|ecn|uxn|ucn|uxt|uct|ucl|ecl|contracted|uncontracted)\.(brf|prn)";
        string fileNamePatternAudioInStim = @"passage_\d+_v[0-9]+(\.[0-9])?_\d+_[a-z]+[0-9]?.(m4a|ogg)";
        public string fileNamePatternAudioGlossary = @"(item|stim)_\d+_[a-z]+_v[0-9]+(\.[0-9])_[a-z]+(_[a-z])?\.(m4a|ogg)"; // these are found in WITs
        string fileNamePatternAudioGlossaryLegacy = @"(item|stim)_\d+_v[0-9]+_\d+_[0-9]+[a-z]+_glossary_ogg_m4a\.(m4a|ogg)"; // this is the legacy audio file naming
        string fileNamePatternImages = @"(item|passage)_[0-9]+_(v[0-9]+(\.[0-9]))?_?(graphics1|stem|equation)_(png256|ENU|ESN)(_(0[0-9]|[0-9]+))?\.(png|svg)";
        string fileNamePatternIllustrationGlossary = @"item_[0-9]+_[a-z]+_v[0-9]+(\.[0-9]+)?_illustration_glossary\.svg";
        List<WitFileNames> witFileNames =  new List<WitFileNames>();

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
        public string PackageFileName { get; set; }
        public bool IncludeImportZip { get; set; }
        public bool IncludeWitFileRenaming { get; set; }
        public bool IncludeManifest { get; set; }

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
            PackageFileName = packageFilename;
            
            using (m_zipArchive = ZipFile.Open(packageFilename, ZipArchiveMode.Create))
            {                
                m_gitLab = new GitLab(ItemBankUrl, ItemBankAccessToken);
                while (m_itemQueue.Count > 0)
                {
                    PackageItem(m_itemQueue.Dequeue());
                    Console.WriteLine($"Completed: {m_itemQueue.CountDequeued} of {m_itemQueue.CountDistinct} items. Elapsed: {TickFormatter.AsElapsed(unchecked((uint)Environment.TickCount - (uint)startTicks))}");
                }

                // Add manifest, only if the IncludeManifest is true
                if (IncludeManifest)
                {
                    Console.WriteLine($"Writing package manifest.");
                    manifestBuilder.BuildContent();
                    AddManifest(manifestBuilder.Content);
                }
                else
                {
                    Console.WriteLine($"Including an empty manifest file.");
                    AddEmptyManifest();
                }
            }
            
            if (recodeOgg) {
                // once package has been built, unzip, and delete the zipped file
                Console.WriteLine($"Audio files have been found that need to be recoded as valid Ogg files. Preparing for that process...");
                ZipFile.ExtractToDirectory(packageFilename, packageFilename.Substring(0, packageFilename.Length - 4));
                System.IO.File.Delete(packageFilename);

                Console.WriteLine($"Starting the Ogg recode process.");
                // run the ogg reencoder
                var process = System.Diagnostics.Process.Start(ConfigurationManager.AppSettings["audioEncodePath"], packageFilename.Substring(0, packageFilename.Length - 4));
                process.WaitForExit();

                Console.WriteLine($"Preforming directory clean up after Ogg recode.");
                // once audio files have been re-encoded, zip up contents
                ZipFile.CreateFromDirectory(packageFilename.Substring(0, packageFilename.Length - 4), packageFilename);

                // delete the unzipped directory
                System.IO.Directory.Delete(packageFilename.Substring(0, packageFilename.Length - 4), true);
                Console.WriteLine($"Audio file Ogg recode process complete.");
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

                //string directoryPath = string.Concat((itemId.Class == ItemClass.Item) ? "Items" : "Stimuli", "/", itemId.ToStringCap(), "/");
                string directoryPath = (itemId.Class == ItemClass.Item) ? "Items" + "/" + itemId.ToStringCap() + "/" : "Stimuli" + "/" + itemId + "/";

                string itemXmlName = itemId.ToString() + ".xml";

                // There is a need to exclude files that are not referenced in the XML content. 
                // This is seperate from files that are referenced in the IMRT item attachments table. 
                // Place the XML content into a variable for inspection further down.
                XElement contentXml = null;
                KeyValuePair<string, string> contentXmlFile = m_gitLab.ListRepositoryTree(projectId)
                                                                      .First(w => w.Key == itemId.Class.ToString().ToLower() + "-" + itemId.BankKey + "-" + itemId.Id + ".xml");
                var contentStr = m_gitLab.ReadBlob(projectId, contentXmlFile.Value);
                MemoryStream contentXmlStream = new MemoryStream();
                contentStr.CopyTo(contentXmlStream);
                contentXmlStream.Position = 0;
                contentXml = XElement.Load(contentXmlStream);

                // Check if there is a RendererSpec element with a filename attribute value. This will be the GAX file, which will need to be inspected for any referenced image files
                // For stacked spanish, specific Spanish image files should be included. The files are not referenced explicitly in the GAX, rather they are the same name with a _ESN suffix
                XElement rendererSpecXml = null;
                if (itemId.Class == ItemClass.Item) { 
                    XElement contentXmlItemElement = contentXml.Element("item");
                    XElement rendererSpecElement = contentXmlItemElement.Element("RendererSpec");
                    if (rendererSpecElement != null)
                    {
                        var rendererSpecFileName = rendererSpecElement.Attribute("filename").Value.ToString(); 
                        
                        // the filename may include 2 leading forward slashes
                        if (rendererSpecFileName.Substring(0, 2).Equals("//"))
                        {
                            rendererSpecFileName = rendererSpecFileName.Substring(2);
                        }

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
                    WitFileNames currentWitFileNames = new WitFileNames();

                    // ignore any sub folders (like glossary, general-attachments), glossary folder files, general-attachment folder files, item.json, import.zip, and the old <itemID>.xml files                   
                    if (entry.Key != "glossary" &&
                        !entry.Key.Contains("glossary/") &&
                        entry.Key != "general-attachments" &&
                        !entry.Key.Contains("general-attachments/") &&
                        entry.Key != "item.json" &&
                        //entry.Key != "import.zip" &&
                        entry.Key != itemId.Id + ".xml")
                    {
                        Console.WriteLine($"   {entry.Key}");

                        bool validEntry = false; // this is the flag used to determine if a file should be added to the content package

                        //test here for Items, Stims (ItemType is "Item"), and Tutorials -- not WITs
                        if (itemId.TypeOfItem == ItemType.Item ||
                            itemId.TypeOfItem == ItemType.Tut)
                        {

                            imrtDb.Connect(ConfigurationManager.ConnectionStrings["imrt_connectionString"].ToString());
                            imrtDb.GetItemAttachments(itemId.Id);
                            imrtDb.Disconnect();

                            if (entry.Key.Substring(entry.Key.Length - 3) != "xml" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "qrx" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "eax" &&
                                entry.Key.Substring(entry.Key.Length - 3) != "gax")
                            {
                                Console.WriteLine($"      Checking if {entry.Key} is a valid attachment file");
                                // look up the attachment in the IMRT database. 
                                // The file type attribute of the ItemAttachment object returs cc, asl, or braille. There are 7 potential file attachments:
                                // 1. asl
                                // 2. audio in stimuli
                                // 3. audio glossary
                                // 4. braille
                                // 5. closed caption
                                // 6. images
                                // 7. illustrated glossary
                                if (imrtDb.itemAttachments.Where(a => a.FileName == entry.Key).Any())
                                {
                                    // from the IMRT database attachments, check file name pattern for cc, asl, or braille depending on FileType attribute in the ItemAttachement object
                                    Console.WriteLine($"      Checking if {entry.Key} has a valid file name pattern for CC, ASL, or Braille");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Checking if " + entry.Key + " has a valid file name pattern for CC, ASL, or Braille.", "");

                                    if (imrtDb.itemAttachments.Where(a => a.FileName == entry.Key).First().FileType.Equals("cc"))
                                    {
                                        // determine if the file name matches the CC file name pattern
                                        Match ccMatch = Regex.Match(entry.Key, fileNamePatternCC, RegexOptions.IgnoreCase);
                                        if (ccMatch.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for CC");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for CC.", "");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for CC. Consider renaming the file.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for CC. Consider renaming the file.", "");
                                        }
                                    }
                                    if (imrtDb.itemAttachments.Where(a => a.FileName == entry.Key).First().FileType.Equals("asl"))
                                    {
                                        Match ccMatch = Regex.Match(entry.Key, fileNamePatternASL, RegexOptions.IgnoreCase);
                                        if (ccMatch.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for ASL");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for ASL.", "");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for ASL. Consider renaming the file.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for ASL. Consider renaming the file.", "");
                                        }
                                    }
                                    if (imrtDb.itemAttachments.Where(a => a.FileName == entry.Key).First().FileType.Equals("braille"))
                                    {
                                        Match ccMatch = Regex.Match(entry.Key, fileNamePatternBraille, RegexOptions.IgnoreCase);
                                        if (ccMatch.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for Braille");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for Braille.", "");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for Braille. Consider renaming the file.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for Braille. Consider renaming the file.", "");
                                        }
                                    }

                                    validEntry = true;
                                }
                                else if (contentXml.ToString().Contains(entry.Key) ||
                                         contentXml.ToString().Contains(entry.Key.Substring(1, entry.Key.Length - 4)))
                                {
                                    // check the content. there may be imbedded references to files not in the attachments table. 
                                    // this includes the case of alternate audio files, independent of the file extension
                                    // the remaining file types can be categorized as images or audio files (#2, 6)
                                    Match imgMatch = Regex.Match(entry.Key, fileNamePatternImages, RegexOptions.IgnoreCase);
                                    if (imgMatch.Success)
                                    {
                                        Console.WriteLine($"      {entry.Key} is a valid file name pattern for images in the content.");
                                        Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for images in the content.", "");
                                    }
                                    else
                                    {
                                        // try the audio in stim match
                                        Match stimAudioMatch = Regex.Match(entry.Key, fileNamePatternAudioInStim, RegexOptions.IgnoreCase);
                                        if (stimAudioMatch.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for audio in the stim.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for audio in the stim.", "");
                                        }
                                        else
                                        {
                                            // no match for images in content or audio in stim
                                            Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for images in the content or audio in stim. Consider renaming the file.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for images in the content or audio in stim. Consider renaming the file.", "");
                                        }
                                    }

                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is a valid file referenced in the stem content");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file referenced in the stem content.", "");
                                }
                                else if (entry.Key == "import.zip" && IncludeImportZip)
                                {
                                    validEntry = true;
                                    Console.WriteLine($"      {entry.Key} is not a valid attachment file, however the file has been set to be included in the content package.");
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Adding the import.zip file.", "");
                                }
                                else if (rendererSpecXml != null)
                                {
                                    // the entry.Key may be the stacked spanish image. The file name would match what is in the GAX file without the _ESN suffix in the file name
                                    // this is case #6 image files

                                    string imageFileName = entry.Key;
                                    if (imageFileName.Contains("_ESN") ||
                                        imageFileName.Contains("_esn"))
                                    {
                                        imageFileName = imageFileName.Replace("_ESN", "");
                                        imageFileName = imageFileName.Replace("_esn", "");
                                    }
                                    // check the GAX content. there may be imbedded references to files not in the item content file.
                                    if (rendererSpecXml.ToString().IndexOf(imageFileName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        Match imgMatch = Regex.Match(entry.Key, fileNamePatternImages, RegexOptions.IgnoreCase);
                                        if (imgMatch.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for images in the content.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for images in the content.", "");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for images in the content. Consider renaming the file.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for images in the content. Consider renaming the file.", "");
                                        }

                                        validEntry = true;
                                        Console.WriteLine($"      {entry.Key} is a valid file referenced in the GAX content. If the file name includes _ESN, this is the stacked spanish version of the image and will be included.");
                                        Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file referenced in the GAX content. " +
                                                                                                     "If the file name includes _ESN, this is the stacked spanish version of the image and will be included.", "");
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
                            if (entry.Key.Substring(entry.Key.Length - 3) != "xml") // only check the non XML files
                            {
                                Console.WriteLine($"      Checking if {entry.Key} is a valid referenced WIT audio or image file");
                                // for each WIT audio file (independent of the file extension), check to see if it is referenced in the WIT XML by doing a simple String.Contains() check. 
                                // A simple String.Contains() check is not sufficient. There are cases where the WIT reference is
                                // item_123456_Word_Language.ogg, but the file name is Word_Language.ogg. A String.Contains will pass with this file name
                                // Rather, check the file name with the start of the href attribute.
                                string fileNameToCheck = @"href=""" + entry.Key.Substring(0, entry.Key.Length - 4);
                                if (contentXml.ToString().Contains(fileNameToCheck)) 
                                {
                                    // The audio file is a valid audio file.
                                    // Before the audio file can be tagged as a valid entry, a check of the encoding of the file must be done.
                                    // During the 2017-2018 content QC, it was discovered that .m4a files were named .ogg which caused problems with certain browsers.
                                    // A fix was found to re-encode the file to the expected file type. The same file check is done here. Additionally, the re-encoded audio file
                                    // will be saved in a seperate directory for replacement in the item bank.
                                    if (entry.Key.Substring(entry.Key.Length - 3, 3).Equals("ogg") ||
                                        entry.Key.Substring(entry.Key.Length - 3, 3).Equals("m4a"))
                                    {
                                        Int32 header0;
                                        Int32 header4;
                                        const Int32 c_oggHeader = 0x5367674f;   // OggS in hex
                                        const Int32 c_m4aHeader = 0x70797466;   // ftyp in hex

                                        var audioFileStr = m_gitLab.ReadBlob(projectId, entry.Value);
                                        MemoryStream audioMemoryStream = new MemoryStream();
                                        audioFileStr.CopyTo(audioMemoryStream);
                                        audioMemoryStream.Position = 0;

                                        using (var file = new BinaryReader(audioMemoryStream))
                                        {
                                            header0 = file.ReadInt32();
                                            header4 = file.ReadInt32();

                                            string foundFormat = (header0 == c_oggHeader) ? "ogg" : ((header4 == c_m4aHeader) ? "m4a" : "unknown");

                                            if (!string.Equals(entry.Key.Substring(entry.Key.Length - 3, 3), foundFormat, StringComparison.Ordinal))
                                            {
                                                recodeOgg = true;
                                                //            Console.WriteLine($"      {entry.Key} encoding does not match the file extension.");
                                                //            // The encoding type does not match the file extension. 
                                                //            // The re-encoder requires the file to be on the file system. The operations will be
                                                //            //  1. save the bad encoded file into a directory
                                                //            //  2. run the ffmpeg.exe encoder on the saved file. Information about ffmpeg.exe found here: https://ffmpeg.org/
                                                //            //      syntax: ffmpeg -i currentfilename.extension currentfilename.ogg OR currentfilename.m4a, depending on which file format is needed
                                                //            //  3. put the file name into an array for later processing
                                                //            //  4. during the processing of the file into the zip file, compare file names with what is contained in the array. if a match, use the re-encoded filecopy the new file to another directory for safe keeping
                                                //            // step 1
                                                //            if (!System.IO.Directory.Exists(Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix"))
                                                //            {
                                                //                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix");
                                                //            }
                                                //            using (FileStream writeStream = File.OpenWrite(Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix\\" + entry.Key))
                                                //            {
                                                //                BinaryWriter writer = new BinaryWriter(writeStream);
                                                //                byte[] buffer = new byte[1024];
                                                //                int bytesRead;

                                                //                while((bytesRead = audioMemoryStream.Read(buffer, 0, 1024)) > 0)
                                                //                {
                                                //                    writeStream.Write(buffer, 0, bytesRead);
                                                //                }                                                    
                                                //            }
                                                //            // step 2
                                                //            //string changeToFormat = foundFormat == "ogg" ? "m4a" : "ogg";
                                                //            //var process = System.Diagnostics.Process.Start("C:\\SmarterBalanced\\content-packaging\\app\\audio-encoding\\ffmpeg.exe", "-i " + 
                                                //            //    Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix\\" + entry.Key + " " +
                                                //            //    Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix\\" + entry.Key.Substring(entry.Key.Length - 3, 3) + changeToFormat);
                                                //            //var process = System.Diagnostics.Process.Start("D:\\GitHub\\AP_BuildSmarterContentPackage\\ffmpeg.exe", "-i " +
                                                //            //    Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix\\" + entry.Key + " -hide_banner -loglevel error -codec:a libvorbis -b:a 48k -f ogg " +
                                                //            //    Path.GetDirectoryName(PackageFileName) + "\\FilesWithAudioFix\\" + entry.Key);

                                            }
                                        }
                                        // check the audio glossary file name pattern. #3
                                        Match audioMatch = Regex.Match(entry.Key, fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);
                                        Match audioMatchLegacy = Regex.Match(entry.Key, fileNamePatternAudioGlossaryLegacy, RegexOptions.IgnoreCase);
                                        if (audioMatch.Success || audioMatchLegacy.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for audio in glossary.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for audio in glossary.", "");

                                            Console.WriteLine($"      {entry.Key} is a valid referenced WIT audio or image file");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid WIT audio or image file.", "");
                                        }
                                        else
                                        {
                                            if (IncludeWitFileRenaming)
                                            {
                                                Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for audio in glossary. Proceeding to rename the file.");
                                                Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for audio in glossary. Proceeding to rename the file.", "");

                                                AudioFile currentAudioFile = new AudioFile();
                                                currentWitFileNames.newFileName = currentAudioFile.Rename(entry.Key, itemId);
                                                currentWitFileNames.oldFileName = entry.Key;

                                                Console.WriteLine($"      Renamed {entry.Key} to {currentWitFileNames.newFileName}.");
                                                Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Renamed " + entry.Key + " to " + currentWitFileNames.newFileName, "");
                                                Console.WriteLine($"      {currentWitFileNames.newFileName} is a valid referenced WIT audio or image file");
                                                Program.ProgressLog.Log(Severity.Message, itemId.ToString(), currentWitFileNames.newFileName + " is a valid WIT audio or image file.", "");

                                                witFileNames.Add(currentWitFileNames);
                                            }
                                            else
                                            {
                                                Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for audio in glossary. Consider renaming the file.");
                                                Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for audio in glossary. Consider renaming the file.", "");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // illustration glossary file. check the file name pattern. #7
                                        Match illustrationMatch = Regex.Match(entry.Key, fileNamePatternIllustrationGlossary, RegexOptions.IgnoreCase);
                                        if (illustrationMatch.Success)
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file name pattern for illustrated glossary.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file name pattern for illustrated glossary.", "");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"      {entry.Key} is a valid file, but the file name pattern is not valid for illustrated glossary. Consider renaming the file.");
                                            Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid file, but the file name pattern is not valid for illustrated glossary. Consider renaming the file.", "");
                                        }
                                        Console.WriteLine($"      {entry.Key} is a valid referenced WIT audio or image file");
                                        Program.ProgressLog.Log(Severity.Message, itemId.ToString(), entry.Key + " is a valid WIT audio or image file.", "");
                                    }

                                    validEntry = true;

                                }
                                else
                                {
                                    validEntry = false;
                                    Console.WriteLine($"      {entry.Key} is NOT a valid referenced WIT audio or image file");
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

                        string fileName = "";
                        if (validEntry) {
                            using (var inStr = m_gitLab.ReadBlob(projectId, entry.Value))
                            {
                                // replace the entry.Key with the updated filename for WIT audio files
                                if (!currentWitFileNames.newFileName.Equals(""))
                                {
                                    fileName = currentWitFileNames.newFileName;
                                }
                                else
                                {
                                    fileName = entry.Key;
                                }

                                // ignore the item file
                                if (!entry.Key.Equals(itemXmlName, StringComparison.OrdinalIgnoreCase)) 
                                { 
                                    var zipEntry = m_zipArchive.CreateEntry(directoryPath + fileName);
                                    using (var outStr = zipEntry.Open())
                                    {
                                        // If this is the item file, save a copy in a memory stream.
                                        //if (entry.Key.Equals(itemXmlName, StringComparison.OrdinalIgnoreCase))
                                        //{
                                            //itemXmlStream = new MemoryStream();
                                            //inStr.CopyTo(itemXmlStream); // this saves the stream to a temporary memory stream for later use 
                                            //itemXmlStream.Position = 0;

                                            //string xmlContent = string.Empty;
                                            //StreamReader sr = new StreamReader(itemXmlStream);
                                            //xmlContent = sr.ReadToEnd();

                                            // for WITS, update the stream with the updated file names from the list, only if the IncludeWitFileRenaming is true

                                            //foreach (WitFileNames currentFileNames in witFileNames)
                                            //{
                                            //    if (IncludeWitFileRenaming)
                                            //    {
                                            //        xmlContent = xmlContent.Replace(currentFileNames.oldFileName, currentFileNames.newFileName);
                                            //    }
                                            //}

                                            //MemoryStream ms = new MemoryStream();
                                            //byte[] contentArray = Encoding.ASCII.GetBytes(xmlContent);
                                            //ms.Write(contentArray, 0, contentArray.Length);
                                            //ms.Position = 0;

                                            // do not save a copy to the zip file just yet. 
                                            // There may be other files that proceed after the item file
                                            // that require renaming, and the item file will need to be updated.
                                            // After the main for loop is done, the remaining renamed file names
                                            // will be updated in the item file, then added to the 
                                            // zip file
                                            //ms.CopyTo(outStr); // this saves the stream to the zip file
                                        //}

                                        // Else, just copy directly as long as it is a valid attachment file
                                        //else
                                        //{
                                            inStr.CopyTo(outStr); // this saves the stream to the zip file
                                        //}
                                    }
                                }
                                else
                                {
                                    itemXmlStream = new MemoryStream();
                                    inStr.CopyTo(itemXmlStream); // this saves the stream to a temporary memory stream for later use 
                                    itemXmlStream.Position = 0;
                                }
                                

                                //place into the manifest. this will need to be an item added to the manifestBuilder.Items (or stim added to the manifestBuilder.Stims)
                                //need to determine a couple of things: 
                                //  1. determine the object. if not metadata, then a dependency, which belongs as an asset type or stim
                                //  2. need to determine the stim, wit, and tutorial dependencies, if any. investigate the contentXml variable
                                //  3. handle metadata on it's own because the file is always called metadata
                                if (fileName != "metadata.xml" &&
                                    !fileName.Equals(itemXmlName, StringComparison.OrdinalIgnoreCase))
                                {
                                    Item assetItem = new Item();
                                    assetItem.Identifier = fileName.Replace('.', '_'); // replace the "dot" file extension as an "underscore" file extension
                                    assetItem.Type = Item.ResourceType.AllOtherAssets;
                                    assetItem.Folder = parentItem.Folder;
                                    assetItem.Href = parentItem.Folder + fileName;
                                    assetItem.IsADependency = true;

                                    parentItem.DependentAssets.Add(assetItem);
                                }
                                else if (fileName == "metadata.xml")
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
                } // end of the for...loop through each and every file in the item folder

                // now that the for..loop is done, take the itemXmlStream, and replace any updated WIT files
                StreamReader sr = new StreamReader(itemXmlStream);
                string xmlContent = string.Empty;
                xmlContent = sr.ReadToEnd();
                if (IncludeWitFileRenaming)
                {
                    foreach (WitFileNames currentFileNames in witFileNames)
                    {
                        xmlContent = xmlContent.Replace(currentFileNames.oldFileName, currentFileNames.newFileName);
                    }
                }
                var zipEntryForItemXml = m_zipArchive.CreateEntry(directoryPath + itemXmlName);

                using (var outStr = zipEntryForItemXml.Open())
                {
                    MemoryStream ms = new MemoryStream();
                    byte[] contentArray = Encoding.ASCII.GetBytes(xmlContent);
                    ms.Write(contentArray, 0, contentArray.Length);
                    ms.Position = 0;
                    ms.CopyTo(outStr); // this saves the stream to the zip file
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

                            // Find any stimulus references. 
                            // Prior to 5/28/2019, the Stim id could be found in two locations: in the <attriblist> elements and the <associatedpassage> element.
                            // After 5/28/2019, the attriblist element <attrib  attid="stm_pass_id"> contains the ITS ID, which is appropriate as the attrib name value is "Stim: ITS ID".
                            // Only the <associatedpassage> element contains the TIMS ID for the stim. Not all items have an <associatedpassage> element, so check for a null XElement object.

                            XElement stimulusElement = itemEle.Element("associatedpassage");
                            if (stimulusElement != null) { 
                                var stimId = new ItemId(ItemClass.Stim,
                                            bankKey,
                                            int.Parse(stimulusElement.Value));
                                Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Item depends on stimulus", stimId.ToString());
                                if (AddId(stimId))
                                {
                                    ++stimsAdded;
                                }

                                // add the stim item for the manifest
                                Item stimItem = new Item();
                                stimItem.Identifier = ItemClass.Stim.ToString().ToLower() + "-" + bankKey + "-" + stimId.Id;
                                stimItem.Type = Item.ResourceType.Stim;
                                stimItem.Folder = "Stimuli/" + ItemClass.Stim.ToString().ToLower() + "-" + bankKey + "-" + stimId.Id;
                                stimItem.Href = stimItem.Folder + stimItem.Identifier + ".xml";
                                stimItem.IsADependency = true;

                                parentItem.DependentStim = stimItem;
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
                                    Program.ProgressLog.Log(Severity.Message, itemId.ToString(), "Stim depends on WordList", witId.ToString());
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
        const string c_emptyManifest = "<manifest xmlns=\"http://www.imsglobal.org/xsd/apip/apipv1p0/imscp_v1p1\"></manifest>";

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

        void AddEmptyManifest()
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
    
    class WitFileNames
    {
        public string newFileName { get; set; }
        public string oldFileName { get; set; }

        public WitFileNames()
        {
            newFileName = "";
            oldFileName = "";
        }
    }

    public class AudioFile
    {
        public string Rename(string oldFileName, ItemId itemId)
        {
            string returnValue = "";
            // Rename the file in the 
            // (item|stim)_<tims id>_<term>_v<major.minor>_<language>_<dialect indicator, if needed>.(m4a|ogg) format
            // the incorrect file name can either be:
            //
            // item_<tims id>_<term>_<language>_<dialect indicator>.(m4a|ogg) - 5 elements
            // or
            // item_<tims id>_<term>_<language>.(m4a|ogg) - 4 elements
            // or
            // <tims id>_<term>_<language>.(m4a|ogg) - 3 elements
            // or
            // <tims id>_<term>_<language>_<dialect indicator>.(m4a|ogg) - 4 elements
            // or
            // <term>_<langauge>.(m4a|ogg) - 2 elements
            // or
            // <term>_<langauge>_<dialect indicator>.(m4a|ogg) - 3 elements
            //
            // split the file name on the underscore, and use the component parts to build the new file name, filling in the missing pieces.
            // default to v1.0 for the version number

            // find the root tims id by subtracting 600000000
            string rootTimsId = (Convert.ToInt32(itemId.Id) - 600000000).ToString();

            string[] fileNameParts = oldFileName.Split('_');

            // check if the first fileNamePart is 'item'. Add it in if it is not
            if (!fileNameParts[0].Equals(itemId.Class.ToString().ToLower()))
            {
                returnValue = itemId.Class.ToString().ToLower();
            }

            if (fileNameParts.Count().Equals(2))
            {
                returnValue += "_" + rootTimsId +
                               "_" + fileNameParts[0].ToLower() +
                               "_v1.0_" + fileNameParts[1].ToLower();

            }
            else if (fileNameParts.Count().Equals(3) &&
                   !fileNameParts[0].Equals(rootTimsId))
            {
                returnValue += "_" + rootTimsId +
                               "_" + fileNameParts[0].ToLower() +
                               "_v1.0_" + fileNameParts[1].ToLower() +
                               "_" + fileNameParts[2].ToLower();
            }
            else if (fileNameParts.Count().Equals(3) &&
                     fileNameParts[0].Equals(rootTimsId))
            {
                returnValue += "_" + fileNameParts[0].ToLower() +
                               "_" + fileNameParts[1].ToLower() +
                               "_v1.0_" + fileNameParts[2].ToLower();
            }
            else if (fileNameParts.Count().Equals(4) &&
                     fileNameParts[0].Equals(itemId.Class.ToString().ToLower()))
            {
                returnValue += fileNameParts[0] + 
                               "_" + fileNameParts[1].ToLower() +
                               "_" + fileNameParts[2].ToLower() +
                               "_v1.0_" + fileNameParts[3].ToLower();
            }
            else if (fileNameParts.Count().Equals(4) &&
                     !fileNameParts[0].Equals(itemId.Class.ToString().ToLower())) //<tims id>_<term>_<language>_<dialect indicator>.(m4a|ogg)
            {
                returnValue += "_" + fileNameParts[0].ToLower() +
                               "_" + fileNameParts[1].ToLower() +
                               "_v1.0_" + fileNameParts[2].ToLower() +
                               "_" + fileNameParts[3].ToLower();
            }
            else if (fileNameParts.Count().Equals(5))
            {
                returnValue += fileNameParts[0].ToLower() +
                               "_" + fileNameParts[1].ToLower() +
                               "_" + fileNameParts[2].ToLower() +
                               "_v1.0_" + fileNameParts[3].ToLower() +
                               "_" + fileNameParts[4].ToLower();
            }

            return returnValue;
        }
    }
}
