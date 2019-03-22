using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Npgsql;

namespace BuildSmarterContentPackage
{
    public class PostGresDb
    {
        private NpgsqlConnection conn = new NpgsqlConnection();

        public string Message { get; set; }
        private bool connectionState { get; set; }
        private bool queryHasResults { get; set; }
        public List<ItemAttachment> itemAttachments = new List<ItemAttachment>();

        public void Connect(string connString)
        {
            conn.ConnectionString = connString;
            try { 
                conn.Open();
                if (conn.State.ToString() == "Open")
                {
                    connectionState = true;                        
                    Message = "Connection to the imrt database is " + conn.State + ".";
                }
                else
                {
                    connectionState = false;
                    Message = "Connection to the imrt database is " + conn.State + ".";
                }
            }
            catch (Exception ex)
            {
                connectionState = false;
                Message = "Exception in connecting to the imrt database. " + ex.Message;
            }            
        }

        
        public void GetItemAttachments(int itemId)
        {            
            using (var cmd = new NpgsqlCommand())
            {
                cmd.Connection = conn;
                cmd.CommandText = "SELECT i.id as item_id, ia.file_name " +
                                  "FROM item_attachment as ia " +
                                  "LEFT JOIN item as i " +
                                  "ON i.key = ia.item_key " +
                                  "WHERE i.id = @itemId";
                cmd.Parameters.AddWithValue("itemId", itemId);
                using (var reader = cmd.ExecuteReader())
                {
                    queryHasResults = reader.HasRows;
                    if (reader.HasRows)
                    {
                        while (reader.Read())
                        {
                            // go through the result set and populate attachment objects into a list
                            ItemAttachment currentAttachment = new ItemAttachment();
                            currentAttachment.FileName = reader.GetString(1);
                            itemAttachments.Add(currentAttachment);
                        }
                        Message = "Item " + itemId + " has " + itemAttachments.Count + " file attachments.";
                    }                    
                }
            }
        }

        public bool QueryReturnedResults()
        {
            return queryHasResults;
        }

        public void Disconnect()
        {
            if (connectionState == true)
            {
                conn.Close();
                Message = "Connection to the imrt database is closed";
            }
        }

        public bool ConnectionStatus()
        {
            return connectionState;
        }
    }
}
