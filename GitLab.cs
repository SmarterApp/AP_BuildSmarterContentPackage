using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

// From GitLab API Documentation Here: https://docs.gitlab.com/ce/api/

namespace BuildSmarterContentPackage
{
    /// <summary>
    /// C# Connection to the GitLab API
    /// </summary>
    class GitLab
    {
        const string c_GitLabApiPath = "/api/v4/";

        // GitLab API max items per page is 100.
        const int c_filesPerPage = 100;

        Uri m_baseAddress;
        string m_accessToken;

        public GitLab(string serverUrl, string accessToken)
        {
            m_baseAddress = new Uri(serverUrl);
            m_accessToken = accessToken;
        }

        /// <summary>
        /// Get the ID that corresponds to a project (repository) name. 
        /// </summary>
        /// <param name="ns">The namespace (username or group name) that owns the project.</param>
        /// <param name="name">The name of the project.</param>
        /// <returns>The project ID.</returns>
        public string ProjectIdFromName(string ns, string name)
        {
            UriBuilder uri = new UriBuilder(m_baseAddress);
            uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(string.Concat(ns, "/", name)));
            var doc = HttpReceiveJson(uri.Uri);
            return doc.Element("id").Value;
        }

        /// <summary>
        /// List the blobs (files) that belong to a project (repository).
        /// </summary>
        /// <param name="projectId">The project ID for which to list files.</param>
        /// <returns>A list of key value pairs. The keys are the names of the blobs (files). The values are the IDs.</returns>
        public IReadOnlyList<KeyValuePair<string, string>> ListRepositoryTree(string projectId)
        {

            var result = new List<KeyValuePair<string, string>>();

            // This API is paginated, it may require multiple requests to retrieve all items.
            int totalExpectedFiles = 0;
            for (int page=1; true; ++page) // Gitlab numbers pages starting with 1
            {
                UriBuilder uri = new UriBuilder(m_baseAddress);
                uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(projectId), "/repository/tree");
                uri.Query = $"recursive=true&page={page}&per_page={c_filesPerPage}";

                var request = HttpPrepareRequest(uri.Uri);
                using (var response = HttpGetResponseHandleErrors(request))
                {
                    // Get the total expected files
                    int.TryParse(response.GetResponseHeader("X-Total"), out totalExpectedFiles);

                    // Get the total pages
                    int totalPages = 0;
                    int.TryParse(response.GetResponseHeader("X-Total-Pages"), out totalPages);

                    // Get the returned page number and check to make sure it was the right one.
                    int pageReturned = 0;
                    int.TryParse(response.GetResponseHeader("X-Page"), out pageReturned);
                    if (pageReturned != page)
                    {
                        throw new ApplicationException($"GitLab returned page {pageReturned} expected {page}");
                    }

                    // Retrieve the files
                    var doc = HttpReceiveJson(response);
                    foreach (var el in doc.Elements("item"))
                    {
                        result.Add(new KeyValuePair<string, string>(el.Element("path").Value, el.Element("id").Value));
                    }

                    // Exit if this is the last page
                    if (page >= totalPages) break;
                }
            }

            if (result.Count != totalExpectedFiles)
            {
                throw new ApplicationException($"Expected {totalExpectedFiles} files in item but received {result.Count}");
            }

            return result;
        }

        /// <summary>
        /// Reads a blob (file) from a project on GitLab.
        /// </summary>
        /// <param name="projectId">The id of the project containing the blob.</param>
        /// <param name="blobId">The id of the blob (not the name).</param>
        /// <returns>A read-forward stream with the contents of the blob.</returns>
        /// <remarks>
        /// Be sure to dispose the stream when reading is complete.
        /// </remarks>
        public Stream ReadBlob(string projectId, string blobId)
        {
            UriBuilder uri = new UriBuilder(m_baseAddress);
            uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(projectId), "/repository/blobs/", blobId, "/raw");

            try
            {
                HttpWebRequest request = HttpPrepareRequest(uri.Uri);
                WebResponse response = HttpGetResponseHandleErrors(request);
                return response.GetResponseStream(); // Closing the stream also closes the response so we don't need to dispose response
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }
        }

        private HttpWebRequest HttpPrepareRequest(Uri uri)
        {
            HttpWebRequest request = WebRequest.CreateHttp(uri);
            request.Headers["PRIVATE-TOKEN"] = m_accessToken;
            return request;
        }

        private static HttpWebResponse HttpGetResponseHandleErrors(HttpWebRequest request)
        {
            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }
        }

        private static XElement HttpReceiveJson(HttpWebResponse response)
        {
            XElement doc = null;
            using (var stream = response.GetResponseStream())
            {
                using (var jsonReader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(stream, new System.Xml.XmlDictionaryReaderQuotas()))
                {
                    doc = XElement.Load(jsonReader);
                }
            }
            return doc;
        }

        private XElement HttpReceiveJson(Uri uri)
        {
            HttpWebRequest request = HttpPrepareRequest(uri);
            using (var response = HttpGetResponseHandleErrors(request))
            {
                return HttpReceiveJson(response);
            }
        }

        private static Exception ConvertWebException(WebException ex)
        {
            HttpWebResponse response = null;
            try
            {
                response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    string detail;
                    using (var reader = new System.IO.StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        detail = reader.ReadToEnd();
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new HttpNotFoundException(string.Concat("HTTP Resource Not Found: ", response.ResponseUri, "\r\n", detail));
                    }
                    else
                    {
                        return new ApplicationException(string.Concat("HTTP ERROR\r\n", detail));
                    }
                }
                return new ApplicationException("HTTP ERROR", ex);
            }
            finally
            {
                if (response != null)
                {
                    response.Dispose();
                    response = null;
                }
            }
        }

        static void DumpXml(XElement xml)
        {
            var settings = new System.Xml.XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = true;
            using (var writer = System.Xml.XmlWriter.Create(Console.Error, settings))
            {
                xml.WriteTo(writer);
            }
            Console.Error.WriteLine();
        }
    }

    class HttpNotFoundException : Exception
    {
        public HttpNotFoundException(string message)
            : base(message)
        {
        }

        public HttpNotFoundException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
