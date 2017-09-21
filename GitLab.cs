using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;
using System.Net;
using System.Net.Http;
using System.IO;

// From GitLab API Documentation Here: https://docs.gitlab.com/ce/api/

namespace BuildSmarterContentPackage
{
    /// <summary>
    /// C# Connection to the GitLab API
    /// </summary>
    class GitLab : IDisposable
    {
        const string c_GitLabApiPath = "/api/v4/";

        HttpClient m_httpClient;
        string m_accessToken;

        public GitLab(string serverUrl, string accessToken)
        {
            m_accessToken = accessToken;
            var handler = new HttpClientHandler();
            handler.CookieContainer = new System.Net.CookieContainer();
            m_httpClient = new HttpClient(handler);
            m_httpClient.BaseAddress = new Uri(serverUrl);
        }

        /// <summary>
        /// Get the ID that corresponds to a project (repository) name. 
        /// </summary>
        /// <param name="ns">The namespace (username or group name) that owns the project.</param>
        /// <param name="name">The name of the project.</param>
        /// <returns>The project ID.</returns>
        public string ProjectIdFromName(string ns, string name)
        {
            UriBuilder uri = new UriBuilder(m_httpClient.BaseAddress);
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
            UriBuilder uri = new UriBuilder(m_httpClient.BaseAddress);
            uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(projectId), "/repository/tree");
            uri.Query = "recursive=true";

            var doc = HttpReceiveJson(uri.Uri);

            var result = new List<KeyValuePair<string, string>>();
            foreach(var el in doc.Elements("item"))
            {
                result.Add(new KeyValuePair<string, string>(el.Element("path").Value, el.Element("id").Value));
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
            UriBuilder uri = new UriBuilder(m_httpClient.BaseAddress);
            uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(projectId), "/repository/blobs/", blobId, "/raw");

            try
            {
                HttpWebRequest request = WebRequest.CreateHttp(uri.Uri);
                request.Headers["PRIVATE-TOKEN"] = m_accessToken;
                WebResponse response = request.GetResponse();
                return response.GetResponseStream();
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }
        }

        private XElement HttpReceiveJson(Uri uri)
        {
            HttpWebRequest request = WebRequest.CreateHttp(uri);
            request.Headers["PRIVATE-TOKEN"] = m_accessToken;
            return HttpReceiveJson(request);
        }

        private static XElement HttpReceiveJson(HttpWebRequest request)
        {
            XElement doc = null;
            try
            {
                WebResponse response = request.GetResponse();
                using (var stream = response.GetResponseStream())
                {
                    using (var jsonReader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(stream, new System.Xml.XmlDictionaryReaderQuotas()))
                    {
                        doc = XElement.Load(jsonReader);
                    }
                }
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }

            return doc;
        }

        private static Exception ConvertWebException(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
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

        public void Dispose()
        {
            if (m_httpClient != null)
            {
                m_httpClient.Dispose();
            }
            m_httpClient = null;
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
