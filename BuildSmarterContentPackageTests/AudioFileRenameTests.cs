using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BuildSmarterContentPackage;
using System.Text.RegularExpressions;

namespace BuildSmarterContentPackageTests
{
    [TestClass]
    public class AudioFileRenameTests
    {
        AudioFile audioFile = new AudioFile();
        
        [TestMethod]
        public void TestRenameAudioFile_2Parts()
        {
            //<term> _<langauge>.(m4a | ogg)
            PackageBuilder packageBuilder = new PackageBuilder();
            ItemId itemId = new ItemId(ItemClass.Item, 200, 600123456);
            string renamedFile = audioFile.Rename("hello_vietnamese.m4a", itemId);
            Match audioMatch = Regex.Match(renamedFile, packageBuilder.fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);

            Assert.IsTrue(audioMatch.Success, "Renamed file: " + renamedFile);
            Console.WriteLine("Success! Renamed audio file matches pattern: {0}", renamedFile);            
        }

        [TestMethod]
        public void TestRenameAudioFile_3PartsA()
        {
            //<tims id>_<term>_<language>.(m4a|ogg)
            PackageBuilder packageBuilder = new PackageBuilder();
            ItemId itemId = new ItemId(ItemClass.Item, 200, 600123456);
            string renamedFile = audioFile.Rename("123456_hello_vietnamese.m4a", itemId);
            Match audioMatch = Regex.Match(renamedFile, packageBuilder.fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);

            Assert.IsTrue(audioMatch.Success, "Renamed file: " + renamedFile);
            Console.WriteLine("Success! Renamed audio file matches pattern: {0}", renamedFile);
        }

        [TestMethod]
        public void TestRenameAudioFile_3PartsB()
        {
            //<term>_<langauge>_<dialect indicator>.(m4a|ogg)
            PackageBuilder packageBuilder = new PackageBuilder();
            ItemId itemId = new ItemId(ItemClass.Item, 200, 600123456);
            string renamedFile = audioFile.Rename("hello_vietnamese_a.m4a", itemId);
            Match audioMatch = Regex.Match(renamedFile, packageBuilder.fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);

            Assert.IsTrue(audioMatch.Success, "Renamed file: " + renamedFile);
            Console.WriteLine("Success! Renamed audio file matches pattern: {0}", renamedFile);
        }

        [TestMethod]
        public void TestRenameAudioFile_4PartsA()
        {
            //item_<tims id>_<term>_<language>.(m4a|ogg)
            PackageBuilder packageBuilder = new PackageBuilder();
            ItemId itemId = new ItemId(ItemClass.Item, 200, 600123456);
            string renamedFile = audioFile.Rename("item_123456_hello_vietnamese.m4a", itemId);
            Match audioMatch = Regex.Match(renamedFile, packageBuilder.fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);

            Assert.IsTrue(audioMatch.Success, "Renamed file: " + renamedFile);
            Console.WriteLine("Success! Renamed audio file matches pattern: {0}", renamedFile);
        }

        [TestMethod]
        public void TestRenameAudioFile_4PartsB()
        {
            //<tims id>_<term>_<language>_<dialect indicator>.(m4a|ogg)
            PackageBuilder packageBuilder = new PackageBuilder();
            ItemId itemId = new ItemId(ItemClass.Item, 200, 600123456);
            string renamedFile = audioFile.Rename("123456_hello_vietnamese_a.m4a", itemId);
            Match audioMatch = Regex.Match(renamedFile, packageBuilder.fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);

            Assert.IsTrue(audioMatch.Success, "Renamed file: " + renamedFile);
            Console.WriteLine("Success! Renamed audio file matches pattern: {0}", renamedFile);
        }

        [TestMethod]
        public void TestRenameAudioFile_5Parts()
        {
            //item_<tims id>_<term>_<language>_<dialect indicator>.(m4a|ogg)
            PackageBuilder packageBuilder = new PackageBuilder();
            ItemId itemId = new ItemId(ItemClass.Item, 200, 600123456);
            string renamedFile = audioFile.Rename("item_123456_hello_vietnamese_a.m4a", itemId);
            Match audioMatch = Regex.Match(renamedFile, packageBuilder.fileNamePatternAudioGlossary, RegexOptions.IgnoreCase);

            Assert.IsTrue(audioMatch.Success, "Renamed file: " + renamedFile);
            Console.WriteLine("Success! Renamed audio file matches pattern: {0}", renamedFile);
        }
    }
}
