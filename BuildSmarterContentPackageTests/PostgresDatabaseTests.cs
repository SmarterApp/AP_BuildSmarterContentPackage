using System;
using System.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BuildSmarterContentPackage;

namespace BuildSmarterContentPackageTests
{
    [TestClass]
    public class PostgresDatabaseTests
    {
        PostGresDb imrtDb = new PostGresDb();

        [TestMethod]
        public void ConnectionTest()
        {
            imrtDb.Connect(ConfigurationManager.ConnectionStrings["imrt_connectionString"].ToString());            
            Assert.IsTrue(imrtDb.ConnectionStatus(), imrtDb.Message);
            Console.WriteLine(imrtDb.Message);
            imrtDb.Disconnect();
        }

        [TestMethod]
        public void QueryTest()
        {
            imrtDb.Connect(ConfigurationManager.ConnectionStrings["imrt_connectionString"].ToString());
            imrtDb.GetItemAttachments(13125);
            Assert.IsTrue(imrtDb.QueryReturnedResults(), imrtDb.Message);
            Console.WriteLine(imrtDb.Message);
            imrtDb.Disconnect();
        }
    }
}
